// =====================================================================
// TAX RATES EDIT - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Admin/TaxRates/Edit.cshtml.cs
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Admin.Web.Services.TaxRates;

using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace MerkaiTrial.Admin.Web.Pages.TaxRates
{
    // [Authorize(Roles = "Admin")] // Uncomment when authorization is ready
    public class EditModel : PageModel
    {
        private readonly ITaxRateService _taxRateService;
        private readonly ICountryService _countryService;
        private readonly ILogger<EditModel> _logger;

        public EditModel(
            ITaxRateService taxRateService,
            ICountryService countryService,
            ILogger<EditModel> logger)
        {
            _taxRateService = taxRateService;
            _countryService = countryService;
            _logger = logger;
        }

        [BindProperty]
        public Guid Id { get; set; }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        // Read-only display fields
        public string CountryCode { get; set; } = string.Empty;
        public string CountryName { get; set; } = string.Empty;
        public string TaxType { get; set; } = string.Empty;

        [TempData]
        public string? ErrorMessage { get; set; }

        public class InputModel
        {
            [Required(ErrorMessage = "Rate name is required")]
            [StringLength(100, ErrorMessage = "Rate name cannot exceed 100 characters")]
            public string Name { get; set; } = string.Empty;

            [Required(ErrorMessage = "Tax rate is required")]
            [Range(0, 100, ErrorMessage = "Tax rate must be between 0 and 100")]
            public decimal Rate { get; set; }

            public bool IsDefault { get; set; }
        }

        public async Task<IActionResult> OnGetAsync(Guid id)
        {
            try
            {
                if (id == Guid.Empty)
                {
                    TempData["ErrorMessage"] = "Tax rate ID is required.";
                    return RedirectToPage("./Index");
                }

                Id = id;
                var taxRate = await _taxRateService.GetByIdAsync(id);

                // Load country details
                var country = await _countryService.GetByCodeAsync(taxRate.CountryCode);
                CountryCode = taxRate.CountryCode;
                CountryName = country.Name;
                TaxType = taxRate.TaxType;

                Input = new InputModel
                {
                    Name = taxRate.Name,
                    Rate = taxRate.Rate,
                    IsDefault = taxRate.IsDefault
                };

                return Page();
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Tax rate not found.";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading tax rate {Id}", id);
                TempData["ErrorMessage"] = "Failed to load tax rate. Please try again.";
                return RedirectToPage("./Index");
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    // Reload read-only data for display
                    var taxRate = await _taxRateService.GetByIdAsync(Id);
                    var country = await _countryService.GetByCodeAsync(taxRate.CountryCode);
                    CountryCode = taxRate.CountryCode;
                    CountryName = country.Name;
                    TaxType = taxRate.TaxType;
                    return Page();
                }

                var dto = new UpdateTaxRateDto
                {
                    Name = Input.Name.Trim(),
                    Rate = Input.Rate,
                    IsDefault = Input.IsDefault
                };

                await _taxRateService.UpdateAsync(Id, dto);

                TempData["SuccessMessage"] = $"Tax rate '{Input.Name}' updated successfully!";
                return RedirectToPage("./Index");
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Tax rate not found.";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating tax rate {Id}", Id);
                ErrorMessage = "Failed to update tax rate. Please try again.";
                
                // Reload read-only data for display
                var taxRate = await _taxRateService.GetByIdAsync(Id);
                var country = await _countryService.GetByCodeAsync(taxRate.CountryCode);
                CountryCode = taxRate.CountryCode;
                CountryName = country.Name;
                TaxType = taxRate.TaxType;
                
                return Page();
            }
        }
    }
}
