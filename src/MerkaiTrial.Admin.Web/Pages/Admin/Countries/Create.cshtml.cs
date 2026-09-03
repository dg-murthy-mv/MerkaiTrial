// =====================================================================
// COUNTRY CREATE - Backend
// Location: Pages/Admin/Countries/Create.cshtml.cs
// =====================================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Application.DTOs;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Countries
{
    public class CreateModel : PageModel
    {
        private readonly ICountryService _countryService;
        private readonly ILogger<CreateModel> _logger;

        public CreateModel(
            ICountryService countryService,
            ILogger<CreateModel> logger)
        {
            _countryService = countryService;
            _logger = logger;
        }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        public class InputModel
        {
            [Required(ErrorMessage = "Country code is required")]
            [StringLength(3, MinimumLength = 2, ErrorMessage = "Code must be 2-3 characters")]
            [RegularExpression(@"^[A-Z]{2,3}$", ErrorMessage = "Code must be 2-3 uppercase letters")]
            public string Code { get; set; } = string.Empty;

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
            public decimal? DefaultTaxRate { get; set; }
        }

        public void OnGet()
        {
            // Page initialization
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!ModelState.IsValid)
            {
                return Page();
            }

            try
            {
                var dto = new CreateCountryDto
                {
                    Code = Input.Code.ToUpper(),
                    Name = Input.Name,
                    DialCode = Input.DialCode,
                    CurrencyCode = Input.CurrencyCode?.ToUpper(),
                    TaxLabel = Input.TaxLabel,
                    DefaultTaxRate = Input.DefaultTaxRate ?? 0
                };

                var country = await _countryService.CreateAsync(dto);

                TempData["SuccessMessage"] = $"Country '{country.Name}' created successfully!";
                return RedirectToPage("./Detail", new { code = country.Code });
            }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating country");
                ModelState.AddModelError(string.Empty, "Failed to create country. Please try again.");
                return Page();
            }
        }
    }
}
