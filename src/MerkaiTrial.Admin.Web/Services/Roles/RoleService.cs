// =====================================================================
// ROLE SERVICE - Updated with Pagination
// Location: MerkaiTrial.Admin.Web/Services/Roles/RoleService.cs
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Configuration;

namespace MerkaiTrial.Admin.Web.Services.Roles
{
    public interface IRoleService
    {
        Task<PaginatedRolesResponse> GetPaginatedAsync(int page = 1, int pageSize = 25, string? search = null, bool? isSystemRole = null);
        Task<List<RoleListItem>> GetAllAsync();
        Task<RoleDto> GetByIdAsync(Guid roleId);
        Task<RoleDto> CreateAsync(CreateRoleCommand command);
        Task UpdateAsync(UpdateRoleCommand command);
        Task DeleteAsync(Guid roleId);
        Task<List<RoleLookupDto>> GetLookupAsync();
        Task<List<UserLookupDto>> GetRoleUsersAsync(Guid roleId);
        Task<RoleStatsDto> GetStatsAsync();
        List<ModuleDefinition> GetAvailableModules();
    }

    public class RoleService : IRoleService
    {
        private readonly IApiService _api;

        public RoleService(IApiService api)
        {
            _api = api;
        }

        public Task<PaginatedRolesResponse> GetPaginatedAsync(int page = 1, int pageSize = 25, string? search = null, bool? isSystemRole = null)
        {
            var queryParams = new List<string>
            {
                $"page={page}",
                $"pageSize={pageSize}"
            };

            if (!string.IsNullOrWhiteSpace(search))
                queryParams.Add($"search={Uri.EscapeDataString(search)}");

            if (isSystemRole.HasValue)
                queryParams.Add($"isSystemRole={isSystemRole.Value}");

            var queryString = string.Join("&", queryParams);
            return _api.GetAsync<PaginatedRolesResponse>($"api/roles/paginated?{queryString}");
        }

        public Task<List<RoleListItem>> GetAllAsync()
        {
            return _api.GetAsync<List<RoleListItem>>("api/roles");
        }

        public Task<RoleDto> GetByIdAsync(Guid roleId)
        {
            return _api.GetAsync<RoleDto>($"api/roles/{roleId}");
        }

        public Task<RoleDto> CreateAsync(CreateRoleCommand command)
        {
            return _api.PostAsync<RoleDto>("api/roles", command);
        }

        public Task UpdateAsync(UpdateRoleCommand command)
        {
            return _api.PutVoidAsync($"api/roles/{command.RoleId}", command);
        }

        public Task DeleteAsync(Guid roleId)
        {
            return _api.DeleteAsync($"api/roles/{roleId}");
        }

        public Task<List<RoleLookupDto>> GetLookupAsync()
        {
            return _api.GetAsync<List<RoleLookupDto>>("api/roles/lookup");
        }

        public Task<List<UserLookupDto>> GetRoleUsersAsync(Guid roleId)
        {
            return _api.GetAsync<List<UserLookupDto>>($"api/roles/{roleId}/users");
        }

        public Task<RoleStatsDto> GetStatsAsync()
        {
            return _api.GetAsync<RoleStatsDto>("api/roles/stats");
        }

        public List<ModuleDefinition> GetAvailableModules()
        {
            return ModulesConfiguration.GetAllModules();
        }
    }
}
