using MerkaiTrial.Admin.Web.Services.Core;
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
            string? countryFilter = null);

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

        public async Task<PaginatedResult<CompanyListItem>> GetAllAsync(
            Guid tenantId,
            int pageNumber = 1,
            int pageSize = 10,
            string? searchTerm = null,
            string? verticalFilter = null,
            string? countryFilter = null)
        {
            try
            {
                var queryParams = new List<string>
                {
                    $"pageNumber={pageNumber}",
                    $"pageSize={pageSize}"
                };

                if (!string.IsNullOrWhiteSpace(searchTerm))
                    queryParams.Add($"searchTerm={Uri.EscapeDataString(searchTerm)}");

                if (!string.IsNullOrWhiteSpace(verticalFilter))
                    queryParams.Add($"vertical={Uri.EscapeDataString(verticalFilter)}");

                if (!string.IsNullOrWhiteSpace(countryFilter))
                    queryParams.Add($"country={countryFilter}");

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
                return await _api.GetAsync<CompanyStatsDto>($"/api/companies/stats?tenantId={tenantId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting company statistics");
                throw;
            }
        }
        public async Task<CompanyDto> GetByIdAsync(Guid tenantId, Guid id)
        {
            try
            {
                return await _api.GetAsync<CompanyDto>($"/api/companies/{id}");
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("404"))
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
            catch (HttpRequestException ex) when (ex.Message.Contains("404"))
            {
                _logger.LogWarning("Company {Id} not found for update", id);
                throw new KeyNotFoundException($"Company {id} not found");
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
            catch (HttpRequestException ex) when (ex.Message.Contains("404"))
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
                var result = await _api.GetAsync<PaginatedResult<CompanyListItem>>(
                    $"/api/companies?pageSize=1000");
                return result.Items;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting companies lookup for tenant {TenantId}", tenantId);
                throw;
            }
        }
    }
}