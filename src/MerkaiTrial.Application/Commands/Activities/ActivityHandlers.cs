// =====================================================================
// ActivityHandlers.cs — UNIFIED Activities + Tasks
// Location: MerkaiTrial.Application/Commands/Activities/ActivityHandlers.cs
//
// COMPLETE FILE — replaces the existing one.
//
// NEW IN THIS PASS
//   • GetAssigneesHandler — the user list for "assign to" dropdowns.
//   • SetActivityOutcomeHandler — adds an outcome AFTER a task is done,
//     so completing stays one click.
//   • ActivityAccessInfo now carries CreatedBy, so the controller can
//     apply "edit your own; someone else's needs tenant admin".
//   • UpdateActivityHandler can reschedule a COMPLETED task (correcting
//     when something actually happened) without un-completing it.
//
// RULES (enforced here, not in the pages):
//   • A LOG (IsTask = false) records something that happened. It is
//     always complete: IsCompleted = true, CompletedAtUtc = ActivityDate.
//   • A TASK (IsTask = true) is something to do. It has a DueDate and is
//     open until completed. "Open work" is exactly IsTask && !IsCompleted.
//   • ActivityType is validated against ActivityType.Loggable or
//     ActivityType.Schedulable. "Task" is a task-only type (a generic
//     to-do) and can't be logged.
//   • Identity (TenantId, CreatedBy, UpdatedBy) is set by the CONTROLLER
//     from the signed-in principal. Handlers never read claims.
//   • CreatedBy / UpdatedBy store the USER ID. Names are resolved when
//     reading (AssignedToName / CreatedByName on ActivityDto).
//   • Any change that alters a lead's engagement recalculates its score.
//   • Every date in and out is UTC. Pages convert user input with
//     ICurrentTenantService.LocalToUtc and display with UtcToLocal.
//
// RECORD VISIBILITY (015 leads, 016 deals)
//   An activity on a LEAD or DEAL is visible when that record is visible to the
//   current user (Own / Team / All) — OR when the activity is a task
//   ASSIGNED to them. Being given a task means you can see and finish that
//   task, even if the lead itself belongs to someone else's pipeline.
//     • Create on a lead outside scope          → 404
//     • Activities tab for a lead outside scope → empty
//     • Complete / outcome / edit / reassign / delete → the controller
//       asks GetActivityAccessHandler first; it returns null ("not
//       found") unless the lead is visible or the task is yours
//     • Timeline for a lead outside scope       → empty
//     • Tasks page: unchanged — it already shows only YOUR tasks to
//       non-admins
//
// 083 — EMAILS ON THE TIMELINE
//   GetTimelineHandler adds the record's received / sent / forwarded email
//   (dbo.InboundEmails), each linking to its email page. A lead shows the
//   emails filed on it; a deal shows its own, its contact's and its
//   source lead's — the same set as the deal's Emails tab. Dismissed and
//   deleted emails are left out.
//
// 096 — THE TASKS PAGE CAN SEARCH
//   GetUpcomingTasksHandler takes a search (the task's subject and notes,
//   or the lead / deal it is on) and a record type. MaxRows is public so
//   the page can say when the 500-task ceiling has cut the list short.
//
// 091 — "A TASK IS ASSIGNED TO ME" NOTIFICATIONS
//   Giving a colleague a task now tells them: in the bell, and by email or
//   WhatsApp if they chose that (Settings → Notifications). Three paths:
//     • CreateActivityHandler   — a task made for someone else
//     • ReassignActivityHandler — "Give to" on the Tasks page
//     • UpdateActivityHandler   — Assign to changed on the edit form
//   All three go through TaskAssignmentNotifier, BEFORE SaveChangesAsync,
//   so the notification commits with the task. Nobody is told about a task
//   they gave themselves, a logged activity, or a task already done.
//   A failure to build the notification is logged and never fails the task.
//   ActivityReadModel.EntityNamesAsync is no longer private, so the
//   notification can name the lead or deal.
//
// 099 — A MERGED LEAD'S HISTORY ON THE KEPT LEAD'S TIMELINE
//   A merge (093) moves the other lead's activities, tasks, notes, files and
//   emails onto the kept lead, so those were already here. What stayed
//   behind, on purpose, is its AUDIT history — when it was added and its
//   status changes — because rewriting an audit row would make it say
//   something that never happened. The timeline now READS that history
//   from the merged lead, labelled "from merged lead <name>", through the
//   LeadMerged audit rows that link the two. Nothing is copied. A lead
//   that was itself the result of earlier merges brings theirs too (up to
//   five levels, fifty leads — far beyond anything real).
//
// GLOBAL QUERY FILTERS: nothing here uses IgnoreQueryFilters. Every query
// also carries an explicit TenantId, which the filter agrees with, so the
// two layers never contradict each other.
// =====================================================================

using DocumentFormat.OpenXml.Presentation;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Notifications;   // 091
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Activities
{
    // ── Record types an activity can hang off ──────────────────────────
    public static class ActivityEntityType
    {
        public const string Lead    = "Lead";
        public const string Deal    = "Deal";
        public const string Contact = "Contact";
        public const string Company = "Company";

        public static readonly IReadOnlySet<string> All =
            new HashSet<string> { Lead, Deal, Contact, Company };

        public static bool IsValid(string? value) => value != null && All.Contains(value);
    }

    // ── Activity types ────────────────────────────────────────────────
    // Behaviour-bearing (WhatsApp/LINE will link to ChannelMessage,
    // Meeting will carry start/end), so these stay in code.
    public static class ActivityType
    {
        public const string Call     = "Call";
        public const string Email    = "Email";
        public const string Meeting  = "Meeting";
        public const string WhatsApp = "WhatsApp";
        public const string Line     = "LINE";
        public const string SMS      = "SMS";
        public const string Task     = "Task";

        // Legacy values that may exist on old rows. Displayed, never created.
        public const string Note = "Note";
        public const string Demo = "Demo";

        /// <summary>Types a rep can LOG (something that happened).</summary>
        public static readonly IReadOnlyList<string> Loggable =
            new[] { Call, Email, Meeting, WhatsApp, Line, SMS };

        /// <summary>Types a rep can SCHEDULE. "Task" is a generic to-do.</summary>
        public static readonly IReadOnlyList<string> Schedulable =
            new[] { Task, Call, Email, Meeting, WhatsApp, Line, SMS };

        public static bool IsValidFor(string? type, bool isTask) =>
            type != null && (isTask ? Schedulable : Loggable).Contains(type);

        public static string Icon(string? type) => type switch
        {
            Call     => "bi-telephone",
            Email    => "bi-envelope",
            Meeting  => "bi-calendar-event",
            WhatsApp => "bi-whatsapp",
            Line     => "bi-line",
            SMS      => "bi-chat",
            Task     => "bi-check-square",
            Note     => "bi-sticky",
            Demo     => "bi-display",
            _        => "bi-activity"
        };
    }

    // =====================================================================
    // SHARED HELPERS
    // =====================================================================

    internal static class ActivityGuards
    {
        public static Task<bool> EntityExistsAsync(
            FlowDbContext db, Guid tenantId, string entityType, Guid entityId, CancellationToken ct)
            => entityType switch
            {
                ActivityEntityType.Lead    => db.Leads.AnyAsync(x => x.Id == entityId && x.TenantId == tenantId && !x.IsDeleted, ct),
                ActivityEntityType.Deal    => db.Deals.AnyAsync(x => x.Id == entityId && x.TenantId == tenantId && !x.IsDeleted, ct),
                ActivityEntityType.Contact => db.Contacts.AnyAsync(x => x.Id == entityId && x.TenantId == tenantId && !x.IsDeleted, ct),
                ActivityEntityType.Company => db.Companies.AnyAsync(x => x.Id == entityId && x.TenantId == tenantId && !x.IsDeleted, ct),
                _ => Task.FromResult(false)
            };

        /// <summary>
        /// Exists AND the current user may see it. Leads and deals go
        /// through record visibility; contacts and companies are not scoped,
        /// so for them this is the plain existence check.
        /// </summary>
        public static async Task<bool> EntityVisibleAsync(
            FlowDbContext db, IRecordScopeService scope,
            Guid tenantId, string entityType, Guid entityId, CancellationToken ct)
            => entityType switch
            {
                ActivityEntityType.Lead => await scope.CanSeeLeadAsync(db, tenantId, entityId, ct),
                ActivityEntityType.Deal => await scope.CanSeeDealAsync(db, tenantId, entityId, ct),
                _ => await EntityExistsAsync(db, tenantId, entityType, entityId, ct)
            };

        /// <summary>
        /// Users has no global filter (login needs it unfiltered), so the
        /// tenant condition here is the ONLY thing scoping it.
        /// </summary>
        public static async Task<bool> IsTenantUserAsync(
            FlowDbContext db, Guid tenantId, string? userId, CancellationToken ct)
        {
            if (!Guid.TryParse(userId, out var id)) return false;
            return await db.Users.AnyAsync(
                u => u.Id == id && u.TenantId == tenantId && !u.IsDeleted && u.IsActive, ct);
        }

        /// <summary>
        /// A deal also shows the history of the lead it came from. Returns
        /// Guid.Empty when the record isn't a deal or has no lead, which
        /// matches no rows when used in a query.
        /// </summary>
        public static async Task<Guid> LinkedLeadIdAsync(
            FlowDbContext db, Guid tenantId, string entityType, Guid entityId, CancellationToken ct)
        {
            if (entityType != ActivityEntityType.Deal) return Guid.Empty;

            return await db.Deals.AsNoTracking()
                .Where(d => d.Id == entityId && d.TenantId == tenantId)
                .Select(d => d.LeadId ?? Guid.Empty)
                .FirstOrDefaultAsync(ct);
        }

        /// <summary>Counts toward lead engagement: logs, and tasks that got done.</summary>
        public static bool AffectsLeadScore(Activity a) =>
            a.EntityType == ActivityEntityType.Lead && (!a.IsTask || a.IsCompleted);
    }

    internal static class ActivityReadModel
    {
        /// <summary>
        /// Maps rows to DTOs, resolving user names and record names in a
        /// fixed number of queries rather than one per row.
        /// </summary>
        public static async Task<List<ActivityDto>> ToDtosAsync(
            FlowDbContext db, Guid tenantId, IReadOnlyCollection<Activity> rows, CancellationToken ct)
        {
            if (rows.Count == 0) return new List<ActivityDto>();

            var userNames = await UserNamesAsync(db, tenantId,
                rows.SelectMany(r => new[] { r.AssignedToUserId, r.CreatedBy }), ct);

            var entityNames = await EntityNamesAsync(db, tenantId, rows, ct);

            return rows.Select(a => new ActivityDto(
                a.Id, a.TenantId, a.EntityType, a.EntityId,
                a.ActivityType, a.Subject, a.Description, a.Duration,
                a.ActivityDate, a.IsTask, a.DueDate,
                a.IsCompleted, a.CompletedAtUtc, a.Outcome,
                a.AssignedToUserId, Lookup(userNames, a.AssignedToUserId),
                a.CreatedAtUtc, a.CreatedBy,
                EntityName:    entityNames.GetValueOrDefault((a.EntityType, a.EntityId)),
                // Old rows hold a name ("Somchai Wiriya") or "seed" instead of
                // an id; fall back to showing whatever was stored.
                CreatedByName: Lookup(userNames, a.CreatedBy) ?? a.CreatedBy
            )).ToList();
        }

        /// <summary>User id (string, any case) → "First Last", this tenant only.</summary>
        public static async Task<Dictionary<string, string>> UserNamesAsync(
            FlowDbContext db, Guid tenantId, IEnumerable<string?> ids, CancellationToken ct)
        {
            var guids = ids
                .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .Distinct()
                .ToList();

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (guids.Count == 0) return result;

            var users = await db.Users.AsNoTracking()
                .Where(u => u.TenantId == tenantId && guids.Contains(u.Id))
                .Select(u => new { u.Id, u.FirstName, u.LastName })
                .ToListAsync(ct);

            foreach (var u in users)
                result[u.Id.ToString()] = $"{u.FirstName} {u.LastName}".Trim();

            return result;
        }

        public static string? Lookup(Dictionary<string, string> names, string? key) =>
            key != null && names.TryGetValue(key, out var name) ? name : null;

        // 091: public (the class is internal) — TaskAssignmentNotifier names the record.
        public static async Task<Dictionary<(string, Guid), string>> EntityNamesAsync(
            FlowDbContext db, Guid tenantId, IReadOnlyCollection<Activity> rows, CancellationToken ct)
        {
            var result = new Dictionary<(string, Guid), string>();

            List<Guid> IdsOf(string type) => rows
                .Where(r => r.EntityType == type)
                .Select(r => r.EntityId)
                .Distinct()
                .ToList();

            // Names are joined in memory: SQL string concatenation with a
            // NULL LastName would return NULL for the whole name.
            var leadIds = IdsOf(ActivityEntityType.Lead);
            if (leadIds.Count > 0)
            {
                var leads = await db.Leads.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && leadIds.Contains(x.Id))
                    .Select(x => new { x.Id, x.FullName })
                    .ToListAsync(ct);
                foreach (var x in leads)
                    result[(ActivityEntityType.Lead, x.Id)] = x.FullName ?? string.Empty;
            }

            var dealIds = IdsOf(ActivityEntityType.Deal);
            if (dealIds.Count > 0)
            {
                var deals = await db.Deals.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && dealIds.Contains(x.Id))
                    .Select(x => new { x.Id, x.Title })
                    .ToListAsync(ct);
                foreach (var x in deals)
                    result[(ActivityEntityType.Deal, x.Id)] = x.Title;
            }

            var contactIds = IdsOf(ActivityEntityType.Contact);
            if (contactIds.Count > 0)
            {
                var contacts = await db.Contacts.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && contactIds.Contains(x.Id))
                    .Select(x => new { x.Id, x.FirstName, x.LastName })
                    .ToListAsync(ct);
                foreach (var x in contacts)
                    result[(ActivityEntityType.Contact, x.Id)] = $"{x.FirstName} {x.LastName}".Trim();
            }

            var companyIds = IdsOf(ActivityEntityType.Company);
            if (companyIds.Count > 0)
            {
                var companies = await db.Companies.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && companyIds.Contains(x.Id))
                    .Select(x => new { x.Id, x.Name })
                    .ToListAsync(ct);
                foreach (var x in companies)
                    result[(ActivityEntityType.Company, x.Id)] = x.Name;
            }

            return result;
        }
    }

    // =====================================================================
    // 091 — TELL THE PERSON A TASK WAS GIVEN TO
    // =====================================================================

    internal static class TaskAssignmentNotifier
    {
        /// <summary>
        /// Adds the "you were given a task" notification for the task's
        /// current assignee. Call it BEFORE SaveChangesAsync. Does nothing
        /// for a logged activity, a completed task, or a task the actor
        /// gave themselves. Never throws — the task matters more than the
        /// notification about it.
        /// </summary>
        public static async Task NotifyAsync(
            FlowDbContext db,
            INotificationDispatcher dispatcher,
            ILogger logger,
            Activity task,
            string? actorUserId,
            CancellationToken ct)
        {
            if (!task.IsTask || task.IsCompleted) return;
            if (string.IsNullOrWhiteSpace(task.AssignedToUserId)) return;
            if (string.Equals(task.AssignedToUserId, actorUserId, StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                var names = await ActivityReadModel.UserNamesAsync(db, task.TenantId, new[] { actorUserId }, ct);
                var records = await ActivityReadModel.EntityNamesAsync(db, task.TenantId, new[] { task }, ct);

                // Tenants is not tenant-filtered (it IS the tenant).
                var timeZone = await db.Tenants.AsNoTracking()
                    .Where(t => t.Id == task.TenantId)
                    .Select(t => t.Timezone)
                    .FirstOrDefaultAsync(ct);

                var request = TaskNotifications.Assigned(
                    tenantId:     task.TenantId,
                    activityId:   task.Id,
                    subject:      task.Subject,
                    activityType: task.ActivityType,
                    recordType:   task.EntityType,
                    recordId:     task.EntityId,
                    recordName:   records.GetValueOrDefault((task.EntityType, task.EntityId)),
                    dueUtc:       task.DueDate,
                    timeZoneId:   timeZone,
                    actorUserId:  Guid.TryParse(actorUserId, out var actor) ? actor : null,
                    actorName:    ActivityReadModel.Lookup(names, actorUserId));

                var written = await dispatcher.AddForOwnerAsync(request, task.AssignedToUserId, ct);

                logger.LogInformation(
                    "Task {ActivityId} assigned to {UserId}: {Count} in-app notification(s) added",
                    task.Id, task.AssignedToUserId, written);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Could not notify {UserId} about task {ActivityId}; the task is saved without it",
                    task.AssignedToUserId, task.Id);
            }
        }
    }

    // =====================================================================
    // ASSIGNEES — who a task can be given to
    // =====================================================================

    public class GetAssigneesHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;

        public GetAssigneesHandler(FlowDbContext db) => _db = db;

        /// <summary>
        /// Active users in this tenant. Deactivated people are excluded:
        /// assigning work to someone who can't sign in creates a task that
        /// is never done and never seen.
        /// </summary>
        public async Task<List<AssigneeDto>> Handle(Guid tenantId, CancellationToken ct = default)
        {
            var users = await _db.Users.AsNoTracking()
                .Where(u => u.TenantId == tenantId && !u.IsDeleted && u.IsActive)
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email })
                .ToListAsync(ct);

            // Name assembled in memory — a NULL LastName would make SQL
            // concatenation return NULL for the whole name.
            return users
                .Select(u => new AssigneeDto(
                    u.Id.ToString(),
                    string.IsNullOrWhiteSpace($"{u.FirstName}{u.LastName}")
                        ? u.Email
                        : $"{u.FirstName} {u.LastName}".Trim(),
                    u.Email))
                .OrderBy(a => a.Name)
                .ToList();
        }
    }

    // =====================================================================
    // CREATE ACTIVITY / TASK
    // =====================================================================

    public class CreateActivityHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILeadScoringService _scoring;
        private readonly ILogger<CreateActivityHandler> _logger;
        private readonly IAuditService _audit;
        private readonly IRecordScopeService _scope;
        private readonly INotificationDispatcher _notifications;   // 091
        public CreateActivityHandler(
            FlowDbContext db,
            ILeadScoringService scoring,
            ILogger<CreateActivityHandler> logger,
            IAuditService audit,
            IRecordScopeService scope,
            INotificationDispatcher notifications)
        {
            _db      = db;
            _notifications = notifications;
            _scope   = scope;
            _scoring = scoring;
            _logger  = logger;
            _audit   = audit;
        }

        public async Task<ActivityDto> Handle(CreateActivityDto dto, CancellationToken ct = default)
        {
            if (!ActivityEntityType.IsValid(dto.EntityType))
                throw new InvalidOperationException($"Unknown record type '{dto.EntityType}'.");

            if (!ActivityType.IsValidFor(dto.ActivityType, dto.IsTask))
                throw new InvalidOperationException(dto.IsTask
                    ? $"'{dto.ActivityType}' can't be scheduled as a task."
                    : $"'{dto.ActivityType}' can't be logged. To plan something for later, create a task.");

            // Deal page's subject is optional; fall back to the type name.
            var subject = string.IsNullOrWhiteSpace(dto.Subject) ? dto.ActivityType : dto.Subject.Trim();
            if (subject.Length > 500)
                throw new InvalidOperationException("Subject can be at most 500 characters.");

            var now = DateTime.UtcNow;

            if (dto.IsTask && dto.DueDate is null)
                throw new InvalidOperationException("A task needs a due date.");

            // 5-minute allowance for clock skew between browser and server.
            if (!dto.IsTask && dto.ActivityDate > now.AddMinutes(5))
                throw new InvalidOperationException(
                    "A logged activity can't be in the future. Create a task instead.");

            if (string.IsNullOrWhiteSpace(dto.CreatedBy))
                throw new InvalidOperationException("CreatedBy must be set by the controller.");

            // Exists AND visible to the current user — a record you can't
            // see is a record that doesn't exist.
            if (!await ActivityGuards.EntityVisibleAsync(_db, _scope, dto.TenantId, dto.EntityType, dto.EntityId, ct))
                throw new KeyNotFoundException($"{dto.EntityType} {dto.EntityId} not found");

            var assignee = string.IsNullOrWhiteSpace(dto.AssignedToUserId)
                ? dto.CreatedBy
                : dto.AssignedToUserId.Trim();

            // Only validate when assigning to SOMEONE ELSE. The caller is
            // already authenticated into this tenant (including via ViewAs).
            if (!string.Equals(assignee, dto.CreatedBy, StringComparison.OrdinalIgnoreCase) &&
                !await ActivityGuards.IsTenantUserAsync(_db, dto.TenantId, assignee, ct))
                throw new InvalidOperationException("The assignee is not an active user in this workspace.");

            var loggedAt = dto.ActivityDate ?? now;

            var activity = new Activity
            {
                Id               = Guid.NewGuid(),
                TenantId         = dto.TenantId,
                EntityType       = dto.EntityType,
                EntityId         = dto.EntityId,
                ActivityType     = dto.ActivityType,
                Subject          = subject,
                Description      = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim(),
                Duration         = dto.Duration,

                // Log: when it happened. Task: when it was created; completing
                // it moves ActivityDate to the completion time.
                ActivityDate     = dto.IsTask ? now : loggedAt,
                IsTask           = dto.IsTask,
                DueDate          = dto.IsTask ? dto.DueDate : null,
                IsCompleted      = !dto.IsTask,
                CompletedAtUtc   = dto.IsTask ? null : loggedAt,

                AssignedToUserId = assignee,
                CreatedAtUtc     = now,
                CreatedBy        = dto.CreatedBy,
                IsDeleted        = false
            };

            _db.Activities.Add(activity);

            // 091: a task made for someone else tells them — in the same save.
            await TaskAssignmentNotifier.NotifyAsync(_db, _notifications, _logger, activity, dto.CreatedBy, ct);

            await _db.SaveChangesAsync(ct);
            await _audit.WriteAsync(
            activity.IsTask ? AuditAction.ActivityCreated : AuditAction.ActivityCreated,
            AuditEntityType.Activity, activity.Id, dto.TenantId,
            new
            {
                entityType = activity.EntityType,
                entityId = activity.EntityId,
                subject = activity.Subject,
                isTask = activity.IsTask,
                activityType = activity.ActivityType,
                assignedTo = activity.AssignedToUserId
            },
            ct);

            if (ActivityGuards.AffectsLeadScore(activity))
                await _scoring.RecalculateAsync(activity.EntityId, activity.TenantId, ct);

            _logger.LogInformation(
                "Created {Kind} {ActivityType} {ActivityId} on {EntityType} {EntityId}",
                activity.IsTask ? "task" : "log", activity.ActivityType, activity.Id,
                activity.EntityType, activity.EntityId);

            var dtos = await ActivityReadModel.ToDtosAsync(_db, dto.TenantId, new[] { activity }, ct);
            return dtos[0];
        }
    }

    // =====================================================================
    // GET ACTIVITIES FOR A RECORD (Lead detail, Deal detail, ...)
    // =====================================================================

    public class GetActivitiesHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly IRecordScopeService _scope;

        public GetActivitiesHandler(FlowDbContext db, IRecordScopeService scope)
        {
            _db = db;
            _scope = scope;
        }

        public async Task<List<ActivityDto>> Handle(GetActivitiesQuery query, CancellationToken ct = default)
        {
            if (!ActivityEntityType.IsValid(query.EntityType)) return new List<ActivityDto>();

            // A lead or deal outside the user's scope has no activity to show.
            // (A deal the user CAN see still shows its source lead's history,
            // even if that lead belongs to someone else — the deal owner needs
            // the story of how it got here.)
            if ((query.EntityType == ActivityEntityType.Lead || query.EntityType == ActivityEntityType.Deal) &&
                !await ActivityGuards.EntityVisibleAsync(_db, _scope, query.TenantId, query.EntityType, query.EntityId, ct))
                return new List<ActivityDto>();

            // A deal shows its own activity AND the history of the lead it
            // came from. Nothing is copied at conversion; the lead's rows are
            // read through Deal.LeadId.
            var linkedLeadId = await ActivityGuards.LinkedLeadIdAsync(
                _db, query.TenantId, query.EntityType, query.EntityId, ct);

            var q = _db.Activities.AsNoTracking().Where(a =>
                a.TenantId == query.TenantId &&
                !a.IsDeleted &&
                ((a.EntityType == query.EntityType && a.EntityId == query.EntityId) ||
                 (a.EntityType == ActivityEntityType.Lead && a.EntityId == linkedLeadId)));

            if (!query.IncludeCompleted) q = q.Where(a => !a.IsCompleted);
            if (query.TasksOnly)         q = q.Where(a => a.IsTask);

            var rows = await q.ToListAsync(ct);

            // Open tasks first, soonest due on top; then history, newest first.
            var ordered = rows
                .OrderByDescending(a => a.IsTask && !a.IsCompleted)
                .ThenBy(a => a.IsTask && !a.IsCompleted ? a.DueDate : null)
                .ThenByDescending(a => a.CompletedAtUtc ?? a.ActivityDate)
                .ToList();

            return await ActivityReadModel.ToDtosAsync(_db, query.TenantId, ordered, ct);
        }
    }

    // =====================================================================
    // GET OPEN TASKS (Tasks page, dashboard) — across all records
    // =====================================================================

    public class GetUpcomingTasksHandler : ICommandHandler
    {
        /// <summary>
        /// The most tasks one read returns. Public (096) so the Tasks page
        /// can say "showing the first 500" when it gets exactly this many,
        /// rather than presenting a cut-off list as the whole of it.
        /// </summary>
        public const int MaxRows = 500;

        private readonly FlowDbContext _db;

        public GetUpcomingTasksHandler(FlowDbContext db) => _db = db;

        public async Task<List<ActivityDto>> Handle(GetUpcomingTasksQuery query, CancellationToken ct = default)
        {
            var cutoff = DateTime.UtcNow.AddDays(Math.Clamp(query.DaysAhead, 0, 365));

            // No lower bound on purpose: overdue tasks are the ones that
            // matter most. Grouping into Overdue / Today / Upcoming happens
            // in the page, in the TENANT's timezone.
            var q = _db.Activities.AsNoTracking().Where(a =>
                a.TenantId == query.TenantId &&
                a.IsTask &&
                !a.IsCompleted &&
                !a.IsDeleted &&
                a.DueDate <= cutoff);

            if (!string.IsNullOrWhiteSpace(query.AssignedToUserId))
                q = q.Where(a => a.AssignedToUserId == query.AssignedToUserId);

            // 096 — one record type (Lead / Deal / …). An unknown value is
            // ignored rather than returning nothing for a typo'd link.
            if (ActivityEntityType.IsValid(query.EntityType))
            {
                var type = query.EntityType!;
                q = q.Where(a => a.EntityType == type);
            }

            // 096 — search the task itself and the NAME of what it is on:
            // "Somchai" finds every task on Somchai's lead. Lead and deal
            // names live in their own tables, so they are id subqueries.
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var s = query.Search.Trim().ToLower();
                var t = query.TenantId;

                var leadIds = _db.Leads
                    .Where(l => l.TenantId == t && !l.IsDeleted && l.FullName.ToLower().Contains(s))
                    .Select(l => l.Id);
                var dealIds = _db.Deals
                    .Where(d => d.TenantId == t && !d.IsDeleted && d.Title.ToLower().Contains(s))
                    .Select(d => d.Id);

                q = q.Where(a =>
                    a.Subject.ToLower().Contains(s) ||
                    (a.Description != null && a.Description.ToLower().Contains(s)) ||
                    (a.EntityType == ActivityEntityType.Lead && leadIds.Contains(a.EntityId)) ||
                    (a.EntityType == ActivityEntityType.Deal && dealIds.Contains(a.EntityId)));
            }

            var rows = await q
                .OrderBy(a => a.DueDate)
                .ThenBy(a => a.Id)            // 096 — the same order on every load
                .Take(MaxRows)
                .ToListAsync(ct);

            return await ActivityReadModel.ToDtosAsync(_db, query.TenantId, rows, ct);
        }
    }

    // =====================================================================
    // ACCESS INFO — what the controller needs to authorise by-id actions
    // =====================================================================

    public record ActivityAccessInfo(
        string EntityType,
        Guid EntityId,
        string? AssignedToUserId,
        string? CreatedBy,
        bool IsTask,
        bool IsCompleted);

    public class GetActivityAccessHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly IRecordScopeService _scope;

        public GetActivityAccessHandler(FlowDbContext db, IRecordScopeService scope)
        {
            _db = db;
            _scope = scope;
        }

        /// <summary>
        /// Every by-id action in ActivitiesController asks this first, so
        /// returning null here is what makes an activity on an invisible
        /// lead "not found" for complete, outcome, edit, reassign and delete.
        ///
        /// Exception: a task assigned to the caller is always theirs to see
        /// and act on — it's on their Tasks page, so it must work from there.
        /// </summary>
        public async Task<ActivityAccessInfo?> Handle(Guid tenantId, Guid activityId, CancellationToken ct = default)
        {
            var info = await _db.Activities.AsNoTracking()
                .Where(a => a.Id == activityId && a.TenantId == tenantId && !a.IsDeleted)
                .Select(a => new ActivityAccessInfo(
                    a.EntityType, a.EntityId, a.AssignedToUserId, a.CreatedBy, a.IsTask, a.IsCompleted))
                .FirstOrDefaultAsync(ct);

            if (info is null) return null;
            if (info.EntityType != ActivityEntityType.Lead && info.EntityType != ActivityEntityType.Deal)
                return info;

            var module = info.EntityType == ActivityEntityType.Lead ? RecordModules.Leads : RecordModules.Deals;
            var access = await _scope.GetAsync(module, ct);
            if (access.SeesAll) return info;

            if (string.Equals(info.AssignedToUserId, access.UserId, StringComparison.OrdinalIgnoreCase))
                return info;

            return await ActivityGuards.EntityVisibleAsync(_db, _scope, tenantId, info.EntityType, info.EntityId, ct)
                ? info : null;
        }
    }

    // =====================================================================
    // COMPLETE TASK
    // =====================================================================

    public class CompleteActivityHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILeadScoringService _scoring;
        private readonly ILogger<CompleteActivityHandler> _logger;

        public CompleteActivityHandler(
            FlowDbContext db,
            ILeadScoringService scoring,
            ILogger<CompleteActivityHandler> logger)
        {
            _db      = db;
            _scoring = scoring;
            _logger  = logger;
        }

        public async Task Handle(CompleteActivityDto dto, CancellationToken ct = default)
        {
            var a = await _db.Activities
                .FirstOrDefaultAsync(x => x.Id == dto.ActivityId && x.TenantId == dto.TenantId && !x.IsDeleted, ct)
                ?? throw new KeyNotFoundException($"Activity {dto.ActivityId} not found");

            if (!a.IsTask)
                throw new InvalidOperationException("Only tasks can be completed. Logged activities already are.");

            // Idempotent: a double-click must not move the completion time.
            if (a.IsCompleted) return;

            var now = DateTime.UtcNow;
            a.IsCompleted    = true;
            a.CompletedAtUtc = now;
            a.ActivityDate   = now;   // "last activity" = when it was actually done
            a.Outcome        = string.IsNullOrWhiteSpace(dto.Outcome) ? a.Outcome : dto.Outcome.Trim();
            a.UpdatedAtUtc   = now;
            a.UpdatedBy      = dto.CompletedBy;

            await _db.SaveChangesAsync(ct);

            if (ActivityGuards.AffectsLeadScore(a))
                await _scoring.RecalculateAsync(a.EntityId, a.TenantId, ct);

            _logger.LogInformation("Completed task {ActivityId}", a.Id);
        }
    }

    // =====================================================================
    // SET OUTCOME — "how did it go?", added after the fact
    //
    // Completing is ONE CLICK so the habit stays cheap. The outcome is
    // added afterwards, only when it's worth the keystrokes. Works on a
    // completed task or a logged activity.
    // =====================================================================

    public class SetActivityOutcomeHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;

        public SetActivityOutcomeHandler(FlowDbContext db) => _db = db;

        public async Task Handle(SetActivityOutcomeDto dto, CancellationToken ct = default)
        {
            var a = await _db.Activities
                .FirstOrDefaultAsync(x => x.Id == dto.ActivityId && x.TenantId == dto.TenantId && !x.IsDeleted, ct)
                ?? throw new KeyNotFoundException($"Activity {dto.ActivityId} not found");

            if (a.IsTask && !a.IsCompleted)
                throw new InvalidOperationException(
                    "Add the outcome once the task is done.");

            var outcome = dto.Outcome?.Trim();
            if (outcome is { Length: > 2000 })
                throw new InvalidOperationException("Outcome can be at most 2000 characters.");

            a.Outcome      = string.IsNullOrWhiteSpace(outcome) ? null : outcome;
            a.UpdatedAtUtc = DateTime.UtcNow;
            a.UpdatedBy    = dto.UpdatedBy;

            await _db.SaveChangesAsync(ct);
        }
    }

    // =====================================================================
    // UPDATE ACTIVITY / TASK  (edit + reschedule)
    // =====================================================================

    public class UpdateActivityHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILeadScoringService _scoring;
        private readonly IAuditService _audit;
        private readonly INotificationDispatcher _notifications;      // 091
        private readonly ILogger<UpdateActivityHandler> _logger;      // 091

        public UpdateActivityHandler(
            FlowDbContext db,
            ILeadScoringService scoring,
            IAuditService audit,
            INotificationDispatcher notifications,
            ILogger<UpdateActivityHandler> logger)
        {
            _db = db;
            _scoring = scoring;
            _audit = audit;
            _notifications = notifications;
            _logger = logger;
        }

        public async Task Handle(UpdateActivityDto dto, CancellationToken ct = default)
        {
            var a = await _db.Activities
                .FirstOrDefaultAsync(x => x.Id == dto.ActivityId && x.TenantId == dto.TenantId && !x.IsDeleted, ct)
                ?? throw new KeyNotFoundException($"Activity {dto.ActivityId} not found");

            var oldSubject = a.Subject;
            var oldDescription = a.Description;
            var oldType = a.ActivityType;
            var oldDue = a.DueDate;
            var oldWhen = a.ActivityDate;
            var oldAssignee = a.AssignedToUserId;
            var oldOutcome = a.Outcome;
            var oldDuration = a.Duration;

            // A log never becomes a task or vice versa — create a new one.
            var type = string.IsNullOrWhiteSpace(dto.ActivityType) ? a.ActivityType : dto.ActivityType;
            if (type != a.ActivityType && !ActivityType.IsValidFor(type, a.IsTask))
                throw new InvalidOperationException($"'{type}' isn't allowed here.");

            var subject = dto.Subject?.Trim();
            if (string.IsNullOrEmpty(subject))
                throw new InvalidOperationException("Subject is required.");
            if (subject.Length > 500)
                throw new InvalidOperationException("Subject can be at most 500 characters.");

            var now = DateTime.UtcNow;

            if (a.IsTask)
            {
                if (dto.DueDate is null)
                    throw new InvalidOperationException("A task needs a due date.");

                // Rescheduling a completed task is allowed — it corrects when
                // something was meant to happen — but it stays completed.
                a.DueDate = dto.DueDate;

                if (a.IsCompleted && dto.ActivityDate <= now.AddMinutes(5))
                {
                    // Correcting when it was actually done.
                    a.ActivityDate   = dto.ActivityDate;
                    a.CompletedAtUtc = dto.ActivityDate;
                }
            }
            else
            {
                if (dto.ActivityDate > now.AddMinutes(5))
                    throw new InvalidOperationException("A logged activity can't be in the future.");
                a.ActivityDate   = dto.ActivityDate;
                a.CompletedAtUtc = dto.ActivityDate;
            }

            // Only reassign when a new assignee is actually sent. Previously
            // a missing value silently wiped the assignee.
            if (!string.IsNullOrWhiteSpace(dto.AssignedToUserId) &&
                !string.Equals(dto.AssignedToUserId, a.AssignedToUserId, StringComparison.OrdinalIgnoreCase))
            {
                if (!await ActivityGuards.IsTenantUserAsync(_db, dto.TenantId, dto.AssignedToUserId, ct))
                    throw new InvalidOperationException("The assignee is not an active user in this workspace.");
                a.AssignedToUserId = dto.AssignedToUserId.Trim();
            }

            a.ActivityType = type;
            a.Subject      = subject;
            a.Description  = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
            a.Duration     = dto.Duration;
            a.Outcome      = string.IsNullOrWhiteSpace(dto.Outcome) ? null : dto.Outcome.Trim();
            a.UpdatedAtUtc = now;
            a.UpdatedBy    = dto.UpdatedBy;

            // 091: handed to someone new on the edit form → tell them.
            if (!string.Equals(oldAssignee, a.AssignedToUserId, StringComparison.OrdinalIgnoreCase))
                await TaskAssignmentNotifier.NotifyAsync(_db, _notifications, _logger, a, dto.UpdatedBy, ct);

            await _db.SaveChangesAsync(ct);

            var changes = new Dictionary<string, object?>();
            if (oldSubject != a.Subject) changes["subject"] = new { from = oldSubject, to = a.Subject };
            if (oldDescription != a.Description) changes["description"] = new { from = oldDescription, to = a.Description };
            if (oldType != a.ActivityType) changes["type"] = new { from = oldType, to = a.ActivityType };
            if (oldDue != a.DueDate) changes["dueDate"] = new { from = oldDue, to = a.DueDate };
            if (oldWhen != a.ActivityDate) changes["when"] = new { from = oldWhen, to = a.ActivityDate };
            if (oldAssignee != a.AssignedToUserId) changes["assignee"] = new { from = oldAssignee, to = a.AssignedToUserId };
            if (oldOutcome != a.Outcome) changes["outcome"] = new { from = oldOutcome, to = a.Outcome };
            if (oldDuration != a.Duration) changes["duration"] = new { from = oldDuration, to = a.Duration };

            if (changes.Count > 0)
            {
                changes["subject_current"] = a.Subject;
                changes["isTask"] = a.IsTask;
                await _audit.WriteAsync(
                    AuditAction.ActivityUpdated, AuditEntityType.Activity, a.Id, dto.TenantId,
                    changes, ct);
            }

            // Editing the date of a logged call moves it in or out of the
            // "today" window the lead stats count.
            if (ActivityGuards.AffectsLeadScore(a))
                await _scoring.RecalculateAsync(a.EntityId, a.TenantId, ct);
        }
    }

    // =====================================================================
    // REASSIGN — used by the Tasks page, without opening the record
    // =====================================================================

    public class ReassignActivityHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<ReassignActivityHandler> _logger;
        private readonly INotificationDispatcher _notifications;      // 091

        public ReassignActivityHandler(
            FlowDbContext db,
            ILogger<ReassignActivityHandler> logger,
            INotificationDispatcher notifications)
        {
            _db = db;
            _logger = logger;
            _notifications = notifications;
        }

        public async Task Handle(ReassignActivityDto dto, CancellationToken ct = default)
        {
            var a = await _db.Activities
                .FirstOrDefaultAsync(x => x.Id == dto.ActivityId && x.TenantId == dto.TenantId && !x.IsDeleted, ct)
                ?? throw new KeyNotFoundException($"Activity {dto.ActivityId} not found");

            if (!a.IsTask)
                throw new InvalidOperationException("Only tasks can be reassigned.");

            if (string.IsNullOrWhiteSpace(dto.AssignedToUserId))
                throw new InvalidOperationException("Choose who this should go to.");

            if (!await ActivityGuards.IsTenantUserAsync(_db, dto.TenantId, dto.AssignedToUserId, ct))
                throw new InvalidOperationException("The assignee is not an active user in this workspace.");

            var previous = a.AssignedToUserId;                  // 091

            a.AssignedToUserId = dto.AssignedToUserId.Trim();
            a.UpdatedAtUtc     = DateTime.UtcNow;
            a.UpdatedBy        = dto.UpdatedBy;

            // 091: "Give to" on the Tasks page → tell the new person. Saving
            // the same person again tells nobody.
            if (!string.Equals(previous, a.AssignedToUserId, StringComparison.OrdinalIgnoreCase))
                await TaskAssignmentNotifier.NotifyAsync(_db, _notifications, _logger, a, dto.UpdatedBy, ct);

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Reassigned task {ActivityId} to {UserId}", a.Id, a.AssignedToUserId);
        }
    }

    // =====================================================================
    // DELETE (soft)
    // =====================================================================

    public class DeleteActivityHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILeadScoringService _scoring;
        private readonly IAuditService _audit;

        public DeleteActivityHandler(FlowDbContext db, ILeadScoringService scoring, IAuditService audit)
        {
            _db      = db;
            _scoring = scoring;
            _audit   = audit;
        }

        public async Task Handle(Guid tenantId, Guid activityId, string? deletedBy, CancellationToken ct = default)
        {
            var a = await _db.Activities
                .FirstOrDefaultAsync(x => x.Id == activityId && x.TenantId == tenantId && !x.IsDeleted, ct)
                ?? throw new KeyNotFoundException($"Activity {activityId} not found");

            var now = DateTime.UtcNow;
            a.IsDeleted    = true;
            a.DeletedAtUtc = now;
            a.UpdatedAtUtc = now;
            a.UpdatedBy    = deletedBy;

            await _db.SaveChangesAsync(ct);
            await _audit.WriteAsync(
                AuditAction.ActivityDeleted, AuditEntityType.Activity, a.Id, tenantId,
                new
                {
                    entityType = a.EntityType,
                    entityId = a.EntityId,
                    subject = a.Subject,
                    isTask = a.IsTask
                },
                ct);


            if (ActivityGuards.AffectsLeadScore(a))
                await _scoring.RecalculateAsync(a.EntityId, a.TenantId, ct);
        }
    }

    // =====================================================================
    // UNIFIED TIMELINE — Lead, Deal, Contact, Company
    //
    // Reads Activities (logs + tasks), notes, and deal stage changes. A
    // deal's timeline includes its source lead's activity and notes,
    // marked "from lead". Lead reminders are gone: after migration 010
    // they are tasks in Activities.
    // =====================================================================

    public record GetTimelineQuery(Guid TenantId, string EntityType, Guid EntityId);

    public class GetTimelineHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly IRecordScopeService _scope;
        private readonly ILogger<GetTimelineHandler> _logger;

        public GetTimelineHandler(FlowDbContext db, IRecordScopeService scope, ILogger<GetTimelineHandler> logger)
        {
            _db = db;
            _scope = scope;
            _logger = logger;
        }

        public async Task<List<TimelineItemDto>> Handle(GetTimelineQuery query, CancellationToken ct = default)
        {
            try
            {
                var items = new List<TimelineItemDto>();

                // Missing record → empty timeline, same as the old lead handler.
                var creation = await CreationEventAsync(query, ct);
                if (creation is null) return items;
                items.Add(creation);

                var linkedLeadId = await ActivityGuards.LinkedLeadIdAsync(
                    _db, query.TenantId, query.EntityType, query.EntityId, ct);

                // ── Activities and tasks ─────────────────────────────────
                var activities = await _db.Activities.AsNoTracking()
                    .Where(a =>
                        a.TenantId == query.TenantId &&
                        !a.IsDeleted &&
                        ((a.EntityType == query.EntityType && a.EntityId == query.EntityId) ||
                         (a.EntityType == ActivityEntityType.Lead && a.EntityId == linkedLeadId)))
                    .ToListAsync(ct);

                // ── Notes ────────────────────────────────────────────────
                var leadIdForNotes = query.EntityType == ActivityEntityType.Lead ? query.EntityId : linkedLeadId;

                var leadNotes = leadIdForNotes == Guid.Empty
                    ? new()
                    : await _db.LeadNotes.AsNoTracking()
                        .Where(n => n.LeadId == leadIdForNotes && n.TenantId == query.TenantId && !n.IsDeleted)
                        .Select(n => new { n.Id, n.Note, n.CreatedAtUtc, n.CreatedBy })
                        .ToListAsync(ct);

                var dealNotes = query.EntityType != ActivityEntityType.Deal
                    ? new()
                    : await _db.DealNotes.AsNoTracking()
                        .Where(n => n.DealId == query.EntityId && n.TenantId == query.TenantId && !n.IsDeleted)
                        .Select(n => new { n.Id, n.Note, n.CreatedAtUtc, n.CreatedBy })
                        .ToListAsync(ct);

                var stageChanges = query.EntityType != ActivityEntityType.Deal
                    ? new()
                    : await _db.DealStageHistory.AsNoTracking()
                        .Where(h => h.DealId == query.EntityId && h.TenantId == query.TenantId)
                        .Select(h => new { h.Id, h.FromStage, h.ToStage, h.ChangedAtUtc, h.ChangedBy })
                        .ToListAsync(ct);

                // ✅ STAGE NAMES — key -> current display name.
                // Only fetched for deals, and only when there is history to
                // render: an extra query on every lead timeline for nothing
                // would be a poor trade.
                var stageNames = stageChanges.Count == 0
                    ? new Dictionary<string, string>()
                    : await _db.PipelineStages.AsNoTracking()
                        .Where(s => s.TenantId == query.TenantId)
                        .ToDictionaryAsync(s => s.Key, s => s.Name, ct);

                // Falls back to the key itself: a stage deleted after its
                // history was written still renders something readable rather
                // than vanishing from the record.
                string StageName(string? key) =>
                    string.IsNullOrEmpty(key) ? "—" : stageNames.GetValueOrDefault(key, key);

                // ✅ AUDIT — lead status history.
                // Lead status changes live in AuditLogs rather than their own
                // table. Deals have DealStageHistory for historical reasons; a
                // second table just for leads would not earn its keep.
                //
                // AuditLogs has NO global query filter (it is in
                // TenantFilterExemptions), so the TenantId condition below is
                // the only thing scoping this. That is deliberate and required.
                var leadIdForStatus = query.EntityType == ActivityEntityType.Lead ? query.EntityId : linkedLeadId;

                var statusChanges = leadIdForStatus == Guid.Empty
                    ? new()
                    : await _db.Set<AuditLog>().AsNoTracking()
                        .Where(l => l.TenantId == query.TenantId &&
                                    l.EntityType == AuditEntityType.Lead &&
                                    l.EntityId == leadIdForStatus &&
                                    l.Action == AuditAction.LeadStatusChanged)
                        .Select(l => new { l.Id, l.Data, l.CreatedAtUtc, l.By })
                        .ToListAsync(ct);

                // ── 099: leads merged into this one, and their history ───
                var merged = leadIdForStatus == Guid.Empty
                    ? new List<(Guid Id, string Name)>()
                    : await MergedLeadsAsync(query.TenantId, leadIdForStatus, ct);
                var mergedIds = merged.Select(m => m.Id).ToList();
                // AuditLog.EntityId is nullable; compared as Guid? either way.
                var mergedIdsN = mergedIds.Select(id => (Guid?)id).ToList();
                var mergedNames = merged.ToDictionary(m => m.Id, m => m.Name);

                var mergedStatus = mergedIds.Count == 0
                    ? new()
                    : await _db.Set<AuditLog>().AsNoTracking()
                        .Where(l => l.TenantId == query.TenantId &&
                                    l.EntityType == AuditEntityType.Lead &&
                                    mergedIdsN.Contains((Guid?)l.EntityId) &&
                                    l.Action == AuditAction.LeadStatusChanged)
                        .Select(l => new { l.Id, EntityId = (Guid?)l.EntityId, l.Data, l.CreatedAtUtc, l.By })
                        .ToListAsync(ct);

                // Deleted by the merge, still there to read: no IsDeleted condition.
                var mergedCreated = mergedIds.Count == 0
                    ? new()
                    : await _db.Leads.AsNoTracking()
                        .Where(l => l.TenantId == query.TenantId && mergedIds.Contains(l.Id))
                        .Select(l => new { l.Id, l.FullName, l.CreatedAtUtc, l.CreatedBy })
                        .ToListAsync(ct);

                // ── 083: emails ──────────────────────────────────────────
                var dealContactId = query.EntityType != ActivityEntityType.Deal
                    ? Guid.Empty
                    : await _db.Deals.AsNoTracking()
                        .Where(d => d.Id == query.EntityId && d.TenantId == query.TenantId)
                        .Select(d => d.ContactId)
                        .FirstOrDefaultAsync(ct);

                var leadIdForEmails = query.EntityType == ActivityEntityType.Lead ? query.EntityId : linkedLeadId;
                var dealIdForEmails = query.EntityType == ActivityEntityType.Deal ? query.EntityId : Guid.Empty;
                var contactIdForEmails = query.EntityType == ActivityEntityType.Contact ? query.EntityId : dealContactId;

                var emails = await _db.InboundEmails.AsNoTracking()
                    .Where(e => e.TenantId == query.TenantId && !e.IsDeleted
                             && e.Status != InboundEmailStatus.Dismissed
                             && ((leadIdForEmails != Guid.Empty && e.LeadId == leadIdForEmails)
                              || (dealIdForEmails != Guid.Empty && e.DealId == dealIdForEmails)
                              || (contactIdForEmails != Guid.Empty && e.ContactId == contactIdForEmails)))
                    .OrderByDescending(e => e.ReceivedAtUtc)
                    .Take(100)
                    .Select(e => new
                    {
                        e.Id, e.Direction, e.FromAddress, e.FromName, e.CounterpartAddress,
                        e.Subject, e.Snippet, e.ReceivedAtUtc, e.ForwardedByUserId, e.IsAutoReply
                    })
                    .ToListAsync(ct);

                // ── Resolve every "who" in one query ─────────────────────
                var names = await ActivityReadModel.UserNamesAsync(_db, query.TenantId,
                    activities.Select(a => a.CreatedBy)
                        .Concat(leadNotes.Select(n => n.CreatedBy))
                        .Concat(dealNotes.Select(n => n.CreatedBy))
                        .Concat(stageChanges.Select(h => h.ChangedBy))
                        .Concat(statusChanges.Select(s => s.By))
                        .Concat(emails.Select(e => e.ForwardedByUserId?.ToString()))    // 083
                        .Concat(mergedStatus.Select(s => s.By))                          // 099
                        .Concat(mergedCreated.Select(c => c.CreatedBy))                  // 099
                        .Append(creation.CreatedBy), ct);

                string? Who(string? stored) => ActivityReadModel.Lookup(names, stored) ?? stored;

                items[0] = creation with { CreatedBy = Who(creation.CreatedBy) };

                var now = DateTime.UtcNow;
                bool FromLinkedLead(string entityType) =>
                    query.EntityType == ActivityEntityType.Deal && entityType == ActivityEntityType.Lead;

                foreach (var a in activities)
                {
                    var suffix = FromLinkedLead(a.EntityType) ? " · from lead" : "";
                    var title = a.ActivityType == ActivityType.Task
                        ? a.Subject
                        : $"{a.ActivityType}: {a.Subject}";

                    if (!a.IsTask)
                    {
                        items.Add(new TimelineItemDto(a.Id, "Activity", title + suffix, a.Description,
                            a.ActivityDate, Who(a.CreatedBy), ActivityType.Icon(a.ActivityType), "bg-success"));
                    }
                    else
                    {
                        var date = a.IsCompleted ? (a.CompletedAtUtc ?? a.ActivityDate) : (a.DueDate ?? a.ActivityDate);
                        var badge = a.IsCompleted ? "bg-secondary"
                                  : a.DueDate < now ? "bg-danger"
                                  : "bg-warning";
                        items.Add(new TimelineItemDto(a.Id, "Task",
                            (a.IsCompleted ? "Done: " : "Task: ") + title + suffix,
                            a.Outcome ?? a.Description, date, Who(a.CreatedBy),
                            ActivityType.Icon(a.ActivityType), badge));
                    }
                }

                var fromLeadNote = query.EntityType == ActivityEntityType.Deal ? " · from lead" : "";
                foreach (var n in leadNotes)
                    items.Add(new TimelineItemDto(n.Id, "Note", "Note added" + fromLeadNote, Truncate(n.Note),
                        n.CreatedAtUtc, Who(n.CreatedBy), "bi-chat-left-text", "bg-info"));

                foreach (var n in dealNotes)
                    items.Add(new TimelineItemDto(n.Id, "Note", "Note added", Truncate(n.Note),
                        n.CreatedAtUtc, Who(n.CreatedBy), "bi-chat-left-text", "bg-info"));

                // ✅ Stage keys resolved to their current names.
                foreach (var h in stageChanges)
                    items.Add(new TimelineItemDto(h.Id, "StageChange",
                        $"Stage: {StageName(h.FromStage)} → {StageName(h.ToStage)}", null,
                        h.ChangedAtUtc, Who(h.ChangedBy), "bi-arrow-right-circle", "bg-primary"));

                // ✅ AUDIT — lead status changes, read out of the Data JSON.
                var fromLeadStatus = query.EntityType == ActivityEntityType.Deal ? " · from lead" : "";

                foreach (var s in statusChanges)
                {
                    var (from, to) = StatusFromTo(s.Data);
                    items.Add(new TimelineItemDto(s.Id, "StatusChange",
                        $"Status: {from ?? "—"} → {to ?? "—"}" + fromLeadStatus, null,
                        s.CreatedAtUtc, Who(s.By), "bi-flag", "bg-info"));
                }

                // 099 — the merged leads' own history, labelled as theirs.
                string FromMerged(Guid id) => $" · from merged lead {mergedNames.GetValueOrDefault(id, "?")}";

                foreach (var c in mergedCreated)
                    items.Add(new TimelineItemDto(c.Id, "Created", "Lead created" + FromMerged(c.Id),
                        "Lead '" + c.FullName + "' was created", c.CreatedAtUtc, Who(c.CreatedBy),
                        "bi-plus-circle", "bg-secondary"));

                foreach (var s in mergedStatus)
                {
                    var (from, to) = StatusFromTo(s.Data);
                    items.Add(new TimelineItemDto(s.Id, "StatusChange",
                        $"Status: {from ?? "—"} → {to ?? "—"}" + FromMerged(s.EntityId ?? Guid.Empty), null,
                        s.CreatedAtUtc, Who(s.By), "bi-flag", "bg-secondary"));
                }

                // 083 — emails, each opening its own page.
                foreach (var e in emails)
                {
                    var name = string.IsNullOrWhiteSpace(e.FromName) ? e.FromAddress : e.FromName;
                    var (title, icon, badge) = e.Direction switch
                    {
                        InboundEmailDirection.SentByUser =>
                            ($"Email sent to {e.CounterpartAddress ?? "a customer"}: {e.Subject}", "bi-send", "bg-primary"),
                        InboundEmailDirection.Forwarded =>
                            ($"Email from {e.CounterpartAddress ?? name} (forwarded): {e.Subject}", "bi-forward", "bg-info"),
                        _ =>
                            ($"Email from {name}: {e.Subject}", "bi-envelope-open", "bg-info")
                    };

                    if (e.IsAutoReply) title = "Auto-reply · " + title;

                    items.Add(new TimelineItemDto(e.Id, "Email", title, e.Snippet, e.ReceivedAtUtc,
                        e.ForwardedByUserId.HasValue ? Who(e.ForwardedByUserId.Value.ToString()) : null,
                        icon, badge, $"/Emails/View/{e.Id}"));
                }

                return items.OrderByDescending(t => t.Date).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting timeline for {EntityType} {EntityId}",
                    query.EntityType, query.EntityId);
                throw;
            }
        }

        private static string? Truncate(string? text) =>
            text is { Length: > 200 } ? text[..200] + "…" : text;

        /// <summary>
        /// The "from" / "to" status NAMES out of a LeadStatusChanged audit
        /// row's Data. A malformed blob must not take the whole timeline down
        /// — the event is shown without the detail.
        /// </summary>
        private static (string? From, string? To) StatusFromTo(string? data)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(data ?? "{}");
                var from = doc.RootElement.TryGetProperty("from", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.String ? f.GetString() : null;
                var to = doc.RootElement.TryGetProperty("to", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString() : null;
                return (from, to);
            }
            catch
            {
                return (null, null);
            }
        }

        /// <summary>
        /// 099. The leads merged into <paramref name="leadId"/> — and into
        /// those, and so on — from the kept side's LeadMerged audit rows
        /// ({ mergedFrom, mergedName }). Bounded: five levels, fifty leads.
        /// </summary>
        private async Task<List<(Guid Id, string Name)>> MergedLeadsAsync(Guid tenantId, Guid leadId, CancellationToken ct)
        {
            var found = new List<(Guid Id, string Name)>();
            var seen = new HashSet<Guid> { leadId };
            var frontier = new List<Guid?> { leadId };

            for (var level = 0; level < 5 && frontier.Count > 0 && found.Count < 50; level++)
            {
                var rows = await _db.Set<AuditLog>().AsNoTracking()
                    .Where(l => l.TenantId == tenantId &&
                                l.EntityType == AuditEntityType.Lead &&
                                l.Action == AuditAction.LeadMerged &&
                                frontier.Contains((Guid?)l.EntityId))
                    .Select(l => l.Data)
                    .ToListAsync(ct);

                var next = new List<Guid?>();
                foreach (var data in rows)
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(data ?? "{}");
                        if (!TryGet(doc.RootElement, "mergedFrom", out var fromEl) ||
                            fromEl.ValueKind != System.Text.Json.JsonValueKind.String ||
                            !Guid.TryParse(fromEl.GetString(), out var mergedId) ||
                            !seen.Add(mergedId))
                            continue;      // the removed lead's own row ({ mergedInto }), or seen already

                        var name = TryGet(doc.RootElement, "mergedName", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String
                            ? n.GetString() ?? "a merged lead"
                            : "a merged lead";

                        found.Add((mergedId, name));
                        next.Add(mergedId);
                        if (found.Count >= 50) break;
                    }
                    catch
                    {
                        // A malformed row is skipped, never fatal.
                    }
                }
                frontier = next;
            }

            return found;
        }

        /// <summary>A property by name, ignoring case — however the audit writer cased it.</summary>
        private static bool TryGet(System.Text.Json.JsonElement obj, string name, out System.Text.Json.JsonElement value)
        {
            if (obj.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var p in obj.EnumerateObject())
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
            value = default;
            return false;
        }

        private async Task<TimelineItemDto?> CreationEventAsync(GetTimelineQuery query, CancellationToken ct)
        {
            var t = query.TenantId;
            var id = query.EntityId;

            // A lead or deal outside the user's scope has no creation event,
            // so the whole timeline comes back empty — same as a missing one.
            var leadAccess = query.EntityType == ActivityEntityType.Lead
                ? await _scope.GetAsync(RecordModules.Leads, ct)
                : null;
            var dealAccess = query.EntityType == ActivityEntityType.Deal
                ? await _scope.GetAsync(RecordModules.Deals, ct)
                : null;

            return query.EntityType switch
            {
                ActivityEntityType.Lead => await _db.Leads.AsNoTracking()
                    .Where(x => x.Id == id && x.TenantId == t && !x.IsDeleted)
                    .VisibleTo(leadAccess!)
                    .Select(x => new TimelineItemDto(
                        x.Id,
                        "Created",
                        "Lead created",
                        "Lead '" + x.FullName + "' was created",
                        x.CreatedAtUtc,
                        x.CreatedBy,
                        "bi-plus-circle",
                        "bg-primary",
                        null // explicitly pass all parameters, including the optional Url
                    ))
                    .FirstOrDefaultAsync(ct),

                ActivityEntityType.Deal => await _db.Deals.AsNoTracking()
                    .Where(x => x.Id == id && x.TenantId == t && !x.IsDeleted)
                    .VisibleTo(dealAccess!)
                    .Select(x => new TimelineItemDto(
                        x.Id,
                        "Created",
                        "Deal created",
                        "Deal '" + x.Title + "' was created",
                        x.CreatedAtUtc,
                        x.CreatedBy,
                        "bi-plus-circle",
                        "bg-primary",
                        null
                    ))
                    .FirstOrDefaultAsync(ct),

                ActivityEntityType.Contact => await _db.Contacts.AsNoTracking()
                    .Where(x => x.Id == id && x.TenantId == t && !x.IsDeleted)
                    .Select(x => new TimelineItemDto(
                        x.Id,
                        "Created",
                        "Contact created",
                        "Contact was created",
                        x.CreatedAtUtc,
                        x.CreatedBy,
                        "bi-plus-circle",
                        "bg-primary",
                        null
                    ))
                    .FirstOrDefaultAsync(ct),

                ActivityEntityType.Company => await _db.Companies.AsNoTracking()
                    .Where(x => x.Id == id && x.TenantId == t && !x.IsDeleted)
                    .Select(x => new TimelineItemDto(
                        x.Id,
                        "Created",
                        "Company created",
                        "Company '" + x.Name + "' was created",
                        x.CreatedAtUtc,
                        x.CreatedBy,
                        "bi-plus-circle",
                        "bg-primary",
                        null
                    ))
                    .FirstOrDefaultAsync(ct),

                _ => null
            };
        }
    }
}
