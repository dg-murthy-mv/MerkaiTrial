// =====================================================================
// WORKAROUND - If you CAN'T change PaginatedResult
// Location: Pages/Admin/Countries/Index.cshtml.cs
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Countries
{
    public class IndexModel : PageModel
    {
        private readonly ICountryService _countryService;
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(
            ICountryService countryService,
            ILogger<IndexModel> logger)
        {
            _countryService = countryService;
            _logger = logger;
        }

        public PaginatedResult<CountryListItem> PaginatedCountries { get; set; } = new();
        public CountryStatsDto Stats { get; set; } = null!;

        /// <summary>120 — the shared pager; every link keeps the search and filters.</summary>
        public MerkaiTrial.Admin.Web.Pages.Shared.PagerVm Pager
        {
            get
            {
                var route = new Dictionary<string, string>();
                if (!string.IsNullOrWhiteSpace(SearchTerm)) route[nameof(SearchTerm)] = SearchTerm!;
                if (ShowInactiveOnly) route[nameof(ShowInactiveOnly)] = "true";
                if (!string.IsNullOrWhiteSpace(CurrencyFilter)) route[nameof(CurrencyFilter)] = CurrencyFilter!;
                return new()
                {
                    Page = PageNumber,
                    PageSize = PageSize,
                    TotalCount = PaginatedCountries.TotalCount,
                    TotalPages = Math.Max(1, PaginatedCountries.TotalPages),
                    PageField = nameof(PageNumber),
                    Route = route,
                };
            }
        }

        // ✅ NEW: Wrapper for pagination component compatibility
        public dynamic PaginatedCountriesForView => new
        {
            Items = PaginatedCountries.Items,
            Page = PaginatedCountries.Page,  // Map Page → Page
            PageSize = PaginatedCountries.PageSize,
            TotalCount = PaginatedCountries.TotalCount,
            TotalPages = PaginatedCountries.TotalPages
        };

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public int PageSize { get; set; } = 25;

        [BindProperty(SupportsGet = true)]
        public string? SearchTerm { get; set; }

        [BindProperty(SupportsGet = true)]
        public bool ShowInactiveOnly { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? CurrencyFilter { get; set; }

        public async Task OnGetAsync()
        {
            // 120 — the pager's sizes only; page 1 at least; a short search.
            if (!MerkaiTrial.Admin.Web.Pages.Shared.PagerVm.Sizes.Contains(PageSize)) PageSize = 25;
            if (PageNumber < 1) PageNumber = 1;
            if (SearchTerm?.Length > 100) SearchTerm = SearchTerm[..100];

            await LoadDataAsync();
        }

        private async Task LoadDataAsync()
        {
            try
            {
                // Get statistics
                Stats = await _countryService.GetStatsAsync();

                // Get paginated countries
                PaginatedCountries = await _countryService.GetAllAsync(
                    pageNumber: PageNumber,
                    pageSize: PageSize,
                    searchTerm: SearchTerm,
                    currencyFilter: CurrencyFilter,
                    showInactiveOnly: ShowInactiveOnly
                );

                _logger.LogInformation(
                    "Loaded page {PageNumber} with {Count} countries (Total: {Total})",
                    PageNumber,
                    PaginatedCountries.Items.Count,
                    PaginatedCountries.TotalCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading countries");
                TempData["ErrorMessage"] = "Failed to load countries. Please try again.";

                // Set defaults
                Stats = new CountryStatsDto(0, 0, 0, 0);
                PaginatedCountries = new PaginatedResult<CountryListItem>
                {
                    Items = new List<CountryListItem>(),
                    Page = 1,
                    PageSize = PageSize,
                    TotalCount = 0
                };
            }
        }
    }
}
