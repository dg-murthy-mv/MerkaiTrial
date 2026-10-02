// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Settings/CompanyProfile/Index.cshtml.cs
//
// NEW FILE (064). Who your quotes are FROM.
//
// Until this round the only seller-side fields on a workspace were Name
// and Phone, so the public quote page showed the customer their own
// company and never named the one that sent it. This is where those
// details get typed in.
//
// ONE PAGE, NOT Index + Edit. There is exactly one row to edit and the
// user is already the only person who can edit it, so a list to click
// through and a separate form would be two clicks and a page load in
// front of a form with eleven fields on it.
//
// MODULE: Settings, same as Tax Rates (058). Your own company's details
// are not something a super admin should have to fill in for you.
//
// WHAT IS NOT ON THIS PAGE, and why
//   Workspace name, plan, country, currency, timezone.
//
//   The country decides tax rates, currency, date format and number
//   grouping, and GetDefaultTaxRateAsync only ever looks for rates
//   matching it. Someone editing their letterhead must not be able to
//   change what their quotes are priced in by accident. Those are
//   platform operations and the API refuses them here regardless — the
//   company-profile endpoint cannot write them at all.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Tenants;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Settings.CompanyProfile
{
    public class IndexModel : AuthorizedPageModel
    {
        private readonly ITenantService _tenants;

        public IndexModel(
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ILogger<IndexModel> logger,
            ITenantService tenants)
            : base(authorizationService, currentUserService, logger)
        {
            _tenants = tenants;
        }

        protected override string ModuleName => Modules.Settings;

        [BindProperty] public InputModel Input { get; set; } = new();

        // ── Read-only context ─────────────────────────────────────────
        public string WorkspaceName { get; private set; } = string.Empty;
        public string CountryName { get; private set; } = string.Empty;
        public string CountryCode { get; private set; } = string.Empty;

        /// <summary>
        /// Country.TaxLabel — "GST", "VAT". The placeholder and the hint
        /// for the tax-number label, so an Indian workspace is nudged
        /// toward GSTIN rather than being shown an empty box.
        /// </summary>
        public string CountryTaxLabel { get; private set; } = "Tax";

        [TempData] public string? ErrorMessage { get; set; }
        [TempData] public string? SuccessMessage { get; set; }

        /// <summary>
        /// True when there is enough filled in for a quote to print a
        /// seller block. Mirrors Tenant.HasCompanyProfile — the page says
        /// the same thing the document will decide.
        /// </summary>
        public bool IsComplete =>
            !string.IsNullOrWhiteSpace(Input.AddressLine1)
            || !string.IsNullOrWhiteSpace(Input.TaxNumber);

        public class InputModel
        {
            [StringLength(200, ErrorMessage = "Legal name cannot exceed 200 characters")]
            [Display(Name = "Registered name")]
            public string? LegalName { get; set; }

            [StringLength(200, ErrorMessage = "Address line 1 cannot exceed 200 characters")]
            [Display(Name = "Address")]
            public string? AddressLine1 { get; set; }

            [StringLength(200, ErrorMessage = "Address line 2 cannot exceed 200 characters")]
            [Display(Name = "Address line 2")]
            public string? AddressLine2 { get; set; }

            [StringLength(100, ErrorMessage = "City cannot exceed 100 characters")]
            [Display(Name = "City")]
            public string? City { get; set; }

            [StringLength(100, ErrorMessage = "State cannot exceed 100 characters")]
            [Display(Name = "State / province")]
            public string? State { get; set; }

            [StringLength(20, ErrorMessage = "Postal code cannot exceed 20 characters")]
            [Display(Name = "Postal code")]
            public string? PostalCode { get; set; }

            [StringLength(50, ErrorMessage = "Phone cannot exceed 50 characters")]
            [Display(Name = "Phone")]
            public string? Phone { get; set; }

            [StringLength(255, ErrorMessage = "Website cannot exceed 255 characters")]
            [Display(Name = "Website")]
            public string? Website { get; set; }

            [StringLength(50, ErrorMessage = "Tax number cannot exceed 50 characters")]
            [Display(Name = "Tax number")]
            public string? TaxNumber { get; set; }

            [StringLength(50, ErrorMessage = "Label cannot exceed 50 characters")]
            [Display(Name = "Called")]
            public string? TaxNumberLabel { get; set; }

            // EmailAddress rather than free text: this is where a customer's
            // reply to a quote email lands, and a typo here means the reply
            // bounces into nowhere and nobody finds out.
            [EmailAddress(ErrorMessage = "That doesn't look like an email address")]
            [StringLength(320, ErrorMessage = "Reply-to email cannot exceed 320 characters")]
            [Display(Name = "Reply-to email")]
            public string? ReplyToEmail { get; set; }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            var denied = await ValidatePermissionAsync(Actions.Read);
            if (denied is not null) return denied;

            await InitializePermissionsAsync();

            try
            {
                var profile = await _tenants.GetCompanyProfileAsync();
                Apply(profile);
                return Page();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load the company profile");
                ErrorMessage = "Could not load your company details. Please try again.";

                // Still render. An empty form the user can fill in beats a
                // redirect to a page that does not explain what went wrong.
                CountryName = TenantCtx.GetCountryName();
                CountryCode = TenantCtx.GetCountryCode();
                CountryTaxLabel = TenantCtx.GetTaxLabel();
                WorkspaceName = TenantCtx.GetTenantName();
                return Page();
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var denied = await ValidatePermissionAsync(Actions.Update);
            if (denied is not null) return denied;

            await InitializePermissionsAsync();

            if (!ModelState.IsValid)
            {
                await ReloadContextAsync();
                return Page();
            }

            try
            {
                // UpdatedBy is left null — the API resolves who did it from
                // the token. A client asserting its own identity in a body
                // is not an identity.
                await _tenants.UpdateCompanyProfileAsync(new UpdateCompanyProfileCommand(
                    LegalName:      Trim(Input.LegalName),
                    AddressLine1:   Trim(Input.AddressLine1),
                    AddressLine2:   Trim(Input.AddressLine2),
                    City:           Trim(Input.City),
                    State:          Trim(Input.State),
                    PostalCode:     Trim(Input.PostalCode),
                    Phone:          Trim(Input.Phone),
                    Website:        Trim(Input.Website),
                    TaxNumber:      Trim(Input.TaxNumber),
                    TaxNumberLabel: Trim(Input.TaxNumberLabel),
                    ReplyToEmail:   Trim(Input.ReplyToEmail)));

                SuccessMessage = "Company details saved. New quotes will carry them.";
                return RedirectToPage();
            }
            catch (InvalidOperationException ex)
            {
                // The API's own sentence, not a second vaguer one.
                ErrorMessage = ex.Message;
                await ReloadContextAsync();
                return Page();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to save the company profile");
                ErrorMessage = "Could not save your company details. Please try again.";
                await ReloadContextAsync();
                return Page();
            }
        }

        /// <summary>
        /// The read-only labels around the form, for a redisplay after a
        /// validation failure. Deliberately does NOT touch Input — the
        /// user's unsaved typing must survive the round trip.
        /// </summary>
        private async Task ReloadContextAsync()
        {
            try
            {
                var profile = await _tenants.GetCompanyProfileAsync();
                WorkspaceName   = profile.Name;
                CountryName     = profile.CountryName ?? TenantCtx.GetCountryName();
                CountryCode     = profile.CountryCode ?? TenantCtx.GetCountryCode();
                CountryTaxLabel = Fallback(profile.CountryTaxLabel, TenantCtx.GetTaxLabel());
            }
            catch (Exception ex)
            {
                // Re-rendering the form with the user's typing intact
                // matters more than four labels.
                Logger.LogWarning(ex, "Could not reload company profile context for redisplay");
                WorkspaceName   = TenantCtx.GetTenantName();
                CountryName     = TenantCtx.GetCountryName();
                CountryCode     = TenantCtx.GetCountryCode();
                CountryTaxLabel = TenantCtx.GetTaxLabel();
            }
        }

        private void Apply(CompanyProfileDto p)
        {
            WorkspaceName   = p.Name;
            CountryName     = p.CountryName ?? TenantCtx.GetCountryName();
            CountryCode     = p.CountryCode ?? TenantCtx.GetCountryCode();
            CountryTaxLabel = Fallback(p.CountryTaxLabel, TenantCtx.GetTaxLabel());

            Input = new InputModel
            {
                LegalName      = p.LegalName,
                AddressLine1   = p.AddressLine1,
                AddressLine2   = p.AddressLine2,
                City           = p.City,
                State          = p.State,
                PostalCode     = p.PostalCode,
                Phone          = p.Phone,
                Website        = p.Website,
                TaxNumber      = p.TaxNumber,

                // Nothing on file yet: offer the country's own word for it
                // rather than an empty box. The user can overwrite it, and
                // the API applies the same fallback if they clear it.
                TaxNumberLabel = string.IsNullOrWhiteSpace(p.TaxNumberLabel)
                    ? Fallback(p.CountryTaxLabel, TenantCtx.GetTaxLabel())
                    : p.TaxNumberLabel,

                ReplyToEmail   = p.ReplyToEmail
            };
        }

        private static string Fallback(string? value, string orElse)
            => string.IsNullOrWhiteSpace(value) ? orElse : value;

        private static string? Trim(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
