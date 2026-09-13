// =====================================================================
// ActivityDto.cs
// Location: MerkaiTrial.Application/DTOs/ActivityDto.cs
//
// COMPLETE FILE — replaces the existing one.
//
// NEW IN THIS PASS (all additive — existing callers still compile)
//   • AssigneeDto           — the "assign to" dropdown
//   • SetActivityOutcomeDto — "how did it go?", added after completing
//   • ReassignActivityDto   — reassign from the Tasks page
// =====================================================================

namespace MerkaiTrial.Application.DTOs
{
    public record ActivityDto(
        Guid      Id,
        Guid      TenantId,
        string    EntityType,
        Guid      EntityId,
        string    ActivityType,
        string    Subject,
        string?   Description,
        int?      Duration,          // minutes
        DateTime  ActivityDate,      // UTC — convert in page model
        bool      IsTask,
        DateTime? DueDate,           // UTC — tasks only
        bool      IsCompleted,
        DateTime? CompletedAtUtc,
        string?   Outcome,
        string?   AssignedToUserId,
        string?   AssignedToName,
        DateTime  CreatedAtUtc,
        string?   CreatedBy,         // user id (older rows: a name or "seed")
        string?   EntityName    = null,
        string?   CreatedByName = null
    );

    /// <summary>A user a task can be assigned to. Active users only.</summary>
    public record AssigneeDto(
        string UserId,
        string Name,
        string Email
    );

    public record CreateActivityDto(
        Guid      TenantId,          // overwritten by the controller
        string    EntityType,        // ActivityEntityType.*
        Guid      EntityId,
        string    ActivityType,      // ActivityType.Loggable or .Schedulable
        string    Subject,
        string?   Description,
        int?      Duration,
        DateTime? ActivityDate,      // UTC; null = now. Logs only.
        bool      IsTask           = false,
        DateTime? DueDate          = null,   // UTC; required when IsTask
        string?   AssignedToUserId = null,   // null = the creator
        string?   CreatedBy        = null    // overwritten by the controller
    );

    public record UpdateActivityDto(
        Guid      TenantId,          // overwritten by the controller
        Guid      ActivityId,
        string    Subject,
        string?   Description,
        int?      Duration,
        DateTime  ActivityDate,      // UTC; logs, or correcting when a task was done
        DateTime? DueDate,           // UTC; required for tasks, ignored for logs
        string?   AssignedToUserId,  // null = keep current assignee
        string?   Outcome,
        string?   UpdatedBy,         // overwritten by the controller
        string?   ActivityType = null // null = keep current type
    );

    public record CompleteActivityDto(
        Guid    TenantId,            // overwritten by the controller
        Guid    ActivityId,
        string? Outcome,             // usually null — one-click completion
        string? CompletedBy          // overwritten by the controller
    );

    /// <summary>
    /// Adds "how did it go?" to something already done. Kept separate from
    /// Complete so completing stays a single click.
    /// </summary>
    public record SetActivityOutcomeDto(
        Guid    TenantId,            // overwritten by the controller
        Guid    ActivityId,
        string? Outcome,
        string? UpdatedBy            // overwritten by the controller
    );

    /// <summary>Hand a task to someone else without opening the record.</summary>
    public record ReassignActivityDto(
        Guid    TenantId,            // overwritten by the controller
        Guid    ActivityId,
        string  AssignedToUserId,
        string? UpdatedBy            // overwritten by the controller
    );

    public record GetActivitiesQuery(
        Guid   TenantId,
        string EntityType,
        Guid   EntityId,
        bool   IncludeCompleted = true,
        bool   TasksOnly        = false
    );

    public record GetUpcomingTasksQuery(
        Guid    TenantId,
        string? AssignedToUserId = null,  // null = all users (tenant admins only)
        int     DaysAhead        = 7
    );
}
