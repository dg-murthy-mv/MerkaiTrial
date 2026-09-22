// =====================================================================
// StageTransitionGuard.cs
// Location: MerkaiTrial.Application/Commands/PipelineStages/StageTransitionGuard.cs
//
// COMPLETE FILE — replaces the 019 version.
//
// WHAT CHANGED FROM 019
//   019 asked the TARGET STAGE what a deal needed. 020 asks the
//   TRANSITION, because the same stage is a different question depending
//   on where the deal came from: Negotiation → Closed Won should want an
//   accepted quote, while an admin putting a deal back into Closed Won
//   after a correction should not be asked for one again.
//
//   The two hard-coded reason cases are gone with it. 019 had "lost
//   reason" and "reopen reason" written into this file; a transition now
//   carries RequiresNote and its own prompt, so a tenant can ask "Which
//   site did you survey?" on any move without a line of code.
//
// WHAT DID NOT CHANGE
//   ApplyToDeal. The closing bookkeeping — set the close date and final
//   value on the way in, CLEAR them on the way out — is the same on every
//   path and was the 019 bug fix. It is untouched apart from the note.
//
// THE THREE WAYS A MOVE CAN BE ALLOWED
//   1. A transition exists, is switched on, and the deal passes it.
//   2. It is a SYSTEM move — a quote accepted, an invoice paid. No matrix,
//      no requirements: the event that caused it is the very thing a
//      requirement would ask about.
//   3. An ADMIN OVERRIDE. A workspace admin can make any move the stages
//      allow, whatever the process says. Without this, one badly pruned
//      matrix strands a deal with no way out. The override skips the
//      PROCESS; it never skips the money rule below.
//
// THE ONE RULE NOTHING SKIPS
//   A won deal with an issued, un-voided invoice cannot be reopened. Not
//   by an admin, not by an override. The invoice is a tax document that
//   has gone to the customer, and the only correct route is to void it —
//   which has its own reason and its own audit trail.
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
/// projection. Building one is three lines at the call site.
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

    /// <summary>Whatever the transition asked the person to type.</summary>
    string? Note = null,

    /// <summary>A quote accepted or an invoice paid, not a person clicking.</summary>
    bool IsSystemMove = false,

    /// <summary>
    /// An admin deliberately stepping outside the process. Refused for
    /// anyone who is not a workspace admin, and recorded on the history
    /// row so it is never invisible.
    /// </summary>
    bool AdminOverride = false);

/// <summary>
/// An allowed move, with everything the caller needs to carry it out.
/// There is no "allowed = false" case: a refusal is an exception carrying
/// the reason, because the reason is the useful part.
/// </summary>
public sealed record StageMoveDecision(
    PipelineStage? From,
    PipelineStage To,
    TenantStages Stages,
    ProcessTransition? Transition,
    bool IsReopen,
    bool IsClosing,
    bool WasOverride,
    string? Note)
{
    /// <summary>
    /// What goes on the stage history row. An override says so, because
    /// "who moved this deal outside the process, and why" is the first
    /// question anyone asks when the numbers look wrong.
    /// </summary>
    public string? HistoryNote
    {
        get
        {
            var body = string.IsNullOrWhiteSpace(Note) ? null : Note!.Trim();

            if (!WasOverride) return body;

            return body is null
                ? "Stage changed by an admin, outside the configured process."
                : $"[admin override] {body}";
        }
    }

    /// <summary>The button the person actually pressed, for the audit entry.</summary>
    public string MoveName => Transition?.Label ?? $"Move to {To.Name}";
}

// ── The guard ─────────────────────────────────────────────────────────

/// <summary>
/// Registered by the Scrutor scan through ICommandHandler and injected by
/// concrete type. Nothing to add to Program.cs.
/// </summary>
public class StageTransitionGuard : ICommandHandler
{
    private const int MaxNoteLength = 1000;

    private readonly FlowDbContext _db;
    private readonly IStageResolver _stages;
    private readonly TransitionCatalog _transitions;
    private readonly ICurrentUserService _currentUser;
    private readonly QuoteApprovalEngine _approvals;

    public StageTransitionGuard(
        FlowDbContext db,
        IStageResolver stages,
        TransitionCatalog transitions,
        ICurrentUserService currentUser,
        QuoteApprovalEngine approvals)
    {
        _db = db;
        _stages = stages;
        _transitions = transitions;
        _currentUser = currentUser;
        _approvals = approvals;
    }

    // =================================================================
    // SETTINGS
    // =================================================================

    /// <summary>
    /// The tenant's row, or the defaults when they have never saved one.
    ///
    /// After 020 this carries one live rule — the invoice block. The other
    /// three moved into the matrix: ForwardOnly is expressed by which
    /// transitions exist, and the two reopen rules by the Actor and
    /// RequiresNote on the transitions out of a closed stage.
    /// </summary>
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
    /// May this move happen? Returns the resolved move when it may, and
    /// throws otherwise:
    ///
    ///   InvalidOperationException   a rule says no — the message is
    ///                               written for the rep, not the log
    ///   UnauthorizedAccessException they are not allowed to make it
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
        // A deal that was sold and is now being recorded as lost is exactly
        // the move that deserves a second pair of eyes.
        var isReopen = from is not null && from.IsTerminal;
        var isClosing = to.IsTerminal;
        var note = Trim(req.Note);

        // ── The money rule. Checked FIRST and for everyone. ───────────
        // Before the process, before permissions, before the override:
        // there is no route to reopening an invoiced deal, so being told
        // that immediately beats being asked for a note first.
        if (isReopen && !req.IsSystemMove)
            await CheckNotInvoicedAsync(req, from!, ct);

        if (req.IsSystemMove)
            return new StageMoveDecision(from, to, stages, null, isReopen, isClosing, false, note);

        var catalog = await _transitions.GetAsync(req.TenantId, ct);
        var transition = catalog.Find(req.Deal.CurrentStageKey, req.ToStageKey);

        // ── The admin override ────────────────────────────────────────
        if (req.AdminOverride)
        {
            var me = await _currentUser.GetCurrentUserAsync();
            if (!me.IsTenantAdmin)
                throw new UnauthorizedAccessException(
                    "Only a workspace admin can move a deal outside the configured process.");

            return new StageMoveDecision(from, to, stages, transition, isReopen, isClosing, true, note);
        }

        // ── The process ───────────────────────────────────────────────
        // An empty matrix means a workspace that was never configured, not
        // one where everything is forbidden. Freezing every deal because a
        // table is empty would be the worst possible failure mode.
        if (!catalog.IsEmpty)
        {
            if (transition is null || !transition.IsActive)
                throw new InvalidOperationException(
                    DescribeRefusal(stages, catalog, from, to, transition));

            await CheckActorAsync(req, transition, from, to, ct);
            await CheckRequirementsAsync(req, transition, to, note, ct);
        }

        return new StageMoveDecision(from, to, stages, transition, isReopen, isClosing, false, note);
    }

    /// <summary>
    /// A refusal that tells the rep where they CAN go. "No" on its own
    /// sends them to find an admin; a list sends them back to work.
    /// </summary>
    private static string DescribeRefusal(
        TenantStages stages, TenantTransitions catalog,
        PipelineStage? from, PipelineStage to, ProcessTransition? switchedOff)
    {
        var fromName = from?.Name ?? "this stage";

        var open = catalog.From(from?.Key)
            .Select(t => t.Label)
            .Take(5)
            .ToList();

        var head = switchedOff is not null
            ? $"Moving from {fromName} to \"{to.Name}\" is not part of this workspace's sales process."
            : $"There is no step from {fromName} to \"{to.Name}\" in this workspace's sales process.";

        if (open.Count == 0)
            return head + " A workspace admin can add it under Settings → Pipeline Rules.";

        return head + $" From here you can: {string.Join(", ", open)}.";
    }

    // ── who may make this move ────────────────────────────────────────

    private async Task CheckActorAsync(
        StageMoveRequest req, ProcessTransition transition,
        PipelineStage? from, PipelineStage to, CancellationToken ct)
    {
        if (transition.Actor == TransitionActor.Anyone) return;

        var me = await _currentUser.GetCurrentUserAsync();
        if (me.IsTenantAdmin) return;          // admins are at the top of every ladder

        if (transition.Actor == TransitionActor.Admins)
            throw new UnauthorizedAccessException(
                $"\"{transition.Label}\" is limited to workspace admins.");

        var ownsIt = !string.IsNullOrEmpty(req.Deal.OwnerUserId)
                     && string.Equals(req.Deal.OwnerUserId, me.UserId.ToString(),
                                      StringComparison.OrdinalIgnoreCase);

        if (transition.Actor == TransitionActor.DealOwner && ownsIt) return;

        // Managers of the deal owner's team — the same people who sign off
        // an over-limit quote. Reusing that lookup means a change to "who
        // is senior here" lands in both places at once.
        var approvers = await _approvals.ApproversForDealAsync(
            req.TenantId, req.Deal.Id,
            excludeUserId: null,
            teamFallbackUserId: me.UserId, ct);

        if (approvers.Any(a => a.UserId == me.UserId)) return;

        throw new UnauthorizedAccessException(transition.Actor switch
        {
            TransitionActor.DealOwner =>
                $"\"{transition.Label}\" is for the deal's owner, their manager, or a workspace admin.",
            _ =>
                $"\"{transition.Label}\" is limited to managers of the deal owner's team and workspace admins."
        });
    }

    // ── what the deal must already have ───────────────────────────────

    private async Task CheckRequirementsAsync(
        StageMoveRequest req, ProcessTransition transition,
        PipelineStage to, string? note, CancellationToken ct)
    {
        if (!transition.HasRequirements) return;

        // The note is asked for on its own, in its own words. Folding it
        // into "needs a value, a close date and a note" would throw away
        // the prompt, which is the part that gets a useful answer.
        if (transition.RequiresNote && string.IsNullOrWhiteSpace(note))
            throw new InvalidOperationException(
                $"\"{transition.Label}\" needs a note: {transition.EffectiveNotePrompt}");

        var missing = new List<string>();

        if (transition.RequiresValue && req.Deal.ExpectedValue <= 0)
            missing.Add("a value above zero");

        if (transition.RequiresCloseDate && !HasCloseDate(req.Deal.ExpectedCloseDateUtc))
            missing.Add("an expected close date");

        if (transition.RequiresAttachment)
        {
            var hasFile = await _db.Attachments.AsNoTracking()
                .AnyAsync(a => a.TenantId == req.TenantId
                            && a.EntityId == req.Deal.Id
                            && !a.IsDeleted, ct);

            if (!hasFile)
                missing.Add("at least one attachment on the deal");
        }

        if (transition.RequiresAcceptedQuote || transition.RequiresQuote)
        {
            var statuses = await _db.Quotes.AsNoTracking()
                .Where(q => q.TenantId == req.TenantId
                         && q.DealId == req.Deal.Id
                         && !q.IsDeleted)
                .Select(q => q.Status)
                .ToListAsync(ct);

            // RequiresAcceptedQuote implies RequiresQuote — saying both
            // ("a quote, and an accepted quote") reads like a mistake.
            if (transition.RequiresAcceptedQuote)
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
            $"This deal isn't ready for \"{transition.Label}\" yet — it needs {JoinReadably(missing)}.");
    }

    // ── the one rule nothing skips ────────────────────────────────────

    private async Task CheckNotInvoicedAsync(
        StageMoveRequest req, PipelineStage from, CancellationToken ct)
    {
        var (settings, _) = await GetSettingsAsync(req.TenantId, ct);

        if (!settings.BlockReopenWithIssuedInvoice) return;
        if (from.Category != StageCategory.Won) return;

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

        if (blocking.Count == 0) return;

        var shown = string.Join(", ", blocking.Take(3));
        var andMore = blocking.Count > 3 ? " and others" : "";

        throw new InvalidOperationException(
            $"This deal has already been invoiced ({shown}{andMore}), so it can't be reopened — " +
            "not even by an admin. Void the invoice first: an issued invoice is a tax document " +
            "and has to be cancelled properly rather than left behind a reopened deal.");
    }

    /// <summary>
    /// Deal.ExpectedCloseDateUtc is non-nullable, so "not set" shows up as
    /// default(DateTime) rather than null. Both count as missing.
    /// </summary>
    private static bool HasCloseDate(DateTime? value)
        => value.HasValue && value.Value != default;

    // =================================================================
    // APPLY — the bookkeeping, identical on every path
    // =================================================================

    /// <summary>
    /// Moves the deal and puts its closing fields into the state the new
    /// stage implies. Does NOT save — the caller owns the transaction and
    /// usually has other changes to write in the same SaveChanges.
    /// </summary>
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

            // A lost stage keeps the note as the reason. Won does not — a
            // deal that was lost and is now won has no lost reason.
            deal.LostReason = decision.To.Category == StageCategory.Lost
                ? decision.Note
                : null;
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
    /// The history row for a move. Built here so all paths write the same
    /// shape — TenantId included, which one of them used to forget,
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
        return t.Length > MaxNoteLength ? t[..MaxNoteLength] : t;
    }

    /// <summary>"a, b and c" — the message is read by a salesperson.</summary>
    private static string JoinReadably(IReadOnlyList<string> parts) => parts.Count switch
    {
        1 => parts[0],
        2 => $"{parts[0]} and {parts[1]}",
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1]
    };
}
