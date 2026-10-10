// =====================================================================
// COMPANY SERVICE
// Location: MerkaiTrial.Admin.Web/Services/Companies/CompanyService.cs
//
// 078 — CUSTOM FIELDS, AND THE SAME FIXES ContactService GOT IN 075/076
//
//   1. GetAllAsync takes optional custom field filters, sent as repeated
//      ?cf= values, each escaped as a whole (the value part is free text).
//      Optional and last, so every existing caller is unchanged.
//
//   2. The COUNTRY filter is escaped now. It went into the URL raw; a
//      value with "&" or "#" in it (a hand-edited link) would have cut
//      the query string short or injected a second parameter.
//
//   3. Create and Update let an InvalidOperationException through WITHOUT
//      logging it as an error. That is how the API's 400
//      { error: "Renewal date must be a valid date." } arrives, and a
//      person leaving a required field empty is not a system fault worth
//      an ERROR line. The page shows the sentence.
//
//   4. GetStatsAsync no longer sends ?tenantId=. The API ignores it (the
//      tenant comes from the signed-in user), and a tenant id in a URL
//      only ends up in access logs.
//
//   5. "Not found" is recognised by the status NAME as well as the code
//      ("NotFound" or "404"), so it does not depend on which of the two
//      the HTTP layer's message happens to contain.
//
//   6. GetLookupAsync calls the new /api/companies/lookup. It used to
//      borrow the paged list with pageSize=1000; the list is capped at 100
//      rows a page from 078, which would have quietly cut any dropdown
//      built from it at 100 companies. The lookup endpoint returns every
//      live company (id, name, country, vertical — no counts), sorted.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.Companies
{
    public interface ICompanyService
    {
        Task<PaginatedResult<CompanyListItem>> GetAllAsync(
            Guid tenantId,
            int pageNumber = 1,
            int pageSize = 10,
            string? searchTerm = null,
            string? verticalFilter = null,
            string? countryFilter = null,
            IReadOnlyList<CustomFieldFilter>? customFilters = null);   // 078

        Task<CompanyDto> GetByIdAsync(Guid tenantId, Guid id);
        Task<CompanyStatsDto> GetStatsAsync(Guid tenantId);
        Task<CompanyDto> CreateAsync(CreateCompanyDto dto);
        Task UpdateAsync(Guid id, UpdateCompanyDto dto);
        Task DeleteAsync(Guid tenantId, Guid id);
        Task<List<CompanyListItem>> GetLookupAsync(Guid tenantId); // For dropdowns
    }

    public class CompanyService : ICompanyService
    {
        private readonly IApiService _api;
        private readonly ILogger<CompanyService> _logger;

        public CompanyService(IApiService api, ILogger<CompanyService> logger)
        {
            _api = api;
            _logger = logger;
        }

        private static bool IsNotFound(HttpRequestException ex)
            => ex.Message.Contains("404") || ex.Message.Contains("NotFound");

        public async Task<PaginatedResult<CompanyListItem>> GetAllAsync(
            Guid tenantId,
            int pageNumber = 1,
            int pageSize = 10,
            string? searchTerm = null,
            string? verticalFilter = null,
            string? countryFilter = null,
            IReadOnlyList<CustomFieldFilter>? customFilters = null)
        {
            try
            {
                var queryParams = new List<string>
                {
                    $"pageNumber={pageNumber}",
                    $"pageSize={pageSize}"
                };

                if (!string.IsNullOrWhiteSpace(searchTerm))
                    queryParams.Add($"searchTerm={Uri.EscapeDataString(searchTerm.Trim())}");

                if (!string.IsNullOrWhiteSpace(verticalFilter))
                    queryParams.Add($"vertical={Uri.EscapeDataString(verticalFilter.Trim())}");

                if (!string.IsNullOrWhiteSpace(countryFilter))
                    queryParams.Add($"country={Uri.EscapeDataString(countryFilter.Trim())}");   // note 2

                // Note 1 — escaped as a whole: the value part is free text.
                foreach (var f in customFilters ?? Array.Empty<CustomFieldFilter>())
                    queryParams.Add($"cf={Uri.EscapeDataString(CustomFieldFilterCodec.Encode(f))}");

                var query = string.Join("&", queryParams);
                return await _api.GetAsync<PaginatedResult<CompanyListItem>>($"/api/companies?{query}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting paginated companies for tenant {TenantId}", tenantId);
                throw;
            }
        }

        public async Task<CompanyStatsDto> GetStatsAsync(Guid tenantId)
        {
            try
            {
                // Note 4 — the API resolves the tenant itself.
                return await _api.GetAsync<CompanyStatsDto>("/api/companies/stats");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting company statistics for tenant {TenantId}", tenantId);
                throw;
            }
        }

        public async Task<CompanyDto> GetByIdAsync(Guid tenantId, Guid id)
        {
            try
            {
                return await _api.GetAsync<CompanyDto>($"/api/companies/{id}");
            }
            catch (HttpRequestException ex) when (IsNotFound(ex))
            {
                _logger.LogWarning("Company {Id} not found", id);
                throw new KeyNotFoundException($"Company {id} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting company {Id}", id);
                throw;
            }
        }

        public async Task<CompanyDto> CreateAsync(CreateCompanyDto dto)
        {
            try
            {
                return await _api.PostAsync<CompanyDto>("/api/companies", dto);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("422")
                                              || ex.Message.Contains("UnprocessableEntity")
                                              || ex.Message.Contains("plan_limit_exceeded"))
            {
                // ✅ FIX 2: Surface plan limit as friendly message for the UI
                throw new InvalidOperationException(
                    "You have reached your plan's company limit. Upgrade your plan to add more companies.", ex);
            }
            catch (InvalidOperationException)
            {
                throw;   // 078 — a sentence for the person; see note 3
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating company {Name}", dto.Name);
                throw;
            }
        }

        public async Task UpdateAsync(Guid id, UpdateCompanyDto dto)
        {
            try
            {
                await _api.PutVoidAsync($"/api/companies/{id}", dto);
            }
            catch (HttpRequestException ex) when (IsNotFound(ex))
            {
                _logger.LogWarning("Company {Id} not found for update", id);
                throw new KeyNotFoundException($"Company {id} not found");
            }
            catch (InvalidOperationException)
            {
                throw;   // 078 — a sentence for the person; see note 3
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating company {Id}", id);
                throw;
            }
        }

        public async Task DeleteAsync(Guid tenantId, Guid id)
        {
            try
            {
                await _api.DeleteAsync($"/api/companies/{id}");
            }
            catch (HttpRequestException ex) when (IsNotFound(ex))
            {
                _logger.LogWarning("Company {Id} not found for deletion", id);
                throw new KeyNotFoundException($"Company {id} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting company {Id}", id);
                throw;
            }
        }

        public async Task<List<CompanyListItem>> GetLookupAsync(Guid tenantId)
        {
            try
            {
                // Note 6 — the dedicated endpoint, not the paged list.
                return await _api.GetAsync<List<CompanyListItem>>("/api/companies/lookup");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting companies lookup for tenant {TenantId}", tenantId);
                throw;
            }
        }
    }
}
