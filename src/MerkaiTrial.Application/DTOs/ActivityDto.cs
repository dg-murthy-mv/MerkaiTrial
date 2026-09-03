using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Application.DTOs
{
    // =====================================================================
    // DTOs
    // =====================================================================

    public record ActivityDto(
        Guid Id,
        Guid TenantId,
        string EntityType,
        Guid EntityId,
        string ActivityType,
        string Subject,
        string? Description,
        int? Duration,          // minutes
        DateTime ActivityDate,      // UTC — convert in page model
        bool IsTask,
        DateTime? DueDate,           // UTC — for tasks only
        bool IsCompleted,
        DateTime? CompletedAtUtc,
        string? Outcome,
        string? AssignedToUserId,
        string? AssignedToName,
        DateTime CreatedAtUtc,
        string? CreatedBy
    );

    public record CreateActivityDto(
        Guid TenantId,
        string EntityType,        // ActivityEntityType.*
        Guid EntityId,
        string ActivityType,      // ActivityType.*
        string Subject,
        string? Description,
        int? Duration,
        DateTime? ActivityDate,      // null = UtcNow
        bool IsTask = false,
        DateTime? DueDate = null,  // required when IsTask = true
        string? AssignedToUserId = null,
        string? CreatedBy = null
    );

    public record UpdateActivityDto(
        Guid TenantId,
        Guid ActivityId,
        string Subject,
        string? Description,
        int? Duration,
        DateTime ActivityDate,
        DateTime? DueDate,
        string? AssignedToUserId,
        string? Outcome,
        string? UpdatedBy
    );

    public record CompleteActivityDto(
        Guid TenantId,
        Guid ActivityId,
        string? Outcome,
        string? CompletedBy
    );

    public record GetActivitiesQuery(
        Guid TenantId,
        string EntityType,
        Guid EntityId,
        bool IncludeCompleted = true,
        bool TasksOnly = false
    );

    public record GetUpcomingTasksQuery(
        Guid TenantId,
        string? AssignedToUserId = null, // null = all users
        int DaysAhead = 7
    );
}
