// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Settings/TaxRates/Create.cshtml.cs
//
// NEW FILE (058). A tax rate belonging to THIS workspace.
//
// THE COUNTRY IS NOT A DROPDOWN, and that is deliberate. A workspace has
// one country (Tenant.CountryId), and GetDefaultTaxRateAsync only ever
// looks for rates matching it. A rate created for anywhere else would sit
// in the table being visibly configured and silently unused, which is a
// worse failure than not being able to create it — nothing would ever
// tell you. The field shows the country, read-only.
//
// TenantId ON THE DTO: the API decides system-versus-tenant from whether
// this is empty, and then takes the ACTUAL owner from the token. Sending
// the workspace's own id is how this page says "mine, not a system rate";
// it is not trusted as an identity. See TaxRatesController (056).
// =====================================================================

using MerkaiTrial.Admin.Web.Services.TaxRates;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Common;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Settings.TaxRates
{
    public class CreateModel : AuthorizedPageModel
    {
        private readonly ITaxRateService _taxRates;

        public CreateModel(
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ILogger<CreateModel> logger,
            ITaxRateService taxRates)
            : base(authorizationService, currentUserService, logger)
        {
            _taxRates = taxRates;
        }

        protected override string ModuleName => Modules.Settings;

        [BindProperty] public InputModel Input { get; set; } = new();

        public string CountryCode { get; private set; } = string.Empty;
        public string CountryName { get; private set; } = string.Empty;
        public string TaxLabel { get; private set; } = "Tax";

        [TempData] public string? ErrorMessage { get; set; }

        public class InputModel
        {
            [Required(ErrorMessage = "Give the rate a name")]
            [StringLength(100, ErrorMessage = "Rate name cannot exceed 100 characters")]
            [Display(Name = "Rate name")]
            public string Name { get; set; } = string.Empty;

            [Required(ErrorMessage = "Pick a tax type")]
            [Display(Name = "Tax type")]
            public string? TaxType { get; set; }

            [Required(ErrorMessage = "Enter the rate")]
            [Range(0, 100, ErrorMessage = "The rate must be between 0 and 100")]
            [Display(Name = "Rate (%)")]
            public decimal Rate { get; set; }

            [Display(Name = "Use this as the default")]
            public bool IsDefault { get; set; } = true;

            // Dates, not date-times — a tax rate changes on a DAY, and a
            // time-of-day field would invite somebody to set 14:30 and then
            // wonder why a morning quote used the old rate.
            [DataType(DataType.Date)]
            [Display(Name = "In force from")]
            public DateTime? EffectiveFrom { get; set; }

            [DataType(DataType.Date)]
            [Display(Name = "In force until")]
            public DateTime? EffectiveTo { get; set; }
        }

        public SelectList TaxTypeOptions { get; private set; } =
            new(Enumerable.Empty<SelectListItem>());

        public async Task<IActionResult> OnGetAsync()
        {
            var denied = await ValidatePermissionAsync(Actions.Create);
            if (denied is not null) return denied;

            LoadContext();

            // The workspace's own word for tax is the obvious default —
            // an Indian tenant should not have to pick "GST" from a list
            // that also offers VAT.
            Input.TaxType = TaxLabel;

            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var denied = await ValidatePermissionAsync(Actions.Create);
            if (denied is not null) return denied;

            LoadContext();

            if (!ModelState.IsValid) return Page();

            // The same validator the API and the command use, so all three
            // reject an inverted window with the same sentence.
            var windowProblem = TaxRateStatus.ValidateWindow(Input.EffectiveFrom, Input.EffectiveTo);
            if (windowProblem is not null)
            {
                ModelState.AddModelError("Input.EffectiveTo", windowProblem);
                return Page();
            }

            try
            {
                var dto = new CreateTaxRateDto
                {
                    // Non-empty = "a rate for my workspace, not a system
                    // rate". The API takes the real owner from the token.
                    TenantId = TenantCtx.GetTenantId(),

                    CountryCode = CountryCode,
                    Name = Input.Name.Trim(),
                    TaxType = Input.TaxType!,
                    Rate = Input.Rate,
                    IsDefault = Input.IsDefault,

                    // Midnight UTC, specified rather than converted: a date
                    // put through ToUniversalTime() from an unspecified
                    // midnight moves back a day on any server east of UTC.
                    EffectiveFrom = Input.EffectiveFrom.HasValue
                        ? DateTime.SpecifyKind(Input.EffectiveFrom.Value.Date, DateTimeKind.Utc)
                        : null,

                    // The END of the last day. With midnight, a rate "in
                    // force until 31 March" would stop applying at 00:00 on
                    // the 31st — not in force on its own last day.
                    EffectiveTo = Input.EffectiveTo.HasValue
                        ? DateTime.SpecifyKind(Input.EffectiveTo.Value.Date, DateTimeKind.Utc)
                                  .AddDays(1).AddTicks(-1)
                        : null,

                    CreatedBy = TenantCtx.GetUserEmail()
                };

                await _taxRates.CreateAsync(dto);

                TempData["SuccessMessage"] = $"Tax rate “{Input.Name.Trim()}” added.";
                return RedirectToPage("./Index");
            }
            catch (InvalidOperationException ex)
            {
                // A rule the API rejected — a duplicate name, an unknown
                // country. IApiService turns a 400 {error} into this, so the
                // message is the API's own wording rather than a second,
                // vaguer one written here.
                ErrorMessage = ex.Message;
                return Page();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to create tax rate for {Country}", CountryCode);
                ErrorMessage = "Could not add the tax rate. Please try again.";
                return Page();
            }
        }

        private void LoadContext()
        {
            CountryCode = TenantCtx.GetCountryCode();
            CountryName = TenantCtx.GetCountryName();
            TaxLabel = TenantCtx.GetTaxLabel();

            // The workspace's own label first, then the rest. Anything the
            // tenant already uses should be at the top of its own list.
            var types = new List<string> { TaxLabel };
            foreach (var t in new[] { "VAT", "GST", "Sales Tax", "Service Tax", "None" })
                if (!types.Contains(t, StringComparer.OrdinalIgnoreCase))
                    types.Add(t);

            TaxTypeOptions = new SelectList(types, Input.TaxType);
        }
    }
}
