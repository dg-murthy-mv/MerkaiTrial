// =====================================================================
// CREATE COMPANY - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Companies/Create.cshtml.cs
//
// ✅ MIGRATED to AuthorizedPageModel (was plain PageModel — had zero
// permission checks before this).
//
// TENANT FIXES (unchanged from before):
//   - Input.Country defaults to tenant country code on GET
//
// 078 — CUSTOM FIELDS, AND THE FIXES Contacts/Create GOT IN 075
//
//   1. CUSTOM FIELDS. Active Company fields render through the shared
//      _CustomFieldInputs partial, are checked here first (so each message
//      sits under its own input), and are sent as
//      CreateCompanyDto.CustomFields. The API checks them again; this is
//      for the person, that is for the data. If the field list cannot be
//      loaded, CustomFields is sent as NULL ("say nothing") and the form
//      says why the extra fields are missing.
//
//   2. ErrorMessage WAS [TempData] AND THE PAGE RENDERED. A [TempData]
//      property set on a response that renders is shown on the NEXT
//      request — so "You have reached your plan's company limit" appeared
//      one click late, on whatever page came next, and this form showed
//      nothing. It is an ordinary property now: shown on this response.
//
//   3. AFTER CREATING, the person lands on the NEW COMPANY'S page, not the
//      list — where they can see what was saved, add contacts and fill in
//      anything else. (Contacts and Deals do the same.)
//
//   4. A VALIDATION FAILURE reloads the dropdowns and custom fields in one
//      place (LoadFormDataAsync) instead of four copies of the same three
//      lines. The Vertical and Country lists come from CompanyFormOptions,
//      shared with Edit.
//
//   5. TenantCountryCode / TenantCurrencyCode were exposed to the view and
//      never used by it; the ICurrentTenantService injection went with
//      them — the base class's TenantCtx gives the country code.
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
    public class CreateModel : AuthorizedPageModel
    {
        private readonly ICompanyService     _companyService;
        private readonly ICountryService     _countryService;
        private readonly IMetaService        _metaService;
        private readonly ICustomFieldService _customFields;

        protected override string ModuleName => Modules.Companies;

        public CreateModel(
            ICompanyService         companyService,
            ICountryService         countryService,
            IMetaService            metaService,
            ICustomFieldService     customFields,
            IAuthorizationService   authorizationService,
            ICurrentUserService     currentUserService,
            ILogger<CreateModel>    logger)
            : base(authorizationService, currentUserService, logger)
        {
            _companyService = companyService;
            _countryService = countryService;
            _metaService    = metaService;
            _customFields   = customFields;
        }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        /// <summary>078. Posted as CustomFields[&lt;field id&gt;] = value.</summary>
        [BindProperty]
        public Dictionary<string, string?> CustomFields { get; set; } = new();

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
            public string? Country { get; set; }  // ✅ TENANT: defaulted in OnGetAsync

            [StringLength(64)]
            [Display(Name = "Tax ID")]
            public string? TaxId { get; set; }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            // ✅ Check CREATE permission
            var permissionCheck = await ValidatePermissionAsync(Actions.Create);
            if (permissionCheck != null) return permissionCheck;

            // ✅ TENANT: pre-fill country with tenant default
            Input.Country = TenantCtx.GetCountryCode();

            await LoadFormDataAsync();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            // ✅ Check CREATE permission
            var permissionCheck = await ValidatePermissionAsync(Actions.Create);
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

                var dto = new CreateCompanyDto
                {
                    TenantId  = tenantId,
                    Name      = Input.Name.Trim(),
                    Vertical  = Input.Vertical!.Trim(),
                    Country   = Input.Country!.Trim(),
                    TaxId     = string.IsNullOrWhiteSpace(Input.TaxId) ? null : Input.TaxId.Trim(),
                    CreatedBy = currentUser.FullName,

                    // 078 — null when the field list could not be loaded:
                    // the form had no custom inputs, so say nothing about them.
                    CustomFields = CustomFieldsLoadFailed
                        ? null
                        : CustomFieldForm.ToSubmission(CustomFields, ActiveCustomFields)
                };

                var company = await _companyService.CreateAsync(dto);

                // TempData is right here: this response REDIRECTS, and the
                // layout shows the message once on the page it lands on.
                TempData["SuccessMessage"] = $"Company '{company.Name}' created successfully!";
                return RedirectToPage("./Detail", new { id = company.Id });   // note 3
            }
            catch (InvalidOperationException ex)
            {
                // The plan limit, or the API's sentence about a custom
                // field — "Renewal date must be a valid date." — as-is.
                ErrorMessage = ex.Message;
                return Page();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error creating company");
                ErrorMessage = "Failed to create company. Please try again.";
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
                Logger.LogWarning(ex, "Custom fields could not be loaded for the Create Company form");
                ActiveCustomFields     = new List<CustomFieldDefinitionDto>();
                CustomFieldsLoadFailed = true;
            }
        }
    }
}
