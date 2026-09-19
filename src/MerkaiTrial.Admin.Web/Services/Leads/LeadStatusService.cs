// =====================================================================
// LeadStatusService.cs
// Location: MerkaiTrial.Admin.Web/Services/Leads/LeadStatusService.cs
//
// NEW FILE. Same thin-wrapper pattern as PipelineStageService.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.Commands.LeadStatuses;

namespace MerkaiTrial.Admin.Web.Services.Leads;

public interface ILeadStatusService
{
    /// <param name="selectableOnly">
    /// True for dropdowns — excludes retired statuses and the system
    /// Converted status. False for settings and for resolving the status
    /// of an existing lead, which may sit in a retired one.
    /// </param>
    Task<List<LeadStatusDto>> GetAsync(bool selectableOnly = false, CancellationToken ct = default);

    Task<LeadStatusDto> CreateAsync(CreateLeadStatusDto dto, CancellationToken ct = default);
    Task UpdateAsync(UpdateLeadStatusDefDto dto, CancellationToken ct = default);
    Task ReorderAsync(ReorderLeadStatusesDto dto, CancellationToken ct = default);
    Task SetDefaultAsync(Guid statusId, CancellationToken ct = default);
    Task DeleteAsync(Guid statusId, CancellationToken ct = default);
}

public class LeadStatusService : ILeadStatusService
{
    private readonly IApiService _api;

    public LeadStatusService(IApiService api) => _api = api;

    public async Task<List<LeadStatusDto>> GetAsync(bool selectableOnly = false, CancellationToken ct = default)
        => await _api.GetAsync<List<LeadStatusDto>>($"api/lead-statuses?selectableOnly={selectableOnly}");

    public async Task<LeadStatusDto> CreateAsync(CreateLeadStatusDto dto, CancellationToken ct = default)
        => await _api.PostAsync<LeadStatusDto>("api/lead-statuses", dto);

    public async Task UpdateAsync(UpdateLeadStatusDefDto dto, CancellationToken ct = default)
        => await _api.PutVoidAsync($"api/lead-statuses/{dto.StatusId}", dto);

    public async Task ReorderAsync(ReorderLeadStatusesDto dto, CancellationToken ct = default)
        => await _api.PostVoidAsync("api/lead-statuses/reorder", dto);

    public async Task SetDefaultAsync(Guid statusId, CancellationToken ct = default)
        => await _api.PostVoidAsync($"api/lead-statuses/{statusId}/default", new { });

    public async Task DeleteAsync(Guid statusId, CancellationToken ct = default)
        => await _api.DeleteAsync($"api/lead-statuses/{statusId}");
}
