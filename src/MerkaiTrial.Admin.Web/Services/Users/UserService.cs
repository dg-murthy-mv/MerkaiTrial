using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.Users
{
    public interface IUserService
    {
        Task<PaginatedUsersResponse> GetPaginatedAsync(Guid tenantId, int page, int pageSize, string? search);
        Task<UserDto> GetByIdAsync(Guid tenantId, Guid userId);
        Task<UserDto> CreateAsync(CreateUserCommand command);
        Task UpdateAsync(UpdateUserCommand command);
        Task UpdateStatusAsync(UpdateUserStatusCommand command);
        Task DeleteAsync(Guid tenantId, Guid userId);
        Task AssignRolesAsync(AssignUserRolesCommand command);
        Task<List<UserRoleDto>> GetUserRolesAsync(Guid tenantId, Guid userId);
        Task<List<UserLookupDto>> GetLookupAsync(Guid tenantId);
        Task<UserStatsDto> GetStatsAsync(Guid tenantId);
        Task<BulkOperationResult> BulkUpdateStatusAsync(BulkUpdateUserStatusCommand command);

        Task<List<SalesTeamMemberDto>> GetSalesTeamAsync(Guid tenantId);

    }

    public class UserService : IUserService
    {
        private readonly IApiService _api;

        public UserService(IApiService api)
        {
            _api = api;
        }

        public Task<PaginatedUsersResponse> GetPaginatedAsync(
            Guid tenantId, int page, int pageSize, string? search)
        {
            var url = $"api/users/paginated?tenantId={tenantId}&page={page}&pageSize={pageSize}";
            if (!string.IsNullOrWhiteSpace(search))
                url += $"&search={Uri.EscapeDataString(search)}";
            
            return _api.GetAsync<PaginatedUsersResponse>(url);
        }

        public Task<UserDto> GetByIdAsync(Guid tenantId, Guid userId)
        {
            return _api.GetAsync<UserDto>($"api/users/{userId}?tenantId={tenantId}");
        }

        public Task<UserDto> CreateAsync(CreateUserCommand command)
        {
            return _api.PostAsync<UserDto>("api/users", command);
        }

        public Task UpdateAsync(UpdateUserCommand command)
        {
            return _api.PutVoidAsync($"api/users/{command.UserId}", command);
        }

        public Task UpdateStatusAsync(UpdateUserStatusCommand command)
        {
            return _api.PatchVoidAsync($"api/users/{command.UserId}/status", command);
        }

        public Task DeleteAsync(Guid tenantId, Guid userId)
        {
            return _api.DeleteAsync($"api/users/{userId}?tenantId={tenantId}");
        }

        public Task AssignRolesAsync(AssignUserRolesCommand command)
        {
            return _api.PostVoidAsync($"api/users/{command.UserId}/roles", command);
        }

        public Task<List<UserRoleDto>> GetUserRolesAsync(Guid tenantId, Guid userId)
        {
            return _api.GetAsync<List<UserRoleDto>>(
                $"api/users/{userId}/roles?tenantId={tenantId}");
        }

        public Task<List<UserLookupDto>> GetLookupAsync(Guid tenantId)
        {
            return _api.GetAsync<List<UserLookupDto>>(
                $"api/users/lookup?tenantId={tenantId}");
        }

        public Task<UserStatsDto> GetStatsAsync(Guid tenantId)
        {
            return _api.GetAsync<UserStatsDto>($"api/users/stats?tenantId={tenantId}");
        }

        public Task<BulkOperationResult> BulkUpdateStatusAsync(BulkUpdateUserStatusCommand command)
        {
            return _api.PostAsync<BulkOperationResult>("api/users/bulk/status", command);
        }

        public async Task<List<SalesTeamMemberDto>> GetSalesTeamAsync(Guid tenantId)
        {
            var url = $"api/users/sales-team?tenantId={tenantId}";
            return await _api.GetAsync<List<SalesTeamMemberDto>>(url)
                ?? new List<SalesTeamMemberDto>();
        }
    }
}
