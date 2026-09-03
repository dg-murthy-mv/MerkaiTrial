// =====================================================================
// IActivityService.cs
// Location: MerkaiTrial.Admin.Web/Services/Activities/IActivityService.cs
//
// Web layer service — follows same pattern as ILeadService.
// Calls the API which routes to ActivityHandlers.
// =====================================================================

using MerkaiTrial.Application.Commands.Activities;
using MerkaiTrial.Application.DTOs;
namespace MerkaiTrial.Admin.Web.Services.Activities;

public interface IActivityService
{
    Task<ActivityDto>        CreateAsync(CreateActivityDto dto, CancellationToken ct = default);
    Task<List<ActivityDto>>  GetForEntityAsync(GetActivitiesQuery query, CancellationToken ct = default);
    Task<List<ActivityDto>>  GetUpcomingTasksAsync(GetUpcomingTasksQuery query, CancellationToken ct = default);
    Task                     CompleteAsync(CompleteActivityDto dto, CancellationToken ct = default);
    Task                     UpdateAsync(UpdateActivityDto dto, CancellationToken ct = default);
    Task                     DeleteAsync(Guid tenantId, Guid activityId, CancellationToken ct = default);
}
