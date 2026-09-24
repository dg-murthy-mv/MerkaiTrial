// =====================================================================
// LeadStatusService.cs
// Location: MerkaiTrial.Admin.Web/Services/Leads/LeadStatusService.cs
//
// COMPLETE FILE — replaces the existing one.
//
// CHANGES (025)
//   ✅ MoveLeadsAsync — returns the result rather than void, because the
//      page has something worth saying afterwards ("14 leads moved to
//      Working. Cold is retired."). IApiService turns a 400 with
//      { "error": "..." } into an InvalidOperationException carrying that
//      message, so every refusal the handler writes reaches the page as a
//      sentence a tenant admin can act on.
//   ✅ A `detail` flag on GetAsync, for the settings page only.
//
// ALREADY REGISTERED. ILeadStatusService is hand-registered in
// Startup/AdminWebServiceRegistration.cs; adding a method to an existing
// interface needs no registration change.
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
    /// <param name="detail">
    /// True only from the lead status settings page. Adds the TRUE
    /// (unscoped) lead counts and the reasons a status cannot be deleted
    /// or retired, at the cost of an extra query. Every other caller
    /// reads this for a dropdown and leaves it false.
    /// </param>
    Task<List<LeadStatusDto>> GetAsync(
        bool selectableOnly = false, bool detail = false, CancellationToken ct = default);

    Task<LeadStatusDto> CreateAsync(CreateLeadStatusDto dto, CancellationToken ct = default);
    Task UpdateAsync(UpdateLeadStatusDefDto dto, CancellationToken ct = default);
    Task ReorderAsync(ReorderLeadStatusesDto dto, CancellationToken ct = default);
    Task SetDefaultAsync(Guid statusId, CancellationToken ct = default);

    /// <summary>
    /// Sends every lead in <paramref name="dto"/>.FromStatusId to another
    /// status of the same kind, optionally retiring the source afterwards.
    /// </summary>
    Task<MoveStatusLeadsResult> MoveLeadsAsync(MoveStatusLeadsDto dto, CancellationToken ct = default);

    Task DeleteAsync(Guid statusId, CancellationToken ct = default);
}

public class LeadStatusService : ILeadStatusService
{
    private readonly IApiService _api;

    public LeadStatusService(IApiService api) => _api = api;

    public async Task<List<LeadStatusDto>> GetAsync(
        bool selectableOnly = false, bool detail = false, CancellationToken ct = default)
        => await _api.GetAsync<List<LeadStatusDto>>(
            $"api/lead-statuses?selectableOnly={selectableOnly}&detail={detail}");

    public async Task<LeadStatusDto> CreateAsync(CreateLeadStatusDto dto, CancellationToken ct = default)
        => await _api.PostAsync<LeadStatusDto>("api/lead-statuses", dto);

    public async Task UpdateAsync(UpdateLeadStatusDefDto dto, CancellationToken ct = default)
        => await _api.PutVoidAsync($"api/lead-statuses/{dto.StatusId}", dto);

    public async Task ReorderAsync(ReorderLeadStatusesDto dto, CancellationToken ct = default)
        => await _api.PostVoidAsync("api/lead-statuses/reorder", dto);

    public async Task SetDefaultAsync(Guid statusId, CancellationToken ct = default)
        => await _api.PostVoidAsync($"api/lead-statuses/{statusId}/default", new { });

    public async Task<MoveStatusLeadsResult> MoveLeadsAsync(
        MoveStatusLeadsDto dto, CancellationToken ct = default)
        => await _api.PostAsync<MoveStatusLeadsResult>(
            $"api/lead-statuses/{dto.FromStatusId}/move-leads", dto);

    public async Task DeleteAsync(Guid statusId, CancellationToken ct = default)
        => await _api.DeleteAsync($"api/lead-statuses/{statusId}");
}
