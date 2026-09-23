// =====================================================================
// TransitionCatalog.cs
// Location: MerkaiTrial.Application/Commands/PipelineStages/TransitionCatalog.cs
//
// NEW FILE (020). Unchanged in 021 apart from the note at the end.
//
// WHAT IT IS
//   A tenant's process, loaded once and asked many questions — the same
//   shape as StageResolver / TenantStages, and for the same reason. The
//   deal page, the kanban and three handlers all need to know "what can
//   this deal do from here", and without one place to ask, each would
//   grow its own query and they would drift.
//
// WHY IT LOADS INACTIVE ROWS TOO
//   Same reasoning as StageResolver loading retired stages. A transition
//   the tenant has switched off must still be FINDABLE, so a stale button
//   posted from an open browser tab gets "that move is no longer part of
//   your process" rather than "that move does not exist" — which is what
//   a tenant would see if we could not tell the difference between
//   switched-off and never-configured.
//
// Registered by the Scrutor scan through ICommandHandler and injected by
// concrete type, so there is nothing to add to Program.cs.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.PipelineStages;

/// <summary>A tenant's transitions, loaded once and asked many questions.</summary>
public sealed class TenantTransitions
{
    private readonly List<ProcessTransition> _all;

    public TenantTransitions(List<ProcessTransition> all) => _all = all;

    public IReadOnlyList<ProcessTransition> All => _all;

    /// <summary>
    /// True when the tenant has no process at all. Treated as "everything
    /// is allowed" rather than "nothing is" — a workspace whose matrix was
    /// never seeded must not have every deal frozen.
    /// </summary>
    public bool IsEmpty => _all.Count == 0;

    /// <summary>
    /// The moves offered to someone looking at a deal in this stage —
    /// switched on, in button order.
    /// </summary>
    public IReadOnlyList<ProcessTransition> From(string? fromStageKey) =>
        string.IsNullOrEmpty(fromStageKey)
            ? Array.Empty<ProcessTransition>()
            : _all.Where(t => t.FromStageKey == fromStageKey && t.IsActive)
                  .OrderBy(t => t.SortOrder)
                  .ThenBy(t => t.Label)
                  .ToList();

    /// <summary>
    /// The configured move, switched on or not. Null means the tenant has
    /// never had a row for this pair at all.
    /// </summary>
    public ProcessTransition? Find(string? fromStageKey, string? toStageKey) =>
        string.IsNullOrEmpty(fromStageKey) || string.IsNullOrEmpty(toStageKey)
            ? null
            : _all.FirstOrDefault(t => t.FromStageKey == fromStageKey && t.ToStageKey == toStageKey);

    /// <summary>Every row out of a stage, including the switched-off ones — for the settings grid.</summary>
    public IReadOnlyList<ProcessTransition> AllFrom(string fromStageKey) =>
        _all.Where(t => t.FromStageKey == fromStageKey)
            .OrderBy(t => t.SortOrder)
            .ToList();

    /// <summary>
    /// Stages a deal here can reach. Used for the message when a move is
    /// refused — "you can go to X, Y or Z from here" beats "no".
    /// </summary>
    public IReadOnlyList<string> DestinationsFrom(string? fromStageKey) =>
        From(fromStageKey).Select(t => t.ToStageKey).ToList();

    /// <summary>
    /// Stages with no way out. A deal in one is stuck unless an admin
    /// overrides, so the settings page warns about them and this is how it
    /// finds them.
    /// </summary>
    public IReadOnlyList<string> DeadEndsAmong(IEnumerable<string> stageKeys) =>
        stageKeys.Where(k => From(k).Count == 0).ToList();
}

public interface ITransitionCatalog
{
    Task<TenantTransitions> GetAsync(Guid tenantId, CancellationToken ct = default);
}

public class TransitionCatalog : ITransitionCatalog, ICommandHandler
{
    private readonly FlowDbContext _db;

    public TransitionCatalog(FlowDbContext db) => _db = db;

    public async Task<TenantTransitions> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        // IsActive is deliberately NOT filtered here — see the header. The
        // callers that want only live moves ask From(); the ones that need
        // to tell "switched off" from "never configured" ask Find().
        var rows = await _db.ProcessTransitions.AsNoTracking()
            .Where(t => t.TenantId == tenantId)
            .OrderBy(t => t.SortOrder)
            .ToListAsync(ct);

        return new TenantTransitions(rows);
    }
}

/* SuggestedProcess moved to ProcessTemplates.cs in 021.
   One "suggested" shape became three named ones — Flexible, Step by step
   and Quote-driven — and they are applied in the browser as a preview
   rather than saved on click, so looking at one costs nothing. */
