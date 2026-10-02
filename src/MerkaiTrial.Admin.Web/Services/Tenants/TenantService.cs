// =====================================================================
// TENANT SERVICE - Complete with All Methods
// Location: MerkaiTrial.Admin.Web/Services/Tenants/TenantService.cs
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.DTOs;
using System.Net.Http;

namespace MerkaiTrial.Admin.Web.Services.Tenants
{
    public interface ITenantService
    {
        Task<PaginatedTenantsResponse> GetPaginatedAsync(int page, int pageSize, string? search, bool? isActive);
        Task<TenantDto> GetByIdAsync(Guid tenantId);
        Task<TenantDto> CreateAsync(CreateTenantCommand command);
        Task UpdateAsync(UpdateTenantCommand command);
        Task UpdateStatusAsync(Guid tenantId, bool isActive);
        Task DeleteAsync(Guid tenantId);
        Task<TenantSettingsDto> GetSettingsAsync(Guid tenantId);
        Task UpdateSettingsAsync(UpdateTenantSettingsCommand command);
        Task<TenantStatsDto> GetStatsAsync(Guid tenantId);

        Task<TenantStatsDto> GetAllTenantsStatsAsync();
        Task<TenantStatsDto> GetAllStatsAsync();
        Task<List<UserListItem>> GetUsersAsync(Guid tenantId);  // ✅ NEW
        Task<List<TenantLookupDto>> GetLookupAsync();
        Task<List<CountryDropdownDto>> GetCountriesAsync();  // ✅ NEW
        Task<List<TimezoneDto>> GetTimezonesAsync(bool commonOnly = false);  // ✅ NEW
        Task<List<string>> GetPlansAsync();  // ✅ NEW

        // ── 064: the CALLER'S OWN workspace letterhead ────────────────
        //
        // No tenantId parameter, on purpose. The API takes it from the
        // token and the route carries no id, so there is no way for one
        // workspace to read or write another's profile — and no id for a
        // page to pass wrongly. Everything above is the SUPER ADMIN's
        // view of ANY workspace and is now guarded as such.
        Task<CompanyProfileDto> GetCompanyProfileAsync();
        Task UpdateCompanyProfileAsync(UpdateCompanyProfileCommand command);
    }

    public class TenantService : ITenantService
    {
        private readonly IApiService _api;

        public TenantService(IApiService api)
        {
            _api = api;
        }

        // ==================== GET PAGINATED TENANTS ====================
        public Task<PaginatedTenantsResponse> GetPaginatedAsync(
            int page, int pageSize, string? search, bool? isActive)
        {
            var url = $"api/tenants/paginated?page={page}&pageSize={pageSize}";
            if (!string.IsNullOrWhiteSpace(search))
                url += $"&search={Uri.EscapeDataString(search)}";
            if (isActive.HasValue)
                url += $"&isActive={isActive.Value}";

            return _api.GetAsync<PaginatedTenantsResponse>(url);
        }

        // ==================== GET TENANT BY ID ====================
        public Task<TenantDto> GetByIdAsync(Guid tenantId)
        {
            return _api.GetAsync<TenantDto>($"api/tenants/{tenantId}");
        }

        // ==================== CREATE TENANT ====================
        public Task<TenantDto> CreateAsync(CreateTenantCommand command)
        {
            return _api.PostAsync<TenantDto>("api/tenants", command);
        }

        // ==================== UPDATE TENANT ====================
        public Task UpdateAsync(UpdateTenantCommand command)
        {
            return _api.PutVoidAsync($"api/tenants/{command.TenantId}", command);
        }

        // ==================== UPDATE TENANT STATUS ====================
        public Task UpdateStatusAsync(Guid tenantId, bool isActive)
        {
            return _api.PatchVoidAsync($"api/tenants/{tenantId}/status?isActive={isActive}", null);
        }

        // ==================== DELETE TENANT ====================
        public Task DeleteAsync(Guid tenantId)
        {
            return _api.DeleteAsync($"api/tenants/{tenantId}");
        }

        // ==================== GET TENANT SETTINGS ====================
        public Task<TenantSettingsDto> GetSettingsAsync(Guid tenantId)
        {
            return _api.GetAsync<TenantSettingsDto>($"api/tenants/{tenantId}/settings");
        }

        // ==================== UPDATE TENANT SETTINGS ====================
        public Task UpdateSettingsAsync(UpdateTenantSettingsCommand command)
        {
            return _api.PutVoidAsync($"api/tenants/{command.TenantId}/settings", command);
        }

        public Task<TenantStatsDto> GetAllTenantsStatsAsync()
        {

            return _api.GetAsync<TenantStatsDto>($"api/tenants/stats/all");
              
            
        }
        // ==================== GET TENANT STATISTICS ====================
        public Task<TenantStatsDto> GetStatsAsync(Guid tenantId)
        {
            return _api.GetAsync<TenantStatsDto>($"api/tenants/{tenantId}/stats");
        }

        // ==================== GET ALL TENANTS STATS ====================
        public Task<TenantStatsDto> GetAllStatsAsync()
        {
            return _api.GetAsync<TenantStatsDto>("api/tenants/stats");
        }

        // ==================== GET TENANT USERS (NEW!) ====================
        public Task<List<UserListItem>> GetUsersAsync(Guid tenantId)
        {
            return _api.GetAsync<List<UserListItem>>($"api/tenants/{tenantId}/users");
        }

        // ==================== GET TENANTS LOOKUP ====================
        public Task<List<TenantLookupDto>> GetLookupAsync()
        {
            return _api.GetAsync<List<TenantLookupDto>>("api/tenants/lookup");
        }

        // ==================== GET COUNTRIES FOR DROPDOWN (NEW!) ====================
        public Task<List<CountryDropdownDto>> GetCountriesAsync()
        {
            return _api.GetAsync<List<CountryDropdownDto>>("api/tenants/countries");
        }

        // ==================== GET TIMEZONES (NEW!) ====================
        public Task<List<TimezoneDto>> GetTimezonesAsync(bool commonOnly = false)
        {
            var url = $"api/tenants/timezones?commonOnly={commonOnly}";
            return _api.GetAsync<List<TimezoneDto>>(url);
        }

        // ==================== GET PLANS (NEW!) ====================
        public Task<List<string>> GetPlansAsync()
        {
            return _api.GetAsync<List<string>>("api/tenants/plans");
        }

        // ==================== 064: COMPANY PROFILE ====================
        //
        // No id in either URL. See the note on the interface.
        //
        // Neither method catches. IApiService turns a 400 { error = "…" }
        // into InvalidOperationException carrying the API's own sentence,
        // and that sentence is what the page should show — swallowing it
        // to log a generic line would throw away the only useful part.

        public Task<CompanyProfileDto> GetCompanyProfileAsync()
            => _api.GetAsync<CompanyProfileDto>("api/tenants/company-profile");

        public Task UpdateCompanyProfileAsync(UpdateCompanyProfileCommand command)
            => _api.PutVoidAsync("api/tenants/company-profile", command);
    }
}
