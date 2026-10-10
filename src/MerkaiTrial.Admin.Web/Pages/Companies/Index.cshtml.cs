// =====================================================================
// COMPANIES INDEX - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Companies/Index.cshtml.cs
//
// ✅ MIGRATED to AuthorizedPageModel (was AppPageModel) — consolidates
// onto the same permission-checking base class used by Products, so
// GET is blocked (redirect to Access Denied) for users without Read,
// not just POST handlers left silently unchecked.
//
// 078 — CUSTOM FIELDS ON THE LIST, AND THE SHARED TOOLBAR AND PAGER (072)
// (the same rebuild Contacts got in 076)
//
//   1. THE SHARED COMPONENTS. Search, the Vertical and Country filters and
//      the count go through _ListToolbar; paging through _Pager. The
//      hand-built search card and pager — with every filter spelled out
//      on each page link — are gone. All list state lives in the URL, so
//      a filtered list can be bookmarked and survives the back button.
//
//   2. CUSTOM FIELD COLUMNS. Fields marked "Show on the list" in
//      Settings → Custom Fields → Companies (at most four) are columns on
//      the desktop table and lines on the mobile cards, in the tenant's
//      own number and date format.
//
//   3. CUSTOM FIELD FILTERS through the shared _CustomFieldFilterPanel,
//      combined with the search and the toolbar's filters in one GET. The
//      search box also finds custom text values and dropdown choices.
//
//   4. A FAILED LOAD NO LONGER USES TempData. The catch set
//      TempData["ErrorMessage"] and then RENDERED the page. TempData set
//      on a response that renders is shown on the NEXT page — so the
//      error appeared one click late, on whatever page the person opened
//      next. It is PageError now: shown on this response, then gone.
//
//   5. `public int Page` IS NOW PageNumber, the shared pager's field name.
//      (It compiled — a CALL to Page() ignores the property — but one name
//      per concept is what keeps the dictionary-based pager safe.)
//
//   6. DELETE KEEPS YOUR PLACE. The dialog posts the list state, and the
//      handler redirects back to the same page of the same filtered list
//      instead of page 1 of everything. A page emptied by the delete is
//      pulled back to the last page by the API. KeyNotFound and a refusal
//      with a reason (InvalidOperationException) now get their own
//      messages instead of "Failed to delete company".
//
//   7. FormatDate / FormatDateTime / FormatCurrency are GONE: they were
//      copies of the base class's, which are tenant-aware already. The
//      ICurrentTenantService injection went with them — the base class's
//      TenantCtx covers the country name. TenantCurrencySymbol/Code and
//      TenantCountryCode were set and never read.
//
//   8. A FILTER VALUE THE LISTS DO NOT KNOW (a bookmarked country that has
//      since been switched off) is still offered in its dropdown, marked,
//      and selected — otherwise the list is filtered while the dropdown
//      claims "All countries".
// =====================================================================

using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Pages.Shared;
using MerkaiTrial.Admin.Web.Services.Companies;
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
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace MerkaiTrial.Admin.Web.Pages.Companies
{
    public class IndexModel : AuthorizedPageModel
    {
        private readonly ICompanyService     _companyService;
        private readonly ICountryService     _countryService;
        private readonly IMetaService        _metaService;
        private readonly ICustomFieldService _customFields;

        protected override string ModuleName => Modules.Companies;

        /// <summary>The toolbar form's id — the filter panel's inputs submit with it.</summary>
        public const string FilterFormId = "companiesFilter";

        public const int DefaultPageSize = 25;

        public IndexModel(
            ICompanyService         companyService,
            ICountryService         countryService,
            IMetaService            metaService,
            ICustomFieldService     customFields,
            IAuthorizationService   authorizationService,
            ICurrentUserService     currentUserService,
            ILogger<IndexModel>     logger)
            : base(authorizationService, currentUserService, logger)
        {
            _companyService = companyService;
            _countryService = countryService;
            _metaService    = metaService;
            _customFields   = customFields;
        }

        // ── What the page renders ─────────────────────────────────────

        public PaginatedResult<CompanyListItem> PaginatedCompanies { get; private set; } = new();
        public CompanyStatsDto Stats { get; private set; } = new(0, 0, 0, 0);
        public List<CountryListItem> Countries { get; private set; } = new();
        public List<VerticalDto> Verticals { get; private set; } = new();

        /// <summary>The tenant's own country, for the subtitle.</summary>
        public string TenantCountryName { get; private set; } = string.Empty;

        /// <summary>Active custom fields shown as list columns, in order.</summary>
        public List<CustomFieldDefinitionDto> ListColumns { get; private set; } = new();

        /// <summary>The custom field filters: parsed, checked, ready for the API and the panel.</summary>
        public CustomFieldListFilterState CustomFilters { get; private set; } = new();

        /// <summary>Shown on THIS response only — see note 4.</summary>
        public string? PageError { get; private set; }

        // ── List state, all in the URL ────────────────────────────────

        [BindProperty(SupportsGet = true)] public string? SearchTerm { get; set; }
        [BindProperty(SupportsGet = true)] public string? VerticalFilter { get; set; }
        [BindProperty(SupportsGet = true)] public string? CountryFilter { get; set; }

        [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
        [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = DefaultPageSize;

        private string? Vertical => string.IsNullOrWhiteSpace(VerticalFilter) ? null : VerticalFilter.Trim();
        private string? Country  => string.IsNullOrWhiteSpace(CountryFilter)  ? null : CountryFilter.Trim();

        private bool ToolbarFiltered =>
            !string.IsNullOrWhiteSpace(SearchTerm) || Vertical is not null || Country is not null;

        public bool IsFiltered => ToolbarFiltered || CustomFilters.HasAny;

        // ── The shared components ─────────────────────────────────────

        public int TotalCount => PaginatedCompanies.TotalCount;
        public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
        public int FirstRow   => TotalCount == 0 ? 0 : (PageNumber - 1) * PageSize + 1;
        public int LastRow    => Math.Min(PageNumber * PageSize, TotalCount);

        public ListToolbarVm Toolbar => new()
        {
            FormId            = FilterFormId,
            SearchName        = nameof(SearchTerm),
            SearchValue       = SearchTerm,
            SearchPlaceholder = "Name, tax ID or any text detail",
            SearchLabel       = "Search companies",
            SearchWidth       = "320px",

            Filters = new List<ListFilterVm>
            {
                new()
                {
                    Name    = nameof(VerticalFilter),
                    Label   = "Filter by vertical",
                    Options = VerticalOptions()
                },
                new()
                {
                    Name    = nameof(CountryFilter),
                    Label   = "Filter by country",
                    Options = CountryOptions()
                }
            },

            // The page SIZE is carried; the page NUMBER never is (072).
            Carry      = new Dictionary<string, string> { [nameof(PageSize)] = PageSize.ToString() },
            ClearRoute = new Dictionary<string, string> { [nameof(PageSize)] = PageSize.ToString() },

            IsFiltered   = IsFiltered,
            TotalCount   = TotalCount,
            FirstRow     = FirstRow,
            LastRow      = LastRow,
            NounSingular = "company",
            NounPlural   = "companies"
        };

        public PagerVm Pager => new()
        {
            Page       = PageNumber,
            PageSize   = PageSize,
            TotalCount = TotalCount,
            TotalPages = TotalPages,
            PageField  = nameof(PageNumber),
            Route      = FilterRoute()
        };

        /// <summary>
        /// The toolbar's filters as route values, WITHOUT the custom ones —
        /// what the panel's "Clear these" keeps.
        /// </summary>
        private Dictionary<string, string> ToolbarRoute()
        {
            var route = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(SearchTerm)) route[nameof(SearchTerm)]     = SearchTerm.Trim();
            if (Vertical is { } v)                      route[nameof(VerticalFilter)] = v;
            if (Country  is { } c)                      route[nameof(CountryFilter)]  = c;
            return route;
        }

        /// <summary>Every active filter — toolbar and custom — for the pager and the delete dialog.</summary>
        public Dictionary<string, string> FilterRoute()
        {
            var route = ToolbarRoute();
            foreach (var (k, v) in CustomFilters.Route) route[k] = v;
            return route;
        }

        /// <summary>FilterRoute plus the page and size — the hidden fields of the delete dialog.</summary>
        public Dictionary<string, string> StateRoute()
        {
            var route = FilterRoute();
            route[nameof(PageNumber)] = PageNumber.ToString();
            route[nameof(PageSize)]   = PageSize.ToString();
            return route;
        }

        private List<SelectListItem> VerticalOptions()
        {
            var items = new List<SelectListItem>
            {
                new() { Value = "", Text = "All verticals", Selected = Vertical is null }
            };
            items.AddRange(Verticals
                .Where(v => !string.IsNullOrWhiteSpace(v.Name))
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .Select(v => new SelectListItem
                {
                    Value    = v.Name,
                    Text     = v.Name,
                    Selected = string.Equals(v.Name, Vertical, StringComparison.OrdinalIgnoreCase)
                }));
            KeepUnknown(items, Vertical);   // note 8
            return items;
        }

        private List<SelectListItem> CountryOptions()
        {
            var items = new List<SelectListItem>
            {
                new() { Value = "", Text = "All countries", Selected = Country is null }
            };
            items.AddRange(Countries
                .Where(c => !string.IsNullOrWhiteSpace(c.Code))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new SelectListItem
                {
                    Value    = c.Code,
                    Text     = string.IsNullOrWhiteSpace(c.Name) ? c.Code : c.Name,
                    Selected = string.Equals(c.Code, Country, StringComparison.OrdinalIgnoreCase)
                }));
            KeepUnknown(items, Country);    // note 8
            return items;
        }

        /// <summary>Offer — and select — a filter value the list does not contain. Note 8.</summary>
        private static void KeepUnknown(List<SelectListItem> items, string? current)
        {
            if (current is null) return;
            if (items.Any(i => string.Equals(i.Value, current, StringComparison.OrdinalIgnoreCase))) return;
            items.Insert(1, new SelectListItem { Value = current, Text = $"{current} (not in the list)", Selected = true });
        }

        // ── Display helpers ───────────────────────────────────────────

        private Dictionary<string, string>? _countryNames;

        /// <summary>"TH" → "Thailand". Falls back to the code.</summary>
        public string GetCountryName(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return "-";
            _countryNames ??= Countries
                .Where(c => !string.IsNullOrWhiteSpace(c.Code))
                .GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Name ?? g.Key, StringComparer.OrdinalIgnoreCase);
            return _countryNames.TryGetValue(code.Trim(), out var name) && !string.IsNullOrWhiteSpace(name) ? name : code;
        }

        /// <summary>The first letter for the avatar circle.</summary>
        public string Initial(string? name)
            => string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[0].ToString().ToUpperInvariant();

        private System.Globalization.CultureInfo? _culture;
        private string? _dateFormat;

        /// <summary>A custom column's value as a person reads it. Empty when not filled in.</summary>
        public string ColumnValue(CompanyListItem company, CustomFieldDefinitionDto field)
        {
            _culture    ??= CustomFieldFormatter.ResolveCulture(CultureName);
            _dateFormat ??= TenantCtx.GetDateFormat();

            company.CustomFieldValues.TryGetValue(field.Id, out var wire);

            // An unticked checkbox has no value; on a list a blank cell says
            // "no" more quietly than a column full of the word.
            if (field.FieldType == CustomFieldTypes.Checkbox)
                return wire == "true" ? "Yes" : string.Empty;

            return CustomFieldFormatter.Display(field, wire, _culture, _dateFormat);
        }

        // =============================================================
        // GET
        // =============================================================

        public async Task<IActionResult> OnGetAsync()
        {
            // ✅ Check READ permission — redirects to Access Denied if missing
            var permissionCheck = await ValidatePermissionAsync(Actions.Read);
            if (permissionCheck != null) return permissionCheck;

            // Initialize permissions for UI buttons (Add Company / Edit / Delete)
            await InitializePermissionsAsync();

            await LoadAsync(Request.Query);
            return Page();
        }

        // =============================================================
        // DELETE
        // =============================================================

        public async Task<IActionResult> OnPostDeleteAsync(Guid id)
        {
            // ✅ Check DELETE permission
            var permissionCheck = await ValidatePermissionAsync(Actions.Delete);
            if (permissionCheck != null) return permissionCheck;

            // TempData is right HERE: every path below REDIRECTS, and the
            // layout shows the message once on the page the redirect lands on.
            try
            {
                var tenantId = CurrentUserService.GetCurrentTenantId();
                await _companyService.DeleteAsync(tenantId, id);
                TempData["SuccessMessage"] = "Company deleted successfully!";
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "That company no longer exists.";
            }
            catch (InvalidOperationException ex)
            {
                TempData["ErrorMessage"] = ex.Message;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error deleting company {Id}", id);
                TempData["ErrorMessage"] = "Failed to delete company. Please try again.";
            }

            // Back to the same page of the same filtered list (note 6). The
            // dialog posted the list state as hidden fields: the toolbar
            // ones bind to the properties above, the custom ones are read
            // straight from the form.
            return RedirectToPage(ReturnRouteFrom(Request.Form));
        }

        /// <summary>A RouteValueDictionary — RedirectToPage reflects over plain objects.</summary>
        private RouteValueDictionary ReturnRouteFrom(IEnumerable<KeyValuePair<string, StringValues>> source)
        {
            var values = new RouteValueDictionary();

            foreach (var (k, v) in ToolbarRoute()) values[k] = v;

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

        private async Task LoadAsync(IEnumerable<KeyValuePair<string, StringValues>> query)
        {
            PageSize   = ClampPageSize(PageSize);
            PageNumber = Math.Max(PageNumber, 1);

            var tenantId = CurrentUserService.GetCurrentTenantId();

            try
            {
                TenantCountryName = TenantCtx.GetCountryName();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Tenant country name could not be resolved");
                TenantCountryName = string.Empty;
            }

            // ── Lookups for the filters. Non-fatal. ───────────────────
            try
            {
                Countries = await _countryService.GetActiveAsync();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Countries could not be loaded for the companies list");
                Countries = new List<CountryListItem>();
            }

            try
            {
                Verticals = await _metaService.GetVerticalsAsync();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Verticals could not be loaded for the companies list");
                Verticals = new List<VerticalDto>();
            }

            // ── Custom fields: columns and filters. Non-fatal. ────────
            List<CustomFieldDefinitionDto> fields;
            var fieldsFailed = false;
            try
            {
                fields = await _customFields.GetDefinitionsAsync(CustomFieldEntityTypes.Company, includeInactive: false);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Custom fields could not be loaded for the companies list");
                fields = new List<CustomFieldDefinitionDto>();
                fieldsFailed = true;
            }

            ListColumns = fields
                .Where(f => f.IsActive && f.ShowInList)
                .OrderBy(f => f.SortOrder)
                .Take(CustomFieldLimits.MaxListColumns)
                .ToList();

            CustomFilters = CustomFieldListFilterState.Parse(query, fields);
            CustomFilters.FormId     = FilterFormId;
            CustomFilters.PageRoute  = "./Index";
            CustomFilters.LoadFailed = fieldsFailed;
            CustomFilters.Noun       = "company";
            CustomFilters.Culture    = CustomFieldFormatter.ResolveCulture(CultureName);
            CustomFilters.DateFormat = TenantCtx.GetDateFormat();

            var clear = ToolbarRoute();
            clear[nameof(PageSize)] = PageSize.ToString();
            CustomFilters.ClearRoute = clear;

            // ── The list ──────────────────────────────────────────────
            try
            {
                Stats = await _companyService.GetStatsAsync(tenantId);

                PaginatedCompanies = await _companyService.GetAllAsync(
                    tenantId:       tenantId,
                    pageNumber:     PageNumber,
                    pageSize:       PageSize,
                    searchTerm:     SearchTerm,
                    verticalFilter: Vertical,
                    countryFilter:  Country,
                    customFilters:  CustomFilters.Filters);

                // The API pulls a page past the end back to the last one;
                // follow it so the pager and the count agree with the rows.
                PageNumber = Math.Max(PaginatedCompanies.Page, 1);

                Logger.LogInformation(
                    "Loaded page {Page} with {Count} companies (Total: {Total})",
                    PageNumber, PaginatedCompanies.Items.Count, PaginatedCompanies.TotalCount);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading companies");
                PageError = "Failed to load companies. Please try again.";   // note 4

                PaginatedCompanies = new PaginatedResult<CompanyListItem>
                {
                    Items      = new List<CompanyListItem>(),
                    Page       = 1,
                    PageSize   = PageSize,
                    TotalCount = 0
                };
            }
        }
    }
}
