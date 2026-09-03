// =====================================================================
// COUNTRY SERVICE - COMPLETE with All Methods
// Location: MerkaiTrial.Admin.Web/Services/Countries/CountryService.cs
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.Countries
{
    public interface ICountryService
    {
        // Paginated list for Index page
        Task<PaginatedResult<CountryListItem>> GetAllAsync(
            int pageNumber,
            int pageSize,
            string? searchTerm = null,
            string? currencyFilter = null,
            bool showInactiveOnly = false);

        // Get single country by code
        Task<CountryDto> GetByCodeAsync(string code);

        // Get statistics
        Task<CountryStatsDto> GetStatsAsync();

        // Create/Update operations
        Task<CountryDto> CreateAsync(CreateCountryDto dto);
        Task UpdateAsync(string code, UpdateCountryDto dto);

        // ✅ NEW: Get active countries for dropdowns
        Task<List<CountryListItem>> GetActiveAsync();

        // ✅ NEW: Get all countries for dropdowns (active + inactive)
        Task<List<CountryListItem>> GetAllForDropdownAsync();
    }

    public class CountryService : ICountryService
    {
        private readonly IApiService _api;

        public CountryService(IApiService api)
        {
            _api = api;
        }

        // ==================== GET PAGINATED (Index Page) ====================
        public Task<PaginatedResult<CountryListItem>> GetAllAsync(
            int pageNumber,
            int pageSize,
            string? searchTerm = null,
            string? currencyFilter = null,
            bool showInactiveOnly = false)
        {
            var url = $"api/countries?pageNumber={pageNumber}&pageSize={pageSize}&activeOnly={!showInactiveOnly}";
            
            if (!string.IsNullOrWhiteSpace(searchTerm))
                url += $"&searchTerm={Uri.EscapeDataString(searchTerm)}";
            
            if (!string.IsNullOrWhiteSpace(currencyFilter))
                url += $"&currencyFilter={Uri.EscapeDataString(currencyFilter)}";
            
            if (showInactiveOnly)
                url += "&showInactiveOnly=true";

            return _api.GetAsync<PaginatedResult<CountryListItem>>(url);
        }

        // ==================== GET BY CODE ====================
        public Task<CountryDto> GetByCodeAsync(string code)
        {
            return _api.GetAsync<CountryDto>($"api/countries/{code}");
        }

        // ==================== GET STATISTICS ====================
        public Task<CountryStatsDto> GetStatsAsync()
        {
            return _api.GetAsync<CountryStatsDto>("api/countries/stats");
        }

        // ==================== CREATE COUNTRY ====================
        public Task<CountryDto> CreateAsync(CreateCountryDto dto)
        {
            return _api.PostAsync<CountryDto>("api/countries", dto);
        }

        // ==================== UPDATE COUNTRY ====================
        public Task UpdateAsync(string code, UpdateCountryDto dto)
        {
            return _api.PutVoidAsync($"api/countries/{code}", dto);
        }

        // ==================== GET ACTIVE COUNTRIES (For Dropdowns) ====================
        public async Task<List<CountryListItem>> GetActiveAsync()
        {
            // Get all active countries without pagination
            var url = "api/countries?activeOnly=true&pageNumber=1&pageSize=300";
            var result = await _api.GetAsync<PaginatedResult<CountryListItem>>(url);
            return result.Items;
        }

        // ==================== GET ALL COUNTRIES (For Dropdowns) ====================
        public async Task<List<CountryListItem>> GetAllForDropdownAsync()
        {
            // Get all countries (active + inactive) without pagination
            var url = "api/countries?activeOnly=false&pageNumber=1&pageSize=300";
            var result = await _api.GetAsync<PaginatedResult<CountryListItem>>(url);
            return result.Items;
        }
    }
}
