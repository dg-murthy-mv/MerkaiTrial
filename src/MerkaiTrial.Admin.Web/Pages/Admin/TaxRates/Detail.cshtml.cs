// =====================================================================
// TAX RATES DETAIL - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Admin/TaxRates/Detail.cshtml.cs
//
// MOVED IN 054, from Pages/TaxRates/. That move IS the security fix.
//
//   AddAdminWebPages() has
//       options.Conventions.AuthorizeFolder("/Admin", "SuperAdmin");
//   and that convention matches on the page's FOLDER under Pages/, not on
//   its URL. These four pages sat in Pages/TaxRates/ with a route override
//   of @@page "/Admin/TaxRates/...", so they SERVED from an /Admin URL while
//   living outside the folder that gate covers — and carried no
//   [Authorize] of their own either, only a commented-out one saying
//   "uncomment when authorization is ready".
//
//   There is a FallbackPolicy, so an anonymous visitor was still stopped.
//   Any SIGNED-IN USER OF ANY TENANT was not: these pages create, edit and
//   delete the PLATFORM-WIDE system tax rates that every tenant's quotes
//   and invoices are priced from.
//
//   Sitting in Pages/Admin/ now, the folder convention covers them, which
//   is better than an attribute per page — a page added here later cannot
//   forget one.
//
//   DELETE the old Pages/TaxRates/ folder. Leaving it there leaves the
//   ungated copy serving.
// =====================================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MerkaiTrial.Admin.Web.Services.TaxRates;
using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Pages.Admin.TaxRates
{
    public class DetailModel : PageModel
    {
        private readonly ITaxRateService _taxRateService;
        private readonly ICountryService _countryService;
        private readonly ILogger<DetailModel> _logger;

        public DetailModel(
            ITaxRateService taxRateService,
            ICountryService countryService,
            ILogger<DetailModel> logger)
        {
            _taxRateService = taxRateService;
            _countryService = countryService;
            _logger = logger;
        }

        public TaxRateDto TaxRate { get; set; } = null!;
        public CountryDto Country { get; set; } = null!;

        [BindProperty(SupportsGet = true)]
        public Guid Id { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                if (Id == Guid.Empty)
                {
                    TempData["ErrorMessage"] = "Tax rate ID is required";
                    return RedirectToPage("./Index");
                }

                TaxRate = await _taxRateService.GetByIdAsync(Id);
                Country = await _countryService.GetByCodeAsync(TaxRate.CountryCode);
                return Page();
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Tax rate not found";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading tax rate detail for {Id}", Id);
                TempData["ErrorMessage"] = "Failed to load tax rate details";
                return RedirectToPage("./Index");
            }
        }
    }
}
