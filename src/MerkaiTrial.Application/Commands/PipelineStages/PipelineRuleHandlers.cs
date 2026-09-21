// =====================================================================
// PipelineRuleHandlers.cs
// Location: MerkaiTrial.Application/Commands/PipelineStages/PipelineRuleHandlers.cs
//
// NEW FILE (019). Read and write side of the transition rules.
//
// WHY THIS IS SEPARATE FROM PipelineStageHandlers
//   That file is about what a stage IS — its name, order, probability and
//   category — and its rules are structural: a tenant must keep one Won
//   stage, exactly one default, and cannot delete a stage deals sit in.
//   This file is about what a stage DEMANDS, which is commercial policy a
//   client changes as their process settles. Keeping them apart means the
//   settings screen for one is not a wall of unrelated switches, and
//   PipelineStageHandlers.cs does not have to be touched by this round at
//   all.
//
// All handlers are ICommandHandler, so the Scrutor scan registers them.
// Nothing to add to Program.cs.
// =====================================================================

using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.PipelineStages;

// ── DTOs ──────────────────────────────────────────────────────────────

/// <summary>What one stage asks of a deal before letting it in.</summary>
public record StageRequirementsDto(
    Guid StageId,
    string Key,
    string Name,
    int SortOrder,
    StageCategory Category,
    bool IsActive,
    bool RequiresQuote,
    bool RequiresAcceptedQuote,
    bool RequiresCloseDate,
    bool RequiresValue,
    bool RequiresLostReason);

/// <summary>
/// Everything the rules screen shows, and everything the pipeline board
/// needs to know which moves will want a reason before it asks for one.
/// </summary>
public record PipelineRulesDto(
    bool ForwardOnly,
    bool ReopenRequiresReason,
    bool ReopenRestrictedToManagers,
    bool BlockReopenWithIssuedInvoice,
    /// <summary>True when the tenant has never saved — the screen says so.</summary>
    bool IsDefault,
    DateTime? UpdatedAtUtc,
    string? UpdatedBy,
    List<StageRequirementsDto> Stages);

public record SavePipelineRulesDto(
    bool ForwardOnly,
    bool ReopenRequiresReason,
    bool ReopenRestrictedToManagers,
    bool BlockReopenWithIssuedInvoice);

public record SaveStageRequirementsDto(
    Guid StageId,
    bool RequiresQuote,
    bool RequiresAcceptedQuote,
    bool RequiresCloseDate,
    bool RequiresValue,
    bool RequiresLostReason);

/// <summary>The whole screen saved in one go — rules and every stage.</summary>
public record SaveAllPipelineRulesDto(
    SavePipelineRulesDto Rules,
    List<SaveStageRequirementsDto> Stages);

// ── READ ──────────────────────────────────────────────────────────────

public class GetPipelineRulesHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly StageTransitionGuard _guard;

    public GetPipelineRulesHandler(FlowDbContext db, StageTransitionGuard guard)
    {
        _db = db;
        _guard = guard;
    }

    public async Task<PipelineRulesDto> Handle(Guid tenantId, CancellationToken ct = default)
    {
        var (settings, isDefault) = await _guard.GetSettingsAsync(tenantId, ct);

        // Retired stages are included. A deal can still sit in one, and
        // the board needs its requirements to explain a refused move.
        var stages = await _db.PipelineStages.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.SortOrder)
            .Select(s => new StageRequirementsDto(
                s.Id, s.Key, s.Name, s.SortOrder, s.Category, s.IsActive,
                s.RequiresQuote, s.RequiresAcceptedQuote, s.RequiresCloseDate,
                s.RequiresValue, s.RequiresLostReason))
            .ToListAsync(ct);

        return new PipelineRulesDto(
            settings.ForwardOnly,
            settings.ReopenRequiresReason,
            settings.ReopenRestrictedToManagers,
            settings.BlockReopenWithIssuedInvoice,
            isDefault,
            isDefault ? null : settings.UpdatedAtUtc,
            isDefault ? null : settings.UpdatedBy,
            stages);
    }
}

// ── WRITE ─────────────────────────────────────────────────────────────

public class SavePipelineRulesHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly IAuditService _audit;
    private readonly ILogger<SavePipelineRulesHandler> _logger;

    public SavePipelineRulesHandler(
        FlowDbContext db, IAuditService audit, ILogger<SavePipelineRulesHandler> logger)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
    }

    public async Task Handle(
        Guid tenantId, SaveAllPipelineRulesDto dto, string updatedBy, CancellationToken ct = default)
    {
        // ── the whole-pipeline rules ──────────────────────────────────
        var row = await _db.PipelineRuleSettings
            .FirstOrDefaultAsync(r => r.TenantId == tenantId, ct);

        if (row is null)
        {
            row = new PipelineRuleSettings { Id = Guid.NewGuid(), TenantId = tenantId };
            _db.PipelineRuleSettings.Add(row);
        }

        row.ForwardOnly = dto.Rules.ForwardOnly;
        row.ReopenRequiresReason = dto.Rules.ReopenRequiresReason;
        row.ReopenRestrictedToManagers = dto.Rules.ReopenRestrictedToManagers;
        row.BlockReopenWithIssuedInvoice = dto.Rules.BlockReopenWithIssuedInvoice;
        row.UpdatedAtUtc = DateTime.UtcNow;
        row.UpdatedBy = updatedBy;

        // ── the per-stage requirements ────────────────────────────────
        var stages = await _db.PipelineStages
            .Where(s => s.TenantId == tenantId)
            .ToListAsync(ct);

        var changed = new List<string>();

        foreach (var incoming in dto.Stages)
        {
            var stage = stages.FirstOrDefault(s => s.Id == incoming.StageId);
            if (stage is null) continue;   // a stale id is not worth failing the save over

            // RequiresAcceptedQuote is the stronger claim. Storing both
            // would make the refusal message read "needs a quote and an
            // accepted quote", so the weaker one is folded in here rather
            // than being second-guessed every time the rule is read.
            var requiresAccepted = incoming.RequiresAcceptedQuote;
            var requiresQuote = incoming.RequiresQuote && !requiresAccepted;

            // A lost reason on an open stage would block every deal
            // entering it for a reason nobody could give. The guard
            // already ignores it there; this stops it being stored at all.
            var requiresLostReason =
                incoming.RequiresLostReason && stage.Category == StageCategory.Lost;

            var before = (stage.RequiresQuote, stage.RequiresAcceptedQuote,
                          stage.RequiresCloseDate, stage.RequiresValue, stage.RequiresLostReason);

            stage.RequiresQuote = requiresQuote;
            stage.RequiresAcceptedQuote = requiresAccepted;
            stage.RequiresCloseDate = incoming.RequiresCloseDate;
            stage.RequiresValue = incoming.RequiresValue;
            stage.RequiresLostReason = requiresLostReason;

            var after = (stage.RequiresQuote, stage.RequiresAcceptedQuote,
                         stage.RequiresCloseDate, stage.RequiresValue, stage.RequiresLostReason);

            if (!before.Equals(after))
            {
                // Deliberately NOT touching UpdatedAtUtc / UpdatedBy here.
                // Those mark an edit to the stage ITSELF — its name,
                // probability, whether it is retired — and the migration
                // uses "never edited" to decide whether it may seed the
                // default requirements. Changing a requirement is not an
                // edit to the stage.
                changed.Add(stage.Name);
            }
        }

        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(
            "PipelineRulesChanged", "PipelineRuleSettings", row.Id, tenantId,
            new
            {
                dto.Rules.ForwardOnly,
                dto.Rules.ReopenRequiresReason,
                dto.Rules.ReopenRestrictedToManagers,
                dto.Rules.BlockReopenWithIssuedInvoice,
                stagesChanged = changed
            },
            ct);

        _logger.LogInformation(
            "Pipeline rules saved for tenant {TenantId} by {User}; stages changed: {Stages}",
            tenantId, updatedBy, changed.Count == 0 ? "none" : string.Join(", ", changed));
    }
}
