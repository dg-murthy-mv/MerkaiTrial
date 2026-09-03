using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MerkaiTrial.Admin.Web.Services.TaxRates;
using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Pages.TaxRates
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
