// =====================================================================
// COUNTRY DETAIL - Backend
// Location: Pages/Admin/Countries/Detail.cshtml.cs
// =====================================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Countries
{
    public class DetailModel : PageModel
    {
        private readonly ICountryService _countryService;
        private readonly ILogger<DetailModel> _logger;

        public DetailModel(
            ICountryService countryService,
            ILogger<DetailModel> logger)
        {
            _countryService = countryService;
            _logger = logger;
        }

        public CountryDto Country { get; set; } = null!;

        [BindProperty(SupportsGet = true)]
        public string Code { get; set; } = string.Empty;

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
    }
}
