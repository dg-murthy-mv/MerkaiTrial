// =====================================================================
// StageResolver.cs
// Location: MerkaiTrial.Application/Commands/PipelineStages/StageResolver.cs
//
// NEW FILE.
//
// WHY THIS EXISTS
//   Eleven places in DealsCommandHandlers alone asked DealStages whether
//   a stage was valid, terminal, or what probability it carried. All of
//   those answers now come from the tenant's own PipelineStages, and
//   without a shared helper every handler would grow the same six lines
//   of lookup — and drift apart the first time one of them was edited.
//
// THE RULE IT ENFORCES
//   Code never asks "is this stage called ClosedWon". It asks "is this
//   stage's CATEGORY Won". A tenant who names their winning stage
//   "Contract Signed" must still have their deal treated as closed, and
//   their revenue counted.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.PipelineStages;

/// <summary>
/// A tenant's stages, loaded once and asked many questions.
/// </summary>
public sealed class TenantStages
{
    private readonly List<PipelineStage> _stages;

    public TenantStages(List<PipelineStage> stages) => _stages = stages;

    public IReadOnlyList<PipelineStage> All => _stages;

    public IReadOnlyList<PipelineStage> Active =>
        _stages.Where(s => s.IsActive).OrderBy(s => s.SortOrder).ToList();

    public PipelineStage? Find(string? key) =>
        key is null ? null : _stages.FirstOrDefault(s => s.Key == key);

    public bool IsValid(string? key) => Find(key) is not null;

    /// <summary>Won or Lost — the deal is over, whatever it is called.</summary>
    public bool IsTerminal(string? key) =>
        Find(key)?.Category is StageCategory.Won or StageCategory.Lost;

    public bool IsWon(string? key)  => Find(key)?.Category == StageCategory.Won;

    /// <summary>Lost stages are the ones that should require a reason.</summary>
    public bool IsLost(string? key) => Find(key)?.Category == StageCategory.Lost;

    public int ProbabilityOf(string? key) => Find(key)?.Probability ?? 0;

    /// <summary>The stage's display name, falling back to the key when a
    /// stage has since been deleted.</summary>
    public string NameOf(string? key) =>
        string.IsNullOrEmpty(key) ? "—" : (Find(key)?.Name ?? key);

    /// <summary>Where new deals start.</summary>
    public PipelineStage? Default =>
        _stages.FirstOrDefault(s => s.IsDefault && s.IsActive)
        ?? Active.FirstOrDefault();

    /// <summary>
    /// The stage to use for a requested key: the one asked for if it
    /// exists and is usable, otherwise the tenant's starting stage.
    /// Returns null only when the tenant has no stages at all, which the
    /// caller should treat as a configuration error rather than guess at.
    /// </summary>
    public PipelineStage? ResolveOrDefault(string? requestedKey)
    {
        var requested = Find(requestedKey);
        if (requested is { IsActive: true }) return requested;

        // An EXISTING but retired stage is still honoured when explicitly
        // asked for — a deal being moved back into one is a real case.
        if (requested is not null) return requested;

        return Default;
    }

    /// <summary>For error messages. Active stages only — offering a
    /// retired one as a valid choice would be misleading.</summary>
    public string ValidKeysText => string.Join(", ", Active.Select(s => s.Key));
}

public interface IStageResolver
{
    Task<TenantStages> GetAsync(Guid tenantId, CancellationToken ct = default);
}

public class StageResolver : IStageResolver
{
    private readonly FlowDbContext _db;

    public StageResolver(FlowDbContext db) => _db = db;

    public async Task<TenantStages> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        // activeOnly is deliberately NOT applied: a deal can sit in a
        // retired stage, and a terminal check on one must still say
        // "closed" rather than "unknown".
        var stages = await _db.PipelineStages.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.SortOrder)
            .ToListAsync(ct);

        return new TenantStages(stages);
    }
}
