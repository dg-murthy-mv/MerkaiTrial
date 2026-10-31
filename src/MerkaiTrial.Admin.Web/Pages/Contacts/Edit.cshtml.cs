// =====================================================================
// EDIT CONTACT - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Contacts/Edit.cshtml.cs
//
// ✅ MIGRATED to AuthorizedPageModel (was plain PageModel — had zero
// permission checks before this).
//
// 075 — CUSTOM FIELDS, AND THE SAME FIXES AS Create
//
//   1. Custom fields. Active fields render through the shared
//      _CustomFieldInputs partial, pre-filled from the contact's stored
//      values (ContactDto.CustomFieldValues), checked here first, and sent
//      as UpdateContactDto.CustomFields.
//
//      Only ACTIVE fields are on the form, and only active fields are
//      sent. A retired field's value is therefore ABSENT from the map,
//      and absent means "leave alone" — so an ordinary edit can never
//      wipe a value the Settings page has retired.
//
//      If the field list could not be loaded, CustomFields is sent as
//      NULL ("say nothing") so the save changes only the standard fields
//      and the page says why the custom ones are missing.
//
//   2. ErrorMessage is an ordinary property, not [TempData] — it was
//      shown on the next page instead of this one.
//
//   3. An InvalidOperationException's sentence is shown as-is.
//
//   4. Country is a dropdown of the Countries table, with the stored
//      value still offered if the table does not know it.
//
// 095 — [EmailRule] and [PhoneNumber] on the input model, as on Create.
//   A contact saved before this round with a number the rules refuse has
//   to have it corrected (or cleared) on its next edit — the message under
//   the box says what the number needs.
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
    public class EditModel : AuthorizedPageModel
    {
        private readonly IContactService       _contactService;
        private readonly ICurrentTenantService _tenantService;
        private readonly ICountryService       _countryService;
        private readonly ICustomFieldService   _customFields;

        protected override string ModuleName => Modules.Contacts;

        public EditModel(
            IContactService         contactService,
            ICurrentTenantService   tenantService,
            ICountryService         countryService,
            ICustomFieldService     customFields,
            IAuthorizationService   authorizationService,
            ICurrentUserService     currentUserService,
            ILogger<EditModel>      logger)
            : base(authorizationService, currentUserService, logger)
        {
            _contactService = contactService;
            _tenantService  = tenantService;
            _countryService = countryService;
            _customFields   = customFields;
        }

        [BindProperty] public Guid Id { get; set; }
        [BindProperty] public InputModel Input { get; set; } = new();

        /// <summary>075. Posted as CustomFields[&lt;field id&gt;] = value.</summary>
        [BindProperty] public Dictionary<string, string?> CustomFields { get; set; } = new();

        // ── 102: two people editing the same record ──────────────────
        /// <summary>
        /// The record's version when this form was loaded, carried in a
        /// hidden field and sent with the save. If someone else saved in
        /// between, the API refuses (409) instead of overwriting them.
        /// </summary>
        [BindProperty] public string? Version { get; set; }

        /// <summary>True when the save was refused because someone else changed the record. Shows the Reload link.</summary>
        public bool RecordChanged { get; private set; }

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

        // ✅ TENANT: exposed to view
        public string TenantCountryCode  { get; private set; } = string.Empty;
        public string TenantCurrencyCode { get; private set; } = string.Empty;

        /// <summary>Shown on THIS response — an ordinary property, not [TempData].</summary>
        public string? ErrorMessage { get; set; }

        public class InputModel
        {
            public Guid? CompanyId { get; set; }

            // 095 — the length messages say what to do; [EmailRule] and
            // [PhoneNumber] are the shared rules (InputRules), checked here
            // when the form posts and in the browser before it does. The
            // phone numbers are checked against the contact's own Country
            // when they are typed without a + code.
            [Required(ErrorMessage = "First name is required")]
            [StringLength(100, ErrorMessage = Keep)] public string FirstName { get; set; } = string.Empty;
            [StringLength(100, ErrorMessage = Keep)] public string? LastName { get; set; }
            [StringLength(100, ErrorMessage = Keep)] public string? JobTitle { get; set; }

            [EmailRule][StringLength(320, ErrorMessage = Keep)] public string? Email { get; set; }

            [PhoneNumber(CountryCodeProperty = nameof(Country))]
            [StringLength(32, ErrorMessage = Keep)] public string? Phone { get; set; }

            [PhoneNumber(CountryCodeProperty = nameof(Country))]
            [StringLength(32, ErrorMessage = Keep)] public string? Mobile { get; set; }

            [StringLength(500, ErrorMessage = Keep)] public string? Address { get; set; }
            [StringLength(100, ErrorMessage = Keep)] public string? City    { get; set; }

            // 100, matching the column — see Create.cshtml.cs.
            [StringLength(100)]
            [Display(Name = "Country")]
            public string? Country { get; set; }   // preserved from DB on load; tenant default if blank

            [StringLength(20, ErrorMessage = Keep)] public string? PostalCode { get; set; }

            /// <summary>095. "Keep it to 100 characters." — {1} is the limit.</summary>
            private const string Keep = "Keep it to {1} characters.";
            public string? Notes  { get; set; }
            public bool IsPrimary { get; set; }
        }

        public async Task<IActionResult> OnGetAsync(Guid id)
        {
            // ✅ Check UPDATE permission
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                Id = id;
                TenantCountryCode  = _tenantService.GetCountryCode();
                TenantCurrencyCode = _tenantService.GetCurrencyCode();

                var tenantId = CurrentUserService.GetCurrentTenantId();
                var contact  = await _contactService.GetByIdAsync(tenantId, id);
                Version = contact.Version;   // 102

                Input = new InputModel
                {
                    CompanyId  = contact.CompanyId,
                    FirstName  = contact.FirstName,
                    LastName   = contact.LastName,
                    JobTitle   = contact.JobTitle,
                    Email      = contact.Email,
                    Phone      = contact.Phone,
                    Mobile     = contact.Mobile,
                    Address    = contact.Address,
                    City       = contact.City,
                    // ✅ TENANT: if contact has no country saved yet, default to tenant country
                    Country    = !string.IsNullOrWhiteSpace(contact.Country)
                                    ? contact.Country
                                    : TenantCountryCode,
                    PostalCode = contact.PostalCode,
                    Notes      = contact.Notes,
                    IsPrimary  = contact.IsPrimary
                };

                // 075 — what the custom inputs start with.
                CustomFields = CustomFieldForm.FromStored(contact.CustomFieldValues);

                await LoadFormDataAsync();
                return Page();
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Contact not found.";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading contact {Id}", id);
                TempData["ErrorMessage"] = "Failed to load contact. Please try again.";
                return RedirectToPage("./Index");
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            // ✅ Check UPDATE permission
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            TenantCountryCode  = _tenantService.GetCountryCode();
            TenantCurrencyCode = _tenantService.GetCurrencyCode();

            await LoadFormDataAsync();

            // 075 — each field's message under its own input.
            CustomFieldForm.Validate(ActiveCustomFields, CustomFields, ModelState);

            if (!ModelState.IsValid)
                return Page();

            try
            {
                var currentUser = await CurrentUserService.GetCurrentUserAsync();

                var dto = new UpdateContactDto
                {
                    Id         = Id,
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
                    UpdatedBy  = currentUser.FullName,

                    // 075 — null when the field list could not be loaded:
                    // the form had no custom inputs, so say nothing about them.
                    CustomFields = CustomFieldsLoadFailed
                        ? null
                        : CustomFieldForm.ToSubmission(CustomFields, ActiveCustomFields),
                    Version = this.Version     // 102
                };

                await _contactService.UpdateAsync(Id, dto);
                TempData["SuccessMessage"] = "Contact updated successfully!";
                return RedirectToPage("./Detail", new { id = Id });
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Contact not found.";
                return RedirectToPage("./Index");
            }
            catch (MerkaiTrial.Infrastructure.Persistence.RecordChangedException ex)
            {
                // 102 — someone else saved this contact after the form was
                // loaded. Their change stands; what was typed stays in the form.
                RecordChanged = true;
                ErrorMessage = ex.Message;
                return Page();
            }
            catch (InvalidOperationException ex)
            {
                // The API's sentence — "Renewal date is required." — as-is.
                ErrorMessage = ex.Message;
                return Page();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error updating contact {Id}", Id);
                ErrorMessage = "Failed to update contact. Please try again.";
                return Page();
            }
        }

        private async Task LoadFormDataAsync()
        {
            await LoadCompaniesAsync();
            CountryOptions = await ContactFormOptions.CountriesAsync(_countryService, Input.Country, Logger);
            await LoadCustomFieldsAsync();
        }

        /// <summary>
        /// 108 — the first 20 companies by name, plus the one already chosen;
        /// the search box above the drop-down reaches every company
        /// (OnGetFindCompaniesAsync). This used to load every company in
        /// the workspace — up to 5,000 — into the page, and a company past
        /// the 5,000th could not be chosen at all.
        /// </summary>
        private async Task LoadCompaniesAsync()
        {
            try
            {
                var companies = await _contactService.FindCompaniesAsync(null, RecordSearch.Take);

                if (Input.CompanyId is Guid chosen && chosen != Guid.Empty && companies.All(c => c.Id != chosen))
                    companies.InsertRange(0, await _contactService.FindCompaniesAsync(null, 1, chosen));

                CompanyOptions = new SelectList(
                    companies,
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

        /// <summary>
        /// 108. GET ?handler=FindCompanies&amp;q=… — companies by name, from the
        /// whole workspace. JSON for wwwroot/js/record-search.js.
        /// </summary>
        public async Task<IActionResult> OnGetFindCompaniesAsync(string? q)
        {
            if (!await CanUpdateAsync()) return new JsonResult(Array.Empty<RecordSearchItem>()) { StatusCode = 403 };
            var term = RecordSearch.Clean(q);
            if (term is null) return new JsonResult(Array.Empty<RecordSearchItem>());

            try
            {
                var found = await _contactService.FindCompaniesAsync(term, RecordSearch.Take);
                return new JsonResult(found.Select(c => new RecordSearchItem(c.Id.ToString(), c.Name)).ToList());
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Company search on the contact form failed");
                return new JsonResult(Array.Empty<RecordSearchItem>()) { StatusCode = 500 };
            }
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
                Logger.LogWarning(ex, "Custom fields could not be loaded for the Edit Contact form");
                ActiveCustomFields     = new List<CustomFieldDefinitionDto>();
                CustomFieldsLoadFailed = true;
            }
        }
    }
}
