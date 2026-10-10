// =====================================================================
// DETAIL COMPANY - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Companies/Detail.cshtml.cs
//
// ✅ MIGRATED to AuthorizedPageModel (was AppPageModel).
//
// 078 — CUSTOM FIELDS
//
//   "Additional details" card: every active Company custom field, plus
//   any retired field this company still has a value in, through the
//   shared _CustomFieldValues partial (the same one Contacts and Deals
//   use). Numbers in the tenant's number culture, dates in the tenant's
//   date pattern and NEVER shifted by timezone. Its Edit link shows only
//   to people who can update companies; "Manage fields" only to people
//   who can read Settings. If the field list cannot be loaded the card
//   says so — the rest of the page still renders.
//
//   The "Add Contact" button checked UserCanCreate("contacts") — a string
//   literal. It uses Modules.Contacts now, so a rename cannot break it
//   silently.
//
//   TempData here is set ONLY before a redirect (the delete and the
//   not-found paths), which is correct: the layout shows it once on the
//   page the redirect lands on. The view renders no TempData itself.
//
// 075 — THE STAGED FIXES FROM THE 074d REVIEW
//
//   1. A FAILED DELETE RETURNED A 500. OnPostDeleteAsync's catch set an
//      error and returned Page() without loading Company — and the view's
//      first line is @Model.Company.Name. So "could not delete" became a
//      NullReferenceException and an error page. It now redirects back to
//      this page with the message in TempData; the GET builds the page.
//      (It also set a [TempData] property and then rendered, which shows
//      the message on the NEXT page instead of this one.)
//
//   2. FormatDate / FormatDateTime / FormatCurrency(decimal) are GONE.
//      They hid the base class's overloads (round 029 precedent); the
//      base versions are tenant-aware.
//
//   3. Dead code removed: GetVerticalBadgeClass, TenantCurrencySymbol,
//      TenantCurrencyCode, TenantCountryName — computed, never read.
//      VerticalDisplayName WAS computed and never read too; the view now
//      shows it instead of the raw stored value.
//
//   4. GetRelativeTime now uses plain UTC subtraction, like Contacts.
//      Converting both ends to tenant-local first drifts by an hour
//      across a daylight-saving change. (Nothing on the page calls it
//      today; it is kept correct so the next person to use it is not
//      caught out.)
//
//   ICurrentTenantService is no longer injected: nothing here needs it
//   that the base class does not already provide.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Pages.Shared;
using MerkaiTrial.Admin.Web.Services.Companies;
using MerkaiTrial.Admin.Web.Services.Contacts;
using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Admin.Web.Services.CustomFields;
using MerkaiTrial.Admin.Web.Services.Meta;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Meta;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages.Companies
{
    public class DetailModel : AuthorizedPageModel
    {
        private readonly ICompanyService _companyService;
        private readonly IContactService _contactService;
        private readonly ICountryService _countryService;
        private readonly IMetaService    _metaService;
        private readonly ICustomFieldService _customFields;

        protected override string ModuleName => Modules.Companies;

        public DetailModel(
            ICompanyService         companyService,
            IContactService         contactService,
            ICountryService         countryService,
            IMetaService            metaService,
            ICustomFieldService     customFields,
            IAuthorizationService   authorizationService,
            ICurrentUserService     currentUserService,
            ILogger<DetailModel>    logger)
            : base(authorizationService, currentUserService, logger)
        {
            _companyService = companyService;
            _contactService = contactService;
            _countryService = countryService;
            _metaService    = metaService;
            _customFields   = customFields;
        }

        public CompanyDto Company { get; set; } = null!;
        public List<ContactListItem> Contacts { get; set; } = new();
        public string CountryName          { get; set; } = string.Empty;
        public string VerticalDisplayName  { get; set; } = string.Empty;

        /// <summary>078. Every custom field for Companies, retired ones included.</summary>
        public List<CustomFieldDefinitionDto> CustomFieldDefinitions { get; private set; } = new();
        public bool CustomFieldsLoadFailed { get; private set; }

        /// <summary>078. The model for _CustomFieldValues.</summary>
        public CustomFieldDisplayVm CustomFieldDisplay => new()
        {
            Fields       = CustomFieldDefinitions,
            Values       = Company?.CustomFieldValues ?? new Dictionary<Guid, string>(),
            Culture      = CustomFieldFormatter.ResolveCulture(CultureName),
            DateFormat   = TenantCtx.GetDateFormat(),
            LoadFailed   = CustomFieldsLoadFailed,
            CanConfigure = UserCanRead(Modules.Settings),
            CanEdit      = CanUpdate,
            RecordId     = Company?.Id ?? Guid.Empty,
            EntityType   = CustomFieldEntityTypes.Company,
            EditPage     = "/Companies/Edit"
        };

        /// <summary>For the "Add Contact" button on the empty Contacts tab.</summary>
        public bool CanCreateContacts => UserCanCreate(Modules.Contacts);

        public async Task<IActionResult> OnGetAsync(Guid id)
        {
            // ✅ Check READ permission
            var permissionCheck = await ValidatePermissionAsync(Actions.Read);
            if (permissionCheck != null) return permissionCheck;

            // Initialize permissions for UI buttons (Edit / Delete)
            await InitializePermissionsAsync();

            try
            {
                var tenantId = CurrentUserService.GetCurrentTenantId();

                Company = await _companyService.GetByIdAsync(tenantId, id);

                try
                {
                    Contacts = await _contactService.GetByCompanyAsync(tenantId, id);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Could not load contacts for company {Id}", id);
                    Contacts = new List<ContactListItem>();
                }

                try
                {
                    var countries = await _countryService.GetActiveAsync();
                    CountryName = countries.FirstOrDefault(c => c.Code == Company.Country)?.Name
                                  ?? Company.Country;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Could not load country info");
                    CountryName = Company.Country;
                }

                try
                {
                    var verticals = await _metaService.GetVerticalsAsync();
                    VerticalDisplayName = verticals.FirstOrDefault(v => v.Name == Company.Vertical)?.Name
                                         ?? Company.Vertical;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Could not load vertical info");
                    VerticalDisplayName = Company.Vertical;
                }

                // 078 — retired fields included, so a value on a field
                // that has since been switched off is still shown.
                try
                {
                    CustomFieldDefinitions = await _customFields.GetDefinitionsAsync(
                        CustomFieldEntityTypes.Company, includeInactive: true);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to load custom fields for company {Id}", id);
                    CustomFieldDefinitions = new();
                    CustomFieldsLoadFailed = true;
                }

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

        public async Task<IActionResult> OnPostDeleteAsync(Guid id)
        {
            // ✅ Check DELETE permission
            var permissionCheck = await ValidatePermissionAsync(Actions.Delete);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var tenantId = CurrentUserService.GetCurrentTenantId();
                await _companyService.DeleteAsync(tenantId, id);
                TempData["SuccessMessage"] = "Company deleted successfully!";
                return RedirectToPage("./Index");
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Company not found.";
                return RedirectToPage("./Index");
            }
            catch (InvalidOperationException ex)
            {
                // A refusal with a reason (for example, linked records).
                TempData["ErrorMessage"] = ex.Message;
                return RedirectToPage("./Detail", new { id });
            }
            catch (Exception ex)
            {
                // Back through the GET, which builds Company — see note 1.
                Logger.LogError(ex, "Error deleting company {Id}", id);
                TempData["ErrorMessage"] = "Failed to delete company. Please try again.";
                return RedirectToPage("./Detail", new { id });
            }
        }

        /// <summary>Plain UTC subtraction — see note 4.</summary>
        public string GetRelativeTime(DateTime utcDateTime)
        {
            var timeSpan = DateTime.UtcNow - utcDateTime;

            if (timeSpan.TotalMinutes < 1)   return "just now";
            if (timeSpan.TotalMinutes < 60)  return $"{(int)timeSpan.TotalMinutes} minutes ago";
            if (timeSpan.TotalHours   < 24)  return $"{(int)timeSpan.TotalHours} hours ago";
            if (timeSpan.TotalDays    < 7)   return $"{(int)timeSpan.TotalDays} days ago";
            if (timeSpan.TotalDays    < 30)  return $"{(int)(timeSpan.TotalDays / 7)} weeks ago";
            if (timeSpan.TotalDays    < 365) return $"{(int)(timeSpan.TotalDays / 30)} months ago";
            return $"{(int)(timeSpan.TotalDays / 365)} years ago";
        }
    }
}
