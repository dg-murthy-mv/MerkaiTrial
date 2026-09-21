// =====================================================================
// StageTransitionGuard.cs
// Location: MerkaiTrial.Application/Commands/PipelineStages/StageTransitionGuard.cs
//
// NEW FILE (019).
//
// WHY THIS EXISTS
//   Three different code paths move a deal between stages:
//
//     UpdateDealHandler        the Edit page saving the whole deal
//     UpdateDealStageHandler   a card dragged on the kanban board
//     TransitionDealStageHandler  a quote being accepted, an invoice paid
//
//   Before this round they agreed about almost nothing. The Edit page
//   saved a lost reason if one arrived but never insisted on it; the
//   kanban had no lost reason at all, so dragging a card to Lost closed
//   the deal with no explanation; and both let anyone drag a closed,
//   invoiced deal straight back into Negotiation.
//
//   This is the same problem StageResolver solved for "what is a stage",
//   and it gets the same answer: one place that knows, and three callers
//   that ask. Without it the next person to fix a rule fixes it in one
//   handler and the other two keep the bug.
//
// THE TWO HALVES
//   CheckAsync   may this move happen at all? Throws with a message the
//                rep can act on. Reads only.
//   ApplyToDeal  the bookkeeping every move needs, identically: the new
//                stage and probability, the close date and final value,
//                and — the part that was wrong everywhere — CLEARING
//                those again when a deal is reopened.
//
// THE REOPEN BUG THIS FIXES
//   Every handler used `deal.ActualCloseDateUtc ??= DateTime.UtcNow`.
//   Reopening a deal left the old close date in place, so closing it
//   again months later kept the ORIGINAL date and the deal landed in the
//   wrong period on every revenue report. ActualValue had the same fault.
//   A reopened deal is not closed, so it now has no close date, no final
//   value and no lost reason until it closes again.
//
// SYSTEM MOVES
//   A move triggered by the system — a quote accepted, an invoice fully
//   paid — skips the permission and requirement checks. The event that
//   caused it is the very thing a requirement would ask about, and a
//   manual invoice has no quote to point at. It still goes through
//   ApplyToDeal, so the bookkeeping is the same as a human's.
// =====================================================================

using MerkaiTrial.Application.Commands.Quotes;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.PipelineStages;

// ── What the guard needs to know about the deal ───────────────────────

/// <summary>
/// The handful of deal fields the rules ask about.
///
/// Deliberately NOT the Deal entity: the guard has to work the same
/// whether the caller has a tracked entity, a DTO off the wire, or a
/// projection. Building one of these is three lines at the call site and
/// keeps the rules readable.
/// </summary>
public sealed record DealStageSnapshot(
    Guid Id,
    string? CurrentStageKey,
    decimal ExpectedValue,
    DateTime? ExpectedCloseDateUtc,
    string? OwnerUserId);

/// <summary>What the caller is asking for.</summary>
public sealed record StageMoveRequest(
    Guid TenantId,
    DealStageSnapshot Deal,
    string ToStageKey,
    string? LostReason = null,
    string? ReopenReason = null,
    /// <summary>A quote accepted or an invoice paid, not a person clicking.</summary>
    bool IsSystemMove = false);

/// <summary>
/// An allowed move, with everything the caller needs to carry it out.
/// Returned only when every rule passed — there is no "allowed = false"
/// case, because a refusal is an exception carrying the reason.
/// </summary>
public sealed record StageMoveDecision(
    PipelineStage? From,
    PipelineStage To,
    TenantStages Stages,
    bool IsReopen,
    bool IsClosing,
    string? LostReason,
    string? ReopenReason)
{
    /// <summary>
    /// What goes on the stage history row. The reopen reason and the lost
    /// reason never apply to the same move — you cannot be leaving a
    /// closed stage and refusing to say why you lost it at once — so one
    /// Note column carries both.
    /// </summary>
    public string? HistoryNote => IsReopen
        ? (string.IsNullOrWhiteSpace(ReopenReason) ? null : "Reopened: " + ReopenReason!.Trim())
        : (string.IsNullOrWhiteSpace(LostReason) ? null : LostReason!.Trim());
}

// ── The guard ─────────────────────────────────────────────────────────

/// <summary>
/// Registered by the Scrutor scan through ICommandHandler, like every
/// other handler. Nothing to add to Program.cs.
/// </summary>
public class StageTransitionGuard : ICommandHandler
{
    private const int MaxReasonLength = 1000;

    private readonly FlowDbContext _db;
    private readonly IStageResolver _stages;
    private readonly ICurrentUserService _currentUser;
    private readonly QuoteApprovalEngine _approvals;

    public StageTransitionGuard(
        FlowDbContext db,
        IStageResolver stages,
        ICurrentUserService currentUser,
        QuoteApprovalEngine approvals)
    {
        _db = db;
        _stages = stages;
        _currentUser = currentUser;
        _approvals = approvals;
    }

    // =================================================================
    // SETTINGS
    // =================================================================

    /// <summary>The tenant's row, or the defaults when they have never saved one.</summary>
    public async Task<(PipelineRuleSettings Settings, bool IsDefault)> GetSettingsAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        var row = await _db.PipelineRuleSettings.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId, ct);

        return row is not null
            ? (row, false)
            : (PipelineRuleDefaults.For(tenantId), true);
    }

    // =================================================================
    // CHECK
    // =================================================================

    /// <summary>
    /// May this move happen? Returns the resolved stages when it may,
    /// and throws otherwise:
    ///
    ///   InvalidOperationException   a rule says no — the message is
    ///                               written for the rep, not the log
    ///   UnauthorizedAccessException they are not allowed to reopen
    ///
    /// The caller is expected to have already checked that the deal is
    /// visible to this user and that the move is not a no-op.
    /// </summary>
    public async Task<StageMoveDecision> CheckAsync(
        StageMoveRequest req, CancellationToken ct = default)
    {
        var stages = await _stages.GetAsync(req.TenantId, ct);

        var to = stages.Find(req.ToStageKey)
            ?? throw new InvalidOperationException(
                $"'{req.ToStageKey}' is not a stage in this workspace. Valid: {stages.ValidKeysText}");

        var from = stages.Find(req.Deal.CurrentStageKey);

        // Leaving a Won or Lost stage is a reopen — including Won to Lost.
        // A deal that has been sold and is now being recorded as lost is
        // exactly the move that deserves a manager's eyes on it.
        var isReopen = from is not null && from.IsTerminal;
        var isClosing = to.IsTerminal;

        var lostReason = Trim(req.LostReason);
        var reopenReason = Trim(req.ReopenReason);

        if (!req.IsSystemMove)
        {
            var (settings, _) = await GetSettingsAsync(req.TenantId, ct);

            if (isReopen)
                await CheckReopenAllowedAsync(req, settings, from!, to, reopenReason, ct);
            else
                CheckDirection(settings, from, to);

            await CheckEntryRequirementsAsync(req, to, lostReason, ct);
        }

        return new StageMoveDecision(
            from, to, stages, isReopen, isClosing, lostReason, reopenReason);
    }

    // ── forward-only ──────────────────────────────────────────────────

    private static void CheckDirection(
        PipelineRuleSettings settings, PipelineStage? from, PipelineStage to)
    {
        if (!settings.ForwardOnly || from is null) return;

        // Only open-to-open moves have a direction. Closing a deal is
        // never "backwards", whatever SortOrder the Lost stage happens to
        // have been dragged to on the settings page.
        if (from.IsTerminal || to.IsTerminal) return;

        if (to.SortOrder < from.SortOrder)
            throw new InvalidOperationException(
                $"This workspace only moves deals forward, so \"{from.Name}\" can't go back to \"{to.Name}\". " +
                "A workspace admin can change that under Settings → Pipeline Rules.");
    }

    // ── reopening a closed deal ───────────────────────────────────────

    private async Task CheckReopenAllowedAsync(
        StageMoveRequest req,
        PipelineRuleSettings settings,
        PipelineStage from,
        PipelineStage to,
        string? reopenReason,
        CancellationToken ct)
    {
        // The hard one first: a won deal that has already been invoiced.
        // Checked before the reason and the permission so a rep is told
        // the real obstacle rather than being asked to write a reason for
        // something that was never going to be allowed.
        if (settings.BlockReopenWithIssuedInvoice && from.Category == StageCategory.Won)
        {
            var blocking = await _db.Invoices.AsNoTracking()
                .Where(i => i.TenantId == req.TenantId
                         && i.DealId == req.Deal.Id
                         && !i.IsDeleted
                         && i.IssuedAtUtc != null      // it left Draft
                         && i.VoidedAtUtc == null)     // and was not voided
                .OrderBy(i => i.Number)
                .Select(i => i.Number)
                .Take(4)
                .ToListAsync(ct);

            if (blocking.Count > 0)
            {
                var shown = string.Join(", ", blocking.Take(3));
                var andMore = blocking.Count > 3 ? " and others" : "";

                throw new InvalidOperationException(
                    $"This deal has already been invoiced ({shown}{andMore}), so it can't be reopened. " +
                    "Void the invoice first — an issued invoice is a tax document and has to be " +
                    "cancelled properly rather than left behind a reopened deal.");
            }
        }

        if (settings.ReopenRestrictedToManagers)
        {
            var me = await _currentUser.GetCurrentUserAsync();

            if (!me.IsTenantAdmin)
            {
                // The same people who sign off an over-limit quote:
                // managers of the deal owner's team, plus admins. Reusing
                // it rather than writing a second copy means a change to
                // "who is senior here" lands in both places at once.
                var approvers = await _approvals.ApproversForDealAsync(
                    req.TenantId,
                    req.Deal.Id,
                    excludeUserId: null,              // reopening your own deal is fine
                    teamFallbackUserId: me.UserId,
                    ct);

                if (!approvers.Any(a => a.UserId == me.UserId))
                    throw new UnauthorizedAccessException(
                        $"\"{from.Name}\" is a closed stage. Only a manager of the deal owner's team, " +
                        "or a workspace admin, can reopen a closed deal.");
            }
        }

        if (settings.ReopenRequiresReason && string.IsNullOrWhiteSpace(reopenReason))
            throw new InvalidOperationException(
                $"Reopening a deal out of \"{from.Name}\" needs a reason. It is kept on the deal's " +
                "stage history, so the change can be explained later.");
    }

    // ── what the target stage asks of the deal ────────────────────────

    private async Task CheckEntryRequirementsAsync(
        StageMoveRequest req, PipelineStage to, string? lostReason, CancellationToken ct)
    {
        if (!to.HasEntryRequirements) return;

        var missing = new List<string>();

        if (to.RequiresValue && req.Deal.ExpectedValue <= 0)
            missing.Add("a value above zero");

        if (to.RequiresCloseDate && !HasCloseDate(req.Deal.ExpectedCloseDateUtc))
            missing.Add("an expected close date");

        // Only meaningful on a Lost stage. A tenant who ticks it on an
        // open stage by accident should not have every deal blocked.
        if (to.RequiresLostReason && to.Category == StageCategory.Lost
            && string.IsNullOrWhiteSpace(lostReason))
            missing.Add("a reason for losing it");

        if (to.RequiresAcceptedQuote || to.RequiresQuote)
        {
            var statuses = await _db.Quotes.AsNoTracking()
                .Where(q => q.TenantId == req.TenantId
                         && q.DealId == req.Deal.Id
                         && !q.IsDeleted)
                .Select(q => q.Status)
                .ToListAsync(ct);

            // RequiresAcceptedQuote implies RequiresQuote — saying both
            // ("a quote, and an accepted quote") reads like a mistake.
            if (to.RequiresAcceptedQuote)
            {
                if (!statuses.Contains(QuoteStatus.Accepted))
                    missing.Add(statuses.Count == 0
                        ? "an accepted quote (this deal has no quote yet)"
                        : "an accepted quote (the quote on this deal hasn't been accepted)");
            }
            else if (statuses.Count == 0)
            {
                missing.Add("a quote");
            }
        }

        if (missing.Count == 0) return;

        throw new InvalidOperationException(
            $"This deal isn't ready for \"{to.Name}\" yet — it needs {JoinReadably(missing)}.");
    }

    /// <summary>
    /// Deal.ExpectedCloseDateUtc is non-nullable, so "not set" shows up as
    /// default(DateTime) rather than null. Both are treated as missing.
    /// </summary>
    private static bool HasCloseDate(DateTime? value)
        => value.HasValue && value.Value != default;

    // =================================================================
    // APPLY — the bookkeeping, identical on every path
    // =================================================================

    /// <summary>
    /// Moves the deal, and puts its closing fields into the state the new
    /// stage implies. Does NOT save — the caller owns the transaction, and
    /// usually has other changes to write in the same SaveChanges.
    /// </summary>
    /// <param name="actualValue">
    /// What the deal was actually worth, when the caller has a figure from
    /// the user. Ignored unless the deal is closing.
    /// </param>
    /// <param name="actualCloseDateUtc">
    /// When it actually closed, if the user gave a date. Ignored unless
    /// the deal is closing; defaults to now.
    /// </param>
    public static void ApplyToDeal(
        Deal deal,
        StageMoveDecision decision,
        string changedBy,
        decimal? actualValue = null,
        DateTime? actualCloseDateUtc = null,
        int? probabilityOverride = null)
    {
        var now = DateTime.UtcNow;

        deal.Stage = decision.To.Key;

        // The tenant's own figure for the stage, unless the caller was
        // given one explicitly (the Edit page's slider).
        deal.Probability = probabilityOverride is > 0
            ? probabilityOverride.Value
            : decision.To.Probability;

        if (decision.IsClosing)
        {
            // Set on EVERY close, not only the first. A deal that was
            // closed, reopened and closed again has to carry the date it
            // actually closed — the old `??=` kept the date of the close
            // that was undone, and every revenue report believed it.
            deal.ActualCloseDateUtc = actualCloseDateUtc ?? now;
            deal.ActualValue = actualValue ?? deal.ActualValue ?? deal.ExpectedValue;

            deal.LostReason = decision.To.Category == StageCategory.Lost
                ? decision.LostReason
                : null;   // a deal that was lost and is now won has no lost reason
        }
        else
        {
            // Reopened, or simply moved between open stages. Either way it
            // is not closed, so it has no closing figures. Leaving them
            // behind is what made a reopened deal still count as revenue.
            deal.ActualCloseDateUtc = null;
            deal.ActualValue = null;
            deal.LostReason = null;
        }

        deal.UpdatedAtUtc = now;
        deal.UpdatedBy = changedBy;
    }

    /// <summary>
    /// The history row for a move. Built here so all three paths write the
    /// same shape — TenantId included, which one of them used to forget,
    /// leaving rows invisible behind the global query filter.
    /// </summary>
    public static DealStageHistory HistoryFor(
        Guid tenantId, Guid dealId, StageMoveDecision decision, string changedBy)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DealId = dealId,
            FromStage = decision.From?.Key,
            ToStage = decision.To.Key,
            ChangedAtUtc = DateTime.UtcNow,
            ChangedBy = changedBy,
            Note = decision.HistoryNote
        };

    // =================================================================
    // small helpers
    // =================================================================

    private static string? Trim(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        return t.Length > MaxReasonLength ? t[..MaxReasonLength] : t;
    }

    /// <summary>"a, b and c" — the message is read by a salesperson.</summary>
    private static string JoinReadably(IReadOnlyList<string> parts) => parts.Count switch
    {
        1 => parts[0],
        2 => $"{parts[0]} and {parts[1]}",
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1]
    };
}
