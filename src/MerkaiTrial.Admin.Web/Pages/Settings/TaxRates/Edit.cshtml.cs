// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Settings/TaxRates/Edit.cshtml.cs
//
// NEW FILE (058).
//
// A workspace may edit ITS OWN rates and no others. This page refuses a
// system rate before it renders — but that refusal is a courtesy, not the
// control: 056's TaxRateOwnership refuses the same write at the API with
// a 403, and another workspace's rate with a 404, whatever any page does.
//
// COUNTRY AND TAX TYPE ARE LOCKED after creation, the same as the admin
// screen. Changing either would silently move the rate to a country this
// workspace does not sell in, where nothing would ever use it again and
// nothing would say so.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.TaxRates;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Common;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Settings.TaxRates
{
    public class EditModel : AuthorizedPageModel
    {
        private readonly ITaxRateService _taxRates;

        public EditModel(
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ILogger<EditModel> logger,
            ITaxRateService taxRates)
            : base(authorizationService, currentUserService, logger)
        {
            _taxRates = taxRates;
        }

        protected override string ModuleName => Modules.Settings;

        [BindProperty] public Guid Id { get; set; }
        [BindProperty] public InputModel Input { get; set; } = new();

        public string CountryCode { get; private set; } = string.Empty;
        public string CountryName { get; private set; } = string.Empty;
        public string TaxType { get; private set; } = string.Empty;

        public TaxRateState State { get; private set; } = TaxRateState.Active;
        public string StateLabel => TaxRateStatus.Label(State);
        public string StateBadge => TaxRateStatus.BadgeClass(State);
        public string StateExplain => TaxRateStatus.Explain(State);

        [TempData] public string? ErrorMessage { get; set; }

        public class InputModel
        {
            [Required(ErrorMessage = "Give the rate a name")]
            [StringLength(100, ErrorMessage = "Rate name cannot exceed 100 characters")]
            [Display(Name = "Rate name")]
            public string Name { get; set; } = string.Empty;

            [Required(ErrorMessage = "Enter the rate")]
            [Range(0, 100, ErrorMessage = "The rate must be between 0 and 100")]
            [Display(Name = "Rate (%)")]
            public decimal Rate { get; set; }

            [Display(Name = "Use this as the default")]
            public bool IsDefault { get; set; }

            [DataType(DataType.Date)]
            [Display(Name = "In force from")]
            public DateTime? EffectiveFrom { get; set; }

            [DataType(DataType.Date)]
            [Display(Name = "In force until")]
            public DateTime? EffectiveTo { get; set; }

            [Display(Name = "Active")]
            public bool IsActive { get; set; } = true;
        }

        public async Task<IActionResult> OnGetAsync(Guid id)
        {
            var denied = await ValidatePermissionAsync(Actions.Update);
            if (denied is not null) return denied;

            if (id == Guid.Empty)
            {
                TempData["ErrorMessage"] = "Which tax rate?";
                return RedirectToPage("./Index");
            }

            Id = id;

            try
            {
                var rate = await _taxRates.GetByIdAsync(id);

                // A system rate is readable here — the list shows them so a
                // workspace can see what its quotes actually use — but not
                // editable. Sent back rather than shown in a form that would
                // fail on save.
                if (rate.IsSystem)
                {
                    TempData["ErrorMessage"] =
                        "That is a standard rate for your country, shared by every workspace, " +
                        "so it can't be changed here. Add your own rate instead and it will be " +
                        "used in place of it.";
                    return RedirectToPage("./Index");
                }

                CountryCode = rate.CountryCode;
                CountryName = TenantCtx.GetCountryName();
                TaxType = rate.TaxType;

                // EffectiveTo is stored as the last TICK of its day so the
                // last day counts; .Date turns 31 Mar 23:59:59.9999999 back
                // into the 31 Mar the person typed.
                Input = new InputModel
                {
                    Name = rate.Name,
                    Rate = rate.Rate,
                    IsDefault = rate.IsDefault,
                    EffectiveFrom = rate.EffectiveFrom?.Date,
                    EffectiveTo = rate.EffectiveTo?.Date,
                    IsActive = rate.IsActive
                };

                State = TaxRateStatus.Of(rate.IsActive, rate.EffectiveFrom, rate.EffectiveTo);

                return Page();
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "That tax rate no longer exists.";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load tax rate {Id}", id);
                TempData["ErrorMessage"] = "Could not open that tax rate.";
                return RedirectToPage("./Index");
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var denied = await ValidatePermissionAsync(Actions.Update);
            if (denied is not null) return denied;

            if (!ModelState.IsValid)
            {
                await ReloadReadOnlyAsync();
                return Page();
            }

            var windowProblem = TaxRateStatus.ValidateWindow(Input.EffectiveFrom, Input.EffectiveTo);
            if (windowProblem is not null)
            {
                ModelState.AddModelError("Input.EffectiveTo", windowProblem);
                await ReloadReadOnlyAsync();
                return Page();
            }

            try
            {
                var dto = new UpdateTaxRateDto
                {
                    Name = Input.Name.Trim(),
                    Rate = Input.Rate,
                    IsDefault = Input.IsDefault,

                    EffectiveFrom = Input.EffectiveFrom.HasValue
                        ? DateTime.SpecifyKind(Input.EffectiveFrom.Value.Date, DateTimeKind.Utc)
                        : null,
                    EffectiveTo = Input.EffectiveTo.HasValue
                        ? DateTime.SpecifyKind(Input.EffectiveTo.Value.Date, DateTimeKind.Utc)
                                  .AddDays(1).AddTicks(-1)
                        : null,

                    IsActive = Input.IsActive,
                    UpdatedBy = TenantCtx.GetUserEmail()
                };

                await _taxRates.UpdateAsync(Id, dto);

                TempData["SuccessMessage"] = $"Tax rate “{Input.Name.Trim()}” saved.";
                return RedirectToPage("./Index");
            }
            catch (KeyNotFoundException)
            {
                // 056 answers "somebody else's rate" with 404 rather than
                // 403, on purpose — confirming a row exists but belongs to
                // another workspace is itself a leak. So this is the honest
                // message for both cases.
                TempData["ErrorMessage"] = "That tax rate no longer exists.";
                return RedirectToPage("./Index");
            }
            catch (InvalidOperationException ex)
            {
                // A rule the API rejected — an inverted window, or switching
                // off the last active default. Its wording, not a second one.
                ErrorMessage = ex.Message;
                await ReloadReadOnlyAsync();
                return Page();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to update tax rate {Id}", Id);
                ErrorMessage = "Could not save the tax rate. Please try again.";
                await ReloadReadOnlyAsync();
                return Page();
            }
        }

        /// <summary>
        /// The read-only fields, in one place. The admin Edit page had this
        /// block copy-pasted into three failure paths, and the third copy is
        /// where a future edit gets forgotten.
        /// </summary>
        private async Task ReloadReadOnlyAsync()
        {
            try
            {
                var rate = await _taxRates.GetByIdAsync(Id);
                CountryCode = rate.CountryCode;
                CountryName = TenantCtx.GetCountryName();
                TaxType = rate.TaxType;
                State = TaxRateStatus.Of(rate.IsActive, rate.EffectiveFrom, rate.EffectiveTo);
            }
            catch (Exception ex)
            {
                // Re-rendering the form matters more than these three labels.
                Logger.LogWarning(ex, "Could not reload tax rate {Id} for redisplay", Id);
                CountryName = TenantCtx.GetCountryName();
            }
        }
    }
}
