// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Products/Index.cshtml.cs
//
// COMPLETE FILE — 080.
//
// 068 (unchanged): the category filter, icons and colours come from the
// workspace's own category list, through ProductCategoryLookup, built
// ONCE per request and held on the model. It falls back rather than
// failing — the workspace's row, then the Merkai default of that name,
// then a grey box — so a product filed under a removed category cannot
// take out the page.
//
// ── 080 ─────────────────────────────────────────────────────────────
//
// 1. CUSTOM FIELDS. Up to four "Show on the list" fields become columns
//    (desktop) and label/value lines (mobile). The search box also finds
//    custom text values and dropdown choices, and the shared
//    _CustomFieldFilterPanel filters by any active field. Same pieces as
//    Contacts, Deals, Companies and Leads.
//
// 2. THE SHARED LIST PIECES. _ListToolbar for search, Category, Status
//    and the "1–25 of 140" count; _Pager for paging and page size. The
//    hand-built pager is gone, and with it three bugs:
//      • the page-size form carried a field called "Page" while the
//        links carried "page", and the pager drew "Showing 1 to 0 of 0";
//      • a custom filter would have been dropped by every page link;
//      • the page number was a property called Page, which hides
//        PageModel.Page(). It is PageNumber now, like every other list.
//
// 3. STATUS IS BOUND AS A STRING ("", "true", "false") and parsed here.
//    A bool? bound straight from the query string turned "" into a model
//    state error and an unexpected value into "all products" silently.
//
// 4. A FAILED LOAD SHOWS ON THIS RESPONSE (PageError). It used to set
//    TempData["ErrorMessage"] and then RENDER, so the banner appeared on
//    the NEXT page the person opened instead of this one. TempData is now
//    used only before a redirect, as everywhere else.
//
//    The list read is GetListAsync, which THROWS: GetAllAsync swallowed
//    the error and returned an empty page, so an outage looked exactly
//    like "No products yet". The stats and categories load separately,
//    so either failing leaves the list working.
//
// 5. DELETE IS SAFE FOR ANY NAME. The buttons called
//    confirmDelete('@id', '@name') — a product called O'Brien's Kit was
//    a JavaScript syntax error, and a name with a double quote broke out
//    of the attribute (an XSS hole). The name is now a data- attribute
//    read by one delegated listener and shown in a modal as text.
//
// 6. DELETE AND THE ACTIVE SWITCH KEEP THE LIST STATE: search, category,
//    status, custom filters, page and size all come back after the
//    redirect, instead of dropping the person on page 1 unfiltered.
//    A refused delete (the product is part of a bundle) shows the API's
//    own sentence.
//
// ── 096 ─────────────────────────────────────────────────────────────
//
// 7. SORTING. Product, SKU, Category, Price and Status sort the whole
//    list on the server; click again to flip. ?Sort=&Dir= ride along on
//    every page link, search, filter, Clear and form. No sort = the
//    catalogue order (category, then name), as before. Phones get a
//    Sort menu above the cards.
//
// 8. 10 PER PAGE, like every other list (it was 25 here), and the size a
//    person picks is remembered on their browser (ListPreferences).
//
// 9. ONE PAGER. The view drew _Pager twice — inside the desktop card and
//    again under the phone cards — so the "Per page" select appeared
//    twice with the same id. It is drawn once now, under both.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Pages.Shared;
using MerkaiTrial.Admin.Web.Services.CustomFields;
using MerkaiTrial.Admin.Web.Services.Products;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace MerkaiTrial.Admin.Web.Pages.Products
{
    public class IndexModel : AuthorizedPageModel
    {
        /// <summary>The toolbar form's id; the custom filter inputs post with it.</summary>
        public const string FilterFormId = "productsFilter";

        /// <summary>Default rows per page; also what an unknown size falls back to. 096: 10, was 25.</summary>
        public const int DefaultPageSize = PagerVm.DefaultSize;

        private readonly IProductService _productService;
        private readonly ICurrentTenantService _tenantService;
        private readonly IProductCategoryService _categoryService;   // 068
        private readonly ICustomFieldService _customFields;          // 080

        protected override string ModuleName => Modules.Products;

        public IndexModel(
            IProductService productService,
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            IProductCategoryService categoryService,                 // 068
            ICustomFieldService customFields,                        // 080
            ILogger<IndexModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _productService  = productService;
            _tenantService   = tenantService;
            _categoryService = categoryService;
            _customFields    = customFields;
        }

        // ── what the view renders ─────────────────────────────────────

        /// <summary>
        /// 068. Name -> icon and colour, built ONCE per request. Never
        /// null: an empty lookup still answers every call.
        /// </summary>
        public ProductCategoryLookup CategoryLookup { get; private set; } =
            new ProductCategoryLookup(null);

        public PaginatedResult<ProductListItem> PaginatedProducts { get; private set; } = new();

        /// <summary>Zeros until loaded; a failed stats read leaves them as zeros.</summary>
        public ProductStatsDto Stats { get; private set; } = new ProductStatsDto(0, 0, 0, 0);

        /// <summary>080. The "Show on the list" fields, in order, at most four.</summary>
        public List<CustomFieldDefinitionDto> ListColumns { get; private set; } = new();

        /// <summary>080. The custom field filter panel's state.</summary>
        public CustomFieldListFilterState CustomFilters { get; private set; } = new();

        /// <summary>080. A failed list load, for THIS response only (note 4).</summary>
        public string? PageError { get; private set; }

        public string TenantCurrencySymbol { get; private set; } = "";
        public string TenantCurrencyCode { get; private set; } = "";

        // ── query string ──────────────────────────────────────────────

        [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
        [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = DefaultPageSize;
        [BindProperty(SupportsGet = true)] public string? SearchTerm { get; set; }
        [BindProperty(SupportsGet = true)] public string? CategoryFilter { get; set; }

        /// <summary>080. "", "true" or "false" — a string, parsed below (note 3).</summary>
        [BindProperty(SupportsGet = true)] public string? StatusFilter { get; set; }

        [BindProperty(SupportsGet = true)] public string? Sort { get; set; }    // 096
        [BindProperty(SupportsGet = true)] public string? Dir { get; set; }     // 096

        private string? Category => string.IsNullOrWhiteSpace(CategoryFilter) ? null : CategoryFilter.Trim();

        /// <summary>080. The parsed status: null = all, true = active, false = inactive.</summary>
        public bool? IsActive => StatusFilter?.Trim().ToLowerInvariant() switch
        {
            "true"  => true,
            "false" => false,
            _       => null
        };

        private bool ToolbarFiltered =>
            !string.IsNullOrWhiteSpace(SearchTerm) || Category is not null || IsActive.HasValue;

        public bool IsFiltered => ToolbarFiltered || CustomFilters.HasAny;

        // ── counts, toolbar, pager ────────────────────────────────────

        public int TotalCount => PaginatedProducts.TotalCount;
        public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
        public int FirstRow   => TotalCount == 0 ? 0 : (PageNumber - 1) * PageSize + 1;
        public int LastRow    => Math.Min(PageNumber * PageSize, TotalCount);

        public ListToolbarVm Toolbar => new()
        {
            FormId            = FilterFormId,
            SearchName        = nameof(SearchTerm),
            SearchValue       = SearchTerm,
            SearchPlaceholder = "Name, SKU, description or any text detail",
            SearchLabel       = "Search products",
            SearchWidth       = "320px",

            Filters = new List<ListFilterVm>
            {
                new()
                {
                    Name    = nameof(CategoryFilter),
                    Label   = "Filter by category",
                    Options = CategoryOptions()
                },
                new()
                {
                    Name    = nameof(StatusFilter),
                    Label   = "Filter by status",
                    Options = StatusOptions()
                }
            },

            // 096 — the size and the sort; never the page number (072).
            Carry      = SizeAndSort(),
            ClearRoute = SizeAndSort(),

            IsFiltered   = IsFiltered,
            TotalCount   = TotalCount,
            FirstRow     = FirstRow,
            LastRow      = LastRow,
            NounSingular = "product",
            NounPlural   = "products"
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

        // ── sorting (096, note 7) ─────────────────────────────────────

        public static readonly SortOption[] SortOptions =
        {
            new("name",     "Product"),
            new("sku",      "SKU"),
            new("category", "Category"),
            new("price",    "Price",  DefaultDescending: true),
            new("status",   "Status", AscLabel: "active first", DescLabel: "inactive first")
        };

        private ListSortVm SortState(Dictionary<string, string>? route = null) => new()
        {
            Options = SortOptions,
            Sort    = Sort,
            Dir     = Dir,
            // The default is the catalogue order (category, then name), so
            // the Category header shows as the active one.
            DefaultKey = "category",
            Route   = route ?? new Dictionary<string, string>()
        };

        /// <summary>The headers' and the phone menu's links: every filter and the size, never the page or the sort.</summary>
        public ListSortVm Sorter
        {
            get
            {
                var route = FilterRoute();
                route.Remove(nameof(Sort));
                route.Remove(nameof(Dir));
                route[nameof(PageSize)] = PageSize.ToString();
                return SortState(route);
            }
        }

        /// <summary>The page size and the current sort — what a search or a Clear keeps.</summary>
        private Dictionary<string, string> SizeAndSort()
        {
            var route = SortState().Current();
            route[nameof(PageSize)] = PageSize.ToString();
            return route;
        }

        /// <summary>096. "Clear the search and filters": the page size and the sort, nothing else.</summary>
        public Dictionary<string, string> ClearAllRoute() => SizeAndSort();

        /// <summary>Search, category and status — what the toolbar holds. 096: and the sort.</summary>
        private Dictionary<string, string> ToolbarRoute()
        {
            var route = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(SearchTerm)) route[nameof(SearchTerm)]     = SearchTerm.Trim();
            if (Category is { } c)                      route[nameof(CategoryFilter)] = c;
            if (IsActive is { } a)                      route[nameof(StatusFilter)]   = a ? "true" : "false";
            foreach (var (k, v) in SortState().Current()) route[k] = v;
            return route;
        }

        /// <summary>Every filter, toolbar and custom — what a page link carries.</summary>
        public Dictionary<string, string> FilterRoute()
        {
            var route = ToolbarRoute();
            foreach (var (k, v) in CustomFilters.Route) route[k] = v;
            return route;
        }

        /// <summary>The whole list state, for the delete and toggle forms.</summary>
        public Dictionary<string, string> StateRoute()
        {
            var route = FilterRoute();
            route[nameof(PageNumber)] = PageNumber.ToString();
            route[nameof(PageSize)]   = PageSize.ToString();
            return route;
        }

        /// <summary>
        /// 068. The workspace's own ACTIVE categories. A current filter that
        /// is no longer a category is KEPT, marked, so the select does not
        /// fall back to "All categories" while the results stay filtered.
        /// </summary>
        private List<SelectListItem> CategoryOptions()
        {
            var current = Category;
            var items = new List<SelectListItem>
            {
                new() { Value = "", Text = "All categories", Selected = current is null }
            };

            var names = CategoryLookup.ActiveNames;

            if (current is not null &&
                !names.Contains(current, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(new SelectListItem
                {
                    Value    = current,
                    Text     = current + " (no longer a category)",
                    Selected = true
                });
            }

            items.AddRange(names.Select(n => new SelectListItem
            {
                Value    = n,
                Text     = n,
                Selected = string.Equals(n, current, StringComparison.OrdinalIgnoreCase)
            }));
            return items;
        }

        private List<SelectListItem> StatusOptions() => new()
        {
            new() { Value = "",      Text = "Active and inactive", Selected = IsActive is null },
            new() { Value = "true",  Text = "Active only",         Selected = IsActive == true },
            new() { Value = "false", Text = "Inactive only",       Selected = IsActive == false }
        };

        // ── GET ───────────────────────────────────────────────────────

        public async Task<IActionResult> OnGetAsync()
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Read);
            if (permissionCheck != null) return permissionCheck;

            await InitializePermissionsAsync();

            // 096 — the URL's size, else this browser's last choice, else 10.
            PageSize   = ListPreferences.PageSize(HttpContext, "products");
            PageNumber = Math.Max(PageNumber, 1);

            TenantCurrencyCode   = _tenantService.GetCurrencyCode();
            TenantCurrencySymbol = _tenantService.GetCurrencySymbol();

            var tenantId = CurrentUserService.GetCurrentTenantId();

            // 068. Before the products, because every row needs it. A
            // failure leaves the empty lookup, which still answers.
            try
            {
                CategoryLookup = await _categoryService.GetLookupAsync();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Product categories could not be loaded for the products list");
            }

            await LoadCustomFieldsAsync(Request.Query);

            try
            {
                Stats = await _productService.GetStatsAsync(tenantId);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Product stats could not be loaded");
            }

            try
            {
                PaginatedProducts = await _productService.GetListAsync(
                    tenantId,
                    PageNumber,
                    PageSize,
                    Category,
                    IsActive,
                    SearchTerm,
                    CustomFilters.Filters,
                    SortState().ApiSort,          // 096
                    SortState().ApiDir);

                // The API moves a page past the end back to the last page.
                PageNumber = Math.Max(PaginatedProducts.Page, 1);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading products");
                PageError = "The products list could not be loaded. Please try again.";
                PaginatedProducts = new PaginatedResult<ProductListItem>
                {
                    Items      = new List<ProductListItem>(),
                    Page       = 1,
                    PageSize   = PageSize,
                    TotalCount = 0
                };
            }

            return Page();
        }

        // ── POST: delete, toggle ──────────────────────────────────────

        public async Task<IActionResult> OnPostDeleteAsync(Guid id)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Delete);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var tenantId = CurrentUserService.GetCurrentTenantId();
                await _productService.DeleteAsync(tenantId, id);
                TempData["SuccessMessage"] = "Product deleted.";
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "That product no longer exists.";
            }
            catch (InvalidOperationException ex)
            {
                TempData["ErrorMessage"] = ex.Message;   // the API's own reason, e.g. part of a bundle
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error deleting product {Id}", id);
                TempData["ErrorMessage"] = "The product could not be deleted. Please try again.";
            }

            return RedirectToPage(ReturnRouteFrom(Request.Form));
        }

        public async Task<IActionResult> OnPostToggleActiveAsync(Guid id)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var tenantId = CurrentUserService.GetCurrentTenantId();
                var isNowActive = await _productService.ToggleActiveAsync(tenantId, id);

                TempData["SuccessMessage"] = isNowActive
                    ? "Product activated."
                    : "Product deactivated. It stays on existing quotes but can't be added to new ones.";
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "That product no longer exists.";
            }
            catch (InvalidOperationException ex)
            {
                TempData["ErrorMessage"] = ex.Message;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error toggling product {Id}", id);
                TempData["ErrorMessage"] = "The product's status could not be changed. Please try again.";
            }

            return RedirectToPage(ReturnRouteFrom(Request.Form));
        }

        /// <summary>
        /// 080. Where a POST lands: the toolbar filters (bound from the
        /// form's hidden fields), every custom filter key (cf_…) that came
        /// with the form, and the page and size.
        /// </summary>
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

        private static int ClampPageSize(int size)
            => PagerVm.Sizes.Contains(size) ? size : DefaultPageSize;

        // ── custom fields ─────────────────────────────────────────────

        private async Task LoadCustomFieldsAsync(IEnumerable<KeyValuePair<string, StringValues>> query)
        {
            List<CustomFieldDefinitionDto> fields;
            var failed = false;
            try
            {
                fields = await _customFields.GetDefinitionsAsync(CustomFieldEntityTypes.Product, includeInactive: false);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Custom fields could not be loaded for the products list");
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
            CustomFilters.Noun       = "product";
            CustomFilters.Culture    = CustomFieldFormatter.ResolveCulture(CultureName);
            CustomFilters.DateFormat = TenantCtx.GetDateFormat();

            var clear = ToolbarRoute();
            clear[nameof(PageSize)] = PageSize.ToString();
            CustomFilters.ClearRoute = clear;
        }

        private System.Globalization.CultureInfo? _culture;
        private string? _dateFormat;

        /// <summary>080. A list-column value, formatted for this workspace. Empty when unset.</summary>
        public string ColumnValue(ProductListItem product, CustomFieldDefinitionDto field)
        {
            _culture    ??= CustomFieldFormatter.ResolveCulture(CultureName);
            _dateFormat ??= TenantCtx.GetDateFormat();

            product.CustomFieldValues.TryGetValue(field.Id, out var wire);

            if (field.FieldType == CustomFieldTypes.Checkbox)
                return wire == "true" ? "Yes" : string.Empty;

            return CustomFieldFormatter.Display(field, wire, _culture, _dateFormat);
        }

        // ── row helpers (068 / 051 / 070, unchanged) ──────────────────

        /// <summary>068. The workspace's own icon, with fallbacks.</summary>
        public string GetCategoryIcon(string? category)
            => CategoryLookup.IconFor(category);

        /// <summary>068 — see GetCategoryIcon.</summary>
        public string GetCategoryColor(string? category)
            => CategoryLookup.ColorFor(category);

        /// <summary>068. True when this product's category no longer has a row.</summary>
        public bool IsCategoryOrphaned(string? category)
            => CategoryLookup.IsOrphaned(category);

        /// <summary>
        /// 068. The tooltip for an orphaned category, or NULL. Built here
        /// rather than in the view: a quoted name inside a Razor attribute
        /// is exactly what breaks when somebody reformats the line.
        /// </summary>
        public string? CategoryWarning(string? category)
            => CategoryLookup.IsOrphaned(category)
                ? $"\"{category}\" is no longer one of your categories"
                : null;

        /// <summary>070. The tooltip on a bundle's badge.</summary>
        public string BundleTooltip(ProductListItem product)
            => product.BundleComponentCount == 0
                ? "This bundle is empty — edit it and add its contents."
                : product.BundleBreakdown ?? "A bundle of products.";

        /// <summary>051. "/ m²", "/ hrs" — or EMPTY for a plain unit.</summary>
        public string PricedPer(string? unitOfMeasure)
        {
            var s = UnitsOfMeasure.ShortOf(unitOfMeasure);
            return string.IsNullOrEmpty(s) ? string.Empty : " / " + s;
        }

        /// <summary>080. The kind's icon (ProductKinds), for the Type badge.</summary>
        public string KindIcon(string? type) => ProductKinds.Icon(type);
    }
}
