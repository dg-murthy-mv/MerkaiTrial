// =====================================================================
// EDIT COMPANY - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Companies/Edit.cshtml.cs
//
// ✅ MIGRATED to AuthorizedPageModel (was plain PageModel — had zero
// permission checks before this).
//
// TENANT FIXES (unchanged from before):
//   - Country falls back to tenant country if blank on existing record
//
// 078 — CUSTOM FIELDS, AND THE SAME FIXES AS Create
//
//   1. CUSTOM FIELDS. Active Company fields render through the shared
//      _CustomFieldInputs partial, pre-filled from the company's stored
//      values (CompanyDto.CustomFieldValues), checked here first, and sent
//      as UpdateCompanyDto.CustomFields.
//
//      Only ACTIVE fields are on the form, and only active fields are
//      sent. A retired field's value is therefore ABSENT from the map, and
//      absent means "leave alone" — so an ordinary edit can never wipe a
//      value the Settings page has retired.
//
//      If the field list could not be loaded, CustomFields is sent as NULL
//      ("say nothing") so the save changes only the standard fields and
//      the page says why the custom ones are missing.
//
//   2. ErrorMessage is an ordinary property, not [TempData] — it was shown
//      on the next page instead of this one.
//
//   3. AN InvalidOperationException'S SENTENCE IS SHOWN AS-IS. Before, the
//      only catch was the generic one, so the API's "Renewal date must be
//      a valid date." became "Failed to update company" — and was logged
//      as a system error.
//
//   4. A vertical or country the lists no longer contain is still offered
//      and selected (CompanyFormOptions), so opening Edit to fix a typo
//      never forces a different country onto the company.
//
//   5. TenantCountryCode / TenantCurrencyCode were exposed to the view and
//      never used; the ICurrentTenantService injection went with them.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Pages.Shared;
using MerkaiTrial.Admin.Web.Services.Companies;
using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Admin.Web.Services.CustomFields;
using MerkaiTrial.Admin.Web.Services.Meta;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Companies
{
    public class EditModel : AuthorizedPageModel
    {
        private readonly ICompanyService     _companyService;
        private readonly ICountryService     _countryService;
        private readonly IMetaService        _metaService;
        private readonly ICustomFieldService _customFields;

        protected override string ModuleName => Modules.Companies;

        public EditModel(
            ICompanyService         companyService,
            ICountryService         countryService,
            IMetaService            metaService,
            ICustomFieldService     customFields,
            IAuthorizationService   authorizationService,
            ICurrentUserService     currentUserService,
            ILogger<EditModel>      logger)
            : base(authorizationService, currentUserService, logger)
        {
            _companyService = companyService;
            _countryService = countryService;
            _metaService    = metaService;
            _customFields   = customFields;
        }

        [BindProperty] public Guid Id { get; set; }
        [BindProperty] public InputModel Input { get; set; } = new();

        /// <summary>078. Posted as CustomFields[&lt;field id&gt;] = value.</summary>
        [BindProperty] public Dictionary<string, string?> CustomFields { get; set; } = new();

        public List<SelectListItem> CountryOptions  { get; private set; } = new();
        public List<SelectListItem> VerticalOptions { get; private set; } = new();

        /// <summary>078. Active custom fields for Companies, in order.</summary>
        public List<CustomFieldDefinitionDto> ActiveCustomFields { get; private set; } = new();
        public bool CustomFieldsLoadFailed { get; private set; }

        /// <summary>078. The model for _CustomFieldInputs.</summary>
        public CustomFieldFormVm CustomFieldInputs => new()
        {
            Fields       = ActiveCustomFields,
            Values       = CustomFields,
            LoadFailed   = CustomFieldsLoadFailed,
            CanConfigure = UserCanRead(Modules.Settings),
            EntityType   = CustomFieldEntityTypes.Company
        };

        /// <summary>Shown on THIS response — an ordinary property, not [TempData]. Note 2.</summary>
        public string? ErrorMessage { get; set; }

        public class InputModel
        {
            [Required(ErrorMessage = "Company name is required")]
            [StringLength(200)]
            [Display(Name = "Company name")]
            public string Name { get; set; } = string.Empty;

            [Required(ErrorMessage = "Please select a vertical")]
            public string? Vertical { get; set; }

            [Required(ErrorMessage = "Please select a country")]
            [StringLength(5)]
            public string? Country { get; set; }

            [StringLength(64)]
            [Display(Name = "Tax ID")]
            public string? TaxId { get; set; }
        }

        public async Task<IActionResult> OnGetAsync(Guid id)
        {
            // ✅ Check UPDATE permission
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            // Every failure below REDIRECTS, so TempData is right for it.
            try
            {
                Id = id;

                var tenantId = CurrentUserService.GetCurrentTenantId();
                var company  = await _companyService.GetByIdAsync(tenantId, id);

                Input = new InputModel
                {
                    Name     = company.Name,
                    Vertical = company.Vertical,
                    // ✅ TENANT: if country missing on old record, fall back to tenant default
                    Country  = !string.IsNullOrWhiteSpace(company.Country)
                                   ? company.Country
                                   : TenantCtx.GetCountryCode(),
                    TaxId    = company.TaxId
                };

                // 078 — what the custom inputs start with.
                CustomFields = CustomFieldForm.FromStored(company.CustomFieldValues);

                await LoadFormDataAsync();
                return Page();
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Company not found.";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading company {Id}", id);
                TempData["ErrorMessage"] = "Failed to load company. Please try again.";
                return RedirectToPage("./Index");
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            // ✅ Check UPDATE permission
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            await LoadFormDataAsync();

            // 078 — each custom field's message under its own input.
            CustomFieldForm.Validate(ActiveCustomFields, CustomFields, ModelState);

            if (!ModelState.IsValid)
                return Page();

            try
            {
                var tenantId    = CurrentUserService.GetCurrentTenantId();
                var currentUser = await CurrentUserService.GetCurrentUserAsync();

                var dto = new UpdateCompanyDto
                {
                    Id        = Id,
                    TenantId  = tenantId,
                    Name      = Input.Name.Trim(),
                    Vertical  = Input.Vertical!.Trim(),
                    Country   = Input.Country!.Trim(),
                    TaxId     = string.IsNullOrWhiteSpace(Input.TaxId) ? null : Input.TaxId.Trim(),
                    UpdatedBy = currentUser.FullName,

                    // 078 — null when the field list could not be loaded:
                    // the form had no custom inputs, so say nothing about them.
                    CustomFields = CustomFieldsLoadFailed
                        ? null
                        : CustomFieldForm.ToSubmission(CustomFields, ActiveCustomFields)
                };

                await _companyService.UpdateAsync(Id, dto);

                // Redirect → the layout shows this once on the Detail page.
                TempData["SuccessMessage"] = "Company updated successfully!";
                return RedirectToPage("./Detail", new { id = Id });
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Company not found.";
                return RedirectToPage("./Index");
            }
            catch (InvalidOperationException ex)
            {
                // The API's sentence — "Renewal date is required." — as-is. Note 3.
                ErrorMessage = ex.Message;
                return Page();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error updating company {Id}", Id);
                ErrorMessage = "Failed to update company. Please try again.";
                return Page();
            }
        }

        // =============================================================
        // Helpers
        // =============================================================

        private async Task LoadFormDataAsync()
        {
            CountryOptions  = await CompanyFormOptions.CountriesAsync(_countryService, Input.Country, Logger);
            VerticalOptions = await CompanyFormOptions.VerticalsAsync(_metaService, Input.Vertical, Logger);
            await LoadCustomFieldsAsync();
        }

        private async Task LoadCustomFieldsAsync()
        {
            try
            {
                ActiveCustomFields = (await _customFields.GetDefinitionsAsync(CustomFieldEntityTypes.Company, includeInactive: false))
                    .Where(f => f.IsActive)
                    .ToList();
                CustomFieldsLoadFailed = false;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Custom fields could not be loaded for the Edit Company form");
                ActiveCustomFields     = new List<CustomFieldDefinitionDto>();
                CustomFieldsLoadFailed = true;
            }
        }
    }
}
