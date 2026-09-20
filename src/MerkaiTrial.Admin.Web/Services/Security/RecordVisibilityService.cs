// =====================================================================
// RecordVisibilityService.cs
// Location: MerkaiTrial.Admin.Web/Services/Security/RecordVisibilityService.cs
//
// NEW FILE. Thin wrapper over api/record-visibility — same pattern as
// LeadStatusService. Tenant id is never sent; the API uses the caller's.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.Commands.RecordVisibility;
using MerkaiTrial.Domain.Entities;

namespace MerkaiTrial.Admin.Web.Services.Security;

public interface IRecordVisibilityService
{
    Task<RecordScopeMatrixDto> GetScopesAsync();
    Task SetScopeAsync(Guid roleId, string module, RecordScope scope);

    Task<TeamsOverviewDto> GetTeamsAsync();
    Task<TeamDto> CreateTeamAsync(string name, string? description);
    Task UpdateTeamAsync(Guid teamId, string name, string? description);
    Task DeleteTeamAsync(Guid teamId);
    Task SetUserTeamAsync(Guid userId, Guid? teamId);
    Task SetManagedTeamsAsync(Guid userId, List<Guid> teamIds);
}

public class RecordVisibilityService : IRecordVisibilityService
{
    private const string Base = "api/record-visibility";
    private readonly IApiService _api;

    public RecordVisibilityService(IApiService api) => _api = api;

    public async Task<RecordScopeMatrixDto> GetScopesAsync()
        => await _api.GetAsync<RecordScopeMatrixDto>($"{Base}/scopes");

    public async Task SetScopeAsync(Guid roleId, string module, RecordScope scope)
        => await _api.PutVoidAsync($"{Base}/scopes",
            new SetRecordScopeDto(Guid.Empty, roleId, module, scope));

    public async Task<TeamsOverviewDto> GetTeamsAsync()
        => await _api.GetAsync<TeamsOverviewDto>($"{Base}/teams");

    public async Task<TeamDto> CreateTeamAsync(string name, string? description)
        => await _api.PostAsync<TeamDto>($"{Base}/teams",
            new CreateTeamDto(Guid.Empty, name, description));

    public async Task UpdateTeamAsync(Guid teamId, string name, string? description)
        => await _api.PutVoidAsync($"{Base}/teams/{teamId}",
            new UpdateTeamDto(Guid.Empty, teamId, name, description));

    public async Task DeleteTeamAsync(Guid teamId)
        => await _api.DeleteAsync($"{Base}/teams/{teamId}");

    public async Task SetUserTeamAsync(Guid userId, Guid? teamId)
        => await _api.PutVoidAsync($"{Base}/teams/members",
            new SetUserTeamDto(Guid.Empty, userId, teamId));

    public async Task SetManagedTeamsAsync(Guid userId, List<Guid> teamIds)
        => await _api.PutVoidAsync($"{Base}/teams/managers",
            new SetUserManagedTeamsDto(Guid.Empty, userId, teamIds));
}
