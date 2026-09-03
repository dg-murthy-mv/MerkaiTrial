// =====================================================================
// ActivityHandlers.cs — UNIFIED Activities + Tasks
// Location: MerkaiTrial.Application/Commands/Activities/ActivityHandlers.cs
//
// WHY UNIFIED:
//   LeadActivity, DealActivity, ContactActivity would be triple the code.
//   This single handler works for ALL entities via EntityType + EntityId.
//   The existing LeadActivityHandlers.cs / LeadReminderHandlers.cs can be
//   DEPRECATED and their calls forwarded here (migration path below).
//
// ENTITY TYPES (string constants — use ActivityEntityType class below):
//   "Lead", "Deal", "Contact", "Company"
//
// ACTIVITY TYPES:
//   "Call", "Email", "Meeting", "SMS", "WhatsApp", "Task", "Note", "Demo"
//
// TASK vs ACTIVITY:
//   IsTask = false → Activity (completed action logged in past/present)
//   IsTask = true  → Task (future action to be done, has DueDate)
//   This single entity covers both — no separate Task table needed.
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Activities
{
    // ── Entity type constants — use these everywhere, never raw strings ──
    public static class ActivityEntityType
    {
        public const string Lead    = "Lead";
        public const string Deal    = "Deal";
        public const string Contact = "Contact";
        public const string Company = "Company";
    }

    // ── Activity type constants ──────────────────────────────────────────
    public static class ActivityType
    {
        public const string Call     = "Call";
        public const string Email    = "Email";
        public const string Meeting  = "Meeting";
        public const string SMS      = "SMS";
        public const string WhatsApp = "WhatsApp";
        public const string Task     = "Task";
        public const string Note     = "Note";
        public const string Demo     = "Demo";
    }

   

    // =====================================================================
    // CREATE ACTIVITY / TASK
    // =====================================================================

    public class CreateActivityHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentTenantService _tenantService;
        private readonly ILogger<CreateActivityHandler> _logger;

        public CreateActivityHandler(
            FlowDbContext db,
            ICurrentTenantService tenantService,
            ILogger<CreateActivityHandler> logger)
        {
            _db            = db;
            _tenantService = tenantService;
            _logger        = logger;
        }

        public async Task<ActivityDto> Handle(CreateActivityDto dto, CancellationToken ct = default)
        {
            // Validate entity exists + belongs to tenant
            var entityExists = await EntityExistsAsync(dto.TenantId, dto.EntityType, dto.EntityId, ct);
            if (!entityExists)
                throw new KeyNotFoundException($"{dto.EntityType} {dto.EntityId} not found");

            if (dto.IsTask && dto.DueDate == null)
                throw new InvalidOperationException("DueDate is required for tasks");

            var activity = new Activity
            {
                Id           = Guid.NewGuid(),
                TenantId     = dto.TenantId,
                EntityType   = dto.EntityType,
                EntityId     = dto.EntityId,
                ActivityType = dto.ActivityType,
                Subject      = dto.Subject,
                Description  = dto.Description,
                Duration     = dto.Duration,
                ActivityDate = dto.ActivityDate ?? DateTime.UtcNow,
                IsTask       = dto.IsTask,
                DueDate      = dto.DueDate,
                IsCompleted  = false,
                AssignedToUserId = dto.AssignedToUserId ?? _tenantService.GetUserId().ToString(),
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy    = dto.CreatedBy
            };

            _db.Set<Activity>().Add(activity);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Created {Type} {ActivityType} for {EntityType} {EntityId}",
                dto.IsTask ? "task" : "activity", dto.ActivityType, dto.EntityType, dto.EntityId);

            return await ToDto(activity, ct);
        }

        private async Task<bool> EntityExistsAsync(Guid tenantId, string entityType, Guid entityId, CancellationToken ct)
        {
            return entityType switch
            {
                ActivityEntityType.Lead    => await _db.Leads.AnyAsync(l => l.Id == entityId && l.TenantId == tenantId && !l.IsDeleted, ct),
                ActivityEntityType.Deal    => await _db.Deals.AnyAsync(d => d.Id == entityId && d.TenantId == tenantId && !d.IsDeleted, ct),
                ActivityEntityType.Contact => await _db.Contacts.AnyAsync(c => c.Id == entityId && c.TenantId == tenantId && !c.IsDeleted, ct),
                ActivityEntityType.Company => await _db.Companies.AnyAsync(c => c.Id == entityId && c.TenantId == tenantId && !c.IsDeleted, ct),
                _ => throw new InvalidOperationException($"Unknown entity type: {entityType}")
            };
        }

        private async Task<ActivityDto> ToDto(Activity a, CancellationToken ct)
        {
            string? assignedName = null;
            if (Guid.TryParse(a.AssignedToUserId, out var assigneeGuid))
            {
                assignedName = await _db.Users
                    .Where(u => u.Id == assigneeGuid)
                    .Select(u => u.FirstName + " " + u.LastName)
                    .FirstOrDefaultAsync(ct);
            }

            return new ActivityDto(
                a.Id, a.TenantId, a.EntityType, a.EntityId,
                a.ActivityType, a.Subject, a.Description, a.Duration,
                a.ActivityDate, a.IsTask, a.DueDate,
                a.IsCompleted, a.CompletedAtUtc, a.Outcome,
                a.AssignedToUserId, assignedName,
                a.CreatedAtUtc, a.CreatedBy
            );
        }
    }

    // =====================================================================
    // GET ACTIVITIES FOR AN ENTITY (Lead detail, Deal detail, etc.)
    // =====================================================================

    public class GetActivitiesHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetActivitiesHandler> _logger;

        public GetActivitiesHandler(FlowDbContext db, ILogger<GetActivitiesHandler> logger)
        {
            _db     = db;
            _logger = logger;
        }

        public async Task<List<ActivityDto>> Handle(GetActivitiesQuery query, CancellationToken ct = default)
        {
            try
            {
                var q = _db.Set<Activity>()
                    .AsNoTracking()
                    .Where(a =>
                        a.TenantId   == query.TenantId   &&
                        a.EntityType == query.EntityType &&
                        a.EntityId   == query.EntityId   &&
                        !a.IsDeleted);

                if (!query.IncludeCompleted)
                    q = q.Where(a => !a.IsCompleted);

                if (query.TasksOnly)
                    q = q.Where(a => a.IsTask);

                var activities = await q
                    .OrderByDescending(a => a.IsTask ? a.DueDate : a.ActivityDate)
                    .ToListAsync(ct);

                // Resolve assignee names in one query
                var userIds = activities
                    .Where(a => a.AssignedToUserId != null)
                    .Select(a => a.AssignedToUserId!)
                    .Distinct()
                    .Where(id => Guid.TryParse(id, out _))
                    .Select(id => Guid.Parse(id))
                    .ToList();

                var userNames = userIds.Any()
                    ? await _db.Users
                        .Where(u => userIds.Contains(u.Id))
                        .ToDictionaryAsync(u => u.Id.ToString(), u => u.FirstName + " " + u.LastName, ct)
                    : new Dictionary<string, string>();

                return activities.Select(a => new ActivityDto(
                    a.Id, a.TenantId, a.EntityType, a.EntityId,
                    a.ActivityType, a.Subject, a.Description, a.Duration,
                    a.ActivityDate, a.IsTask, a.DueDate,
                    a.IsCompleted, a.CompletedAtUtc, a.Outcome,
                    a.AssignedToUserId,
                    a.AssignedToUserId != null
                        ? userNames.GetValueOrDefault(a.AssignedToUserId)
                        : null,
                    a.CreatedAtUtc, a.CreatedBy
                )).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting activities for {EntityType} {EntityId}",
                    query.EntityType, query.EntityId);
                throw;
            }
        }
    }

    // =====================================================================
    // GET UPCOMING TASKS (Dashboard widget — all entities, all users or one)
    // =====================================================================

    public class GetUpcomingTasksHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetUpcomingTasksHandler> _logger;

        public GetUpcomingTasksHandler(FlowDbContext db, ILogger<GetUpcomingTasksHandler> logger)
        {
            _db     = db;
            _logger = logger;
        }

        public async Task<List<ActivityDto>> Handle(GetUpcomingTasksQuery query, CancellationToken ct = default)
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(query.DaysAhead);

                var q = _db.Set<Activity>()
                    .AsNoTracking()
                    .Where(a =>
                        a.TenantId    == query.TenantId &&
                        a.IsTask                        &&
                        !a.IsCompleted                  &&
                        !a.IsDeleted                    &&
                        a.DueDate     <= cutoff);

                if (!string.IsNullOrWhiteSpace(query.AssignedToUserId))
                    q = q.Where(a => a.AssignedToUserId == query.AssignedToUserId);

                var tasks = await q
                    .OrderBy(a => a.DueDate)
                    .ToListAsync(ct);

                var userIds = tasks
                    .Where(a => a.AssignedToUserId != null)
                    .Select(a => a.AssignedToUserId!)
                    .Distinct()
                    .Where(id => Guid.TryParse(id, out _))
                    .Select(id => Guid.Parse(id))
                    .ToList();

                var userNames = userIds.Any()
                    ? await _db.Users
                        .Where(u => userIds.Contains(u.Id))
                        .ToDictionaryAsync(u => u.Id.ToString(), u => u.FirstName + " " + u.LastName, ct)
                    : new Dictionary<string, string>();

                return tasks.Select(a => new ActivityDto(
                    a.Id, a.TenantId, a.EntityType, a.EntityId,
                    a.ActivityType, a.Subject, a.Description, a.Duration,
                    a.ActivityDate, a.IsTask, a.DueDate,
                    a.IsCompleted, a.CompletedAtUtc, a.Outcome,
                    a.AssignedToUserId,
                    a.AssignedToUserId != null
                        ? userNames.GetValueOrDefault(a.AssignedToUserId)
                        : null,
                    a.CreatedAtUtc, a.CreatedBy
                )).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting upcoming tasks for tenant {TenantId}", query.TenantId);
                throw;
            }
        }
    }

    // =====================================================================
    // COMPLETE ACTIVITY / TASK
    // =====================================================================

    public class CompleteActivityHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<CompleteActivityHandler> _logger;

        public CompleteActivityHandler(FlowDbContext db, ILogger<CompleteActivityHandler> logger)
        {
            _db     = db;
            _logger = logger;
        }

        public async Task Handle(CompleteActivityDto dto, CancellationToken ct = default)
        {
            var activity = await _db.Set<Activity>()
                .FirstOrDefaultAsync(a => a.Id == dto.ActivityId && a.TenantId == dto.TenantId && !a.IsDeleted, ct)
                ?? throw new KeyNotFoundException($"Activity {dto.ActivityId} not found");

            activity.IsCompleted    = true;
            activity.CompletedAtUtc = DateTime.UtcNow;
            activity.Outcome        = dto.Outcome;
            activity.UpdatedAtUtc   = DateTime.UtcNow;
            activity.UpdatedBy      = dto.CompletedBy;

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Completed activity {ActivityId}", dto.ActivityId);
        }
    }

    // =====================================================================
    // UPDATE ACTIVITY
    // =====================================================================

    public class UpdateActivityHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<UpdateActivityHandler> _logger;

        public UpdateActivityHandler(FlowDbContext db, ILogger<UpdateActivityHandler> logger)
        {
            _db     = db;
            _logger = logger;
        }

        public async Task Handle(UpdateActivityDto dto, CancellationToken ct = default)
        {
            var activity = await _db.Set<Activity>()
                .FirstOrDefaultAsync(a => a.Id == dto.ActivityId && a.TenantId == dto.TenantId && !a.IsDeleted, ct)
                ?? throw new KeyNotFoundException($"Activity {dto.ActivityId} not found");

            activity.Subject         = dto.Subject;
            activity.Description     = dto.Description;
            activity.Duration        = dto.Duration;
            activity.ActivityDate    = dto.ActivityDate;
            activity.DueDate         = dto.DueDate;
            activity.AssignedToUserId = dto.AssignedToUserId;
            activity.Outcome         = dto.Outcome;
            activity.UpdatedAtUtc    = DateTime.UtcNow;
            activity.UpdatedBy       = dto.UpdatedBy;

            await _db.SaveChangesAsync(ct);
        }
    }

    // =====================================================================
    // DELETE ACTIVITY
    // =====================================================================

    public class DeleteActivityHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<DeleteActivityHandler> _logger;

        public DeleteActivityHandler(FlowDbContext db, ILogger<DeleteActivityHandler> logger)
        {
            _db     = db;
            _logger = logger;
        }

        public async Task Handle(Guid tenantId, Guid activityId, CancellationToken ct = default)
        {
            var activity = await _db.Set<Activity>()
                .FirstOrDefaultAsync(a => a.Id == activityId && a.TenantId == tenantId && !a.IsDeleted, ct)
                ?? throw new KeyNotFoundException($"Activity {activityId} not found");

            activity.IsDeleted    = true;
            activity.UpdatedAtUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);
        }
    }

    // =====================================================================
    // UNIFIED TIMELINE (replaces GetLeadTimelineHandler — works for all entities)
    // =====================================================================

    public record GetTimelineQuery(Guid TenantId, string EntityType, Guid EntityId);

    public class GetTimelineHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentTenantService _tenantService;
        private readonly ILogger<GetTimelineHandler> _logger;

        public GetTimelineHandler(
            FlowDbContext db,
            ICurrentTenantService tenantService,
            ILogger<GetTimelineHandler> logger)
        {
            _db            = db;
            _tenantService = tenantService;
            _logger        = logger;
        }

        public async Task<List<TimelineItemDto>> Handle(GetTimelineQuery query, CancellationToken ct = default)
        {
            try
            {
                var timeline = new List<TimelineItemDto>();

                // ── Creation event ───────────────────────────────────────
                var creation = await GetCreationEventAsync(query, ct);
                if (creation != null) timeline.Add(creation);

                // ── Activities (unified) ─────────────────────────────────
                var activities = await _db.Set<Activity>()
                    .AsNoTracking()
                    .Where(a =>
                        a.TenantId   == query.TenantId   &&
                        a.EntityType == query.EntityType &&
                        a.EntityId   == query.EntityId   &&
                        !a.IsDeleted)
                    .ToListAsync(ct);

                foreach (var a in activities)
                {
                    timeline.Add(new TimelineItemDto(
                        Id:         a.Id,
                        Type:       a.IsTask ? "Task" : "Activity",
                        Title:      $"{a.ActivityType}: {a.Subject}",
                        Description: a.Description,
                        Date:       a.IsTask ? (a.DueDate ?? a.ActivityDate) : a.ActivityDate,
                        CreatedBy:  a.CreatedBy,
                        Icon:       IconFor(a.ActivityType),
                        BadgeClass: a.IsTask
                            ? (a.IsCompleted ? "bg-secondary" : "bg-warning")
                            : "bg-success"
                    ));
                }

                // ── Lead-specific: notes + reminders (backward compat) ───
                // NOTE: Once fully migrated to unified Activities, remove these
                // and create notes/reminders as Activity records instead.
                if (query.EntityType == ActivityEntityType.Lead)
                {
                    var notes = await _db.Set<LeadNote>()
                        .AsNoTracking()
                        .Where(n => n.LeadId == query.EntityId && n.TenantId == query.TenantId && !n.IsDeleted)
                        .ToListAsync(ct);

                    foreach (var n in notes)
                        timeline.Add(new TimelineItemDto(
                            Id: n.Id, Type: "Note", Title: "Note Added",
                            Description: n.Note?.Length > 100 ? n.Note[..100] + "..." : n.Note,
                            Date: n.CreatedAtUtc, CreatedBy: n.CreatedBy,
                            Icon: "bi-chat-left-text", BadgeClass: "bg-info"
                        ));

                    var reminders = await _db.Set<LeadReminder>()
                        .AsNoTracking()
                        .Where(r => r.LeadId == query.EntityId && r.TenantId == query.TenantId && !r.IsDeleted)
                        .ToListAsync(ct);

                    foreach (var r in reminders)
                        timeline.Add(new TimelineItemDto(
                            Id: r.Id, Type: "Reminder", Title: r.Title,
                            Description: r.Description, Date: r.ReminderDate, CreatedBy: r.CreatedBy,
                            Icon: "bi-bell", BadgeClass: r.IsCompleted ? "bg-secondary" : "bg-warning"
                        ));
                }

                return timeline.OrderByDescending(t => t.Date).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting timeline for {EntityType} {EntityId}",
                    query.EntityType, query.EntityId);
                throw;
            }
        }

        private async Task<TimelineItemDto?> GetCreationEventAsync(GetTimelineQuery query, CancellationToken ct)
        {
            return query.EntityType switch
            {
                ActivityEntityType.Lead => await _db.Leads
                    .AsNoTracking()
                    .Where(l => l.Id == query.EntityId && l.TenantId == query.TenantId && !l.IsDeleted)
                    .Select(l => new TimelineItemDto(l.Id, "Created", "Lead Created",
                        $"Lead '{l.FullName}' was created",
                        l.CreatedAtUtc, l.CreatedBy, "bi-plus-circle", "bg-primary"))
                    .FirstOrDefaultAsync(ct),

                ActivityEntityType.Deal => await _db.Deals
                    .AsNoTracking()
                    .Where(d => d.Id == query.EntityId && d.TenantId == query.TenantId && !d.IsDeleted)
                    .Select(d => new TimelineItemDto(d.Id, "Created", "Deal Created",
                        $"Deal '{d.Title}' was created",
                        d.CreatedAtUtc, d.CreatedBy, "bi-plus-circle", "bg-primary"))
                    .FirstOrDefaultAsync(ct),

                ActivityEntityType.Contact => await _db.Contacts
                    .AsNoTracking()
                    .Where(c => c.Id == query.EntityId && c.TenantId == query.TenantId && !c.IsDeleted)
                    .Select(c => new TimelineItemDto(c.Id, "Created", "Contact Created",
                        $"Contact '{c.FirstName} {c.LastName}' was created",
                        c.CreatedAtUtc, c.CreatedBy, "bi-plus-circle", "bg-primary"))
                    .FirstOrDefaultAsync(ct),

                _ => null
            };
        }

        private static string IconFor(string activityType) => activityType switch
        {
            ActivityType.Call     => "bi-telephone",
            ActivityType.Email    => "bi-envelope",
            ActivityType.Meeting  => "bi-calendar-event",
            ActivityType.SMS      => "bi-chat",
            ActivityType.WhatsApp => "bi-whatsapp",
            ActivityType.Task     => "bi-check-square",
            ActivityType.Note     => "bi-sticky",
            ActivityType.Demo     => "bi-display",
            _                    => "bi-activity"
        };
    }
}
