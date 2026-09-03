// =====================================================================
// CONTACTS INDEX - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Contacts/Index.cshtml.cs
//
// ✅ MIGRATED to AuthorizedPageModel (was AppPageModel).
//
// TENANT FIX (unchanged from before): ICurrentTenantService injected,
// FormatDate/CurrencySymbol/CountryName helpers exposed for the view.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Services.Contacts;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MerkaiTrial.Admin.Web.Pages.Contacts
{
    public class IndexModel : AuthorizedPageModel
    {
        private readonly IContactService       _contactService;
        private readonly ICurrentTenantService _tenantService;

        protected override string ModuleName => Modules.Contacts;

        public IndexModel(
            IContactService         contactService,
            ICurrentTenantService   tenantService,
            IAuthorizationService   authorizationService,
            ICurrentUserService     currentUserService,
            ILogger<IndexModel>     logger)
            : base(authorizationService, currentUserService, logger)
        {
            _contactService = contactService;
            _tenantService  = tenantService;
        }

        public PaginatedResult<ContactListItem> PaginatedContacts { get; set; } = new();
        public ContactStatsDto Stats { get; set; } = null!;
        public List<CompanyListItem> Companies { get; set; } = new();

        // ✅ TENANT: Expose formatted values to view
        public string TenantCountryName  { get; private set; } = string.Empty;
        public string TenantCurrencyCode { get; private set; } = string.Empty;

        [BindProperty(SupportsGet = true)] public int Page { get; set; } = 1;
        [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = 25;
        [BindProperty(SupportsGet = true)] public Guid? CompanyFilter { get; set; }
        [BindProperty(SupportsGet = true)] public string? SearchTerm { get; set; }
        [BindProperty(SupportsGet = true)] public bool? IsPrimaryFilter { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            // ✅ Check READ permission
            var permissionCheck = await ValidatePermissionAsync(Actions.Read);
            if (permissionCheck != null) return permissionCheck;

            // Initialize permissions for UI buttons (Add Contact / Edit / Delete)
            await InitializePermissionsAsync();

            if (PageSize < 5) PageSize = 5;
            if (PageSize > 100) PageSize = 100;

            try
            {
                var tenantId = CurrentUserService.GetCurrentTenantId();

                // ✅ TENANT: resolve display values once per request
                TenantCountryName  = _tenantService.GetCountryName();
                TenantCurrencyCode = _tenantService.GetCurrencyCode();

                Stats = await _contactService.GetStatsAsync(tenantId);

                PaginatedContacts = await _contactService.GetAllAsync(
                    tenantId, Page, PageSize, CompanyFilter, SearchTerm, IsPrimaryFilter);

                Companies = await _contactService.GetCompaniesLookupAsync(tenantId);

                Logger.LogInformation("Loaded page {Page} with {Count} contacts (Total: {Total})",
                    Page, PaginatedContacts.Items.Count, PaginatedContacts.TotalCount);

                return Page();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading contacts");
                TempData["ErrorMessage"] = "Failed to load contacts. Please try again.";

                Stats = new ContactStatsDto(0, 0, 0, 0);
                PaginatedContacts = new PaginatedResult<ContactListItem>
                {
                    Items      = new List<ContactListItem>(),
                    Page       = 1,
                    PageSize   = PageSize,
                    TotalCount = 0
                };
                Companies = new List<CompanyListItem>();

                return Page();
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
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error deleting contact {Id}", id);
                TempData["ErrorMessage"] = "Failed to delete contact. Please try again.";
                return RedirectToPage();
            }
        }

        // ✅ TENANT: Format a UTC date using tenant timezone + date format
        public string FormatDate(DateTime utcDate)
            => _tenantService.FormatDate(utcDate);

        public string FormatDateTime(DateTime utcDate)
            => _tenantService.FormatDateTime(utcDate);

        public SelectList GetCompanyFilterOptions()
        {
            var items = new List<SelectListItem>
            {
                new SelectListItem { Value = "", Text = "All Companies" }
            };
            items.AddRange(Companies.Select(c => new SelectListItem
            {
                Value = c.Id.ToString(),
                Text  = c.Name
            }));
            return new SelectList(items, "Value", "Text", CompanyFilter?.ToString());
        }

        public SelectList GetIsPrimaryFilterOptions()
        {
            var items = new List<SelectListItem>
            {
                new SelectListItem { Value = "",      Text = "All Contacts" },
                new SelectListItem { Value = "true",  Text = "Primary Only" },
                new SelectListItem { Value = "false", Text = "Non-Primary" }
            };
            return new SelectList(items, "Value", "Text", IsPrimaryFilter?.ToString().ToLower());
        }

        public string GetCompanyName(Guid? companyId)
        {
            if (!companyId.HasValue) return "No Company";
            return Companies.FirstOrDefault(c => c.Id == companyId)?.Name ?? "Unknown";
        }
    }
}
