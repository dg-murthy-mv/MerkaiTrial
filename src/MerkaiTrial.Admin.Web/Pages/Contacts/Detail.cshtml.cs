// =====================================================================
// DETAIL CONTACT - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Contacts/Detail.cshtml.cs
//
// ✅ MIGRATED to AuthorizedPageModel (was AppPageModel).
//
// BUG 3 FIX (unchanged from before): ContactDeals loaded via
// IDealService.GetByContactAsync(), replacing the hardcoded
// "coming soon" placeholder in the view.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Services.Contacts;
using MerkaiTrial.Admin.Web.Services.Deals;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Deals;   // ContactDealItem
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages.Contacts
{
    public class DetailModel : AuthorizedPageModel
    {
        private readonly IContactService       _contactService;
        private readonly IDealService          _dealService;       // ✅ BUG 3
        private readonly ICurrentTenantService _tenantService;

        protected override string ModuleName => Modules.Contacts;

        public DetailModel(
            IContactService         contactService,
            IDealService            dealService,                   // ✅ BUG 3
            ICurrentTenantService   tenantService,
            IAuthorizationService   authorizationService,
            ICurrentUserService     currentUserService,
            ILogger<DetailModel>    logger)
            : base(authorizationService, currentUserService, logger)
        {
            _contactService = contactService;
            _dealService    = dealService;                          // ✅ BUG 3
            _tenantService  = tenantService;
        }

        public ContactDto Contact { get; set; } = null!;

        // ✅ BUG 3: real deals for this contact
        public List<ContactDealItem> ContactDeals { get; set; } = new();

        // Tenant
        public string CurrencySymbol { get; private set; } = string.Empty;
        public string CurrencyCode { get; private set; } = string.Empty;
        public string TenantCountry { get; private set; } = string.Empty;

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

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

                CurrencySymbol = _tenantService.GetCurrencySymbol();
                CurrencyCode = _tenantService.GetCurrencyCode();
                TenantCountry = _tenantService.GetCountryName();

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
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error deleting contact {Id}", id);
                ErrorMessage = "Failed to delete contact. Please try again.";
                var tenantId = CurrentUserService.GetCurrentTenantId();
                Contact = await _contactService.GetByIdAsync(tenantId, id);
                return Page();
            }
        }

        // ── VIEW HELPERS ──────────────────────────────────────────────────────

        public string FormatDate(DateTime utcDate) => _tenantService.FormatDate(utcDate);
        public string FormatDateTime(DateTime utcDate) => _tenantService.FormatDateTime(utcDate);
        public string FormatCurrency(decimal amount) => _tenantService.FormatCurrency(amount);

        public string ResolveCountryName(string? countryCode)
        {
            if (string.IsNullOrWhiteSpace(countryCode)) return "Not provided";
            return countryCode.ToUpperInvariant() switch
            {
                "IN" => "India",
                "TH" => "Thailand",
                "PH" => "Philippines",
                "AE" => "United Arab Emirates",
                "SG" => "Singapore",
                "MY" => "Malaysia",
                "ID" => "Indonesia",
                "VN" => "Vietnam",
                "AU" => "Australia",
                "GB" => "United Kingdom",
                "US" => "United States",
                "CN" => "China",
                "JP" => "Japan",
                "KR" => "South Korea",
                _ => countryCode
            };
        }

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
