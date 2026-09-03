// =====================================================================
// ActivityService.cs
// Location: MerkaiTrial.Admin.Web/Services/Activities/ActivityService.cs
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.Commands.Activities;
using MerkaiTrial.Application.DTOs;
namespace MerkaiTrial.Admin.Web.Services.Activities;

public class ActivityService : IActivityService
{
    private readonly IApiService _api;
    private readonly ILogger<ActivityService> _logger;

    public ActivityService(IApiService api, ILogger<ActivityService> logger)
    {
        _api    = api;
        _logger = logger;
    }

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

    public async Task UpdateAsync(UpdateActivityDto dto, CancellationToken ct = default)
        => await _api.PutVoidAsync($"api/activities/{dto.ActivityId}", dto);

    public async Task DeleteAsync(Guid tenantId, Guid activityId, CancellationToken ct = default)
        => await _api.DeleteAsync($"api/activities/{activityId}?tenantId={tenantId}");
}
