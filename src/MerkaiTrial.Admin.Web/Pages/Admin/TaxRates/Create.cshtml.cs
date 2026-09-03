// =====================================================================
// TAX RATES CREATE - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Admin/TaxRates/Create.cshtml.cs
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Admin.Web.Services.TaxRates;
using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace MerkaiTrial.Admin.Web.Pages.TaxRates
{
    // [Authorize(Roles = "Admin")] // Uncomment when authorization is ready
    public class CreateModel : PageModel
    {
        private readonly ITaxRateService _taxRateService;
        private readonly ICountryService _countryService;
        private readonly ILogger<CreateModel> _logger;

        public CreateModel(
            ITaxRateService taxRateService,
            ICountryService countryService,
            ILogger<CreateModel> logger)
        {
            _taxRateService = taxRateService;
            _countryService = countryService;
            _logger = logger;
        }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        // ✅ FIXED: Initialize with empty SelectList instead of null
        public SelectList CountryOptions { get; set; } = new SelectList(Enumerable.Empty<SelectListItem>());
        public SelectList TaxTypeOptions { get; set; } = new SelectList(Enumerable.Empty<SelectListItem>());

        [TempData]
        public string? ErrorMessage { get; set; }

        public class InputModel
        {
            [Required(ErrorMessage = "Please select a country")]
            public string? CountryCode { get; set; }

            [Required(ErrorMessage = "Rate name is required")]
            [StringLength(100, ErrorMessage = "Rate name cannot exceed 100 characters")]
            public string Name { get; set; } = string.Empty;

            [Required(ErrorMessage = "Please select a tax type")]
            public string? TaxType { get; set; }

            [Required(ErrorMessage = "Tax rate is required")]
            [Range(0, 100, ErrorMessage = "Tax rate must be between 0 and 100")]
            public decimal Rate { get; set; }

            public bool IsDefault { get; set; }
        }

        public async Task OnGetAsync()
        {
            await LoadDropdownsAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    await LoadDropdownsAsync();
                    return Page();
                }

                var dto = new CreateTaxRateDto
                {
                    TenantId = Guid.Empty,  // ✅ FIXED: Maps to NULL (system tax rate)
                    CountryCode = Input.CountryCode!,
                    Name = Input.Name.Trim(),
                    TaxType = Input.TaxType!,
                    Rate = Input.Rate,
                    IsDefault = Input.IsDefault,
                    CreatedBy = "admin"  // ✅ FIXED: TODO - Get from current user
                };

                await _taxRateService.CreateAsync(dto);

                TempData["SuccessMessage"] = $"Tax rate '{Input.Name}' created successfully!";
                return RedirectToPage("./Index");
            }
            catch (InvalidOperationException ex)
            {
                // Business rule violation (e.g., country doesn't exist)
                ErrorMessage = ex.Message;
                await LoadDropdownsAsync();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating tax rate");
                ErrorMessage = "Failed to create tax rate. Please try again.";
                await LoadDropdownsAsync();
                return Page();
            }
        }

        private async Task LoadDropdownsAsync()
        {
            try
            {
                // Load active countries
                var countries = await _countryService.GetActiveAsync();
                CountryOptions = new SelectList(
                    countries.OrderBy(c => c.Name),
                    nameof(CountryListItem.Code),
                    nameof(CountryListItem.Name),
                    Input.CountryCode
                );

                _logger.LogInformation("Loaded {Count} countries for dropdown", countries.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load countries");
                CountryOptions = new SelectList(Enumerable.Empty<SelectListItem>());
            }

            try
            {
                // Tax types
                var taxTypes = new List<SelectListItem>
                {
                    new SelectListItem { Value = "VAT", Text = "VAT (Value Added Tax)" },
                    new SelectListItem { Value = "GST", Text = "GST (Goods & Services Tax)" },
                    new SelectListItem { Value = "Sales Tax", Text = "Sales Tax" },
                    new SelectListItem { Value = "Service Tax", Text = "Service Tax" },
                    new SelectListItem { Value = "None", Text = "No Tax" }
                };

                TaxTypeOptions = new SelectList(taxTypes, "Value", "Text", Input.TaxType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load tax types");
                TaxTypeOptions = new SelectList(Enumerable.Empty<SelectListItem>());
            }
        }
    }
}