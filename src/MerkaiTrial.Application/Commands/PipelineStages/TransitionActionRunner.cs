// =====================================================================
// TransitionActionRunner.cs
// Location: MerkaiTrial.Application/Commands/PipelineStages/TransitionActionRunner.cs
//
// NEW FILE (022).
//
// THE ONE RULE THIS FILE EXISTS TO ENFORCE
//   An action never undoes a move.
//
//   If a deal was legitimately marked Won — the rules passed, the person
//   was allowed, the history is written — then a broken task template
//   must not reverse that. A CRM that refuses to close a deal because a
//   follow-up could not be created is a CRM people route around.
//
//   So: the caller commits the move FIRST and calls this afterwards.
//   Every action runs in its own try/catch, a failure is logged and
//   returned, and the deal stays exactly where the person put it. The
//   page says "the deal moved, but a follow-up couldn't be created",
//   which is the truth and is actionable.
//
// WHY IT BUILDS ON Activities
//   Tasks and logged activities already exist, with their own validation,
//   their own visibility rules and their own place in the timeline.
//   Creating a task here is one call to CreateActivityHandler rather than
//   a second kind of task that behaves almost the same.
//
// WHO THE TASK GOES TO
//   Resolved at the moment of the move, never stored. A deal that changes
//   hands sends its follow-ups to whoever owns it now, which is what
//   anyone would expect and what a stored user id would get wrong.
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Commands.Activities;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.PipelineStages;

/// <summary>What happened when the actions ran. Never an exception.</summary>
public sealed record ActionRunResult(
    int Created,
    /// <summary>
    /// One line per failure, written for the person who made the move —
    /// they are the one who will see it and the one who can tell an admin
    /// the template is wrong.
    /// </summary>
    List<string> Problems)
{
    public static readonly ActionRunResult None = new(0, new List<string>());
    public bool HasProblems => Problems.Count > 0;
}

/// <summary>
/// Registered by the Scrutor scan through ICommandHandler and injected by
/// concrete type. Nothing to add to Program.cs.
/// </summary>
public class TransitionActionRunner : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly CreateActivityHandler _activities;
    private readonly ILogger<TransitionActionRunner> _logger;

    public TransitionActionRunner(
        FlowDbContext db,
        CreateActivityHandler activities,
        ILogger<TransitionActionRunner> logger)
    {
        _db = db;
        _activities = activities;
        _logger = logger;
    }

    /// <summary>
    /// Runs everything configured on the step that was just taken.
    ///
    /// CALL THIS AFTER THE MOVE IS COMMITTED. It does not throw — a
    /// failure comes back in the result so the caller can mention it
    /// without the move being in doubt.
    /// </summary>
    /// <param name="movedByUserId">
    /// The person who pressed the button, or null for a system move (a
    /// quote accepted, an invoice paid). Null means "assign to whoever
    /// moved it" falls back to the deal's owner, because there is no
    /// person to give the work to.
    /// </param>
    public async Task<ActionRunResult> RunAsync(
        Guid tenantId,
        Guid dealId,
        StageMoveDecision decision,
        string? movedByUserId,
        CancellationToken ct = default)
    {
        // No transition means a system move or an admin override — there
        // is no configured step, so there is nothing to run.
        if (decision.Transition is null) return ActionRunResult.None;

        var actions = await _db.TransitionActions.AsNoTracking()
            .Where(a => a.TenantId == tenantId
                     && a.TransitionId == decision.Transition.Id
                     && a.IsActive)
            .OrderBy(a => a.SortOrder)
            .ToListAsync(ct);

        if (actions.Count == 0) return ActionRunResult.None;

        // Loaded once: every action needs the deal's title for its tokens
        // and its owner for the assignee.
        var deal = await _db.Deals.AsNoTracking()
            .Where(d => d.Id == dealId && d.TenantId == tenantId)
            .Select(d => new { d.Title, d.OwnerUserId })
            .FirstOrDefaultAsync(ct);

        if (deal is null)
        {
            _logger.LogWarning(
                "Transition actions skipped: deal {DealId} not found after its own move", dealId);
            return ActionRunResult.None;
        }

        var ownerName = await OwnerNameAsync(tenantId, deal.OwnerUserId, ct);

        var created = 0;
        var problems = new List<string>();

        foreach (var action in actions)
        {
            try
            {
                var assignee = ResolveAssignee(action, deal.OwnerUserId, movedByUserId);

                if (assignee is null)
                {
                    // Every route to a person came back empty: an unowned
                    // deal moved by the system, or a named assignee who has
                    // since been deactivated. A task assigned to nobody
                    // appears on no list, so not creating it is the honest
                    // outcome.
                    problems.Add(
                        $"\"{action.Subject}\" wasn't created — there is nobody to assign it to. " +
                        "Give the deal an owner, or change who the step assigns to.");
                    continue;
                }

                var subject = ActionTokens.Fill(
                    action.Subject, deal.Title, decision.To.Name, ownerName);

                if (string.IsNullOrWhiteSpace(subject))
                    subject = action.Kind == TransitionActionKind.CreateTask
                        ? $"Follow up — {deal.Title}"
                        : decision.MoveName;

                var description = ActionTokens.Fill(
                    action.Description, deal.Title, decision.To.Name, ownerName);

                var isTask = action.Kind == TransitionActionKind.CreateTask;

                await _activities.Handle(new CreateActivityDto(
                    TenantId: tenantId,
                    EntityType: ActivityEntityType.Deal,
                    EntityId: dealId,
                    ActivityType: action.ActivityType,
                    Subject: subject,
                    Description: string.IsNullOrWhiteSpace(description) ? null : description,
                    Duration: null,

                    // A log records something that has just happened, so it
                    // is stamped now. A task has no activity date.
                    ActivityDate: isTask ? null : DateTime.UtcNow,
                    IsTask: isTask,
                    DueDate: isTask
                        ? DateTime.UtcNow.Date.AddDays(Math.Clamp(action.DueInDays, 0, 365)).AddHours(9)
                        : null,
                    AssignedToUserId: assignee,

                    // Honest about who wrote this. Attributing an
                    // auto-created task to a person who never saw it is
                    // worse than a row that says "process" — and it makes
                    // every one of them findable in a single query.
                    CreatedBy: ProcessAuthor.Value
                ), ct);

                created++;
            }
            catch (Exception ex)
            {
                // The move is already committed. Whatever went wrong here,
                // the deal stays where the person put it.
                _logger.LogError(ex,
                    "Transition action {ActionId} ({Kind}) failed on deal {DealId} after {Move}",
                    action.Id, action.Kind, dealId, decision.MoveName);

                problems.Add(
                    $"\"{action.Subject}\" couldn't be created automatically. " +
                    "The deal moved; ask a workspace admin to check this step under Settings → Sales Process.");
            }
        }

        if (created > 0)
            _logger.LogInformation(
                "Ran {Count} transition action(s) on deal {DealId} after {Move}",
                created, dealId, decision.MoveName);

        return new ActionRunResult(created, problems);
    }

    /// <summary>
    /// Who gets the work, resolved now rather than stored.
    ///
    /// Each choice falls back rather than failing: a deal with no owner
    /// still gives its follow-up to whoever moved it, and a system move on
    /// an owned deal still gives it to the owner. Only when every route is
    /// empty does the action decline to create a task nobody would see.
    /// </summary>
    private static string? ResolveAssignee(
        TransitionAction action, string? dealOwnerId, string? movedByUserId)
    {
        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        return action.AssignTo switch
        {
            TransitionAssignee.SpecificUser =>
                action.AssignToUserId?.ToString() ?? Clean(dealOwnerId) ?? Clean(movedByUserId),

            TransitionAssignee.WhoeverMovedIt =>
                Clean(movedByUserId) ?? Clean(dealOwnerId),

            _ => Clean(dealOwnerId) ?? Clean(movedByUserId)
        };
    }

    private async Task<string?> OwnerNameAsync(Guid tenantId, string? ownerUserId, CancellationToken ct)
    {
        if (!Guid.TryParse(ownerUserId, out var id)) return null;

        // Users has no global tenant filter, so the TenantId condition is
        // the only thing scoping this.
        var owner = await _db.Users.AsNoTracking()
            .Where(u => u.Id == id && u.TenantId == tenantId && !u.IsDeleted)
            .Select(u => new { u.FirstName, u.LastName })
            .FirstOrDefaultAsync(ct);

        return owner is null ? null : $"{owner.FirstName} {owner.LastName}".Trim();
    }
}
