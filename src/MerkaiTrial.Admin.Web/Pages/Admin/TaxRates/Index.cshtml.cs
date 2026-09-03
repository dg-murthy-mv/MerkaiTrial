// =====================================================================
// TAX RATES INDEX - BACKEND
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Admin.Web.Services.TaxRates;
using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MerkaiTrial.Admin.Web.Pages.TaxRates
{
    public class IndexModel : PageModel
    {
        private readonly ITaxRateService _taxRateService;
        private readonly ICountryService _countryService;
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(
            ITaxRateService taxRateService,
            ICountryService countryService,
            ILogger<IndexModel> logger)
        {
            _taxRateService = taxRateService;
            _countryService = countryService;
            _logger = logger;
        }

        public PaginatedResult<TaxRateListItem> PaginatedTaxRates { get; set; } = new();
        public TaxRateStatsDto Stats { get; set; } = null!;
        public List<CountryListItem> Countries { get; set; } = new();

        [BindProperty(SupportsGet = true)]
        public int Page { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public int PageSize { get; set; } = 25;

        [BindProperty(SupportsGet = true)]
        public string? SearchTerm { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? CountryFilter { get; set; }

        public async Task OnGetAsync()
        {
            if (PageSize < 5) PageSize = 5;
            if (PageSize > 100) PageSize = 100;

            try
            {
                Stats = await _taxRateService.GetStatsAsync();

                PaginatedTaxRates = await _taxRateService.GetAllAsync(
                    tenantId: null,
                    pageNumber: Page,
                    pageSize: PageSize,
                    searchTerm: SearchTerm,
                    countryFilter: CountryFilter
                );

                Countries = await _countryService.GetActiveAsync();

                _logger.LogInformation(
                    "Loaded page {Page} with {Count} tax rates (Total: {Total})",
                    Page,
                    PaginatedTaxRates.Items.Count,
                    PaginatedTaxRates.TotalCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading tax rates");
                TempData["ErrorMessage"] = "Failed to load tax rates. Please try again.";

                Stats = new TaxRateStatsDto(0, 0, 0, 0);
                PaginatedTaxRates = new PaginatedResult<TaxRateListItem>
                {
                    Items = new List<TaxRateListItem>(),
                    Page = 1,
                    PageSize = PageSize,
                    TotalCount = 0
                };
                Countries = new List<CountryListItem>();
            }
        }

        public async Task<IActionResult> OnPostDeleteAsync(Guid id)
        {
            try
            {
                await _taxRateService.DeleteAsync(id);
                TempData["SuccessMessage"] = "Tax rate deleted successfully!";
                return RedirectToPage();
            }
            catch (InvalidOperationException ex)
            {
                TempData["ErrorMessage"] = ex.Message;
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting tax rate {Id}", id);
                TempData["ErrorMessage"] = "Failed to delete tax rate. Please try again.";
                return RedirectToPage();
            }
        }

        public SelectList GetCountryFilterOptions()
        {
            var items = new List<SelectListItem>
            {
                new SelectListItem { Value = "", Text = "All Countries" }
            };
            items.AddRange(Countries.Select(c => new SelectListItem
            {
                Value = c.Code,
                Text = c.Name
            }));
            return new SelectList(items, "Value", "Text", CountryFilter);
        }

        public string GetCountryName(string countryCode)
        {
            return Countries.FirstOrDefault(c => c.Code == countryCode)?.Name ?? countryCode;
        }
    }
}
