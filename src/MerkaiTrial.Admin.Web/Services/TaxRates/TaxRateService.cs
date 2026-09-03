using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.TaxRates
{
    // =====================================================================
    // TAX RATE SERVICE INTERFACE
    // =====================================================================
    public interface ITaxRateService
    {
        Task<PaginatedResult<TaxRateListItem>> GetAllAsync(
            Guid? tenantId = null,  // ✅ CHANGED: Made nullable
            int pageNumber = 1,
            int pageSize = 10,
            string? searchTerm = null,
            string? countryFilter = null);
        Task<TaxRateStatsDto> GetStatsAsync();
        Task<List<TaxRateListItem>> GetByCountryAsync(Guid? tenantId, string countryCode);  // ✅ CHANGED
        Task<TaxRateDto> GetByIdAsync(Guid id);
        Task<TaxRateDto> CreateAsync(CreateTaxRateDto dto);
        Task UpdateAsync(Guid id, UpdateTaxRateDto dto);
        Task DeleteAsync(Guid id);
        Task<TaxRateDto> GetDefaultForCountryAsync(Guid? tenantId, string countryCode);  // ✅ CHANGED
        Task<List<TaxRateDto>> GetByCountryAsync(Guid tenantId, string countryCode);
        Task<List<TaxRateDto>> GetAllAsync(Guid tenantId);
    }

    // =====================================================================
    // TAX RATE SERVICE IMPLEMENTATION
    // =====================================================================
    public class TaxRateService : ITaxRateService
    {
        private readonly IApiService _api;
        private readonly ILogger<TaxRateService> _logger;

        public TaxRateService(IApiService api, ILogger<TaxRateService> logger)
        {
            _api = api;
            _logger = logger;
        }

        public async Task<PaginatedResult<TaxRateListItem>> GetAllAsync(
            Guid? tenantId = null,
            int pageNumber = 1,
            int pageSize = 10,
            string? searchTerm = null,
            string? countryFilter = null)
        {
            try
            {
                var queryParams = new List<string>
                {
                   
                    $"pageNumber={pageNumber}",
                    $"pageSize={pageSize}"
                };

                if (tenantId.HasValue && tenantId.Value != Guid.Empty)
                {
                    queryParams.Add($"tenantId={tenantId.Value}");
                }
                if (!string.IsNullOrWhiteSpace(searchTerm))
                    queryParams.Add($"searchTerm={Uri.EscapeDataString(searchTerm)}");

                if (!string.IsNullOrWhiteSpace(countryFilter))
                    queryParams.Add($"countryCode={countryFilter}");

                var query = string.Join("&", queryParams);
                return await _api.GetAsync<PaginatedResult<TaxRateListItem>>($"/api/tax-rates?{query}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting paginated tax rates for tenant {TenantId}", tenantId);
                throw;
            }
        }
        public async Task<List<TaxRateDto>> GetAllAsync(Guid tenantId)
        {
            try
            {
                var url = $"api/taxrates?tenantId={tenantId}";
                var rates = await _api.GetAsync<List<TaxRateDto>>(url);
                return rates ?? new List<TaxRateDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get tax rates for tenant {TenantId}", tenantId);
                return new List<TaxRateDto>();
            }
        }
        public async Task<List<TaxRateDto>> GetByCountryAsync(Guid tenantId, string countryCode)
        {
            try
            {
                var url = $"api/taxrates?tenantId={tenantId}&countryCode={countryCode}";
                var rates = await _api.GetAsync<List<TaxRateDto>>(url);
                return rates ?? new List<TaxRateDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get tax rates for country {CountryCode}", countryCode);
                return new List<TaxRateDto>();
            }
        }

        public async Task<List<TaxRateListItem>> GetByCountryAsync(Guid? tenantId, string countryCode)
        {
            try
            {
                var url = $"/api/tax-rates?countryCode={countryCode}&pageSize=1000";
                if (tenantId.HasValue && tenantId.Value != Guid.Empty)
                {
                    url += $"&tenantId={tenantId.Value}";
                }

                var result = await _api.GetAsync<PaginatedResult<TaxRateListItem>>(url);
                return result.Items;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting tax rates for country {Code}", countryCode);
                throw;
            }
        }
        public async Task<TaxRateStatsDto> GetStatsAsync()
        {
            try
            {
                return await _api.GetAsync<TaxRateStatsDto>("/api/tax-rates/stats");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting tax rate statistics");
                throw;
            }
        }
        public async Task<TaxRateDto> GetByIdAsync(Guid id)
        {
            try
            {
                return await _api.GetAsync<TaxRateDto>($"/api/tax-rates/{id}");
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("404"))
            {
                _logger.LogWarning("Tax rate {Id} not found", id);
                throw new KeyNotFoundException($"Tax rate {id} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting tax rate {Id}", id);
                throw;
            }
        }

        public async Task<TaxRateDto> CreateAsync(CreateTaxRateDto dto)
        {
            try
            {
                return await _api.PostAsync<TaxRateDto>("/api/tax-rates", dto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating tax rate for country {Code}", dto.CountryCode);
                throw;
            }
        }

        public async Task UpdateAsync(Guid id, UpdateTaxRateDto dto)
        {
            try
            {
                await _api.PutVoidAsync($"/api/tax-rates/{id}", dto);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("404"))
            {
                _logger.LogWarning("Tax rate {Id} not found for update", id);
                throw new KeyNotFoundException($"Tax rate {id} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating tax rate {Id}", id);
                throw;
            }
        }

        public async Task DeleteAsync(Guid id)
        {
            try
            {
                await _api.DeleteAsync($"/api/tax-rates/{id}");
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("404"))
            {
                _logger.LogWarning("Tax rate {Id} not found for deletion", id);
                throw new KeyNotFoundException($"Tax rate {id} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting tax rate {Id}", id);
                throw;
            }
        }

        public async Task<TaxRateDto> GetDefaultForCountryAsync(Guid? tenantId, string countryCode)
        {
            try
            {
                var url = $"/api/tax-rates/country/{countryCode}/default";
                if (tenantId.HasValue && tenantId.Value != Guid.Empty)
                {
                    url += $"?tenantId={tenantId.Value}";
                }

                return await _api.GetAsync<TaxRateDto>(url);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("404"))
            {
                _logger.LogWarning("No default tax rate found for country {Code}", countryCode);
                throw new KeyNotFoundException($"No default tax rate for country {countryCode}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting default tax rate for country {Code}", countryCode);
                throw;
            }
        }
    }
}