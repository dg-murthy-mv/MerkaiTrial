// =====================================================================
// DETAIL COMPANY - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Companies/Detail.cshtml.cs
//
// ✅ MIGRATED to AuthorizedPageModel (was AppPageModel).
//
// TENANT FIXES (unchanged from before):
//   - ICurrentTenantService injected
//   - FormatDate / FormatDateTime / FormatCurrency helpers exposed
//   - GetRelativeTime uses tenant timezone (not raw UtcNow)
//   - CurrencySymbol / TenantCountry exposed to view
// =====================================================================

using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Services.Companies;
using MerkaiTrial.Admin.Web.Services.Contacts;
using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Admin.Web.Services.Meta;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Meta;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages.Companies
{
    public class DetailModel : AuthorizedPageModel
    {
        private readonly ICompanyService       _companyService;
        private readonly IContactService       _contactService;
        private readonly ICountryService       _countryService;
        private readonly IMetaService          _metaService;
        private readonly ICurrentTenantService _tenantService;

        protected override string ModuleName => Modules.Companies;

        public DetailModel(
            ICompanyService         companyService,
            IContactService         contactService,
            ICountryService         countryService,
            IMetaService            metaService,
            ICurrentTenantService   tenantService,
            IAuthorizationService   authorizationService,
            ICurrentUserService     currentUserService,
            ILogger<DetailModel>    logger)
            : base(authorizationService, currentUserService, logger)
        {
            _companyService = companyService;
            _contactService = contactService;
            _countryService = countryService;
            _metaService    = metaService;
            _tenantService  = tenantService;
        }

        public CompanyDto Company { get; set; } = null!;
        public List<ContactListItem> Contacts { get; set; } = new();
        public string CountryName          { get; set; } = string.Empty;
        public string VerticalDisplayName  { get; set; } = string.Empty;

        // ✅ TENANT: exposed to view
        public string TenantCurrencySymbol { get; private set; } = string.Empty;
        public string TenantCurrencyCode   { get; private set; } = string.Empty;
        public string TenantCountryName    { get; private set; } = string.Empty;

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage   { get; set; }

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

                // ✅ TENANT: resolve once per request
                TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
                TenantCurrencyCode   = _tenantService.GetCurrencyCode();
                TenantCountryName    = _tenantService.GetCountryName();

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
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error deleting company {Id}", id);
                ErrorMessage = "Failed to delete company. Please try again.";
                return Page();
            }
        }

        // ✅ TENANT: format UTC dates using tenant timezone + date format
        public string FormatDate(DateTime utcDate)     => _tenantService.FormatDate(utcDate);
        public string FormatDateTime(DateTime utcDate) => _tenantService.FormatDateTime(utcDate);
        public string FormatCurrency(decimal amount)   => _tenantService.FormatCurrency(amount);

        // ✅ TENANT: relative time uses tenant local time, not server UTC
        public string GetRelativeTime(DateTime utcDateTime)
        {
            var localNow  = _tenantService.UtcToLocal(DateTime.UtcNow);
            var localTime = _tenantService.UtcToLocal(utcDateTime);
            var timeSpan  = localNow - localTime;

            if (timeSpan.TotalMinutes < 1)   return "just now";
            if (timeSpan.TotalMinutes < 60)  return $"{(int)timeSpan.TotalMinutes} minutes ago";
            if (timeSpan.TotalHours   < 24)  return $"{(int)timeSpan.TotalHours} hours ago";
            if (timeSpan.TotalDays    < 7)   return $"{(int)timeSpan.TotalDays} days ago";
            if (timeSpan.TotalDays    < 30)  return $"{(int)(timeSpan.TotalDays / 7)} weeks ago";
            if (timeSpan.TotalDays    < 365) return $"{(int)(timeSpan.TotalDays / 30)} months ago";
            return $"{(int)(timeSpan.TotalDays / 365)} years ago";
        }

        public string GetVerticalBadgeClass(string vertical) => vertical switch
        {
            "Healthcare"    => "bg-danger",
            "Finance"       => "bg-success",
            "Technology"    => "bg-primary",
            "Manufacturing" => "bg-info",
            "Retail"        => "bg-warning",
            "Education"     => "bg-secondary",
            _               => "bg-dark"
        };
    }
}
