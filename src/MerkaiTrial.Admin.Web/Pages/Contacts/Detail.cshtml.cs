// =====================================================================
// DETAIL CONTACT - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Contacts/Detail.cshtml.cs
//
// ✅ MIGRATED to AuthorizedPageModel (was AppPageModel).
//
// BUG 3 FIX (unchanged from before): ContactDeals loaded via
// IDealService.GetByContactAsync(), replacing the hardcoded
// "coming soon" placeholder in the view.
//
// 075 — CUSTOM FIELDS, AND THE STAGED FIXES FROM THE 074d REVIEW
//
//   1. "Additional details" card: every active custom field, plus any
//      retired field this contact still has a value in, through the
//      shared _CustomFieldValues partial. Numbers in the tenant's number
//      culture, dates in the tenant's date pattern and NEVER shifted by
//      timezone.
//
//   2. FormatDate / FormatDateTime / FormatCurrency(decimal) are GONE.
//      They were second copies of what AuthorizedPageModel already
//      provides, tenant-aware. (They did compile: overload resolution
//      drops the one-argument copy for a two-argument call and finds the
//      base FormatCurrency(decimal, int?). But two copies drift.)
//
//   3. CurrencySymbol and CurrencyCode are gone too. They hid the base
//      class's properties of the same name (warning CS0108), and nothing
//      read them; the layout reads the base ones.
//
//   4. ResolveCountryName's hardcoded table of fourteen countries is
//      gone. The name comes from the Countries table via ICountryService,
//      the way Companies/Detail does it.
//
//   5. THE DELETE FAILURE PATH. It set a [TempData] ErrorMessage and
//      returned Page() — so the message appeared on the NEXT page, not
//      this one — without calling InitializePermissionsAsync, so the
//      Edit and Delete buttons vanished from that render, and with
//      ContactDeals never loaded. It now redirects back to this page
//      with the message in TempData, and the GET builds everything
//      properly. One code path for rendering the page, not two.
//
//   6. Delete is confirmed in a Bootstrap modal, not confirm(). The old
//      confirm() put the contact's name inside a JavaScript string in the
//      page: a name with an apostrophe ("O'Brien") arrived HTML-encoded
//      inside script and showed as &#x27; in the dialog.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Pages.Shared;
using MerkaiTrial.Admin.Web.Services.Contacts;
using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Admin.Web.Services.CustomFields;
using MerkaiTrial.Admin.Web.Services.Deals;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Deals;   // ContactDealItem
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages.Contacts
{
    public class DetailModel : AuthorizedPageModel
    {
        private readonly IContactService     _contactService;
        private readonly IDealService        _dealService;       // ✅ BUG 3
        private readonly ICountryService     _countryService;    // 075
        private readonly ICustomFieldService _customFields;      // 075

        protected override string ModuleName => Modules.Contacts;

        public DetailModel(
            IContactService         contactService,
            IDealService            dealService,                   // ✅ BUG 3
            ICountryService         countryService,
            ICustomFieldService     customFields,
            IAuthorizationService   authorizationService,
            ICurrentUserService     currentUserService,
            ILogger<DetailModel>    logger)
            : base(authorizationService, currentUserService, logger)
        {
            _contactService = contactService;
            _dealService    = dealService;                          // ✅ BUG 3
            _countryService = countryService;
            _customFields   = customFields;
        }

        public ContactDto Contact { get; set; } = null!;

        // ✅ BUG 3: real deals for this contact
        public List<ContactDealItem> ContactDeals { get; set; } = new();

        /// <summary>075. From the Countries table; the code itself if unknown.</summary>
        public string CountryName { get; private set; } = "Not provided";

        /// <summary>075. Every custom field for Contacts, retired ones included.</summary>
        public List<CustomFieldDefinitionDto> CustomFieldDefinitions { get; private set; } = new();
        public bool CustomFieldsLoadFailed { get; private set; }

        /// <summary>075. The model for _CustomFieldValues.</summary>
        public CustomFieldDisplayVm CustomFieldDisplay => new()
        {
            Fields       = CustomFieldDefinitions,
            Values       = Contact?.CustomFieldValues ?? new Dictionary<Guid, string>(),
            Culture      = CustomFieldFormatter.ResolveCulture(CultureName),
            DateFormat   = TenantCtx.GetDateFormat(),
            LoadFailed   = CustomFieldsLoadFailed,
            CanConfigure = UserCanRead(Modules.Settings),
            CanEdit      = CanUpdate,
            RecordId     = Contact?.Id ?? Guid.Empty,
            EntityType   = CustomFieldEntityTypes.Contact,
            EditPage     = "/Contacts/Edit"
        };

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
                Contact = await _contactService.GetByIdAsync(tenantId, id);

                // ✅ BUG 3: load deals for this contact (non-fatal — don't break page if it fails)
                try
                {
                    ContactDeals = await _dealService.GetByContactAsync(tenantId.ToString(), id);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to load deals for contact {Id}", id);
                    ContactDeals = new();
                }

                // 075 — non-fatal, and says so on the page if it fails.
                CountryName = await ContactFormOptions.CountryNameAsync(_countryService, Contact.Country, Logger);

                try
                {
                    CustomFieldDefinitions = await _customFields.GetDefinitionsAsync(
                        CustomFieldEntityTypes.Contact, includeInactive: true);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to load custom fields for contact {Id}", id);
                    CustomFieldDefinitions = new();
                    CustomFieldsLoadFailed = true;
                }

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

        public async Task<IActionResult> OnPostDeleteAsync(Guid id)
        {
            // ✅ Check DELETE permission
            var permissionCheck = await ValidatePermissionAsync(Actions.Delete);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var tenantId = CurrentUserService.GetCurrentTenantId();
                await _contactService.DeleteAsync(tenantId, id);
                TempData["SuccessMessage"] = "Contact deleted successfully!";
                return RedirectToPage("./Index");
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Contact not found.";
                return RedirectToPage("./Index");
            }
            catch (InvalidOperationException ex)
            {
                // A refusal with a reason, written for the person.
                TempData["ErrorMessage"] = ex.Message;
                return RedirectToPage("./Detail", new { id });
            }
            catch (Exception ex)
            {
                // Back to the page through its own GET — see note 5.
                Logger.LogError(ex, "Error deleting contact {Id}", id);
                TempData["ErrorMessage"] = "Failed to delete contact. Please try again.";
                return RedirectToPage("./Detail", new { id });
            }
        }

        // ── VIEW HELPERS ──────────────────────────────────────────────────────
        // FormatDate, FormatDateTime and FormatCurrency come from
        // AuthorizedPageModel — see note 2.

        public string GetDealStageBadgeClass(string stage) => stage switch
        {
            "Won" => "bg-success",
            "Lost" => "bg-danger",
            "Proposal" => "bg-primary",
            "Negotiation" => "bg-warning text-dark",
            "Qualification" => "bg-info",
            "Discovery" => "bg-secondary",
            _ => "bg-secondary"
        };

        /// <summary>
        /// Plain UTC subtraction — the correct form. Both ends are UTC, so the
        /// difference is right regardless of the tenant's timezone or any DST
        /// change in between. (Companies/Detail now uses the same.)
        /// </summary>
        public string GetRelativeTime(DateTime utcDateTime)
        {
            var timeSpan = DateTime.UtcNow - utcDateTime;
            if (timeSpan.TotalMinutes < 1) return "just now";
            if (timeSpan.TotalMinutes < 60) return $"{(int)timeSpan.TotalMinutes} minutes ago";
            if (timeSpan.TotalHours < 24) return $"{(int)timeSpan.TotalHours} hours ago";
            if (timeSpan.TotalDays < 7) return $"{(int)timeSpan.TotalDays} days ago";
            if (timeSpan.TotalDays < 30) return $"{(int)(timeSpan.TotalDays / 7)} weeks ago";
            if (timeSpan.TotalDays < 365) return $"{(int)(timeSpan.TotalDays / 30)} months ago";
            return $"{(int)(timeSpan.TotalDays / 365)} years ago";
        }
    }
}
