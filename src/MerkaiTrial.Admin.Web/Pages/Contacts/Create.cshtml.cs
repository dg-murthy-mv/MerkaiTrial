// =====================================================================
// CREATE CONTACT - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Contacts/Create.cshtml.cs
//
// ✅ MIGRATED to AuthorizedPageModel (was plain PageModel — had zero
// permission checks before this).
//
// 075 — CUSTOM FIELDS, AND THREE FIXES ON THE WAY THROUGH
//
//   1. Custom fields. The active fields for Contacts render in an
//      "Additional details" section through the shared
//      _CustomFieldInputs partial, are checked here first (so each
//      message lands under its own input), and travel to the API as
//      CreateContactDto.CustomFields. The API checks them again; this is
//      the friendly copy, not the guard.
//
//   2. ErrorMessage WAS [TempData] AND THE PAGE RENDERED. A [TempData]
//      property set on a request that returns Page() is saved for the
//      NEXT request — so "Failed to create contact" appeared on whatever
//      page the person opened after this one, and not here. It is an
//      ordinary property now.
//
//   3. EVERY FAILURE SAID "Failed to create contact." The plan-limit
//      message ContactService builds, and now every custom field
//      message, arrive as InvalidOperationException carrying a sentence
//      for the person. That sentence is shown as-is now; only a genuine
//      fault falls through to the generic one.
//
//   4. COUNTRY IS A DROPDOWN of the Countries table (ICountryService),
//      not a free 5-character box. A typed "Thailand" or "TH " was saved
//      as-is and the Detail page could not resolve it. A legacy value not
//      in the table is still offered, so an Edit never silently changes it.
//
//   On success it now goes to the new contact's Detail page rather than
//   the list, so the person sees what they saved — including the custom
//   fields — instead of hunting for it.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Pages.Shared;
using MerkaiTrial.Admin.Web.Services.Contacts;
using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Admin.Web.Services.CustomFields;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Contacts
{
    public class CreateModel : AuthorizedPageModel
    {
        private readonly IContactService       _contactService;
        private readonly ICurrentTenantService _tenantService;
        private readonly ICountryService       _countryService;
        private readonly ICustomFieldService   _customFields;

        protected override string ModuleName => Modules.Contacts;

        public CreateModel(
            IContactService         contactService,
            ICurrentTenantService   tenantService,
            ICountryService         countryService,
            ICustomFieldService     customFields,
            IAuthorizationService   authorizationService,
            ICurrentUserService     currentUserService,
            ILogger<CreateModel>    logger)
            : base(authorizationService, currentUserService, logger)
        {
            _contactService = contactService;
            _tenantService  = tenantService;
            _countryService = countryService;
            _customFields   = customFields;
        }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        /// <summary>
        /// 075. Posted as CustomFields[&lt;field id&gt;] = value. String keys —
        /// see CustomFieldComponents.cs for why.
        /// </summary>
        [BindProperty]
        public Dictionary<string, string?> CustomFields { get; set; } = new();

        public SelectList CompanyOptions { get; set; } = new SelectList(Enumerable.Empty<SelectListItem>());
        public List<SelectListItem> CountryOptions { get; private set; } = new();

        /// <summary>075. Active custom fields for Contacts, in order.</summary>
        public List<CustomFieldDefinitionDto> ActiveCustomFields { get; private set; } = new();
        public bool CustomFieldsLoadFailed { get; private set; }

        /// <summary>075. The model for _CustomFieldInputs.</summary>
        public CustomFieldFormVm CustomFieldInputs => new()
        {
            Fields       = ActiveCustomFields,
            Values       = CustomFields,
            LoadFailed   = CustomFieldsLoadFailed,
            CanConfigure = UserCanRead(Modules.Settings),
            EntityType   = CustomFieldEntityTypes.Contact
        };

        // ✅ TENANT: exposed to view for placeholder hints
        public string TenantCountryCode  { get; private set; } = string.Empty;
        public string TenantCurrencyCode { get; private set; } = string.Empty;

        /// <summary>
        /// Shown on THIS response. An ordinary property, not [TempData] —
        /// see note 2 in the header.
        /// </summary>
        public string? ErrorMessage { get; set; }

        public class InputModel
        {
            public Guid? CompanyId { get; set; }

            [Required(ErrorMessage = "First name is required")]
            [StringLength(100)] public string FirstName { get; set; } = string.Empty;
            [StringLength(100)] public string? LastName { get; set; }
            [StringLength(100)] public string? JobTitle { get; set; }

            [EmailAddress][StringLength(320)] public string? Email { get; set; }
            [StringLength(32)] public string? Phone   { get; set; }
            [StringLength(32)] public string? Mobile  { get; set; }
            [StringLength(500)] public string? Address { get; set; }
            [StringLength(100)] public string? City    { get; set; }

            // 100, matching the column. The old 5 was a free-text guard;
            // the value now comes from a dropdown, and a legacy value
            // longer than 5 must not make the form impossible to save.
            [StringLength(100)]
            [Display(Name = "Country")]
            public string? Country { get; set; }  // ✅ TENANT: defaulted to tenant country on GET

            [StringLength(20)] public string? PostalCode { get; set; }
            public string? Notes    { get; set; }
            public bool IsPrimary   { get; set; }
        }

        public async Task<IActionResult> OnGetAsync(Guid? companyId = null)
        {
            // ✅ Check CREATE permission
            var permissionCheck = await ValidatePermissionAsync(Actions.Create);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                TenantCountryCode  = _tenantService.GetCountryCode();
                TenantCurrencyCode = _tenantService.GetCurrencyCode();

                // ✅ TENANT: pre-fill country with tenant default
                Input.Country = TenantCountryCode;

                if (companyId.HasValue)
                    Input.CompanyId = companyId;

                await LoadFormDataAsync();
                return Page();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading Create Contact form");
                ErrorMessage = "Failed to load form. Please try again.";
                return Page();
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            // ✅ Check CREATE permission
            var permissionCheck = await ValidatePermissionAsync(Actions.Create);
            if (permissionCheck != null) return permissionCheck;

            TenantCountryCode  = _tenantService.GetCountryCode();
            TenantCurrencyCode = _tenantService.GetCurrencyCode();

            // Needed both to check the posted values and to re-render.
            await LoadFormDataAsync();

            // 075 — each field's message under its own input.
            CustomFieldForm.Validate(ActiveCustomFields, CustomFields, ModelState);

            if (!ModelState.IsValid)
                return Page();

            try
            {
                var currentUser = await CurrentUserService.GetCurrentUserAsync();

                var dto = new CreateContactDto
                {
                    TenantId   = CurrentUserService.GetCurrentTenantId(),
                    CompanyId  = Input.CompanyId,
                    FirstName  = Input.FirstName,
                    LastName   = Input.LastName,
                    JobTitle   = Input.JobTitle,
                    Email      = Input.Email,
                    Phone      = Input.Phone,
                    Mobile     = Input.Mobile,
                    Address    = Input.Address,
                    City       = Input.City,
                    Country    = Input.Country,
                    PostalCode = Input.PostalCode,
                    Notes      = Input.Notes,
                    IsPrimary  = Input.IsPrimary,
                    CreatedBy  = currentUser.FullName,

                    // 075. If the field list could not be loaded the form had
                    // no custom inputs, so say NOTHING (null) rather than an
                    // empty map — a required-field check against inputs the
                    // person never saw would be a refusal they cannot fix.
                    CustomFields = CustomFieldsLoadFailed
                        ? null
                        : CustomFieldForm.ToSubmission(CustomFields, ActiveCustomFields)
                };

                var contact = await _contactService.CreateAsync(dto);
                TempData["SuccessMessage"] = $"Contact '{contact.DisplayName}' created successfully!";
                return RedirectToPage("./Detail", new { id = contact.Id });
            }
            catch (InvalidOperationException ex)
            {
                // The API's sentence (a custom field rule) or ContactService's
                // (the plan limit). Written for the person; shown as-is.
                ErrorMessage = ex.Message;
                return Page();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error creating contact");
                ErrorMessage = "Failed to create contact. Please try again.";
                return Page();
            }
        }

        private async Task LoadFormDataAsync()
        {
            await LoadCompaniesAsync();
            await LoadCountriesAsync();
            await LoadCustomFieldsAsync();
        }

        private async Task LoadCompaniesAsync()
        {
            try
            {
                var tenantId  = CurrentUserService.GetCurrentTenantId();
                var companies = await _contactService.GetCompaniesLookupAsync(tenantId);
                CompanyOptions = new SelectList(
                    companies.OrderBy(c => c.Name),
                    nameof(CompanyListItem.Id),
                    nameof(CompanyListItem.Name),
                    Input.CompanyId);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load companies dropdown");
                CompanyOptions = new SelectList(Enumerable.Empty<SelectListItem>());
            }
        }

        private async Task LoadCountriesAsync()
        {
            CountryOptions = await ContactFormOptions.CountriesAsync(_countryService, Input.Country, Logger);
        }

        private async Task LoadCustomFieldsAsync()
        {
            try
            {
                ActiveCustomFields = (await _customFields.GetDefinitionsAsync(CustomFieldEntityTypes.Contact, includeInactive: false))
                    .Where(f => f.IsActive)
                    .ToList();
                CustomFieldsLoadFailed = false;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Custom fields could not be loaded for the Create Contact form");
                ActiveCustomFields     = new List<CustomFieldDefinitionDto>();
                CustomFieldsLoadFailed = true;
            }
        }
    }
}
