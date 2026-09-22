// =====================================================================
// TransitionCatalog.cs
// Location: MerkaiTrial.Application/Commands/PipelineStages/TransitionCatalog.cs
//
// NEW FILE (020).
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

/// <summary>
/// Builds the process most pipelines actually want, from the stages a
/// tenant already has. Behind "Apply suggested process" on the settings
/// page.
///
/// WHY THIS EXISTS
///   The migration seeds every stage to every other stage, which is
///   correct — it takes nothing away — but it means a deal page shows a
///   button for every other stage, which is a dropdown with extra steps.
///   Pruning 30 rows by hand before anyone has used the feature is a poor
///   first experience. This gets a tenant to a sensible process in one
///   click, which they then adjust.
///
/// THE SHAPE IT PROPOSES
///   • each open stage → the next open stage (the normal path)
///   • each open stage → the previous open stage (deals do go backwards)
///   • each open stage → every Won and Lost stage (you can close from
///     anywhere; a deal can die in Discovery)
///   • each closed stage → the tenant's starting stage only, as a reopen
///   • nothing else
/// </summary>
public static class SuggestedProcess
{
    /// <summary>
    /// The from→to pairs the suggestion switches ON. Everything else the
    /// tenant has is switched off rather than deleted, so a tenant who
    /// clicks this and changes their mind has only lost the toggles.
    /// </summary>
    public static HashSet<(string From, string To)> Build(IReadOnlyList<PipelineStage> stages)
    {
        var live = stages.Where(s => s.IsActive).OrderBy(s => s.SortOrder).ToList();

        var open = live.Where(s => s.Category == StageCategory.Open).ToList();
        var closed = live.Where(s => s.IsTerminal).ToList();

        var pairs = new HashSet<(string, string)>();

        for (var i = 0; i < open.Count; i++)
        {
            // Forward one, back one.
            if (i + 1 < open.Count) pairs.Add((open[i].Key, open[i + 1].Key));
            if (i - 1 >= 0)         pairs.Add((open[i].Key, open[i - 1].Key));

            // Closing is allowed from any open stage. A deal can be lost in
            // Discovery, and a firm that only ever loses deals at the end
            // is a firm that is not recording the truth.
            foreach (var c in closed)
                pairs.Add((open[i].Key, c.Key));
        }

        // Out of a closed stage: back to where deals start, and nowhere
        // else. Reopening into the middle of the pipeline is a judgement
        // call the tenant can add themselves; the safe default is one door.
        var start = live.FirstOrDefault(s => s.IsDefault && s.Category == StageCategory.Open)
                    ?? open.FirstOrDefault();

        if (start is not null)
            foreach (var c in closed)
                pairs.Add((c.Key, start.Key));

        return pairs;
    }
}
