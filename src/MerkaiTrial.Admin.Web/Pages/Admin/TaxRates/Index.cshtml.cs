// =====================================================================
// TAX RATES INDEX - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Admin/TaxRates/Index.cshtml.cs
//
// MOVED IN 054, from Pages/TaxRates/. That move IS the security fix.
//
//   AddAdminWebPages() has
//       options.Conventions.AuthorizeFolder("/Admin", "SuperAdmin");
//   and that convention matches on the page's FOLDER under Pages/, not on
//   its URL. These four pages sat in Pages/TaxRates/ with a route override
//   of @@page "/Admin/TaxRates/...", so they SERVED from an /Admin URL while
//   living outside the folder that gate covers — and carried no
//   [Authorize] of their own either, only a commented-out one saying
//   "uncomment when authorization is ready".
//
//   There is a FallbackPolicy, so an anonymous visitor was still stopped.
//   Any SIGNED-IN USER OF ANY TENANT was not: these pages create, edit and
//   delete the PLATFORM-WIDE system tax rates that every tenant's quotes
//   and invoices are priced from.
//
//   Sitting in Pages/Admin/ now, the folder convention covers them, which
//   is better than an attribute per page — a page added here later cannot
//   forget one.
//
//   DELETE the old Pages/TaxRates/ folder. Leaving it there leaves the
//   ungated copy serving.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Admin.Web.Services.TaxRates;
using MerkaiTrial.Application.Common;        // 057: TaxRateStatus
using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MerkaiTrial.Admin.Web.Pages.Admin.TaxRates
{
    public class IndexModel : PageModel
    {
        private readonly ITaxRateService _taxRateService;
        private readonly ICountryService _countryService;
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(
            ITaxRateService taxRateService,
            ICountryService countryService,
            ILogger<IndexModel> logger)
        {
            _taxRateService = taxRateService;
            _countryService = countryService;
            _logger = logger;
        }

        public PaginatedResult<TaxRateListItem> PaginatedTaxRates { get; set; } = new();
        public TaxRateStatsDto Stats { get; set; } = null!;
        public List<CountryListItem> Countries { get; set; } = new();

        [BindProperty(SupportsGet = true)]
        public int Page { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public int PageSize { get; set; } = 25;

        [BindProperty(SupportsGet = true)]
        public string? SearchTerm { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? CountryFilter { get; set; }

        public async Task OnGetAsync()
        {
            if (PageSize < 5) PageSize = 5;
            if (PageSize > 100) PageSize = 100;

            try
            {
                Stats = await _taxRateService.GetStatsAsync();

                // 056: the tenantId argument is gone — the API takes the
                // workspace from the token. A super admin has none, so this
                // lists the SYSTEM rates, which is what this screen is for.
                // 057: includeInactive. A management screen that hides a
                // switched-off rate is a one-way door — nothing else can turn
                // it back on. The status column says which is which.
                PaginatedTaxRates = await _taxRateService.GetAllAsync(
                    pageNumber: Page,
                    pageSize: PageSize,
                    searchTerm: SearchTerm,
                    countryFilter: CountryFilter,
                    includeInactive: true
                );

                Countries = await _countryService.GetActiveAsync();

                _logger.LogInformation(
                    "Loaded page {Page} with {Count} tax rates (Total: {Total})",
                    Page,
                    PaginatedTaxRates.Items.Count,
                    PaginatedTaxRates.TotalCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading tax rates");
                TempData["ErrorMessage"] = "Failed to load tax rates. Please try again.";

                Stats = new TaxRateStatsDto(0, 0, 0, 0);
                PaginatedTaxRates = new PaginatedResult<TaxRateListItem>
                {
                    Items = new List<TaxRateListItem>(),
                    Page = 1,
                    PageSize = PageSize,
                    TotalCount = 0
                };
                Countries = new List<CountryListItem>();
            }
        }

        public async Task<IActionResult> OnPostDeleteAsync(Guid id)
        {
            try
            {
                await _taxRateService.DeleteAsync(id);
                TempData["SuccessMessage"] = "Tax rate deleted successfully!";
                return RedirectToPage();
            }
            catch (InvalidOperationException ex)
            {
                TempData["ErrorMessage"] = ex.Message;
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting tax rate {Id}", id);
                TempData["ErrorMessage"] = "Failed to delete tax rate. Please try again.";
                return RedirectToPage();
            }
        }

        public SelectList GetCountryFilterOptions()
        {
            var items = new List<SelectListItem>
            {
                new SelectListItem { Value = "", Text = "All Countries" }
            };
            items.AddRange(Countries.Select(c => new SelectListItem
            {
                Value = c.Code,
                Text = c.Name
            }));
            return new SelectList(items, "Value", "Text", CountryFilter);
        }

        public string GetCountryName(string countryCode)
        {
            return Countries.FirstOrDefault(c => c.Code == countryCode)?.Name ?? countryCode;
        }

        // ── 057: status, worked out ONCE per request ─────────────────
        // AsOf is fixed at the top of the request rather than read per row.
        // Four calls to DateTime.UtcNow inside one render can disagree with
        // each other across midnight, which is exactly when a scheduled rate
        // change happens.
        private readonly DateTime _asOf = DateTime.UtcNow;

        public TaxRateState StateOf(TaxRateListItem r)
            => TaxRateStatus.Of(r.IsActive, r.EffectiveFrom, r.EffectiveTo, _asOf);

        public string StateLabel(TaxRateListItem r) => TaxRateStatus.Label(StateOf(r));
        public string StateBadge(TaxRateListItem r) => TaxRateStatus.BadgeClass(StateOf(r));
        public string StateExplain(TaxRateListItem r) => TaxRateStatus.Explain(StateOf(r));

        /// <summary>"1 Apr 2026 – 31 Mar 2027", "from 1 Apr 2026", "—".</summary>
        public string Window(TaxRateListItem r)
        {
            const string fmt = "d MMM yyyy";

            if (r.EffectiveFrom is null && r.EffectiveTo is null) return "—";
            if (r.EffectiveFrom is null) return $"until {r.EffectiveTo!.Value.ToString(fmt)}";
            if (r.EffectiveTo is null) return $"from {r.EffectiveFrom.Value.ToString(fmt)}";

            return $"{r.EffectiveFrom.Value.ToString(fmt)} – {r.EffectiveTo.Value.ToString(fmt)}";
        }
    }
}
