// =====================================================================
// ActivityService.cs  +  IActivityService.cs
// Location: MerkaiTrial.Admin.Web/Services/Activities/
//
// COMPLETE FILE — replaces BOTH existing files (interface and class are
// together here; split them back into two files if you prefer).
//
// NEW: GetAssigneesAsync, SetOutcomeAsync, ReassignAsync.
// tenantId is still sent on the query string for compatibility; the API
// ignores it and uses the signed-in user.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.Activities;

public interface IActivityService
{
    Task<List<AssigneeDto>>  GetAssigneesAsync(CancellationToken ct = default);

    Task<ActivityDto>        CreateAsync(CreateActivityDto dto, CancellationToken ct = default);
    Task<List<ActivityDto>>  GetForEntityAsync(GetActivitiesQuery query, CancellationToken ct = default);
    Task<List<ActivityDto>>  GetUpcomingTasksAsync(GetUpcomingTasksQuery query, CancellationToken ct = default);

    Task CompleteAsync(CompleteActivityDto dto, CancellationToken ct = default);
    Task SetOutcomeAsync(SetActivityOutcomeDto dto, CancellationToken ct = default);
    Task UpdateAsync(UpdateActivityDto dto, CancellationToken ct = default);
    Task ReassignAsync(ReassignActivityDto dto, CancellationToken ct = default);
    Task DeleteAsync(Guid tenantId, Guid activityId, CancellationToken ct = default);
}

public class ActivityService : IActivityService
{
    private readonly IApiService _api;
    private readonly ILogger<ActivityService> _logger;

    public ActivityService(IApiService api, ILogger<ActivityService> logger)
    {
        _api    = api;
        _logger = logger;
    }

    public async Task<List<AssigneeDto>> GetAssigneesAsync(CancellationToken ct = default)
        => await _api.GetAsync<List<AssigneeDto>>("api/activities/assignees");

    public async Task<ActivityDto> CreateAsync(CreateActivityDto dto, CancellationToken ct = default)
        => await _api.PostAsync<ActivityDto>("api/activities", dto);

    public async Task<List<ActivityDto>> GetForEntityAsync(GetActivitiesQuery query, CancellationToken ct = default)
    {
        var url = $"api/activities?tenantId={query.TenantId}" +
                  $"&entityType={query.EntityType}" +
                  $"&entityId={query.EntityId}" +
                  $"&includeCompleted={query.IncludeCompleted}" +
                  $"&tasksOnly={query.TasksOnly}";

        return await _api.GetAsync<List<ActivityDto>>(url);
    }

    public async Task<List<ActivityDto>> GetUpcomingTasksAsync(GetUpcomingTasksQuery query, CancellationToken ct = default)
    {
        var url = $"api/activities/upcoming-tasks?tenantId={query.TenantId}" +
                  $"&daysAhead={query.DaysAhead}" +
                  (query.AssignedToUserId != null ? $"&assignedToUserId={query.AssignedToUserId}" : "");

        return await _api.GetAsync<List<ActivityDto>>(url);
    }

    public async Task CompleteAsync(CompleteActivityDto dto, CancellationToken ct = default)
        => await _api.PostVoidAsync($"api/activities/{dto.ActivityId}/complete", dto);

    public async Task SetOutcomeAsync(SetActivityOutcomeDto dto, CancellationToken ct = default)
        => await _api.PostVoidAsync($"api/activities/{dto.ActivityId}/outcome", dto);

    public async Task UpdateAsync(UpdateActivityDto dto, CancellationToken ct = default)
        => await _api.PutVoidAsync($"api/activities/{dto.ActivityId}", dto);

    public async Task ReassignAsync(ReassignActivityDto dto, CancellationToken ct = default)
        => await _api.PostVoidAsync($"api/activities/{dto.ActivityId}/reassign", dto);

    public async Task DeleteAsync(Guid tenantId, Guid activityId, CancellationToken ct = default)
        => await _api.DeleteAsync($"api/activities/{activityId}?tenantId={tenantId}");
}
