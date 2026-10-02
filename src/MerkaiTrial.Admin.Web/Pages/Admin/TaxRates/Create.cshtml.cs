// =====================================================================
// TAX RATES CREATE - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Admin/TaxRates/Create.cshtml.cs
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

using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Admin.Web.Services.TaxRates;
using MerkaiTrial.Application.Common;        // 057: TaxRateStatus
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

namespace MerkaiTrial.Admin.Web.Pages.Admin.TaxRates
{
    // No [Authorize] here on purpose — AuthorizeFolder("/Admin", "SuperAdmin")
    // covers this page now that it lives in the folder. The comment that used
    // to sit here said "uncomment when authorization is ready"; authorization
    // had been ready for rounds, the page was just in the wrong place.
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

            // ── 057: effective dating ────────────────────────────────
            // Dates, not date-times. A tax rate changes on a DAY, and a
            // time-of-day field would invite somebody to set 14:30 and then
            // wonder why a morning quote used the old rate.
            [DataType(DataType.Date)]
            [Display(Name = "In force from")]
            public DateTime? EffectiveFrom { get; set; }

            [DataType(DataType.Date)]
            [Display(Name = "In force until")]
            public DateTime? EffectiveTo { get; set; }
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

                // 057. The same validator the API and the command use, so
                // the page says the same sentence rather than a second one.
                var windowProblem = TaxRateStatus.ValidateWindow(Input.EffectiveFrom, Input.EffectiveTo);
                if (windowProblem is not null)
                {
                    ErrorMessage = windowProblem;
                    await LoadDropdownsAsync();
                    return Page();
                }

                var dto = new CreateTaxRateDto
                {
                    // 056: NULL, and it genuinely means NULL now. This said
                    // "Maps to NULL (system tax rate)" and mapped to
                    // 00000000-0000-0000-0000-000000000000, while every query
                    // that looked for a system rate looked for NULL. They
                    // never met, which is why the System Rates tile read 0.
                    //
                    // The API refuses this unless the caller is a super
                    // admin — which, this page being under Pages/Admin, they
                    // are.
                    TenantId = null,
                    CountryCode = Input.CountryCode!,
                    Name = Input.Name.Trim(),
                    TaxType = Input.TaxType!,
                    Rate = Input.Rate,
                    IsDefault = Input.IsDefault,

                    // 057. Midnight UTC, like every other calendar date in
                    // this system (see the Quotes issue-date note): a date
                    // sent through ToUniversalTime() from an Unspecified
                    // midnight moves back a day on any server east of UTC,
                    // which is every server this product runs on.
                    EffectiveFrom = Input.EffectiveFrom.HasValue
                        ? DateTime.SpecifyKind(Input.EffectiveFrom.Value.Date, DateTimeKind.Utc)
                        : null,

                    // The END of the last day, not its midnight. With
                    // midnight, a rate "in force until 31 March" stops
                    // applying at 00:00 on the 31st — so it is not in force
                    // on its own last day.
                    EffectiveTo = Input.EffectiveTo.HasValue
                        ? DateTime.SpecifyKind(Input.EffectiveTo.Value.Date, DateTimeKind.Utc)
                                  .AddDays(1).AddTicks(-1)
                        : null,
                    // 054: the signed-in super admin, not the literal "admin".
                    // Every system tax rate ever created carries that string,
                    // so the audit trail on a platform-wide setting cannot say
                    // WHO changed the tax basis for every tenant. Straight off
                    // the principal — no extra service to register.
                    CreatedBy = CurrentUserName()
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

        /// <summary>
        /// Email if the cookie carries one, otherwise the login name, and
        /// "admin" only if the principal somehow has neither — which the
        /// FallbackPolicy should already have made impossible.
        /// </summary>
        private string CurrentUserName()
            => User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
               ?? User.Identity?.Name
               ?? "admin";

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