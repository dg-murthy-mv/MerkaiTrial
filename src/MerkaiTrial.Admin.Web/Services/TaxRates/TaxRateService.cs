// =====================================================================
// TaxRateService.cs
// Location: MerkaiTrial.Admin.Web/Services/TaxRates/TaxRateService.cs
//
// COMPLETE FILE — 056.
//
// TWO METHODS CALLED THE WRONG URL. The controller route is
// "api/tax-rates"; these asked for "api/taxrates":
//
//     GetAllAsync(Guid tenantId)
//     GetByCountryAsync(Guid tenantId, string countryCode)
//
// Both 404'd, both caught it, and both returned an empty list — for ever,
// with one log line and no other symptom. They also deserialised
// List<TaxRateDto> from an endpoint that returns a paginated envelope, so
// even at the right URL they would have come back empty.
//
// They were a trap in their own right, too: TWO GetByCountryAsync
// overloads differing only by Guid vs Guid?, returning DIFFERENT types.
// Which one you called depended on whether the variable you happened to
// pass was nullable. Both are gone; one method, one shape.
//
// AND THE tenantId PARAMETERS ARE GONE. The API resolves the workspace
// from the token now — system rates plus this workspace's own for a
// tenant, system rates only for a super admin. A client naming a
// workspace could only ever have been naming somebody else's.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.TaxRates
{
    // =====================================================================
    // TAX RATE SERVICE INTERFACE
    // =====================================================================
    public interface ITaxRateService
    {
        /// <summary>
        /// A page of rates in this caller's scope. 056: no tenantId — the
        /// API takes the workspace from the token.
        /// </summary>
        Task<PaginatedResult<TaxRateListItem>> GetAllAsync(
            int pageNumber = 1,
            int pageSize = 10,
            string? searchTerm = null,
            string? countryFilter = null,
            bool includeInactive = false);

        Task<TaxRateStatsDto> GetStatsAsync();

        /// <summary>Every rate for one country, in this caller's scope.</summary>
        Task<List<TaxRateListItem>> GetByCountryAsync(string countryCode);

        Task<TaxRateDto> GetByIdAsync(Guid id);
        Task<TaxRateDto> CreateAsync(CreateTaxRateDto dto);
        Task UpdateAsync(Guid id, UpdateTaxRateDto dto);
        Task DeleteAsync(Guid id);

        /// <summary>
        /// The default for a country: this workspace's own rate if it has
        /// one, otherwise the system rate.
        /// </summary>
        Task<TaxRateDto> GetDefaultForCountryAsync(string countryCode);
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
            int pageNumber = 1,
            int pageSize = 10,
            string? searchTerm = null,
            string? countryFilter = null,
            bool includeInactive = false)
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

                if (!string.IsNullOrWhiteSpace(countryFilter))
                    queryParams.Add($"countryCode={Uri.EscapeDataString(countryFilter)}");

                if (includeInactive)
                    queryParams.Add("includeInactive=true");     // 057

                var query = string.Join("&", queryParams);
                return await _api.GetAsync<PaginatedResult<TaxRateListItem>>($"/api/tax-rates?{query}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting paginated tax rates");
                throw;
            }
        }

        // 056: the two "api/taxrates" methods that lived here are GONE —
        // wrong URL, wrong response shape, and a swallowed 404 that made them
        // look like "no rates configured" for ever. This is the one that
        // worked, with the dead tenantId parameter removed.
        public async Task<List<TaxRateListItem>> GetByCountryAsync(string countryCode)
        {
            try
            {
                var url = $"/api/tax-rates?countryCode={Uri.EscapeDataString(countryCode)}&pageSize=1000";
                var result = await _api.GetAsync<PaginatedResult<TaxRateListItem>>(url);
                return result?.Items ?? new List<TaxRateListItem>();
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

        public async Task<TaxRateDto> GetDefaultForCountryAsync(string countryCode)
        {
            try
            {
                // 056: no tenantId. The API prefers this workspace's own rate
                // over the system one, resolved from the token.
                var url = $"/api/tax-rates/country/{Uri.EscapeDataString(countryCode)}/default";
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
