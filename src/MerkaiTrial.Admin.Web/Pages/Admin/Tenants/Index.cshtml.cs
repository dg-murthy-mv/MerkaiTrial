// =====================================================================
// TENANTS INDEX - Backend
// Location: Pages/Admin/Tenants/Index.cshtml.cs
//
// 120 — search, filters, order and pages that work together:
//   Search   a workspace's name or email, OR the email of anyone in it
//            ("which workspace is priya@… in?")
//   Status   active / switched off        Plan   one plan
//   Order    newest, oldest, name, most people, most leads
//   Pages    the shared pager (10 / 25 / 50 / 100 a page); every link keeps
//            every filter. A page number past the end goes to the last page.
// The cards at the top count ALL workspaces (they counted the filtered
// list, and "Active" showed a number of people).
//
// 119 — the Delete and switch-off handlers that were here are gone (they
// live on the workspace's own page, with their confirmations).
// =====================================================================

using MerkaiTrial.Admin.Web.Pages.Shared;
using MerkaiTrial.Admin.Web.Services.Tenants;
using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Tenants
{
    public class IndexModel : PageModel
    {
        private readonly ITenantService _tenantService;
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(ITenantService tenantService, ILogger<IndexModel> logger)
        {
            _tenantService = tenantService;
            _logger = logger;
        }

        public PaginatedTenantsResponse Tenants { get; set; } = new(new List<TenantListItem>(), 0, 1, PagerVm.DefaultSize, 0);
        public TenantStatsDto Stats { get; set; } = new();
        public List<string> Plans { get; private set; } = new();
        public string? LoadError { get; private set; }

        [BindProperty(SupportsGet = true)] public string? SearchTerm { get; set; }
        [BindProperty(SupportsGet = true)] public bool? IsActive { get; set; }
        [BindProperty(SupportsGet = true)] public string? Plan { get; set; }
        [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
        [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
        [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = 25;

        public static readonly (string Value, string Label)[] Sorts =
        {
            ("newest", "Newest first"), ("oldest", "Oldest first"), ("name", "Name A–Z"),
            ("users", "Most people"), ("leads", "Most leads"),
        };

        public int AllWorkspaces => Stats.ActiveUsers + Stats.InactiveUsers;   // the stats DTO's names are reused (see GetAllTenantsStatsHandler)

        public bool Filtered => !string.IsNullOrWhiteSpace(SearchTerm) || IsActive.HasValue || !string.IsNullOrWhiteSpace(Plan);

        public async Task<IActionResult> OnGetAsync()
        {
            if (!PagerVm.Sizes.Contains(PageSize)) PageSize = 25;
            if (PageNumber < 1) PageNumber = 1;
            if (SearchTerm?.Length > 100) SearchTerm = SearchTerm[..100];
            if (!Sorts.Any(s => s.Value == Sort)) Sort = "newest";

            try
            {
                Stats = await _tenantService.GetAllTenantsStatsAsync();
                Plans = await _tenantService.GetPlansAsync();
                Tenants = await _tenantService.GetPaginatedAsync(PageNumber, PageSize, SearchTerm, IsActive, Plan, Sort);

                // Past the last page (a filter shrank the list): go to the last one.
                if (Tenants.TotalCount > 0 && PageNumber > Tenants.TotalPages)
                {
                    var r = FilterRoute();
                    r[nameof(PageNumber)] = Tenants.TotalPages.ToString();
                    r[nameof(PageSize)] = PageSize.ToString();
                    return RedirectToPage(new RouteValueDictionary(r.ToDictionary(k => k.Key, k => (object?)k.Value)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load the workspace list");
                LoadError = "The workspaces could not be loaded. Please try again.";
            }
            return Page();
        }

        public PagerVm Pager => new()
        {
            Page = PageNumber,
            PageSize = PageSize,
            TotalCount = Tenants.TotalCount,
            TotalPages = Math.Max(1, Tenants.TotalPages),
            PageField = nameof(PageNumber),
            Route = FilterRoute(),
        };

        private Dictionary<string, string> FilterRoute()
        {
            var r = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(SearchTerm)) r[nameof(SearchTerm)] = SearchTerm!;
            if (IsActive.HasValue) r[nameof(IsActive)] = IsActive.Value ? "true" : "false";
            if (!string.IsNullOrWhiteSpace(Plan)) r[nameof(Plan)] = Plan!;
            if (!string.IsNullOrWhiteSpace(Sort) && Sort != "newest") r[nameof(Sort)] = Sort!;
            return r;
        }
    }
}
