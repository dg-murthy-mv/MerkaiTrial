// =====================================================================
// LEADS INDEX - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Leads/Index.cshtml.cs
//
// COMPLETE FILE — replaces the existing one.
//
// STATUS TABS (earlier pass)
//   One tab per tenant status, with counts, plus Active (default: Open +
//   Qualified categories) and All. StatusFilter in the URL:
//     empty → Active, "all" → All, <key> → that status.
//   The API receives a comma-separated key list for Active.
//
// CHANGES (031)
//   ✅ SalesTeamOptions, so the "Assigned to" filter is a dropdown of names.
//
// 079 — CUSTOM FIELDS ON THE LIST, AND THE LIST FIXES THE OTHER LISTS GOT
//
//   1. CUSTOM FIELD COLUMNS. Lead fields marked "Show on the list" in
//      Settings → Custom Fields → Leads (at most four) are columns on the
//      table, in the tenant's own number and date format.
//
//   2. CUSTOM FIELD FILTERS through the shared _CustomFieldFilterPanel,
//      joined to the toolbar's form (FilterFormId), so one Filter button
//      applies search, owner and custom filters together. The search box
//      also finds custom text values and dropdown choice names.
//
//   3. EVERY LINK KEEPS EVERY FILTER. The tabs and the pager carried
//      search and owner by hand (asp-route-* on each link); a custom
//      filter would have been dropped the moment somebody changed tab or
//      page. Tabs now use TabRoute(...) and the pager is the shared
//      _Pager, both built from one dictionary. The pager also offers a
//      page size (10 / 25 / 50 / 100), like Contacts and Companies.
//
//   4. A FAILED LOAD NO LONGER USES TempData. The catch set
//      [TempData] ErrorMessage and then RENDERED — so "Failed to load
//      leads" appeared one click late, on whatever page came next. It is
//      PageError now: shown on this response, then gone. The unused
//      [TempData] SuccessMessage property went too; the layout shows
//      success messages from TempData directly.
//
//   5. DELETE KEEPS YOUR PLACE — tab, search, owner, custom filters, page
//      and size all come back after the redirect. "That lead no longer
//      exists" and the API's own refusal get their own messages instead
//      of "Failed to delete lead".
//
//   6. FormatDate / FormatDateTime / FormatCurrency copies are GONE. They
//      hid the base class's tenant-aware versions; the view's calls now
//      reach those (FormatCurrency(x, 0) resolves to the base
//      FormatCurrency(decimal, int?)). ICurrentTenantService went with
//      them; TenantCurrency/TenantCurrencySymbol were set and never read.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages.Shared;
using MerkaiTrial.Admin.Web.Services.CustomFields;
using MerkaiTrial.Admin.Web.Services.Leads;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.LeadStatuses;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace MerkaiTrial.Admin.Web.Pages.Leads
{
    public class IndexModel : AuthorizedPageModel
    {
        public const string AllTab = "all";

        /// <summary>The toolbar form's id — the filter panel's inputs submit with it.</summary>
        public const string FilterFormId = "leadsFilter";

        public const int DefaultPageSize = 10;

        private readonly ILeadService _leadService;
        private readonly ILeadStatusService _statusService;
        private readonly ICustomFieldService _customFields;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<IndexModel> _logger;

        protected override string ModuleName => Modules.Leads;

        public IndexModel(
            ILeadService leadService,
            ILeadStatusService statusService,
            ICustomFieldService customFields,
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ILogger<IndexModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _leadService = leadService;
            _statusService = statusService;
            _customFields = customFields;
            _currentUserService = currentUserService;
            _logger = logger;
        }

        // ── What the page renders ─────────────────────────────────────

        public PaginatedResult<LeadListItem> PaginatedLeads { get; private set; }
            = new PaginatedResult<LeadListItem>();

        // Still needed: QuotaUsed drives the quota line and the Add Lead limit.
        public LeadStatsDto Stats { get; private set; } = new LeadStatsDto(0, 0, 0, 0, 0, 0, 0, 0);

        /// <summary>All of this tenant's statuses, including retired and Converted.</summary>
        public List<LeadStatusDto> Statuses { get; private set; } = new();

        /// <summary>(031) The sales team, for the "Assigned to" filter dropdown.</summary>
        public List<SelectListItem> SalesTeamOptions { get; private set; } = new();

        /// <summary>079. Active custom fields shown as list columns, in order.</summary>
        public List<CustomFieldDefinitionDto> ListColumns { get; private set; } = new();

        /// <summary>079. The custom field filters: parsed, checked, ready for the API and the panel.</summary>
        public CustomFieldListFilterState CustomFilters { get; private set; } = new();

        /// <summary>079. Shown on THIS response only — see note 4.</summary>
        public string? PageError { get; private set; }

        // ── List state, all in the URL ────────────────────────────────

        [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
        [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = DefaultPageSize;
        [BindProperty(SupportsGet = true)] public string? SearchTerm { get; set; }
        [BindProperty(SupportsGet = true)] public string? StatusFilter { get; set; }   // "", "all" or a status KEY
        [BindProperty(SupportsGet = true)] public string? AssignedToFilter { get; set; }

        public int TotalLeads => PaginatedLeads.TotalCount;

        /// <summary>Search or owner set — the toolbar's own filters.</summary>
        public bool HasSearch =>
            !string.IsNullOrWhiteSpace(SearchTerm) || !string.IsNullOrWhiteSpace(AssignedToFilter);

        /// <summary>Anything narrowing the list beyond the tab.</summary>
        public bool IsFiltered => HasSearch || CustomFilters.HasAny;

        // ── Tabs ──────────────────────────────────────────────────────────

        public bool IsActiveTab => string.IsNullOrEmpty(StatusFilter);
        public bool IsAllTab => string.Equals(StatusFilter, AllTab, StringComparison.OrdinalIgnoreCase);
        public bool IsStatusTab(string key) => StatusFilter == key;

        /// <summary>
        /// Statuses whose leads still need work: Open + Qualified categories.
        /// Retired ones included — a lead sitting in a retired status is still active.
        /// </summary>
        public IEnumerable<LeadStatusDto> ActiveStatuses => Statuses.Where(s =>
            s.Category == LeadStatusCategory.Open ||
            s.Category == LeadStatusCategory.Qualified);

        /// <summary>One tab per status, in the tenant's order. Retired only while they hold leads.</summary>
        public IEnumerable<LeadStatusDto> TabStatuses => Statuses
            .Where(s => s.IsActive || s.IsSystem || s.LeadCount > 0)
            .OrderBy(s => s.SortOrder);

        public int ActiveCount => ActiveStatuses.Sum(s => s.LeadCount);
        public int AllCount => Statuses.Sum(s => s.LeadCount);

        /// <summary>Heading for the empty state and page context.</summary>
        public string CurrentTabName =>
            IsActiveTab ? "Active" :
            IsAllTab    ? "All"    :
            StatusName(StatusFilter);

        // ── Routes (079, note 3) ──────────────────────────────────────

        /// <summary>Search and owner — the toolbar's filters, without the tab.</summary>
        private Dictionary<string, string> SearchRoute()
        {
            var route = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(SearchTerm))       route[nameof(SearchTerm)]       = SearchTerm.Trim();
            if (!string.IsNullOrWhiteSpace(AssignedToFilter)) route[nameof(AssignedToFilter)] = AssignedToFilter.Trim();
            return route;
        }

        /// <summary>The tab, search, owner and custom filters — for the pager and the delete dialog.</summary>
        public Dictionary<string, string> FilterRoute()
        {
            var route = SearchRoute();
            if (!string.IsNullOrWhiteSpace(StatusFilter)) route[nameof(StatusFilter)] = StatusFilter;
            foreach (var (k, v) in CustomFilters.Route) route[k] = v;
            return route;
        }

        /// <summary>A tab link: every filter, the page SIZE, never the page number.</summary>
        public Dictionary<string, string> TabRoute(string? statusFilter)
        {
            var route = SearchRoute();
            if (!string.IsNullOrWhiteSpace(statusFilter)) route[nameof(StatusFilter)] = statusFilter;
            foreach (var (k, v) in CustomFilters.Route) route[k] = v;
            route[nameof(PageSize)] = PageSize.ToString();
            return route;
        }

        /// <summary>"Clear" beside the search: drops search and owner, keeps the tab and custom filters.</summary>
        public Dictionary<string, string> ClearSearchRoute()
        {
            var route = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(StatusFilter)) route[nameof(StatusFilter)] = StatusFilter;
            foreach (var (k, v) in CustomFilters.Route) route[k] = v;
            route[nameof(PageSize)] = PageSize.ToString();
            return route;
        }

        /// <summary>FilterRoute plus page and size — the hidden fields of the delete form.</summary>
        public Dictionary<string, string> StateRoute()
        {
            var route = FilterRoute();
            route[nameof(PageNumber)] = PageNumber.ToString();
            route[nameof(PageSize)]   = PageSize.ToString();
            return route;
        }

        public PagerVm Pager => new()
        {
            Page       = PageNumber,
            PageSize   = PageSize,
            TotalCount = PaginatedLeads.TotalCount,
            TotalPages = PaginatedLeads.TotalCount == 0 ? 1 : (int)Math.Ceiling(PaginatedLeads.TotalCount / (double)PageSize),
            PageField  = nameof(PageNumber),
            Route      = FilterRoute()
        };

        public int FirstRow => PaginatedLeads.TotalCount == 0 ? 0 : (PageNumber - 1) * PageSize + 1;
        public int LastRow  => Math.Min(PageNumber * PageSize, PaginatedLeads.TotalCount);

        // =============================================================
        // GET
        // =============================================================

        public async Task<IActionResult> OnGetAsync()
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Read);
            if (permissionCheck != null) return permissionCheck;

            await InitializePermissionsAsync();

            PageSize   = ClampPageSize(PageSize);
            PageNumber = Math.Max(PageNumber, 1);

            // Statuses first: the tab decides which keys the list asks for.
            await LoadStatusesAsync();
            await LoadCustomFieldsAsync(Request.Query);

            var tenantId = _currentUserService.GetCurrentTenantId();

            try
            {
                PaginatedLeads = await _leadService.GetPaginatedAsync(
                    tenantId: tenantId,
                    pageNumber: PageNumber,
                    pageSize: PageSize,
                    searchTerm: SearchTerm,
                    status: ResolveStatusQuery(),
                    assignedTo: AssignedToFilter,
                    customFilters: CustomFilters.Filters);

                // The API pulls a page past the end back to the last one;
                // follow it so the pager and the count agree with the rows.
                PageNumber = Math.Max(PaginatedLeads.Page, 1);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading leads");
                PageError = "Failed to load leads. Please try again.";   // note 4
                PaginatedLeads = new PaginatedResult<LeadListItem>
                {
                    Items = new List<LeadListItem>(),
                    Page = 1,
                    PageSize = PageSize,
                    TotalCount = 0
                };
            }

            try
            {
                Stats = await _leadService.GetStatsAsync(tenantId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load lead stats");
            }

            // (031) For the owner filter. A failure here just means the
            // dropdown has only "Anyone" in it — never a broken page.
            try
            {
                var salesTeam = await _leadService.GetSalesTeamAsync(tenantId);
                SalesTeamOptions = salesTeam.Select(u => new SelectListItem
                {
                    Value    = u.Id.ToString(),
                    Text     = u.FullName,
                    Selected = string.Equals(u.Id.ToString(), AssignedToFilter,
                                             StringComparison.OrdinalIgnoreCase)
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load the sales team for the owner filter");
            }

            return Page();
        }

        /// <summary>
        /// Turns the selected tab into the API's status parameter.
        ///   All            → null (no filter)
        ///   Active         → "New,Working,Qualified" (comma-separated keys)
        ///   a status tab   → that key
        /// If statuses failed to load, Active falls back to All rather than
        /// showing an empty list that looks like "you have no leads".
        /// </summary>
        private string? ResolveStatusQuery()
        {
            if (IsAllTab) return null;

            if (IsActiveTab)
            {
                var keys = ActiveStatuses.Select(s => s.Key).ToList();
                return keys.Count == 0 ? null : string.Join(",", keys);
            }

            return StatusFilter;
        }

        // =============================================================
        // DELETE
        // =============================================================

        public async Task<IActionResult> OnPostDeleteAsync(Guid id)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Delete);
            if (permissionCheck != null) return permissionCheck;

            // Every path REDIRECTS, so TempData is right here: the layout
            // shows the message once on the page the redirect lands on.
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();

                var lead = await _leadService.GetByIdAsync(tenantId, id);
                if (lead == null)
                {
                    TempData["ErrorMessage"] = "That lead no longer exists.";
                }
                else if (lead.IsConverted)
                {
                    TempData["ErrorMessage"] = "Converted leads cannot be deleted. Manage this record via the associated Deal.";
                }
                else
                {
                    await _leadService.DeleteAsync(tenantId, id);
                    TempData["SuccessMessage"] = "Lead deleted successfully!";
                }
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "That lead no longer exists.";
            }
            catch (InvalidOperationException ex)
            {
                TempData["ErrorMessage"] = ex.Message;    // the API's own reason
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting lead {Id}", id);
                TempData["ErrorMessage"] = "Failed to delete lead. Please try again.";
            }

            // Back to the same tab, page and filters (note 5).
            return RedirectToPage(ReturnRouteFrom(Request.Form));
        }

        /// <summary>A RouteValueDictionary — RedirectToPage reflects over plain objects.</summary>
        private RouteValueDictionary ReturnRouteFrom(IEnumerable<KeyValuePair<string, StringValues>> source)
        {
            var values = new RouteValueDictionary();

            foreach (var (k, v) in SearchRoute()) values[k] = v;
            if (!string.IsNullOrWhiteSpace(StatusFilter)) values[nameof(StatusFilter)] = StatusFilter;

            foreach (var (key, val) in source)
            {
                if (!key.StartsWith(CustomFieldListFilterState.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var first = val.Count > 0 ? val[0] : null;
                if (!string.IsNullOrWhiteSpace(first)) values[key] = first;
            }

            values[nameof(PageNumber)] = Math.Max(PageNumber, 1);
            values[nameof(PageSize)]   = ClampPageSize(PageSize);
            return values;
        }

        // =============================================================
        // Helpers
        // =============================================================

        private static int ClampPageSize(int size)
            => PagerVm.Sizes.Contains(size) ? size : DefaultPageSize;

        private async Task LoadStatusesAsync()
        {
            try
            {
                Statuses = await _statusService.GetAsync(selectableOnly: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load lead statuses");
                Statuses = new();
            }
        }

        /// <summary>079. Columns and filters. Non-fatal: a failure hides them and the panel says why.</summary>
        private async Task LoadCustomFieldsAsync(IEnumerable<KeyValuePair<string, StringValues>> query)
        {
            List<CustomFieldDefinitionDto> fields;
            var failed = false;
            try
            {
                fields = await _customFields.GetDefinitionsAsync(CustomFieldEntityTypes.Lead, includeInactive: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Custom fields could not be loaded for the leads list");
                fields = new List<CustomFieldDefinitionDto>();
                failed = true;
            }

            ListColumns = fields
                .Where(f => f.IsActive && f.ShowInList)
                .OrderBy(f => f.SortOrder)
                .Take(CustomFieldLimits.MaxListColumns)
                .ToList();

            CustomFilters = CustomFieldListFilterState.Parse(query, fields);
            CustomFilters.FormId     = FilterFormId;
            CustomFilters.PageRoute  = "./Index";
            CustomFilters.LoadFailed = failed;
            CustomFilters.Noun       = "lead";
            CustomFilters.Culture    = CustomFieldFormatter.ResolveCulture(CultureName);
            CustomFilters.DateFormat = TenantCtx.GetDateFormat();

            // The panel's "Clear these" keeps the tab, search, owner and size.
            var clear = SearchRoute();
            if (!string.IsNullOrWhiteSpace(StatusFilter)) clear[nameof(StatusFilter)] = StatusFilter;
            clear[nameof(PageSize)] = PageSize.ToString();
            CustomFilters.ClearRoute = clear;
        }

        // ── View helpers — call these in cshtml, never format inline ──

        private System.Globalization.CultureInfo? _culture;
        private string? _dateFormat;

        /// <summary>079. A custom column's value as a person reads it. Empty when not filled in.</summary>
        public string ColumnValue(LeadListItem lead, CustomFieldDefinitionDto field)
        {
            _culture    ??= CustomFieldFormatter.ResolveCulture(CultureName);
            _dateFormat ??= TenantCtx.GetDateFormat();

            lead.CustomFieldValues.TryGetValue(field.Id, out var wire);

            // An unticked checkbox has no value; on a list a blank cell says
            // "no" more quietly than a column full of the word.
            if (field.FieldType == CustomFieldTypes.Checkbox)
                return wire == "true" ? "Yes" : string.Empty;

            return CustomFieldFormatter.Display(field, wire, _culture, _dateFormat);
        }

        /// <summary>Use: @Model.StatusName(lead.Status). Falls back to the key.</summary>
        public string StatusName(string? key)
        {
            if (string.IsNullOrEmpty(key)) return "—";
            return Statuses.FirstOrDefault(s => s.Key == key)?.Name ?? key;
        }

        /// <summary>
        /// True when the key is the tenant's Converted (system) status.
        /// Use instead of lead.Status == "Converted" — the name is editable.
        /// </summary>
        public bool IsConvertedStatus(string? key) =>
            Statuses.FirstOrDefault(s => s.Key == key)?.Category == LeadStatusCategory.Converted;

        /// <summary>Badge colour by CATEGORY, so renamed/new statuses stay correct.</summary>
        public string GetStatusBadgeClass(string? key)
        {
            var status = Statuses.FirstOrDefault(s => s.Key == key);
            if (status == null) return "bg-dark";
            return CategoryBadgeClass(status.Category);
        }

        public static string CategoryBadgeClass(LeadStatusCategory category) => category switch
        {
            LeadStatusCategory.Qualified    => "bg-success",
            LeadStatusCategory.Disqualified => "bg-secondary",
            LeadStatusCategory.Converted    => "bg-warning text-dark",
            _                               => "bg-primary"   // Open
        };

        /// <summary>Small coloured dot for tab labels.</summary>
        public static string CategoryDotClass(LeadStatusCategory category) => category switch
        {
            LeadStatusCategory.Qualified    => "text-success",
            LeadStatusCategory.Disqualified => "text-secondary",
            LeadStatusCategory.Converted    => "text-warning",
            _                               => "text-primary"
        };
    }
}
