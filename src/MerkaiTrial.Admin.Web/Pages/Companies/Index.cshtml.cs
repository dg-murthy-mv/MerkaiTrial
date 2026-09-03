// =====================================================================
// COMPANIES INDEX - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Companies/Index.cshtml.cs
//
// ✅ MIGRATED to AuthorizedPageModel (was AppPageModel) — consolidates
// onto the same permission-checking base class used by Products, so
// GET is blocked (redirect to Access Denied) for users without Read,
// not just POST handlers left silently unchecked.
//
// TENANT FIXES (unchanged from before):
//   - ICurrentTenantService injected
//   - FormatDate / FormatDateTime / FormatCurrency helpers exposed
//   - TenantCountryCode / TenantCurrencySymbol exposed to view
//   - Stats endpoint no longer passes ?tenantId= (controller resolves it)
// =====================================================================

using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Services.Companies;
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
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MerkaiTrial.Admin.Web.Pages.Companies
{
    public class IndexModel : AuthorizedPageModel
    {
        private readonly ICompanyService       _companyService;
        private readonly ICountryService       _countryService;
        private readonly IMetaService          _metaService;
        private readonly ICurrentTenantService _tenantService;

        protected override string ModuleName => Modules.Companies;

        public IndexModel(
            ICompanyService         companyService,
            ICountryService         countryService,
            IMetaService            metaService,
            ICurrentTenantService   tenantService,
            IAuthorizationService   authorizationService,
            ICurrentUserService     currentUserService,
            ILogger<IndexModel>     logger)
            : base(authorizationService, currentUserService, logger)
        {
            _companyService = companyService;
            _countryService = countryService;
            _metaService    = metaService;
            _tenantService  = tenantService;
        }

        public PaginatedResult<CompanyListItem> PaginatedCompanies { get; set; } = new();
        public CompanyStatsDto Stats { get; set; } = null!;
        public List<CountryListItem> Countries { get; set; } = new();
        public List<VerticalDto> Verticals { get; set; } = new();

        // ✅ TENANT: exposed to view
        public string TenantCountryCode    { get; private set; } = string.Empty;
        public string TenantCountryName    { get; private set; } = string.Empty;
        public string TenantCurrencySymbol { get; private set; } = string.Empty;
        public string TenantCurrencyCode   { get; private set; } = string.Empty;

        [BindProperty(SupportsGet = true)] public int Page { get; set; } = 1;
        [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = 25;
        [BindProperty(SupportsGet = true)] public string? SearchTerm { get; set; }
        [BindProperty(SupportsGet = true)] public string? VerticalFilter { get; set; }
        [BindProperty(SupportsGet = true)] public string? CountryFilter { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            // ✅ Check READ permission — redirects to Access Denied if missing
            var permissionCheck = await ValidatePermissionAsync(Actions.Read);
            if (permissionCheck != null) return permissionCheck;

            // Initialize permissions for UI buttons (Add Company / Edit / Delete)
            await InitializePermissionsAsync();

            if (PageSize < 5)  PageSize = 5;
            if (PageSize > 100) PageSize = 100;

            try
            {
                var tenantId = CurrentUserService.GetCurrentTenantId();

                // ✅ TENANT: resolve once per request
                TenantCountryCode    = _tenantService.GetCountryCode();
                TenantCountryName    = _tenantService.GetCountryName();
                TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
                TenantCurrencyCode   = _tenantService.GetCurrencyCode();

                Stats = await _companyService.GetStatsAsync(tenantId);

                PaginatedCompanies = await _companyService.GetAllAsync(
                    tenantId:      tenantId,
                    pageNumber:    Page,
                    pageSize:      PageSize,
                    searchTerm:    SearchTerm,
                    verticalFilter: VerticalFilter,
                    countryFilter: CountryFilter);

                Countries = await _countryService.GetActiveAsync();
                Verticals = await _metaService.GetVerticalsAsync();

                Logger.LogInformation(
                    "Loaded page {Page} with {Count} companies (Total: {Total})",
                    Page, PaginatedCompanies.Items.Count, PaginatedCompanies.TotalCount);

                return Page();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading companies");
                TempData["ErrorMessage"] = "Failed to load companies. Please try again.";

                Stats              = new CompanyStatsDto(0, 0, 0, 0);
                PaginatedCompanies = new PaginatedResult<CompanyListItem>
                {
                    Items      = new List<CompanyListItem>(),
                    Page       = 1,
                    PageSize   = PageSize,
                    TotalCount = 0
                };
                Countries = new List<CountryListItem>();
                Verticals = new List<VerticalDto>();

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
                await _companyService.DeleteAsync(tenantId, id);
                TempData["SuccessMessage"] = "Company deleted successfully!";
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error deleting company {Id}", id);
                TempData["ErrorMessage"] = "Failed to delete company. Please try again.";
                return RedirectToPage();
            }
        }

        // ✅ TENANT: format helpers for the view
        public string FormatDate(DateTime utcDate)     => _tenantService.FormatDate(utcDate);
        public string FormatDateTime(DateTime utcDate) => _tenantService.FormatDateTime(utcDate);
        public string FormatCurrency(decimal amount)   => _tenantService.FormatCurrency(amount);

        public SelectList GetVerticalFilterOptions()
        {
            var items = new List<SelectListItem>
            {
                new SelectListItem { Value = "", Text = "All Verticals" }
            };
            items.AddRange(Verticals.Select(v =>
                new SelectListItem { Value = v.Name, Text = v.Name }));
            return new SelectList(items, "Value", "Text", VerticalFilter);
        }

        public SelectList GetCountryFilterOptions()
        {
            var items = new List<SelectListItem>
            {
                new SelectListItem { Value = "", Text = "All Countries" }
            };
            items.AddRange(Countries.Select(c =>
                new SelectListItem { Value = c.Code, Text = c.Name }));
            return new SelectList(items, "Value", "Text", CountryFilter);
        }

        public string GetCountryName(string code)
            => Countries.FirstOrDefault(c => c.Code == code)?.Name ?? code;
    }
}
