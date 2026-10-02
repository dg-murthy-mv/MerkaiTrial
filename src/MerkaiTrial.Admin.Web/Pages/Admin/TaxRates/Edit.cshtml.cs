// =====================================================================
// TAX RATES EDIT - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Admin/TaxRates/Edit.cshtml.cs
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
using Microsoft.Extensions.Logging;
using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace MerkaiTrial.Admin.Web.Pages.Admin.TaxRates
{
    // See Create.cshtml.cs — the folder convention gates this now.
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

        /// <summary>057: where this rate stands today, for the banner.</summary>
        public TaxRateState State { get; private set; } = TaxRateState.Active;
        public string StateLabel => TaxRateStatus.Label(State);
        public string StateBadge => TaxRateStatus.BadgeClass(State);
        public string StateExplain => TaxRateStatus.Explain(State);

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

            // ── 057 ──────────────────────────────────────────────────
            [DataType(DataType.Date)]
            [Display(Name = "In force from")]
            public DateTime? EffectiveFrom { get; set; }

            [DataType(DataType.Date)]
            [Display(Name = "In force until")]
            public DateTime? EffectiveTo { get; set; }

            /// <summary>
            /// 057. The manual retire switch. Nothing could set it before, so
            /// no rate has ever been switched off — and if one somehow had
            /// been, the list filtered it out and no page could switch it
            /// back on.
            /// </summary>
            [Display(Name = "Active")]
            public bool IsActive { get; set; } = true;
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

                // 057. EffectiveTo is stored as the last TICK of its day
                // (see Create) so that the last day counts. .Date on the way
                // back into the form turns 31 Mar 23:59:59.9999999 into
                // 31 Mar — which is the day the person typed.
                Input = new InputModel
                {
                    Name = taxRate.Name,
                    Rate = taxRate.Rate,
                    IsDefault = taxRate.IsDefault,
                    EffectiveFrom = taxRate.EffectiveFrom?.Date,
                    EffectiveTo = taxRate.EffectiveTo?.Date,
                    IsActive = taxRate.IsActive
                };

                State = TaxRateStatus.Of(
                    taxRate.IsActive, taxRate.EffectiveFrom, taxRate.EffectiveTo);

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

        /// <summary>
        /// 057. The three read-only fields, in one place. This block was
        /// copy-pasted into all three failure paths below, and the third copy
        /// is where a future edit would have been forgotten.
        /// </summary>
        private async Task ReloadReadOnlyAsync()
        {
            var taxRate = await _taxRateService.GetByIdAsync(Id);
            var country = await _countryService.GetByCodeAsync(taxRate.CountryCode);
            CountryCode = taxRate.CountryCode;
            CountryName = country.Name;
            TaxType = taxRate.TaxType;
            State = TaxRateStatus.Of(taxRate.IsActive, taxRate.EffectiveFrom, taxRate.EffectiveTo);
        }

        /// <summary>
        /// 057: the signed-in super admin, for UpdatedBy. Same helper as
        /// Create.cshtml.cs — off the principal, no service to register.
        /// </summary>
        private string CurrentUserName()
            => User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
               ?? User.Identity?.Name
               ?? "admin";

        public async Task<IActionResult> OnPostAsync()
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    await ReloadReadOnlyAsync();
                    return Page();
                }

                // 057 — the same validator as create and the API.
                var windowProblem = TaxRateStatus.ValidateWindow(Input.EffectiveFrom, Input.EffectiveTo);
                if (windowProblem is not null)
                {
                    ErrorMessage = windowProblem;
                    await ReloadReadOnlyAsync();
                    return Page();
                }

                var dto = new UpdateTaxRateDto
                {
                    Name = Input.Name.Trim(),
                    Rate = Input.Rate,
                    IsDefault = Input.IsDefault,

                    // See Create.cshtml.cs for why From is midnight and To is
                    // the last tick of its day.
                    EffectiveFrom = Input.EffectiveFrom.HasValue
                        ? DateTime.SpecifyKind(Input.EffectiveFrom.Value.Date, DateTimeKind.Utc)
                        : null,
                    EffectiveTo = Input.EffectiveTo.HasValue
                        ? DateTime.SpecifyKind(Input.EffectiveTo.Value.Date, DateTimeKind.Utc)
                                  .AddDays(1).AddTicks(-1)
                        : null,

                    IsActive = Input.IsActive,

                    // 057. This was never set, so every edit stored an empty
                    // UpdatedBy — on a row that decides what an entire country
                    // is charged. Same source as Create's CreatedBy.
                    UpdatedBy = CurrentUserName()
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
            catch (InvalidOperationException ex)
            {
                // 057. A business rule the API rejected — an inverted window,
                // or switching off the last active default. IApiService turns
                // a 400 {error} into this, so the message is the API's own
                // wording rather than "Failed to update tax rate".
                ErrorMessage = ex.Message;
                await ReloadReadOnlyAsync();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating tax rate {Id}", Id);
                ErrorMessage = "Failed to update tax rate. Please try again.";
                await ReloadReadOnlyAsync();
                return Page();
            }
        }
    }
}
