// =====================================================================
// PipelineRuleHandlers.cs
// Location: MerkaiTrial.Application/Commands/PipelineStages/PipelineRuleHandlers.cs
//
// COMPLETE FILE — replaces the 022 version.
//
// CHANGES (042)
//   ★ THE THREE QUOTE-DRIVEN MOVES ARE NOW SETTABLE. 036 replaced the
//     hardcoded "Proposal" / "Negotiation" / "ClosedLost" literals in
//     QuotesCommandHandler with QuoteSentStageKey, QuoteAcceptedStageKey
//     and QuoteRejectedStageKey on PipelineRuleSettings — and there it
//     stopped. Nothing in the product could write them. A tenant whose
//     migration had no matching stage got null (no move), correctly, and
//     then had no way to say what they actually wanted. A setting only we
//     can change with a SQL statement is not a setting.
//
//     They read and write with the rest of the process now, in the same
//     call and the same transaction as the matrix:
//       • PipelineRulesDto carries them out.
//       • SaveAllPipelineRulesDto carries them back.
//       • ResolveQuoteStage decides what is storable — see the long note
//         on that method for why an unchanged key is not re-validated and
//         why the category checks matter.
//     The three keys go into the audit payload too: they move deals with
//     nobody pressing anything, which is exactly the kind of change that
//     should be answerable six months later.
//
// CHANGES (022)
//   ✅ Transitions carry their ACTIONS — what happens after a deal takes
//      that step. Read with the process, saved with it.
//   ✅ Assignees travel with the process too, for the "a specific person"
//      option. One call rather than two on a page that already makes
//      several.
//
// WHAT CHANGED
//   019 read and wrote five requirement flags per STAGE. Those are gone
//   from here: requirements now live on the transition, and this file
//   reads and writes the matrix instead.
//
//   PipelineRuleSettings survives with ONE live rule — the invoice block.
//   ForwardOnly is now expressed by which transitions exist, and the two
//   reopen rules by the Actor and RequiresNote on transitions out of a
//   closed stage. All three are still stored; nothing reads them.
//
// WHAT IS NEW
//   GetAvailableTransitionsHandler — what THIS deal can do right now, and
//   for the ones it cannot, why. That last part is the difference between
//   a deal page that guides someone and one that just says no.
//
// All handlers are ICommandHandler, so the Scrutor scan registers them.
// Nothing to add to Program.cs.
// =====================================================================

using MerkaiTrial.Application.Commands.Quotes;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.PipelineStages;

// ── DTOs ──────────────────────────────────────────────────────────────

/// <summary>A stage, as the settings grid needs it.</summary>
public record StageLiteDto(
    Guid Id,
    string Key,
    string Name,
    int SortOrder,
    StageCategory Category,
    bool IsActive,
    bool IsDefault,
    /// <summary>
    /// 042. The stage's own forecast probability. Added because the three
    /// quote-driven moves in this round decide what the forecast says as
    /// well as where the card sits — "a raised quote moves the deal to
    /// Proposal" also means "and sets it to 40%", which is not something a
    /// stage NAME tells you. Trailing with a default, so nothing that
    /// builds a StageLiteDto elsewhere needs changing.
    /// </summary>
    int Probability = 0);

/// <summary>Something that happens after a step is taken.</summary>
public record TransitionActionDto(
    Guid Id,
    TransitionActionKind Kind,
    int SortOrder,
    bool IsActive,
    string Subject,
    string? Description,
    string ActivityType,
    TransitionAssignee AssignTo,
    Guid? AssignToUserId,
    int DueInDays);

public record SaveTransitionActionDto(
    TransitionActionKind Kind,
    bool IsActive,
    string Subject,
    string? Description,
    string ActivityType,
    TransitionAssignee AssignTo,
    Guid? AssignToUserId,
    int DueInDays);

/// <summary>One cell of the matrix.</summary>
public record TransitionDto(
    Guid Id,
    string FromStageKey,
    string ToStageKey,
    string Label,
    int SortOrder,
    bool IsActive,
    TransitionActor Actor,
    bool RequiresQuote,
    bool RequiresAcceptedQuote,
    bool RequiresValue,
    bool RequiresCloseDate,
    bool RequiresNote,
    string? NotePrompt,
    bool RequiresAttachment,
    List<TransitionActionDto> Actions);

/// <summary>Everything the process settings screen shows.</summary>
public record PipelineRulesDto(
    /// <summary>The one rule that is not a property of any transition.</summary>
    bool BlockReopenWithIssuedInvoice,
    /// <summary>
    /// 042. Where a deal goes when a quote is raised, accepted or rejected.
    /// Null means DO NOT MOVE THE DEAL, which is the default and the only
    /// safe answer for a pipeline nobody has described to us.
    ///
    /// 036 put these three on PipelineRuleSettings and taught the quote
    /// handler to use them, and that was where it stopped: there was no way
    /// to SET them except in SQL. A setting only we can change is not a
    /// setting, so they travel with the rest of the process from here.
    /// </summary>
    string? QuoteSentStageKey,
    string? QuoteAcceptedStageKey,
    string? QuoteRejectedStageKey,
    bool IsDefault,
    DateTime? UpdatedAtUtc,
    string? UpdatedBy,
    List<StageLiteDto> Stages,
    List<TransitionDto> Transitions,
    /// <summary>
    /// Active stages with no way out. A deal in one is stuck unless an
    /// admin overrides, so the page warns rather than letting it happen
    /// quietly.
    /// </summary>
    List<string> DeadEndStageKeys,
    /// <summary>
    /// Who a task can be given to, for the "a specific person" option.
    /// Carried here so the settings page makes one call, not two.
    /// </summary>
    List<AssigneeDto> Assignees);

public record SaveTransitionDto(
    string FromStageKey,
    string ToStageKey,
    string Label,
    bool IsActive,
    TransitionActor Actor,
    bool RequiresQuote,
    bool RequiresAcceptedQuote,
    bool RequiresValue,
    bool RequiresCloseDate,
    bool RequiresNote,
    string? NotePrompt,
    bool RequiresAttachment,
    List<SaveTransitionActionDto> Actions);

/// <summary>The whole screen, saved in one call.</summary>
public record SaveAllPipelineRulesDto(
    bool BlockReopenWithIssuedInvoice,
    /// <summary>
    /// 042. Null or empty means "don't move the deal". Unlike an owner id,
    /// these two do NOT need to be told apart: there is no third case here,
    /// because there is nothing to preserve — a stage key is either chosen
    /// or it is not. That is why the page needs no
    /// ConvertEmptyStringToNull annotation for these, and does need one for
    /// the owner dropdown.
    /// </summary>
    string? QuoteSentStageKey,
    string? QuoteAcceptedStageKey,
    string? QuoteRejectedStageKey,
    List<SaveTransitionDto> Transitions);

/// <summary>A button on the deal page.</summary>
public record AvailableTransitionDto(
    string ToStageKey,
    string ToStageName,
    string Label,
    StageCategory ToCategory,
    bool RequiresNote,
    string NotePrompt,
    /// <summary>False renders the button disabled rather than hiding it.</summary>
    bool IsAllowed,
    /// <summary>Why not. Null when IsAllowed.</summary>
    string? BlockedReason);

/// <summary>What the deal page needs to draw its transition bar.</summary>
public record DealTransitionsDto(
    Guid DealId,
    string CurrentStageKey,
    string CurrentStageName,
    StageCategory CurrentCategory,
    List<AvailableTransitionDto> Transitions,
    /// <summary>
    /// The viewer is a workspace admin, so the page offers the escape
    /// hatch out of a stage whose transitions are all blocked.
    /// </summary>
    bool CanOverride,
    /// <summary>
    /// Every stage, for the override picker. Only sent to admins.
    /// </summary>
    List<StageLiteDto> AllStages);

// ── READ: the settings screen ─────────────────────────────────────────

public class GetPipelineRulesHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly StageTransitionGuard _guard;
    private readonly TransitionCatalog _transitions;

    public GetPipelineRulesHandler(
        FlowDbContext db, StageTransitionGuard guard, TransitionCatalog transitions)
    {
        _db = db;
        _guard = guard;
        _transitions = transitions;
    }

    public async Task<PipelineRulesDto> Handle(Guid tenantId, CancellationToken ct = default)
    {
        var (settings, isDefault) = await _guard.GetSettingsAsync(tenantId, ct);

        // Retired stages are included. A deal can still sit in one, and the
        // grid has to show the way out.
        var stages = await _db.PipelineStages.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.SortOrder)
            .Select(s => new StageLiteDto(
                s.Id, s.Key, s.Name, s.SortOrder, s.Category, s.IsActive, s.IsDefault,
                s.Probability))                                   // 042
            .ToListAsync(ct);

        var catalog = await _transitions.GetAsync(tenantId, ct);

        // One query for every action in the workspace, grouped in memory.
        // A query per transition would be thirty round trips to draw one
        // settings page.
        var actionsByTransition = (await _db.TransitionActions.AsNoTracking()
                .Where(a => a.TenantId == tenantId)
                .OrderBy(a => a.SortOrder)
                .ToListAsync(ct))
            .GroupBy(a => a.TransitionId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var transitions = catalog.All
            .Select(t => new TransitionDto(
                t.Id, t.FromStageKey, t.ToStageKey, t.Label, t.SortOrder, t.IsActive, t.Actor,
                t.RequiresQuote, t.RequiresAcceptedQuote, t.RequiresValue, t.RequiresCloseDate,
                t.RequiresNote, t.NotePrompt, t.RequiresAttachment,
                actionsByTransition.GetValueOrDefault(t.Id, new List<TransitionAction>())
                    .Select(a => new TransitionActionDto(
                        a.Id, a.Kind, a.SortOrder, a.IsActive, a.Subject, a.Description,
                        a.ActivityType, a.AssignTo, a.AssignToUserId, a.DueInDays))
                    .ToList()))
            .ToList();

        // Active users only: a task assigned to someone who cannot sign in
        // is a task that is never done and never seen.
        var assignees = (await _db.Users.AsNoTracking()
                .Where(u => u.TenantId == tenantId && !u.IsDeleted && u.IsActive)
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email })
                .ToListAsync(ct))
            .Select(u => new AssigneeDto(
                u.Id.ToString(),
                string.IsNullOrWhiteSpace($"{u.FirstName}{u.LastName}")
                    ? u.Email
                    : $"{u.FirstName} {u.LastName}".Trim(),
                u.Email))
            .OrderBy(a => a.Name)
            .ToList();

        // Only ACTIVE stages can be dead ends worth warning about. A
        // retired stage with no way out matters too, but only if a deal is
        // parked there — the page works that out from the stage list.
        var deadEnds = catalog
            .DeadEndsAmong(stages.Where(s => s.IsActive).Select(s => s.Key))
            .ToList();

        return new PipelineRulesDto(
            settings.BlockReopenWithIssuedInvoice,
            settings.QuoteSentStageKey,          // 042
            settings.QuoteAcceptedStageKey,      // 042
            settings.QuoteRejectedStageKey,      // 042
            isDefault,
            isDefault ? null : settings.UpdatedAtUtc,
            isDefault ? null : settings.UpdatedBy,
            stages,
            transitions,
            deadEnds,
            assignees);
    }
}

// ── WRITE: the settings screen ────────────────────────────────────────

public class SavePipelineRulesHandler : ICommandHandler
{
    private const int MaxLabel = 100;
    private const int MaxPrompt = 200;

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
        // ── the one whole-pipeline rule ───────────────────────────────
        var row = await _db.PipelineRuleSettings
            .FirstOrDefaultAsync(r => r.TenantId == tenantId, ct);

        if (row is null)
        {
            row = new PipelineRuleSettings { Id = Guid.NewGuid(), TenantId = tenantId };
            _db.PipelineRuleSettings.Add(row);
        }

        row.BlockReopenWithIssuedInvoice = dto.BlockReopenWithIssuedInvoice;
        row.UpdatedAtUtc = DateTime.UtcNow;
        row.UpdatedBy = updatedBy;

        // ── the matrix ────────────────────────────────────────────────
        var stages = await _db.PipelineStages.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .ToDictionaryAsync(s => s.Key, ct);

        // ── 042: the three quote-driven moves ─────────────────────────
        // Written after `stages` is loaded, because each one has to BE a
        // stage this workspace has. Everything here happens before the
        // single SaveChangesAsync below, so a refusal leaves the whole
        // screen unsaved rather than half saved.
        row.QuoteSentStageKey = ResolveQuoteStage(
            dto.QuoteSentStageKey, row.QuoteSentStageKey, stages,
            "When a quote is raised", QuoteMoveKind.Sent);

        row.QuoteAcceptedStageKey = ResolveQuoteStage(
            dto.QuoteAcceptedStageKey, row.QuoteAcceptedStageKey, stages,
            "When a quote is accepted", QuoteMoveKind.Accepted);

        row.QuoteRejectedStageKey = ResolveQuoteStage(
            dto.QuoteRejectedStageKey, row.QuoteRejectedStageKey, stages,
            "When a quote is rejected", QuoteMoveKind.Rejected);

        var existing = await _db.ProcessTransitions
            .Where(t => t.TenantId == tenantId)
            .ToListAsync(ct);

        var byPair = existing.ToDictionary(t => (t.FromStageKey, t.ToStageKey));

        var added = 0;
        var changed = 0;

        foreach (var incoming in dto.Transitions)
        {
            if (incoming.FromStageKey == incoming.ToStageKey) continue;

            // A cell naming a stage the tenant no longer has is a stale
            // form, not a reason to fail the whole save. The foreign keys
            // would refuse it anyway, with a far worse message.
            if (!stages.ContainsKey(incoming.FromStageKey)) continue;
            if (!stages.ContainsKey(incoming.ToStageKey)) continue;

            var toStage = stages[incoming.ToStageKey];
            var fromStage = stages[incoming.FromStageKey];

            var label = Clip(incoming.Label, MaxLabel);
            if (string.IsNullOrWhiteSpace(label))
                label = TransitionLabels.For(fromStage.Category, toStage.Category, toStage.Name);

            // RequiresAcceptedQuote is the stronger claim; storing both
            // would make a refusal read "needs a quote and an accepted
            // quote".
            var requiresAccepted = incoming.RequiresAcceptedQuote;
            var requiresQuote = incoming.RequiresQuote && !requiresAccepted;

            var prompt = Clip(incoming.NotePrompt, MaxPrompt);
            if (incoming.RequiresNote && string.IsNullOrWhiteSpace(prompt))
                prompt = toStage.Category == StageCategory.Lost
                    ? TransitionLabels.LostPrompt
                    : fromStage.IsTerminal ? TransitionLabels.ReopenPrompt : null;

            if (byPair.TryGetValue((incoming.FromStageKey, incoming.ToStageKey), out var t))
            {
                var before = Snapshot(t);

                t.Label = label;
                t.IsActive = incoming.IsActive;
                t.Actor = incoming.Actor;
                t.RequiresQuote = requiresQuote;
                t.RequiresAcceptedQuote = requiresAccepted;
                t.RequiresValue = incoming.RequiresValue;
                t.RequiresCloseDate = incoming.RequiresCloseDate;
                t.RequiresNote = incoming.RequiresNote;
                t.NotePrompt = prompt;
                t.RequiresAttachment = incoming.RequiresAttachment;

                if (before != Snapshot(t))
                {
                    t.UpdatedAtUtc = DateTime.UtcNow;
                    t.UpdatedBy = updatedBy;
                    changed++;
                }
            }
            else
            {
                // The migration seeds every pair, but a stage added later
                // has none — and a tenant who ran 020 with ForwardOnly on
                // has no backward rows at all. Creating on save is what
                // lets the grid show a complete matrix regardless.
                _db.ProcessTransitions.Add(new ProcessTransition
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    FromStageKey = incoming.FromStageKey,
                    ToStageKey = incoming.ToStageKey,
                    Label = label,
                    SortOrder = toStage.SortOrder,
                    IsActive = incoming.IsActive,
                    Actor = incoming.Actor,
                    RequiresQuote = requiresQuote,
                    RequiresAcceptedQuote = requiresAccepted,
                    RequiresValue = incoming.RequiresValue,
                    RequiresCloseDate = incoming.RequiresCloseDate,
                    RequiresNote = incoming.RequiresNote,
                    NotePrompt = prompt,
                    RequiresAttachment = incoming.RequiresAttachment,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedBy = updatedBy
                });
                added++;
            }
        }

        // Transitions first: an action needs a transition id to hang off,
        // and a cell the tenant has just switched on may not have had a row
        // until this moment.
        await _db.SaveChangesAsync(ct);

        var actionsChanged = await SaveActionsAsync(tenantId, dto, updatedBy, ct);

        await _audit.WriteAsync(
            "PipelineProcessChanged", "PipelineRuleSettings", row.Id, tenantId,
            new
            {
                dto.BlockReopenWithIssuedInvoice,
                // 042. These three change where deals go on their own, with
                // nobody pressing anything, so they belong in the trail as
                // much as the matrix does.
                quoteSentStageKey = row.QuoteSentStageKey,
                quoteAcceptedStageKey = row.QuoteAcceptedStageKey,
                quoteRejectedStageKey = row.QuoteRejectedStageKey,
                transitionsAdded = added,
                transitionsChanged = changed,
                actionsConfigured = actionsChanged
            },
            ct);

        _logger.LogInformation(
            "Pipeline process saved for tenant {TenantId} by {User}: {Added} added, {Changed} changed",
            tenantId, updatedBy, added, changed);
    }

    /// <summary>
    /// Replaces each transition's actions with what was posted.
    ///
    /// Delete-and-insert rather than a diff. Nothing references a
    /// TransitionAction — not a deal, not an activity, not history — so
    /// there is no identity worth preserving, and a diff would be more
    /// code to get subtly wrong for no benefit anyone can observe.
    ///
    /// Only transitions the form actually mentioned are touched, so a
    /// partial post can never silently clear a step it said nothing about.
    /// </summary>
    private async Task<int> SaveActionsAsync(
        Guid tenantId, SaveAllPipelineRulesDto dto, string updatedBy, CancellationToken ct)
    {
        var byPair = await _db.ProcessTransitions.AsNoTracking()
            .Where(t => t.TenantId == tenantId)
            .Select(t => new { t.Id, t.FromStageKey, t.ToStageKey })
            .ToDictionaryAsync(t => (t.FromStageKey, t.ToStageKey), t => t.Id, ct);

        var touched = dto.Transitions
            .Where(x => byPair.ContainsKey((x.FromStageKey, x.ToStageKey)))
            .Select(x => byPair[(x.FromStageKey, x.ToStageKey)])
            .ToHashSet();

        if (touched.Count == 0) return 0;

        var existing = await _db.TransitionActions
            .Where(a => a.TenantId == tenantId && touched.Contains(a.TransitionId))
            .ToListAsync(ct);

        if (existing.Count > 0) _db.TransitionActions.RemoveRange(existing);

        var written = 0;
        var now = DateTime.UtcNow;

        foreach (var t in dto.Transitions)
        {
            if (t.Actions is null || t.Actions.Count == 0) continue;
            if (!byPair.TryGetValue((t.FromStageKey, t.ToStageKey), out var transitionId)) continue;

            var order = 0;

            foreach (var a in t.Actions)
            {
                var subject = Clip(a.Subject, 200);
                if (string.IsNullOrWhiteSpace(subject)) continue;   // nothing to create

                // "A specific person" with nobody chosen would make a task
                // assigned to nothing, which lands on no one's list. Fall
                // back to the deal's owner rather than storing a row the
                // check constraint would reject anyway.
                var assignTo = a.AssignTo;
                var assignToUserId = a.AssignToUserId;

                if (assignTo == TransitionAssignee.SpecificUser && assignToUserId is null)
                    assignTo = TransitionAssignee.DealOwner;

                if (assignTo != TransitionAssignee.SpecificUser)
                    assignToUserId = null;

                _db.TransitionActions.Add(new TransitionAction
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    TransitionId = transitionId,
                    Kind = a.Kind,
                    SortOrder = order++,
                    IsActive = a.IsActive,
                    Subject = subject!,
                    Description = Clip(a.Description, 1000),
                    ActivityType = string.IsNullOrWhiteSpace(a.ActivityType) ? "Task" : a.ActivityType.Trim(),
                    AssignTo = assignTo,
                    AssignToUserId = assignToUserId,
                    DueInDays = Math.Clamp(a.DueInDays, 0, 365),
                    CreatedAtUtc = now,
                    CreatedBy = updatedBy
                });

                written++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return written;
    }

    private static object Snapshot(ProcessTransition t) => new
    {
        t.Label, t.IsActive, t.Actor, t.RequiresQuote, t.RequiresAcceptedQuote,
        t.RequiresValue, t.RequiresCloseDate, t.RequiresNote, t.NotePrompt,
        t.RequiresAttachment
    }.ToString()!;

    private static string? Clip(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        return t.Length > max ? t[..max] : t;
    }

    // ── 042: the three quote-driven moves ─────────────────────────────

    private enum QuoteMoveKind { Sent, Accepted, Rejected }

    /// <summary>
    /// Turns what the form sent into a stage key worth storing.
    ///
    ///   empty / whitespace  → null, meaning "don't move the deal"
    ///   the stored value    → kept, without re-checking (see below)
    ///   anything else       → must be a stage this workspace has, and must
    ///                         make sense for the event
    ///
    /// WHY AN UNCHANGED KEY IS NOT RE-CHECKED. The same reason 041 does not
    /// re-validate an unchanged owner: if a tenant retires the stage this
    /// points at, re-checking it would refuse every later save of the
    /// process screen, and the only way out would be to notice the real
    /// cause in a message about something else. An inert key does no harm —
    /// QuoteStageAutomation refuses the move and logs why.
    ///
    /// THE CATEGORY CHECKS ARE NOT FUSSINESS. Pointing "quote accepted" at a
    /// Lost stage is a mistake that CLOSES DEALS AS LOST on their happiest
    /// day, and it closes them silently, because nobody presses anything —
    /// the automatic move does it. ApplyToDeal fills in the close date and
    /// the actual value on the way through, so undoing it is not a matter of
    /// dragging the card back. This is the one place where refusing a save
    /// is much kinder than accepting it.
    /// </summary>
    private static string? ResolveQuoteStage(
        string? incoming,
        string? current,
        IReadOnlyDictionary<string, PipelineStage> stages,
        string label,
        QuoteMoveKind kind)
    {
        var wanted = incoming?.Trim();

        // "Don't move the deal" — the default, and always allowed.
        if (string.IsNullOrEmpty(wanted))
            return null;

        if (string.Equals(wanted, current?.Trim(), StringComparison.OrdinalIgnoreCase))
            return current;

        // Case-insensitive, and the tenant's own spelling wins. A key that
        // differs only in case would pass the foreign key and then fail to
        // match in TenantStages.Find, which is the worst of both.
        var match = stages.Values
            .FirstOrDefault(s => string.Equals(s.Key, wanted, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            throw new InvalidOperationException(
                $"\"{label}\" points at a stage this workspace does not have. " +
                "Pick one from the list, or choose \"Don't move the deal\".");

        var forbidden = kind switch
        {
            // Raising a quote is not closing a deal, either way.
            QuoteMoveKind.Sent =>
                match.Category != StageCategory.Open
                    ? "Raising a quote does not close a deal, so this has to be an open stage."
                    : null,

            // Accepted may be Open (usual) or Won (a workspace that treats
            // acceptance as the sale). Lost would close the deal as lost the
            // moment the customer said yes.
            QuoteMoveKind.Accepted =>
                match.Category == StageCategory.Lost
                    ? "An accepted quote can't move a deal into a lost stage."
                    : null,

            // Rejected may be Open (another quote to come) or Lost. Won
            // would mark the deal as sold when the customer said no.
            QuoteMoveKind.Rejected =>
                match.Category == StageCategory.Won
                    ? "A rejected quote can't move a deal into a won stage."
                    : null,

            _ => null
        };

        if (forbidden is not null)
            throw new InvalidOperationException($"\"{label}\" — {forbidden}");

        return match.Key;
    }
}

/* ApplySuggestedProcessHandler was removed in 021.

   It wrote to the database the moment someone clicked, which made "what
   does this button do?" an expensive question to ask. The three named
   templates that replaced it are computed over the tenant's own stages
   and applied in the BROWSER: the click fills the form, and nothing is
   written until Save. Cancel undoes it. See ProcessTemplates.cs. */

// ── READ: the deal page's transition bar ──────────────────────────────

public class GetAvailableTransitionsHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly IStageResolver _stages;
    private readonly TransitionCatalog _transitions;
    private readonly ICurrentUserService _currentUser;
    private readonly IRecordScopeService _scope;
    private readonly QuoteApprovalEngine _approvals;

    public GetAvailableTransitionsHandler(
        FlowDbContext db,
        IStageResolver stages,
        TransitionCatalog transitions,
        ICurrentUserService currentUser,
        IRecordScopeService scope,
        QuoteApprovalEngine approvals)
    {
        _db = db;
        _stages = stages;
        _transitions = transitions;
        _currentUser = currentUser;
        _scope = scope;
        _approvals = approvals;
    }

    /// <summary>
    /// Every move offered from this deal's stage, each marked allowed or
    /// not, with the reason when not.
    ///
    /// The reasons are computed HERE rather than left to the guard's
    /// refusal because a disabled button that says "needs an accepted
    /// quote" teaches the process; a live button that fails when pressed
    /// only frustrates. The guard still checks everything again on the
    /// way in — this is guidance, not the gate.
    /// </summary>
    public async Task<DealTransitionsDto> HandleAsync(
        Guid tenantId, Guid dealId, CancellationToken ct = default)
    {
        var access = await _scope.GetAsync(RecordModules.Deals, ct);

        var deal = await _db.Deals.AsNoTracking()
            .Where(d => d.Id == dealId && d.TenantId == tenantId && !d.IsDeleted)
            .VisibleTo(access)
            .Select(d => new
            {
                d.Id, d.Stage, d.ExpectedValue, d.ExpectedCloseDateUtc, d.OwnerUserId
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Deal {dealId} not found");

        var stages = await _stages.GetAsync(tenantId, ct);
        var catalog = await _transitions.GetAsync(tenantId, ct);
        var me = await _currentUser.GetCurrentUserAsync();

        var current = stages.Find(deal.Stage);

        var allStages = stages.All
            .OrderBy(s => s.SortOrder)
            .Select(s => new StageLiteDto(
                s.Id, s.Key, s.Name, s.SortOrder, s.Category, s.IsActive, s.IsDefault,
                s.Probability))                                   // 042
            .ToList();

        var moves = catalog.From(deal.Stage);

        // ── the facts every transition is judged against, fetched once ──
        var needsQuoteInfo = moves.Any(t => t.RequiresQuote || t.RequiresAcceptedQuote);
        var needsFileInfo = moves.Any(t => t.RequiresAttachment);
        var needsApprovers = moves.Any(t => t.Actor is TransitionActor.DealOwner or TransitionActor.TeamManagers);

        var quoteStatuses = needsQuoteInfo
            ? await _db.Quotes.AsNoTracking()
                .Where(q => q.TenantId == tenantId && q.DealId == dealId && !q.IsDeleted)
                .Select(q => q.Status)
                .ToListAsync(ct)
            : new List<QuoteStatus>();

        var hasAttachment = needsFileInfo
            && await _db.Attachments.AsNoTracking()
                .AnyAsync(a => a.TenantId == tenantId && a.EntityId == dealId && !a.IsDeleted, ct);

        var isApprover = false;
        if (needsApprovers && !me.IsTenantAdmin)
        {
            var approvers = await _approvals.ApproversForDealAsync(
                tenantId, dealId, excludeUserId: null, teamFallbackUserId: me.UserId, ct);
            isApprover = approvers.Any(a => a.UserId == me.UserId);
        }

        var ownsIt = !string.IsNullOrEmpty(deal.OwnerUserId)
                     && string.Equals(deal.OwnerUserId, me.UserId.ToString(),
                                      StringComparison.OrdinalIgnoreCase);

        // A won deal that has been invoiced cannot be reopened by anyone,
        // so every move out of it is blocked for the same reason. One
        // query, asked only when it can matter.
        string? invoiceBlock = null;
        if (current?.Category == StageCategory.Won && moves.Count > 0)
        {
            var settings = await _db.PipelineRuleSettings.AsNoTracking()
                .FirstOrDefaultAsync(r => r.TenantId == tenantId, ct);

            if (settings?.BlockReopenWithIssuedInvoice ?? PipelineRuleDefaults.BlockReopenWithIssuedInvoice)
            {
                var number = await _db.Invoices.AsNoTracking()
                    .Where(i => i.TenantId == tenantId && i.DealId == dealId && !i.IsDeleted
                             && i.IssuedAtUtc != null && i.VoidedAtUtc == null)
                    .OrderBy(i => i.Number)
                    .Select(i => i.Number)
                    .FirstOrDefaultAsync(ct);

                if (!string.IsNullOrEmpty(number))
                    invoiceBlock = $"Invoice {number} has been issued. Void it before reopening this deal.";
            }
        }

        var list = moves.Select(t =>
        {
            var to = stages.Find(t.ToStageKey);
            var toName = to?.Name ?? t.ToStageKey;
            var reason = Blocked(t, to);

            return new AvailableTransitionDto(
                t.ToStageKey,
                toName,
                t.Label,
                to?.Category ?? StageCategory.Open,
                t.RequiresNote,
                t.EffectiveNotePrompt,
                reason is null,
                reason);
        }).ToList();

        return new DealTransitionsDto(
            dealId,
            deal.Stage,
            current?.Name ?? deal.Stage,
            current?.Category ?? StageCategory.Open,
            list,
            me.IsTenantAdmin,
            allStages);

        // ── local: why this one is not available ──────────────────────
        string? Blocked(ProcessTransition t, PipelineStage? to)
        {
            // Leaving a Won stage at all is a reopen, so the invoice block
            // applies to every move out of it.
            if (invoiceBlock is not null) return invoiceBlock;

            if (!me.IsTenantAdmin)
            {
                switch (t.Actor)
                {
                    case TransitionActor.Admins:
                        return "Workspace admins only.";
                    case TransitionActor.TeamManagers when !isApprover:
                        return "Managers of the deal owner's team, and workspace admins.";
                    case TransitionActor.DealOwner when !ownsIt && !isApprover:
                        return "The deal's owner, their manager, or a workspace admin.";
                }
            }

            if (t.RequiresValue && deal.ExpectedValue <= 0)
                return "This deal needs a value above zero first.";

            if (t.RequiresCloseDate && deal.ExpectedCloseDateUtc == default)
                return "This deal needs an expected close date first.";

            if (t.RequiresAttachment && !hasAttachment)
                return "This deal needs at least one attachment first.";

            if (t.RequiresAcceptedQuote && !quoteStatuses.Contains(QuoteStatus.Accepted))
                return quoteStatuses.Count == 0
                    ? "This deal has no quote yet."
                    : "The quote on this deal hasn't been accepted yet.";

            if (t.RequiresQuote && !t.RequiresAcceptedQuote && quoteStatuses.Count == 0)
                return "This deal needs a quote first.";

            return null;
        }
    }
}
