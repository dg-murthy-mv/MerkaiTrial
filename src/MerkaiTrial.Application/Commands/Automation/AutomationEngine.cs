// =====================================================================
// AutomationEngine.cs
// Location: MerkaiTrial.Application/Commands/Automation/AutomationEngine.cs
//
// NEW FILE (098 — workflow automation).
//
// THE TWO RULES THIS FILE EXISTS TO KEEP
//
//   1. A RULE NEVER UNDOES THE CHANGE THAT FIRED IT.
//      The lead handlers and deal handlers call the engine AFTER their own
//      SaveChangesAsync. By then the lead is saved, the deal has moved.
//      Nothing in here throws back to the caller: every rule runs in its
//      own try/catch, every action in its own, and what went wrong is
//      written to the rule log where the admin can read it. Same promise
//      as the sales process's follow-up actions (022, TransitionActionRunner).
//
//   2. A RULE'S OWN CHANGE NEVER FIRES RULES.
//      "Set the owner" must not trigger "when the owner changes", which
//      might set the status, which might trigger… Two rules could feed
//      each other for ever. The actions here write to the records
//      directly — never through the lead or deal handlers that call the
//      engine — and an AsyncLocal guard refuses re-entry on top, so even a
//      future action that does go through a handler cannot start a loop.
//
// WHAT A RUN LOOKS LIKE
//   For each ACTIVE rule for this kind of record and this trigger, oldest
//   first, within the plan's limit:
//     When  — FieldChanged rules look for their field among the changes
//             and check from / to. A rule whose field did not change is
//             not logged: that is not news, and logging it would bury the
//             log in every unrelated edit.
//     If    — every condition must hold. A condition that does not is
//             logged as "Skipped", with the value it found — that is the
//             answer to "why didn't my rule fire?".
//     Then  — each action in order. One failing does not stop the rest.
//   One SaveChangesAsync per rule, so one bad rule cannot lose another's
//   work.
//
// WHO DID IT
//   Tasks, notes on the timeline and audit rows say "Automation", never
//   the person whose save fired the rule — they did not write that task,
//   and a list of "tasks Priya created" that includes ones she never saw
//   would be a lie. Notifications go out with no actor, so the person who
//   made the change is told too when a rule addresses them.
//
// THE BACKGROUND CHECK (099)
//   Time-based rules run from AutomationTimeRunner — the worker every few
//   minutes, or "Check now" on the Automation page — through
//   RunTimeRuleAsync. The worker gives its scope the workspace
//   (OverridableTenantProvider), so the resolvers and the custom-field reader,
//   which go through the tenant filter, see the right rows; the engine's
//   own queries name TenantId explicitly as well.
//
//   A time-based rule behaves differently from a change-set-off one in
//   three small ways, all to keep the log readable when the same records
//   are looked at every few minutes:
//     • a record whose conditions don't hold is NOT logged as Skipped and
//       NOT marked done — it is simply looked at again next time, so it
//       can still fire if it starts matching while the situation lasts;
//     • over the plan's limit, it is quietly not run (not "held back"
//       every quarter of an hour);
//     • whatever the outcome once it runs, an AutomationTimeFire row is
//       written with the run, so the same episode is never acted on twice.
//
// TWO CHECKS AT ONCE (100)
//   The worker, a second WebApi instance and "Check now" can look at the
//   same record in the same instant. Both find it due, both stage the
//   task, and both try to save it WITH its AutomationTimeFire row. The
//   unique (rule, record, episode) index lets exactly one of those saves
//   through; the other fails as a whole, so its task never exists. Until
//   100 that second save was then treated as the rule failing: an error
//   in the log and a second attempt to write a "Failed" line. Now it is
//   recognised for what it is — the other check already did it — and is
//   dropped quietly: nothing logged as failed, nothing counted as run.
// =====================================================================

using System.Globalization;
using MerkaiTrial.Application.Commands.Activities;
using MerkaiTrial.Application.Common;
using MerkaiTrial.Application.Commands.CustomFields;
using MerkaiTrial.Application.Commands.LeadStatuses;
using MerkaiTrial.Application.Commands.PipelineStages;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Notifications;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MerkaiTrial.Application.Security;

namespace MerkaiTrial.Application.Commands.Automation;

/// <summary>One field that changed, as its key and its before / after values in wire form.</summary>
public sealed record AutomationFieldChange(string Field, string? From, string? To);

/// <summary>099. A record a time-based rule is due on, and the episode it is due for.</summary>
public sealed record AutomationDue(Guid EntityId, DateTime EpisodeAtUtc);

/// <summary>
/// Runs the workspace's automation rules for a lead or deal that was just
/// added or changed. Registered by the Scrutor scan through ICommandHandler
/// and injected by concrete type, like TransitionActionRunner.
///
/// CALL IT AFTER YOUR OWN SaveChangesAsync. It never throws.
/// </summary>
public class AutomationEngine : ICommandHandler
{
    /// <summary>What tasks, timeline entries and audit rows written by a rule are attributed to.</summary>
    public const string Author = "Automation";

    /// <summary>Set while rules run — see rule 2 in the header.</summary>
    private static readonly AsyncLocal<bool> Running = new();

    private readonly FlowDbContext _db;
    private readonly INotificationDispatcher _notify;
    private readonly ILeadStatusResolver _statuses;
    private readonly IStageResolver _stages;
    private readonly ILeadScoringService _scoring;
    private readonly IAuditService _audit;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<AutomationEngine> _logger;

    public AutomationEngine(
        FlowDbContext db,
        INotificationDispatcher notify,
        ILeadStatusResolver statuses,
        IStageResolver stages,
        ILeadScoringService scoring,
        IAuditService audit,
        ICurrentUserService currentUser,
        ILogger<AutomationEngine> logger)
    {
        _db = db;
        _notify = notify;
        _statuses = statuses;
        _stages = stages;
        _scoring = scoring;
        _audit = audit;
        _currentUser = currentUser;
        _logger = logger;
    }

    // =================================================================
    // ENTRY POINTS
    // =================================================================

    /// <summary>A lead or deal was added.</summary>
    public Task RecordCreatedAsync(
        Guid tenantId, string entityType, Guid entityId, string? actorUserId, CancellationToken ct = default)
        => RunAsync(tenantId, entityType, new[] { entityId }, AutomationTrigger.RecordCreated,
                    Array.Empty<AutomationFieldChange>(), actorUserId, ct);

    /// <summary>Several were added at once — an import.</summary>
    public Task RecordsCreatedAsync(
        Guid tenantId, string entityType, IReadOnlyCollection<Guid> entityIds, string? actorUserId, CancellationToken ct = default)
        => RunAsync(tenantId, entityType, entityIds, AutomationTrigger.RecordCreated,
                    Array.Empty<AutomationFieldChange>(), actorUserId, ct);

    /// <summary>
    /// Fields on a lead or deal changed. Pass every change you know of;
    /// ones whose value did not really change are dropped here.
    /// </summary>
    public Task FieldsChangedAsync(
        Guid tenantId, string entityType, Guid entityId,
        IEnumerable<AutomationFieldChange> changes, string? actorUserId, CancellationToken ct = default)
    {
        var real = changes
            .Where(c => !string.Equals(Norm(c.From), Norm(c.To), StringComparison.OrdinalIgnoreCase))
            .ToList();

        return real.Count == 0
            ? Task.CompletedTask
            : RunAsync(tenantId, entityType, new[] { entityId }, AutomationTrigger.FieldChanged, real, actorUserId, ct);
    }

    /// <summary>"12500.00" and "12500" are the same value; "" and null too.</summary>
    private static string? Norm(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim();
        return decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? d.ToString("0.####", CultureInfo.InvariantCulture)
            : v;
    }

    /// <summary>
    /// "Whoever made the change". Callers that know pass it; otherwise the
    /// signed-in person, if there is one. The background worker (099) has
    /// nobody signed in, so "the person who made the change" is simply
    /// nobody there — an action aimed at them is logged as having no one.
    /// </summary>
    private string? ActorOrSignedIn(string? actorUserId)
    {
        if (!string.IsNullOrWhiteSpace(actorUserId) && Guid.TryParse(actorUserId, out var given) && given != Guid.Empty)
            return given.ToString();
        try
        {
            var id = _currentUser.GetCurrentUserId();
            return id == Guid.Empty ? null : id.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 099. The custom fields that changed between two reads of a record's
    /// values (CustomFieldValueWriter.ReadAsync before the edit and after
    /// the save), as changes the engine understands. A null "before" means
    /// the caller didn't touch custom fields: no changes.
    /// </summary>
    public static IEnumerable<AutomationFieldChange> CustomChanges(
        IReadOnlyDictionary<Guid, string>? before, IReadOnlyDictionary<Guid, string>? after)
    {
        if (before is null || after is null) yield break;
        foreach (var id in before.Keys.Union(after.Keys))
        {
            before.TryGetValue(id, out var from);
            after.TryGetValue(id, out var to);
            if (!string.Equals(from, to, StringComparison.Ordinal))
                yield return new AutomationFieldChange(AutomationFields.CustomKey(id), from, to);
        }
    }

    /// <summary>A decimal as the engine compares and stores it.</summary>
    public static string? Wire(decimal? d) => d?.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// 099. One time-based rule, on the records it is due on. Returns how
    /// many of them it actually ran for (conditions held). Never throws.
    /// </summary>
    public async Task<int> RunTimeRuleAsync(
        Guid tenantId, Guid ruleId, IReadOnlyList<AutomationDue> due, CancellationToken ct = default)
    {
        if (Running.Value || tenantId == Guid.Empty || due.Count == 0) return 0;

        var ran = 0;
        try
        {
            Running.Value = true;

            var rule = await _db.AutomationRules.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Id == ruleId && r.TenantId == tenantId
                                       && r.Trigger == AutomationTrigger.TimeBased && r.IsActive && !r.IsDeleted, ct);
            if (rule is null || AutomationEntity.Normalise(rule.EntityType) is not { } entity) return 0;

            // Over the plan's limit: quietly not run — see the header.
            var (limit, _) = await AutomationPlan.LimitAsync(_db, tenantId, ct);
            var withinLimit = await _db.AutomationRules.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.TenantId == tenantId && r.IsActive && !r.IsDeleted)
                .OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Id)
                .Select(r => r.Id)
                .Take(Math.Max(limit, 0))
                .AnyAsync(id => id == ruleId, ct);
            if (!withinLimit) return 0;

            var options = await AutomationOptionsBuilder.BuildAsync(_db, _statuses, _stages, tenantId, ct);
            var context = await RunContext.LoadAsync(_db, _statuses, _stages, tenantId, options, ct);

            foreach (var d in due)
            {
                if (ct.IsCancellationRequested) break;
                if (await RunRuleAsync(rule, entity, d.EntityId, Array.Empty<AutomationFieldChange>(),
                        actorUserId: null, withinLimit: true, context, ct, d.EpisodeAtUtc))
                    ran++;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Time-based automation rule {RuleId} could not run in tenant {TenantId}", ruleId, tenantId);
        }
        finally
        {
            Running.Value = false;
        }
        return ran;
    }

    // =================================================================
    // THE RUN
    // =================================================================

    private async Task RunAsync(
        Guid tenantId, string entityType, IReadOnlyCollection<Guid> entityIds,
        AutomationTrigger trigger, IReadOnlyList<AutomationFieldChange> changes,
        string? actorUserId, CancellationToken ct)
    {
        if (Running.Value) return;                       // rule 2
        if (tenantId == Guid.Empty || entityIds.Count == 0) return;
        if (AutomationEntity.Normalise(entityType) is not { } entity) return;

        try
        {
            Running.Value = true;
            actorUserId = ActorOrSignedIn(actorUserId);

            // The one query every save pays for. Most workspaces have no
            // rule for most saves, and this returns nothing.
            var rules = await _db.AutomationRules.IgnoreQueryFilters()
                .Where(r => r.TenantId == tenantId && r.EntityType == entity
                         && r.Trigger == trigger && r.IsActive && !r.IsDeleted)
                .OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Id)
                .ToListAsync(ct);

            if (rules.Count == 0) return;

            // ── The plan's limit, enforced here as well as on the switch ──
            // A workspace moved to a smaller plan keeps its rules switched
            // on; only the oldest N run. The rest are logged as held back,
            // so nobody wonders why a rule went quiet.
            var (limit, _) = await AutomationPlan.LimitAsync(_db, tenantId, ct);
            var allowed = (await _db.AutomationRules.IgnoreQueryFilters().AsNoTracking()
                    .Where(r => r.TenantId == tenantId && r.IsActive && !r.IsDeleted)
                    .OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Id)
                    .Select(r => r.Id)
                    .Take(Math.Max(limit, 0))
                    .ToListAsync(ct))
                .ToHashSet();

            var options = await AutomationOptionsBuilder.BuildAsync(_db, _statuses, _stages, tenantId, ct);
            var context = await RunContext.LoadAsync(_db, _statuses, _stages, tenantId, options, ct);

            foreach (var entityId in entityIds)
            {
                foreach (var rule in rules)
                {
                    if (ct.IsCancellationRequested) return;
                    await RunRuleAsync(rule, entity, entityId, changes, actorUserId, allowed.Contains(rule.Id), context, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down. The change that fired the rules is already saved.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Automation rules could not run for {EntityType} in tenant {TenantId}; the change itself is saved",
                entityType, tenantId);
        }
        finally
        {
            Running.Value = false;
        }
    }

    /// <returns>True when the rule ran (its conditions held), whatever the actions did.</returns>
    private async Task<bool> RunRuleAsync(
        AutomationRule listed, string entity, Guid entityId,
        IReadOnlyList<AutomationFieldChange> changes, string? actorUserId,
        bool withinLimit, RunContext context, CancellationToken ct,
        DateTime? episodeAtUtc = null)
    {
        var rule = listed;
        var timeBased = episodeAtUtc is not null;
        try
        {
            // ── When ─────────────────────────────────────────────────
            if (rule.Trigger == AutomationTrigger.FieldChanged && !TriggerMatches(rule, changes))
                return false;                             // not this rule's field — not logged

            // The TRACKED rule, fetched again: if an earlier rule failed,
            // the change tracker was cleared and the instance from the list
            // no longer saves — its run count and round-robin turn would be
            // silently lost. Returns the same instance when still tracked.
            rule = await _db.AutomationRules.IgnoreQueryFilters()
                       .FirstOrDefaultAsync(r => r.Id == listed.Id && r.TenantId == listed.TenantId, ct)
                   ?? listed;
            if (!rule.IsActive || rule.IsDeleted) return false;   // switched off in the meantime

            var record = await RecordSnapshot.LoadAsync(_db, rule.TenantId, entity, entityId, context, ct);
            if (record is null) return false;             // deleted in between

            if (!withinLimit)
            {
                if (timeBased) return false;
                Log(rule, record, AutomationRunStatus.Skipped,
                    "Held back: the workspace's plan allows fewer switched-on rules than it has. " +
                    "Switch one off, or ask about a bigger plan.");
                await _db.SaveChangesAsync(ct);
                return false;
            }

            // ── If ───────────────────────────────────────────────────
            foreach (var condition in AutomationJson.Conditions(rule.ConditionsJson))
            {
                if (!context.Holds(condition, record, out var why))
                {
                    if (timeBased) return false;          // looked at again next time — see the header
                    Log(rule, record, AutomationRunStatus.Skipped, why);
                    await _db.SaveChangesAsync(ct);
                    return false;
                }
            }

            // ── Then ─────────────────────────────────────────────────
            var done = new List<string>();
            var problems = new List<string>();
            var after = new List<Func<Task>>();           // audit rows and score updates, once saved

            var actions = AutomationJson.Actions(rule.ActionsJson);
            if (actions.Count == 0) problems.Add("The rule has nothing to do — open it and add an action.");

            foreach (var action in actions)
            {
                try
                {
                    var line = action.Kind switch
                    {
                        AutomationActionKind.CreateTask  => await CreateTaskAsync(rule, action, record, actorUserId, context, ct),
                        AutomationActionKind.Notify      => await NotifyAsync(rule, action, record, actorUserId, context, ct),
                        AutomationActionKind.UpdateField => await UpdateFieldAsync(rule, action, record, context, after, ct),
                        _ => throw new AutomationProblem("An action of a kind this version doesn't know was skipped.")
                    };
                    done.Add(line);
                }
                catch (AutomationProblem p)
                {
                    problems.Add(p.Message);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Automation rule {RuleId} action {Kind} failed on {EntityType} {EntityId}",
                        rule.Id, action.Kind, entity, entityId);
                    problems.Add(ex is InvalidOperationException
                        ? $"Couldn't {Verb(action)}: {ex.Message}"
                        : $"Couldn't {Verb(action)} — something went wrong.");
                }
            }

            var status = problems.Count == 0 ? AutomationRunStatus.Done
                       : done.Count == 0     ? AutomationRunStatus.Failed
                       : AutomationRunStatus.Partial;

            Log(rule, record, status, string.Join(" · ", done.Concat(problems)));

            if (done.Count > 0)
            {
                rule.RunCount++;
                rule.LastRunAtUtc = DateTime.UtcNow;
            }

            if (timeBased) MarkFired(rule, entityId, episodeAtUtc!.Value);   // 099 — with the run, one save

            await _db.SaveChangesAsync(ct);

            foreach (var step in after)
            {
                try { await step(); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Automation rule {RuleId}: bookkeeping after its changes failed", rule.Id);
                }
            }

            return true;
        }
        catch (DbUpdateException ex) when (timeBased && IsAlreadyFired(ex))
        {
            // 100 — another check acted on this episode a moment ago; its
            // save won the unique index and ours carried the same task. Not
            // a failure, and nothing of ours was saved. See the header.
            _db.ChangeTracker.Clear();
            _logger.LogInformation(
                "Automation rule {RuleId}: {EntityType} {EntityId} was already handled by another check",
                rule.Id, entity, entityId);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Automation rule {RuleId} failed on {EntityType} {EntityId}", rule.Id, entity, entityId);

            // Whatever this rule staged did not save and must not ride along
            // with the next rule's save. The caller's change is already
            // committed, so nothing of theirs is lost by clearing.
            _db.ChangeTracker.Clear();

            try
            {
                _db.AutomationRuns.Add(new AutomationRun
                {
                    Id = Guid.NewGuid(),
                    TenantId = rule.TenantId,
                    RuleId = rule.Id,
                    RuleName = Cut(rule.Name, 150)!,
                    EntityType = entity,
                    EntityId = entityId,
                    Status = AutomationRunStatus.Failed,
                    Detail = "The rule couldn't run — something went wrong. Nothing it does was saved.",
                    CreatedAtUtc = DateTime.UtcNow
                });
                // 099: marked as acted on even so — retrying a failing rule
                // every few minutes would fill the log with the same line.
                if (timeBased) MarkFired(rule, entityId, episodeAtUtc!.Value);
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception logEx) when (logEx is not OperationCanceledException)
            {
                _db.ChangeTracker.Clear();
                _logger.LogError(logEx, "Automation rule {RuleId}: the failure could not be logged either", rule.Id);
            }
            return timeBased;
        }
    }

    /// <summary>100. True when a save failed on AutomationTimeFires' unique (rule, record, episode) index.</summary>
    private static bool IsAlreadyFired(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (e.Message.Contains("UX_AutomationTimeFires_Rule_Entity_Episode", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void MarkFired(AutomationRule rule, Guid entityId, DateTime episodeAtUtc)
        => _db.AutomationTimeFires.Add(new AutomationTimeFire
        {
            Id = Guid.NewGuid(),
            TenantId = rule.TenantId,
            RuleId = rule.Id,
            EntityId = entityId,
            EpisodeAtUtc = episodeAtUtc,
            FiredAtUtc = DateTime.UtcNow
        });

    private static string Verb(AutomationAction a) => a.Kind switch
    {
        AutomationActionKind.CreateTask  => "create the task",
        AutomationActionKind.Notify      => "send the message",
        AutomationActionKind.UpdateField => "set the field",
        _ => "do that"
    };

    /// <summary>Is this rule's field among the changes, going from / to what it asks?</summary>
    private static bool TriggerMatches(AutomationRule rule, IReadOnlyList<AutomationFieldChange> changes)
    {
        var change = changes.FirstOrDefault(c => string.Equals(c.Field, rule.TriggerField, StringComparison.OrdinalIgnoreCase));
        if (change is null) return false;

        if (!string.IsNullOrWhiteSpace(rule.TriggerToValue)
            && !string.Equals(Norm(change.To), Norm(rule.TriggerToValue), StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(rule.TriggerFromValue)
            && !string.Equals(Norm(change.From), Norm(rule.TriggerFromValue), StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private void Log(AutomationRule rule, RecordSnapshot record, AutomationRunStatus status, string detail)
        => _db.AutomationRuns.Add(new AutomationRun
        {
            Id = Guid.NewGuid(),
            TenantId = rule.TenantId,
            RuleId = rule.Id,
            RuleName = Cut(rule.Name, 150)!,
            EntityType = record.EntityType,
            EntityId = record.Id,
            EntityName = Cut(record.Name, 300),
            Status = status,
            Detail = Cut(detail, 2000),
            CreatedAtUtc = DateTime.UtcNow
        });

    // =================================================================
    // ACTIONS — each returns one line for the log, or throws AutomationProblem
    // =================================================================

    private async Task<string> CreateTaskAsync(
        AutomationRule rule, AutomationAction a, RecordSnapshot record, string? actorUserId,
        RunContext context, CancellationToken ct)
    {
        var assignee = context.Person(a.AssignTo, a.UserId, record.OwnerUserId, actorUserId)
            ?? throw new AutomationProblem(
                $"The task \"{Cut(a.Subject, 60)}\" wasn't created — there was nobody to give it to " +
                "(no owner, or the chosen person is no longer active).");

        var subject = Cut(context.Fill(a.Subject, record, rule), AutomationRuleCheck.MaxSubjectLength);
        if (string.IsNullOrWhiteSpace(subject)) subject = $"Follow up — {record.Name}";

        var description = context.Fill(a.Description, record, rule);
        var type = string.IsNullOrWhiteSpace(a.ActivityType) ? ActivityType.Task : a.ActivityType!;
        var now = DateTime.UtcNow;

        var task = new Activity
        {
            Id = Guid.NewGuid(),
            TenantId = rule.TenantId,
            EntityType = record.EntityType,
            EntityId = record.Id,
            ActivityType = type,
            Subject = subject!,
            Description = string.IsNullOrWhiteSpace(description) ? null : Cut(description, 2000),
            ActivityDate = now,
            IsTask = true,
            DueDate = context.DueUtc(a.DueInDays, now),
            IsCompleted = false,
            AssignedToUserId = assignee,
            CreatedAtUtc = now,
            CreatedBy = Author,
            IsDeleted = false
        };

        _db.Activities.Add(task);

        // The same "you were given a task" message a person's task sends
        // (091). No actor, so the assignee is always told.
        await TaskAssignmentNotifier.NotifyAsync(_db, _notify, _logger, task, actorUserId: null, ct);

        return $"Created the task \"{subject}\" for {context.NameOf(assignee)}";
    }

    private async Task<string> NotifyAsync(
        AutomationRule rule, AutomationAction a, RecordSnapshot record, string? actorUserId,
        RunContext context, CancellationToken ct)
    {
        var recipient = context.Person(a.AssignTo, a.UserId, record.OwnerUserId, actorUserId);
        if (recipient is null || !Guid.TryParse(recipient, out var recipientId))
            throw new AutomationProblem(
                "The message wasn't sent — there was nobody to send it to (no owner, or the chosen person is no longer active).");

        var message = context.Fill(a.Message, record, rule);
        if (string.IsNullOrWhiteSpace(message)) message = $"{rule.Name}: {record.Name}";

        var written = await _notify.AddForUsersAsync(
            new NotificationRequest(
                TenantId: rule.TenantId,
                EventType: NotificationEventType.AutomationNotice,
                Title: Cut(message, 200)!,
                Body: Cut($"From the automation rule \"{rule.Name}\"", 500),
                EntityType: record.EntityType,
                EntityId: record.Id,
                ActorUserId: null,
                ActorName: Author,
                Headline: record.Name),
            new[] { recipientId }, ct);

        // 0 is not a failure: the person switched these off in the app.
        return written > 0
            ? $"Notified {context.NameOf(recipient)}"
            : $"Prepared a message for {context.NameOf(recipient)} (they have these switched off in the app, so only email or WhatsApp — if they chose them — will carry it)";
    }

    private async Task<string> UpdateFieldAsync(
        AutomationRule rule, AutomationAction a, RecordSnapshot record,
        RunContext context, List<Func<Task>> after, CancellationToken ct)
    {
        var field = a.Field ?? string.Empty;
        var label = context.LabelOf(record.EntityType, field);

        // ── Owner ───────────────────────────────────────────────────
        if (string.Equals(field, AutomationFields.Owner, StringComparison.OrdinalIgnoreCase))
        {
            string newOwner;

            if (a.Mode == AutomationUpdateMode.RoundRobin)
            {
                var people = (a.UserIds ?? new List<string>())
                    .Where(context.IsActiveUser)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (people.Count == 0)
                    throw new AutomationProblem("Nobody to take turns — everyone on the list is no longer active.");

                var slot = ((rule.RoundRobinCursor % people.Count) + people.Count) % people.Count;
                newOwner = people[slot];
                rule.RoundRobinCursor = (slot + 1) % people.Count;
            }
            else
            {
                newOwner = a.Value?.Trim() ?? string.Empty;
                if (!context.IsActiveUser(newOwner))
                    throw new AutomationProblem($"The owner wasn't changed — {context.NameOf(newOwner)} is no longer active.");
            }

            if (string.Equals(record.OwnerUserId, newOwner, StringComparison.OrdinalIgnoreCase))
                return $"Owner was already {context.NameOf(newOwner)}";

            var previous = record.OwnerUserId;
            record.SetOwner(newOwner, Author);

            // The new owner hears the same "you were assigned" a person's
            // assignment sends, from "Automation".
            var assigned = record.Lead is { } lead
                ? DealNotifications.LeadAssigned(rule.TenantId, lead.Id, lead.FullName, lead.CompanyName,
                    lead.EstimatedValue, lead.Currency, null, Author)
                : DealNotifications.DealAssigned(rule.TenantId, record.Deal!.Id, record.Deal.Title,
                    context.StageName(record.Deal.Stage), record.Deal.ExpectedValue, record.Deal.Currency, null, Author);

            await _notify.AddForOwnerAsync(assigned, newOwner, ct);

            after.Add(() => _audit.WriteAsync(
                record.Lead is not null ? AuditAction.LeadOwnerChanged : AuditAction.DealUpdated,
                record.Lead is not null ? AuditEntityType.Lead : AuditEntityType.Deal,
                record.Id, rule.TenantId,
                new { owner = new { from = previous, to = newOwner }, by = Author, rule = rule.Name }, ct));

            return a.Mode == AutomationUpdateMode.RoundRobin
                ? $"Gave it to {context.NameOf(newOwner)} (taking turns)"
                : $"Set the owner to {context.NameOf(newOwner)}";
        }

        // ── Lead status ─────────────────────────────────────────────
        if (record.Lead is { } l && string.Equals(field, AutomationFields.Status, StringComparison.OrdinalIgnoreCase))
        {
            var target = context.Statuses.Find(a.Value?.Trim());
            if (target is null || !target.IsActive)
                throw new AutomationProblem("The status wasn't changed — that status no longer exists or has been retired.");
            if (target.IsSystem)
                throw new AutomationProblem($"\"{target.Name}\" is set only when a lead becomes a deal.");
            if (string.Equals(l.Status, target.Key, StringComparison.OrdinalIgnoreCase))
                return $"Status was already {target.Name}";
            if (context.Statuses.IsConverted(l.Status))
                throw new AutomationProblem("The status wasn't changed — the lead has already become a deal.");

            var fromName = context.Statuses.NameOf(l.Status);
            l.Status = target.Key;
            l.UpdatedAtUtc = DateTime.UtcNow;
            l.UpdatedBy = Author;

            // The same audit row a person's status change writes, so the
            // lead's timeline shows it (GetTimelineHandler reads these).
            after.Add(() => _audit.WriteAsync(
                AuditAction.LeadStatusChanged, AuditEntityType.Lead, l.Id, rule.TenantId,
                new { from = fromName, to = target.Name, by = Author, rule = rule.Name }, ct));
            after.Add(() => _scoring.RecalculateAsync(l.Id, rule.TenantId, ct));

            return $"Set the status to {target.Name}";
        }

        // ── Deal probability ────────────────────────────────────────
        if (record.Deal is { } d && string.Equals(field, AutomationFields.Probability, StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(a.Value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) || p is < 0 or > 100)
                throw new AutomationProblem("The probability wasn't changed — it must be a whole number from 0 to 100.");
            if (d.Probability == p) return $"Probability was already {p}%";

            d.Probability = p;
            d.UpdatedAtUtc = DateTime.UtcNow;
            d.UpdatedBy = Author;
            return $"Set the probability to {p}%";
        }

        // ── A custom field ──────────────────────────────────────────
        if (AutomationFields.CustomId(field) is { } definitionId)
        {
            var kind = context.KindOf(record.EntityType, field);
            var value = a.Value?.Trim();
            if (string.IsNullOrEmpty(value) || (kind == AutomationFieldKind.Checkbox && value == "false"))
                value = null;

            // The same writer the forms use, so its checks (a dropdown
            // choice that still exists, a number within its places) apply
            // to a rule exactly as to a person. It throws a sentence.
            await CustomFieldValueWriter.ApplyAsync(
                _db, rule.TenantId, record.EntityType, record.Id,
                new Dictionary<Guid, string?> { [definitionId] = value },
                Author, isNew: false, ct);

            record.Touch(Author);

            return value is null
                ? $"Cleared {label}"
                : $"Set {label} to {context.ValueLabel(record.EntityType, field, value)}";
        }

        throw new AutomationProblem($"\"{label}\" can't be set by a rule.");
    }

    private static string? Cut(string? s, int max)
        => s is null ? null : (s.Length <= max ? s : s[..(max - 1)] + "…");
}

/// <summary>A failure written for the rule log. Never leaves the engine.</summary>
internal sealed class AutomationProblem : Exception
{
    public AutomationProblem(string message) : base(message) { }
}

// =====================================================================
// THE RECORD A RULE IS LOOKING AT
// =====================================================================

internal sealed class RecordSnapshot
{
    public string EntityType { get; private init; } = string.Empty;
    public Guid Id { get; private init; }
    public string Name { get; private init; } = string.Empty;
    public Lead? Lead { get; private init; }
    public Deal? Deal { get; private init; }

    /// <summary>Field key → value in wire form, read once when the rule starts.</summary>
    public Dictionary<string, string?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? OwnerUserId => Lead?.OwnerUserId ?? Deal?.OwnerUserId;

    public void SetOwner(string userId, string by)
    {
        if (Lead is not null) Lead.OwnerUserId = userId;
        if (Deal is not null) Deal.OwnerUserId = userId;
        Values[AutomationFields.Owner] = userId;
        Touch(by);
    }

    public void Touch(string by)
    {
        if (Lead is not null) { Lead.UpdatedAtUtc = DateTime.UtcNow; Lead.UpdatedBy = by; }
        if (Deal is not null) { Deal.UpdatedAtUtc = DateTime.UtcNow; Deal.UpdatedBy = by; }
    }

    /// <summary>
    /// Loaded TRACKED: a rule may change it, and the caller has already
    /// saved, so the instance is the caller's own (same context) or a fresh
    /// one. IgnoreQueryFilters + an explicit TenantId — see the header.
    /// </summary>
    public static async Task<RecordSnapshot?> LoadAsync(
        FlowDbContext db, Guid tenantId, string entityType, Guid id, RunContext context, CancellationToken ct)
    {
        RecordSnapshot snap;

        if (entityType == AutomationEntity.Lead)
        {
            var lead = await db.Leads.IgnoreQueryFilters()
                .FirstOrDefaultAsync(l => l.Id == id && l.TenantId == tenantId && !l.IsDeleted, ct);
            if (lead is null) return null;

            snap = new RecordSnapshot { EntityType = entityType, Id = id, Name = lead.FullName, Lead = lead };
            snap.Values[AutomationFields.Status]  = lead.Status;
            snap.Values[AutomationFields.Owner]   = lead.OwnerUserId;
            snap.Values[AutomationFields.Value]   = AutomationEngine.Wire(lead.EstimatedValue);
            snap.Values[AutomationFields.Source]  = lead.SourceId?.ToString("D");
            snap.Values[AutomationFields.Company] = lead.CompanyName;
        }
        else
        {
            var deal = await db.Deals.IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId && !d.IsDeleted, ct);
            if (deal is null) return null;

            snap = new RecordSnapshot { EntityType = entityType, Id = id, Name = deal.Title, Deal = deal };
            snap.Values[AutomationFields.Stage]       = deal.Stage;
            snap.Values[AutomationFields.Owner]       = deal.OwnerUserId;
            snap.Values[AutomationFields.Value]       = AutomationEngine.Wire(deal.ExpectedValue);
            snap.Values[AutomationFields.Probability] = deal.Probability.ToString(CultureInfo.InvariantCulture);
        }

        // Custom fields only when some rule could ask about one.
        if (context.HasCustomFields(entityType))
        {
            var custom = await CustomFieldValueWriter.ReadAsync(db, tenantId, entityType, id, ct);
            foreach (var (definitionId, wire) in custom)
                snap.Values[AutomationFields.CustomKey(definitionId)] = wire;
        }

        return snap;
    }
}

// =====================================================================
// WHAT A RUN NEEDS TO KNOW ABOUT THE WORKSPACE — loaded once per run
// =====================================================================

internal sealed class RunContext
{
    public TenantLeadStatuses Statuses { get; private init; } = null!;
    private TenantStages _stages = null!;
    private AutomationOptionsDto _options = null!;
    private Dictionary<string, string> _users = new(StringComparer.OrdinalIgnoreCase);
    private TimeZoneInfo _zone = TimeZoneInfo.Utc;

    public static async Task<RunContext> LoadAsync(
        FlowDbContext db, ILeadStatusResolver statuses, IStageResolver stages,
        Guid tenantId, AutomationOptionsDto options, CancellationToken ct)
    {
        // 103 — the same zone rule as the pages (WorkspaceTime): the
        // workspace's zone, else its country's, else Asia/Kolkata. It used
        // to fall back to UTC, so a workspace with only a country set got
        // "due today 18:00" in London time.
        var zone = await WorkspaceTime.ForTenantAsync(db, tenantId, ct);

        return new RunContext
        {
            Statuses = await statuses.GetAsync(tenantId, ct),
            _stages = await stages.GetAsync(tenantId, ct),
            _options = options,
            _users = options.Users.ToDictionary(u => u.Value, u => u.Label, StringComparer.OrdinalIgnoreCase),
            _zone = zone
        };
    }

    public bool HasCustomFields(string entityType)
        => _options.Fields.TryGetValue(entityType, out var f) && f.Any(x => AutomationFields.CustomId(x.Key) is not null);

    public bool IsActiveUser(string? userId) => userId is not null && _users.ContainsKey(userId);

    public string NameOf(string? userId)
        => userId is not null && _users.TryGetValue(userId, out var n) ? n : "someone no longer here";

    public string StageName(string? key) => _stages.NameOf(key);

    private AutomationFieldDto? Field(string entityType, string key)
        => _options.Fields.TryGetValue(entityType, out var list)
            ? list.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase))
            : null;

    public string LabelOf(string entityType, string key) => Field(entityType, key)?.Label ?? "a field that no longer exists";

    public string KindOf(string entityType, string key) => Field(entityType, key)?.Kind ?? AutomationFieldKind.Text;

    public string ValueLabel(string entityType, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "nothing";
        var f = Field(entityType, key);
        if (f is null) return value;
        if (f.Kind == AutomationFieldKind.User) return NameOf(value);
        if (f.Kind == AutomationFieldKind.Checkbox) return value == "true" ? "ticked" : "not ticked";
        var opt = (f.Options ?? new()).Concat(f.SettableOptions ?? new())
            .FirstOrDefault(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase));
        return opt?.Label ?? value;
    }

    /// <summary>Who a task or message goes to. Null when nobody active fits.</summary>
    public string? Person(string? who, string? userId, string? ownerUserId, string? actorUserId)
    {
        var id = who switch
        {
            AutomationPerson.Owner => ownerUserId,
            AutomationPerson.Actor => actorUserId,
            AutomationPerson.User  => userId,
            _ => null
        };
        return IsActiveUser(id) ? id!.Trim() : null;
    }

    /// <summary>
    /// When a rule's task is due, in the WORKSPACE's time: N days on at
    /// 10:00. "Due today" means 18:00 today — or an hour from now, if the
    /// working day is over — so a task due today is never born overdue.
    /// </summary>
    public DateTime DueUtc(int dueInDays, DateTime nowUtc)
    {
        var days = Math.Clamp(dueInDays, 0, 365);
        var localNow = WorkspaceTime.ToLocal(nowUtc, _zone);

        var local = days == 0
            ? localNow.Date.AddHours(18)
            : localNow.Date.AddDays(days).AddHours(10);

        // 103 — daylight saving: a skipped 10:00 moves forward by the jump,
        // a doubled one is the first. ConvertTimeToUtc threw on the first.
        var utc = WorkspaceTime.ToUtc(local, _zone);
        return utc <= nowUtc ? nowUtc.AddHours(1) : utc;
    }

    /// <summary>
    /// {name} {owner} {status} {stage} {value} {rule} in a task title,
    /// description or message, filled from the record.
    /// </summary>
    public string? Fill(string? template, RecordSnapshot record, AutomationRule rule)
    {
        if (string.IsNullOrWhiteSpace(template)) return template;

        var currency = record.Lead?.Currency ?? record.Deal?.Currency;
        var value = record.Lead?.EstimatedValue ?? record.Deal?.ExpectedValue;

        return template
            .Replace("{name}", record.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{owner}", record.OwnerUserId is null ? "unassigned" : NameOf(record.OwnerUserId), StringComparison.OrdinalIgnoreCase)
            .Replace("{status}", record.Lead is { } l ? Statuses.NameOf(l.Status) : "", StringComparison.OrdinalIgnoreCase)
            .Replace("{stage}", record.Deal is { } d ? _stages.NameOf(d.Stage) : "", StringComparison.OrdinalIgnoreCase)
            .Replace("{value}", value is > 0 ? $"{currency} {value.Value:N0}".Trim() : "no value", StringComparison.OrdinalIgnoreCase)
            .Replace("{rule}", rule.Name, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    /// <summary>
    /// Does one "If" line hold? When it does not, <paramref name="why"/> is
    /// the sentence for the log: what the record has, and what the rule
    /// wanted.
    /// </summary>
    public bool Holds(AutomationCondition c, RecordSnapshot record, out string why)
    {
        var field = Field(record.EntityType, c.Field);
        if (field is null)
        {
            why = "Skipped: one of the rule's conditions points at a field that no longer exists. Open the rule and fix it.";
            return false;
        }

        record.Values.TryGetValue(field.Key, out var actual);
        var has = !string.IsNullOrWhiteSpace(actual)
                  && !(field.Kind == AutomationFieldKind.Checkbox && actual != "true");

        bool ok;
        switch (c.Op)
        {
            case AutomationOp.IsEmpty:  ok = !has; break;
            case AutomationOp.NotEmpty: ok = has;  break;

            case AutomationOp.Is:
            case AutomationOp.IsNot:
            {
                bool same;
                if (field.Kind == AutomationFieldKind.Number && has
                    && decimal.TryParse(actual, NumberStyles.Number, CultureInfo.InvariantCulture, out var a1)
                    && decimal.TryParse(c.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var b1))
                    same = a1 == b1;
                else
                    same = string.Equals(actual?.Trim(), c.Value?.Trim(), StringComparison.OrdinalIgnoreCase);
                ok = c.Op == AutomationOp.Is ? same : !same;
                break;
            }

            case AutomationOp.Contains:
                ok = has && c.Value is not null
                     && actual!.Contains(c.Value.Trim(), StringComparison.OrdinalIgnoreCase);
                break;

            case AutomationOp.Greater:
            case AutomationOp.AtLeast:
            case AutomationOp.Less:
            case AutomationOp.AtMost:
            {
                int? cmp = null;
                if (has && field.Kind == AutomationFieldKind.Number
                    && decimal.TryParse(actual, NumberStyles.Number, CultureInfo.InvariantCulture, out var a2)
                    && decimal.TryParse(c.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var b2))
                    cmp = a2.CompareTo(b2);
                else if (has && field.Kind == AutomationFieldKind.Date && c.Value is not null)
                    cmp = string.CompareOrdinal(actual, c.Value.Trim());     // yyyy-MM-dd sorts as text

                ok = cmp is { } x && c.Op switch
                {
                    AutomationOp.Greater => x > 0,
                    AutomationOp.AtLeast => x >= 0,
                    AutomationOp.Less    => x < 0,
                    _                    => x <= 0
                };
                break;
            }

            default:
                ok = false;
                break;
        }

        if (ok) { why = string.Empty; return true; }

        var wanted = AutomationOp.NeedsValue(c.Op)
            ? $"{AutomationOp.Words(c.Op, field.Kind)} {ValueLabel(record.EntityType, field.Key, c.Value)}"
            : AutomationOp.Words(c.Op, field.Kind);

        why = $"Skipped: {field.Label} is {(has ? ValueLabel(record.EntityType, field.Key, actual) : "empty")} — the rule only runs when {field.Label} {wanted}.";
        return false;
    }
}
