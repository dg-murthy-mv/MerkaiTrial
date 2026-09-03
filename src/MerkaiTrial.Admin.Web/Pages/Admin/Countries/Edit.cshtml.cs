// =====================================================================
// COUNTRY EDIT - Backend
// Location: Pages/Admin/Countries/Edit.cshtml.cs
// =====================================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Application.DTOs;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Countries
{
    public class EditModel : PageModel
    {
        private readonly ICountryService _countryService;
        private readonly ILogger<EditModel> _logger;

        public EditModel(
            ICountryService countryService,
            ILogger<EditModel> logger)
        {
            _countryService = countryService;
            _logger = logger;
        }

        public CountryDto Country { get; set; } = null!;

        [BindProperty(SupportsGet = true)]
        public string Code { get; set; } = string.Empty;

        [BindProperty]
        public InputModel Input { get; set; } = new();

        public class InputModel
        {
            [Required(ErrorMessage = "Country name is required")]
            [StringLength(100, ErrorMessage = "Name cannot exceed 100 characters")]
            public string Name { get; set; } = string.Empty;

            [StringLength(10, ErrorMessage = "Dial code cannot exceed 10 characters")]
            [RegularExpression(@"^\+\d{1,4}$", ErrorMessage = "Dial code must be in format +XX (e.g., +1, +44)")]
            public string? DialCode { get; set; }

            [StringLength(3, ErrorMessage = "Currency code must be 3 characters")]
            [RegularExpression(@"^[A-Z]{3}$", ErrorMessage = "Currency code must be 3 uppercase letters")]
            public string? CurrencyCode { get; set; }

            [StringLength(32, ErrorMessage = "Tax label cannot exceed 32 characters")]
            public string? TaxLabel { get; set; }

            [Range(0, 100, ErrorMessage = "Tax rate must be between 0 and 100")]
            public decimal DefaultTaxRate { get; set; }

            public bool IsActive { get; set; }
        }

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

                // Populate form
                Input = new InputModel
                {
                    Name = Country.Name,
                    DialCode = Country.DialCode,
                    CurrencyCode = Country.CurrencyCode,
                    TaxLabel = Country.TaxLabel,
                    DefaultTaxRate = Country.DefaultTaxRate ?? 0,
                    IsActive = Country.IsActive
                };

                return Page();
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = $"Country {Code} not found";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading country for edit {Code}", Code);
                TempData["ErrorMessage"] = "Failed to load country";
                return RedirectToPage("./Index");
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            // Reload country for display
            Country = await _countryService.GetByCodeAsync(Code);

            if (!ModelState.IsValid)
            {
                return Page();
            }

            try
            {
                var dto = new UpdateCountryDto
                {
                    Name = Input.Name,
                    DialCode = Input.DialCode,
                    CurrencyCode = Input.CurrencyCode?.ToUpper(),
                    TaxLabel = Input.TaxLabel,
                    DefaultTaxRate = Input.DefaultTaxRate,
                    IsActive = Input.IsActive
                };

                await _countryService.UpdateAsync(Code, dto);

                TempData["SuccessMessage"] = $"Country '{Input.Name}' updated successfully!";
                return RedirectToPage("./Detail", new { code = Code });
            }
            catch (KeyNotFoundException)
            {
                ModelState.AddModelError(string.Empty, "Country not found");
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating country {Code}", Code);
                ModelState.AddModelError(string.Empty, "Failed to update country. Please try again.");
                return Page();
            }
        }
    }
}
