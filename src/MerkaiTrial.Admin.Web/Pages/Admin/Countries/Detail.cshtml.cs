// =====================================================================
// COUNTRY DETAIL - Backend
// Location: Pages/Admin/Countries/Detail.cshtml.cs
//
// 117 — THE COUNTRY'S STANDARD TAX RATES LIVE HERE NOW. They were a menu
// item of their own (Admin → Tax Rates); a country's standard rates are
// reference data like its currency and dial code, so they sit on its page
// (the "Tax Settings" tab): listed with where each stands today (in force,
// scheduled, ended, switched off), and added, edited and deleted from
// here. ?tab=tax opens that tab — where Add and Edit come back to.
// Only SYSTEM rates are shown: a workspace's own rates are the
// workspace's business (Settings → Tax Rates).
// =====================================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Admin.Web.Services.TaxRates;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Countries
{
    public class DetailModel : PageModel
    {
        private readonly ICountryService _countryService;
        private readonly ITaxRateService _taxRateService;
        private readonly ILogger<DetailModel> _logger;

        public DetailModel(
            ICountryService countryService,
            ITaxRateService taxRateService,
            ILogger<DetailModel> logger)
        {
            _countryService = countryService;
            _taxRateService = taxRateService;
            _logger = logger;
        }

        public CountryDto Country { get; set; } = null!;

        /// <summary>117: the country's standard (system) tax rates, newest window first.</summary>
        public List<TaxRateListItem> Rates { get; private set; } = new();

        /// <summary>117: the rates could not be read — the page still opens, and says so.</summary>
        public bool RatesFailed { get; private set; }

        [BindProperty(SupportsGet = true)]
        public string Code { get; set; } = string.Empty;

        /// <summary>117: "tax" opens the Tax Settings tab.</summary>
        [BindProperty(SupportsGet = true)]
        public string? Tab { get; set; }

        public bool TaxTab => string.Equals(Tab, "tax", StringComparison.OrdinalIgnoreCase);

        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(Code))
                {
                    TempData["ErrorMessage"] = "Country code is required";
                    return RedirectToPage("./Index");
                }

                Country = await _countryService.GetByCodeAsync(Code);
                await LoadRatesAsync();
                return Page();
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = $"Country {Code} not found";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading country detail for {Code}", Code);
                TempData["ErrorMessage"] = "Failed to load country details";
                return RedirectToPage("./Index");
            }
        }

        /// <summary>117: delete one of this country's standard rates.</summary>
        public async Task<IActionResult> OnPostDeleteRateAsync(Guid rateId)
        {
            try
            {
                var rate = await _taxRateService.GetByIdAsync(rateId);
                if (!string.Equals(rate.CountryCode, Code, StringComparison.OrdinalIgnoreCase))
                {
                    TempData["ErrorMessage"] = "That tax rate belongs to another country.";
                }
                else
                {
                    await _taxRateService.DeleteAsync(rateId);
                    TempData["SuccessMessage"] = $"Tax rate '{rate.Name}' deleted.";
                }
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "That tax rate no longer exists.";
            }
            catch (InvalidOperationException ex)
            {
                TempData["ErrorMessage"] = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting tax rate {RateId} of {Code}", rateId, Code);
                TempData["ErrorMessage"] = "The tax rate could not be deleted. Please try again.";
            }

            return RedirectToPage(new { code = Code, tab = "tax" });
        }

        private async Task LoadRatesAsync()
        {
            try
            {
                Rates = (await _taxRateService.GetByCountryAsync(Country.Code))
                    .Where(r => r.IsSystem)
                    .OrderByDescending(r => r.IsDefault)
                    .ThenByDescending(r => r.EffectiveFrom ?? DateTime.MinValue)
                    .ThenBy(r => r.Name)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load tax rates for {Code}", Country.Code);
                RatesFailed = true;
            }
        }
    }
}
