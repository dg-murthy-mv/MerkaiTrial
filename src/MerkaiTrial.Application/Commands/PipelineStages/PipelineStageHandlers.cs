// =====================================================================
// PipelineStageHandlers.cs
// Location: MerkaiTrial.Application/Commands/PipelineStages/
//
// NEW FILE. Read and write side of the tenant's pipeline.
//
// THE RULES, ENFORCED HERE RATHER THAN IN THE UI
//   • Key is generated once from the name and never changes. Renaming a
//     stage must not move the deals sitting in it.
//   • A tenant must always have at least one Won and one Lost stage, or
//     no deal can ever be closed.
//   • Exactly one default stage, or new deals have nowhere to start.
//   • A stage holding deals cannot be deleted — only deactivated. A hard
//     delete would orphan them, and the FK would refuse anyway.
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text;

namespace MerkaiTrial.Application.Commands.PipelineStages;

// ── DTOs ──────────────────────────────────────────────────────────────

public record PipelineStageDto(
    Guid Id,
    string Key,
    string Name,
    int SortOrder,
    int Probability,
    StageCategory Category,
    bool IsActive,
    bool IsDefault,
    /// <summary>How many live deals sit in this stage. Drives whether it
    /// can be removed, and warns before a retirement.</summary>
    int DealCount);

public record CreatePipelineStageDto(
    Guid TenantId,
    string Name,
    int Probability,
    StageCategory Category,
    string? CreatedBy = null);

public record UpdatePipelineStageDto(
    Guid TenantId,
    Guid StageId,
    string Name,
    int Probability,
    bool IsActive,
    string? UpdatedBy = null);

public record ReorderPipelineStagesDto(
    Guid TenantId,
    /// <summary>Stage ids in the order they should appear.</summary>
    List<Guid> OrderedIds,
    string? UpdatedBy = null);

// ── READ ──────────────────────────────────────────────────────────────

public class GetPipelineStagesHandler : ICommandHandler
{
    private readonly FlowDbContext _db;

    public GetPipelineStagesHandler(FlowDbContext db) => _db = db;

    /// <param name="activeOnly">
    /// True for pickers — a retired stage should not be offered for new
    /// work. False for settings and for resolving the stage of an
    /// existing deal, which may sit in a retired one.
    /// </param>
    public async Task<List<PipelineStageDto>> Handle(
        Guid tenantId, bool activeOnly = false, CancellationToken ct = default)
    {
        var q = _db.PipelineStages.AsNoTracking().Where(s => s.TenantId == tenantId);
        if (activeOnly) q = q.Where(s => s.IsActive);

        var stages = await q.OrderBy(s => s.SortOrder).ToListAsync(ct);

        // One grouped query rather than a count per stage.
        var counts = await _db.Deals.AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted)
            .GroupBy(d => d.Stage)
            .Select(g => new { Stage = g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Stage, x => x.N, ct);

        return stages.Select(s => new PipelineStageDto(
            s.Id, s.Key, s.Name, s.SortOrder, s.Probability,
            s.Category, s.IsActive, s.IsDefault,
            counts.GetValueOrDefault(s.Key))).ToList();
    }
}

// ── SHARED ────────────────────────────────────────────────────────────

internal static class StageKeys
{
    /// <summary>
    /// "Site Visit" -> "SiteVisit". Letters and digits only, so the key
    /// is safe in a URL, a constraint and a Thai-named stage alike — a
    /// stage called "ตรวจหน้างาน" still needs a usable key.
    /// </summary>
    public static string FromName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
            if (char.IsLetterOrDigit(c) && c < 128) sb.Append(c);

        var key = sb.ToString();

        // Non-Latin names produce nothing usable — fall back to something
        // stable rather than an empty key.
        return key.Length == 0 ? "Stage" + DateTime.UtcNow.Ticks.ToString()[^6..] : key;
    }

    public static async Task<string> UniqueAsync(
        FlowDbContext db, Guid tenantId, string name, CancellationToken ct)
    {
        var baseKey = FromName(name);
        var key = baseKey;
        var n = 2;

        while (await db.PipelineStages.AnyAsync(s => s.TenantId == tenantId && s.Key == key, ct))
            key = baseKey + n++;

        return key;
    }
}

// ── CREATE ────────────────────────────────────────────────────────────

public class CreatePipelineStageHandler : ICommandHandler
{
    private const int MaxStages = 20;

    private readonly FlowDbContext _db;
    private readonly ILogger<CreatePipelineStageHandler> _logger;

    public CreatePipelineStageHandler(FlowDbContext db, ILogger<CreatePipelineStageHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<PipelineStageDto> Handle(CreatePipelineStageDto dto, CancellationToken ct = default)
    {
        var name = dto.Name?.Trim() ?? "";
        if (name.Length == 0)
            throw new InvalidOperationException("Give the stage a name.");
        if (name.Length > 100)
            throw new InvalidOperationException("Stage names can be at most 100 characters.");

        var existing = await _db.PipelineStages
            .Where(s => s.TenantId == dto.TenantId)
            .ToListAsync(ct);

        if (existing.Count >= MaxStages)
            throw new InvalidOperationException(
                $"A pipeline can have at most {MaxStages} stages. Retire one you no longer use.");

        if (existing.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"You already have a stage called \"{name}\".");

        var probability = Math.Clamp(dto.Probability, 0, 100);

        var stage = new PipelineStage
        {
            Id = Guid.NewGuid(),
            TenantId = dto.TenantId,
            Key = await StageKeys.UniqueAsync(_db, dto.TenantId, name, ct),
            Name = name,
            // New stages go at the end; the user drags them where they want.
            SortOrder = existing.Count == 0 ? 1 : existing.Max(s => s.SortOrder) + 1,
            Probability = probability,
            Category = dto.Category,
            IsActive = true,
            IsDefault = existing.Count == 0,   // first stage ever is the default
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = dto.CreatedBy
        };

        _db.PipelineStages.Add(stage);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Created pipeline stage {Name} ({Key}) for tenant {TenantId}",
            stage.Name, stage.Key, dto.TenantId);

        return new PipelineStageDto(stage.Id, stage.Key, stage.Name, stage.SortOrder,
            stage.Probability, stage.Category, stage.IsActive, stage.IsDefault, 0);
    }
}

// ── UPDATE ────────────────────────────────────────────────────────────

public class UpdatePipelineStageHandler : ICommandHandler
{
    private readonly FlowDbContext _db;

    public UpdatePipelineStageHandler(FlowDbContext db) => _db = db;

    public async Task Handle(UpdatePipelineStageDto dto, CancellationToken ct = default)
    {
        var stage = await _db.PipelineStages
            .FirstOrDefaultAsync(s => s.Id == dto.StageId && s.TenantId == dto.TenantId, ct)
            ?? throw new KeyNotFoundException("Stage not found");

        var name = dto.Name?.Trim() ?? "";
        if (name.Length == 0)
            throw new InvalidOperationException("Give the stage a name.");

        var clash = await _db.PipelineStages.AnyAsync(
            s => s.TenantId == dto.TenantId && s.Id != dto.StageId && s.Name == name, ct);
        if (clash)
            throw new InvalidOperationException($"You already have a stage called \"{name}\".");

        // Retiring a stage: check it would not leave the pipeline unable
        // to close a deal, and warn if deals are sitting in it.
        if (stage.IsActive && !dto.IsActive)
        {
            if (stage.IsDefault)
                throw new InvalidOperationException(
                    "This is where new deals start. Make another stage the starting point first.");

            var stillActive = await _db.PipelineStages.CountAsync(
                s => s.TenantId == dto.TenantId && s.Category == stage.Category
                     && s.IsActive && s.Id != dto.StageId, ct);

            if (stillActive == 0 && stage.Category != StageCategory.Open)
                throw new InvalidOperationException(
                    $"This is your only {stage.Category} stage. Without it, no deal could be marked as {stage.Category.ToString().ToLower()}.");

            var dealsHere = await _db.Deals.CountAsync(
                d => d.TenantId == dto.TenantId && d.Stage == stage.Key && !d.IsDeleted, ct);

            if (dealsHere > 0)
                throw new InvalidOperationException(
                    $"{dealsHere} deal(s) are still in this stage. Move them first, then retire it.");
        }

        // Key is deliberately NOT recalculated from the new name. Deals
        // store the key; regenerating it would strand every deal in this
        // stage and break the foreign key.
        stage.Name = name;
        stage.Probability = Math.Clamp(dto.Probability, 0, 100);
        stage.IsActive = dto.IsActive;
        stage.UpdatedAtUtc = DateTime.UtcNow;
        stage.UpdatedBy = dto.UpdatedBy;

        await _db.SaveChangesAsync(ct);
    }
}

// ── REORDER ───────────────────────────────────────────────────────────

public class ReorderPipelineStagesHandler : ICommandHandler
{
    private readonly FlowDbContext _db;

    public ReorderPipelineStagesHandler(FlowDbContext db) => _db = db;

    public async Task Handle(ReorderPipelineStagesDto dto, CancellationToken ct = default)
    {
        var stages = await _db.PipelineStages
            .Where(s => s.TenantId == dto.TenantId)
            .ToListAsync(ct);

        var order = 1;
        foreach (var id in dto.OrderedIds)
        {
            var stage = stages.FirstOrDefault(s => s.Id == id);
            if (stage is null) continue;   // a stale id is not worth failing over

            stage.SortOrder = order++;
            stage.UpdatedAtUtc = DateTime.UtcNow;
            stage.UpdatedBy = dto.UpdatedBy;
        }

        await _db.SaveChangesAsync(ct);
    }
}

// ── SET DEFAULT ───────────────────────────────────────────────────────

public class SetDefaultPipelineStageHandler : ICommandHandler
{
    private readonly FlowDbContext _db;

    public SetDefaultPipelineStageHandler(FlowDbContext db) => _db = db;

    public async Task Handle(Guid tenantId, Guid stageId, string? updatedBy, CancellationToken ct = default)
    {
        var stages = await _db.PipelineStages.Where(s => s.TenantId == tenantId).ToListAsync(ct);

        var target = stages.FirstOrDefault(s => s.Id == stageId)
            ?? throw new KeyNotFoundException("Stage not found");

        if (!target.IsActive)
            throw new InvalidOperationException("A retired stage can't be where new deals start.");

        if (target.Category != StageCategory.Open)
            throw new InvalidOperationException(
                "New deals must start in an open stage, not a won or lost one.");

        foreach (var s in stages)
        {
            s.IsDefault = s.Id == stageId;
            s.UpdatedAtUtc = DateTime.UtcNow;
            s.UpdatedBy = updatedBy;
        }

        await _db.SaveChangesAsync(ct);
    }
}

// ── DELETE ────────────────────────────────────────────────────────────

public class DeletePipelineStageHandler : ICommandHandler
{
    private readonly FlowDbContext _db;

    public DeletePipelineStageHandler(FlowDbContext db) => _db = db;

    public async Task Handle(Guid tenantId, Guid stageId, CancellationToken ct = default)
    {
        var stage = await _db.PipelineStages
            .FirstOrDefaultAsync(s => s.Id == stageId && s.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException("Stage not found");

        // Deals hold the key, and the FK would refuse anyway — but a clear
        // message beats a constraint violation surfacing as "please try
        // again".
        var dealsHere = await _db.Deals.CountAsync(
            d => d.TenantId == tenantId && d.Stage == stage.Key && !d.IsDeleted, ct);

        if (dealsHere > 0)
            throw new InvalidOperationException(
                $"{dealsHere} deal(s) are in this stage. Move them first, or retire the stage instead of deleting it.");

        // History keeps the key as text, so old rows still read correctly
        // after the stage is gone.
        var everUsed = await _db.DealStageHistory.AnyAsync(
            h => h.TenantId == tenantId && (h.ToStage == stage.Key || h.FromStage == stage.Key), ct);

        if (everUsed)
            throw new InvalidOperationException(
                "Deals have passed through this stage before. Retire it instead, so the history still makes sense.");

        if (stage.IsDefault)
            throw new InvalidOperationException(
                "This is where new deals start. Make another stage the starting point first.");

        _db.PipelineStages.Remove(stage);
        await _db.SaveChangesAsync(ct);
    }
}
