// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Products/Index.cshtml.cs
//
// COMPLETE FILE — 068.
//
// WHAT CHANGED: the category filter, the icons and the colours all came
// from ProductCategoriesConfiguration, a static list of eight C#
// constants. They come from the workspace's own category list now.
//
// Three calls replaced, one dependency added:
//
//   GetCategoryFilterOptions()  — the dropdown is the workspace's list.
//   GetCategoryIcon(category)   — asks the lookup, which falls back.
//   GetCategoryColor(category)  — likewise.
//
// THE LOOKUP IS BUILT ONCE PER REQUEST, in OnGetAsync, and held on the
// model. The view calls GetCategoryIcon once per product and twice per
// row (desktop table and mobile card), so on a page of 100 products
// that is 400 calls. Each one is a dictionary hit against an object
// built from a single API response — not 400 API calls, which is what
// asking the service per row would have been.
//
// IT FALLS BACK RATHER THAN FAILING. A product can carry a category
// that no longer has a row: removed on the Settings page while its
// products were reassigned in another tab, or imported before 068.
// ProductCategoryLookup answers for any name — the workspace's row, then
// the Merkai default of that name, then a grey box — so one such product
// cannot take out the page. See ProductCategoryService.cs.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Services.Products;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MerkaiTrial.Admin.Web.Pages.Products
{
    public class IndexModel : AuthorizedPageModel
    {
        private readonly IProductService _productService;
        private readonly ICurrentTenantService _tenantService;
        private readonly IProductCategoryService _categoryService;   // 068

        protected override string ModuleName => Modules.Products;

        public IndexModel(
            IProductService productService,
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            IProductCategoryService categoryService,                 // 068
            ILogger<IndexModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _productService = productService;
            _tenantService = tenantService;
            _categoryService = categoryService;
        }

        /// <summary>
        /// 068. Name -> icon and colour, built ONCE per request. Never
        /// null: an empty lookup still answers every call, with the Merkai
        /// default for a known name and a grey box otherwise.
        /// </summary>
        public ProductCategoryLookup CategoryLookup { get; private set; } =
            new ProductCategoryLookup(null);

        public PaginatedResult<ProductListItem> PaginatedProducts { get; set; } = new();
        public ProductStatsDto Stats { get; set; } = null!;

        [BindProperty(SupportsGet = true)]
        public int Page { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public int PageSize { get; set; } = 25;

        [BindProperty(SupportsGet = true)]
        public string? CategoryFilter { get; set; }

        [BindProperty(SupportsGet = true)]
        public bool? IsActiveFilter { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? SearchTerm { get; set; }

        public string TenantCurrencySymbol { get; private set; } = "";
        public string TenantCurrencyCode { get; private set; } = "";

        public async Task<IActionResult> OnGetAsync()
        {
            TenantCurrencyCode = _tenantService.GetCurrencyCode();
            TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
            try
            {
                // Validate read permission (now returns IActionResult if denied)
                var permissionCheck = await ValidatePermissionAsync(Actions.Read);
                if (permissionCheck != null) return permissionCheck; // Redirect to access denied

                // Initialize permissions for UI
                await InitializePermissionsAsync();

                if (PageSize < 5) PageSize = 5;
                if (PageSize > 100) PageSize = 100;

                var tenantId = CurrentUserService.GetCurrentTenantId();

                // 068. BEFORE the products are read, because every row
                // rendered needs it and the view has no way to await.
                // includeInactive: a product filed under a category
                // somebody switched off still has to render with its own
                // icon rather than a grey box.
                CategoryLookup = await _categoryService.GetLookupAsync();

                Stats = await _productService.GetStatsAsync(tenantId);

                PaginatedProducts = await _productService.GetAllAsync(
                    tenantId,
                    Page,
                    PageSize,
                    CategoryFilter,
                    IsActiveFilter,
                    SearchTerm);

                Logger.LogInformation("Loaded page {Page} with {Count} products",
                    Page, PaginatedProducts.Items.Count);

                return Page();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading products");
                TempData["ErrorMessage"] = "Failed to load products. Please try again.";

                Stats = new ProductStatsDto(0, 0, 0, 0);
                PaginatedProducts = new PaginatedResult<ProductListItem>();

                // Leave CategoryLookup as the empty one it was
                // constructed with. It still answers every call, so the
                // error banner renders instead of a second exception.
                return Page();
            }
        }



        public async Task<IActionResult> OnPostDeleteAsync(Guid id)
        {
            try
            {
                // Validate delete permission
                var permissionCheck = await ValidatePermissionAsync(Actions.Delete);
                if (permissionCheck != null) return permissionCheck; // Redirect to access denied

                var tenantId = CurrentUserService.GetCurrentTenantId();
                await _productService.DeleteAsync(tenantId, id);
                TempData["SuccessMessage"] = "Product deleted successfully!";
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error deleting product {Id}", id);
                TempData["ErrorMessage"] = "Failed to delete product. Please try again.";
                return RedirectToPage();
            }
        }



        /// <summary>
        /// 068. The workspace's own categories, not the eight C# constants.
        ///
        /// Active ones only — filtering by a retired category would return
        /// the products still carrying it, which is arguably useful but is
        /// not what a dropdown of current choices should offer. The
        /// Settings page is where a retired category is visible.
        ///
        /// A CURRENT FILTER THAT IS NO LONGER A CATEGORY IS KEPT. Somebody
        /// may have the page open with ?CategoryFilter=Hardware while
        /// Hardware was removed in another tab; dropping it from the list
        /// would make the select fall back to "All Categories" while the
        /// results stayed filtered, and the page would be lying.
        /// </summary>
        public SelectList GetCategoryFilterOptions()
        {
            var items = new List<SelectListItem>
            {
                new SelectListItem { Value = "", Text = "All Categories" }
            };

            var names = CategoryLookup.ActiveNames;

            if (!string.IsNullOrWhiteSpace(CategoryFilter) &&
                !names.Contains(CategoryFilter, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(new SelectListItem
                {
                    Value = CategoryFilter,
                    Text  = CategoryFilter + " (no longer a category)"
                });
            }

            items.AddRange(names.Select(c => new SelectListItem { Value = c, Text = c }));
            return new SelectList(items, "Value", "Text", CategoryFilter);
        }

        public SelectList GetIsActiveFilterOptions()
        {
            var items = new List<SelectListItem>
            {
                new SelectListItem { Value = "", Text = "All Products" },
                new SelectListItem { Value = "true", Text = "Active Only" },
                new SelectListItem { Value = "false", Text = "Inactive Only" }
            };
            return new SelectList(items, "Value", "Text", IsActiveFilter?.ToString().ToLower());
        }

        public async Task<IActionResult> OnPostToggleActiveAsync(Guid id)
        {
            try
            {
                var permissionCheck = await ValidatePermissionAsync(Actions.Update);
                if (permissionCheck != null) return permissionCheck;

                var tenantId = CurrentUserService.GetCurrentTenantId();
                var isNowActive = await _productService.ToggleActiveAsync(tenantId, id);

                TempData["SuccessMessage"] = isNowActive
                    ? "Product activated."
                    : "Product deactivated.";
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error toggling product {Id}", id);
                TempData["ErrorMessage"] = "Failed to update product status.";
            }

            return RedirectToPage();
        }
        /// <summary>
        /// 068. The workspace's own icon for this category, falling back
        /// to the Merkai default of that name and then to a grey box. The
        /// fallback is not theoretical: a category can be removed while
        /// products still carry its name.
        /// </summary>
        public string GetCategoryIcon(string? category)
            => CategoryLookup.IconFor(category);

        /// <summary>068 — see GetCategoryIcon.</summary>
        public string GetCategoryColor(string? category)
            => CategoryLookup.ColorFor(category);

        /// <summary>
        /// 068. True when this product's category no longer has a row.
        /// The list uses it for a quiet tooltip on the icon — otherwise
        /// the only hint is a grey box, which looks like a styling bug
        /// rather than a category somebody removed.
        /// </summary>
        public bool IsCategoryOrphaned(string? category)
            => CategoryLookup.IsOrphaned(category);

        /// <summary>
        /// 068. The tooltip for an orphaned category, or NULL.
        ///
        /// Built HERE rather than in the view on purpose: the sentence
        /// contains the category name in quotes, and a C# interpolated
        /// string with escaped quotes inside a Razor attribute value is
        /// exactly the kind of thing that parses today and breaks when
        /// somebody reformats the line. A method returning string? renders
        /// as nothing when it is null, which is what the attribute wants.
        /// </summary>
        /// <summary>
        /// 070. The tooltip on a bundle's badge: its contents, or a nudge
        /// when it is empty.
        ///
        /// Built HERE, not in the view, for the reason 068's
        /// CategoryWarning gives: a C# ternary with string literals
        /// inside a Razor attribute value parses today and breaks the
        /// first time somebody reformats the line. A method returning a
        /// string is unambiguous.
        /// </summary>
        public string BundleTooltip(ProductListItem product)
            => product.BundleComponentCount == 0
                ? "This bundle is empty — edit it and add its contents."
                : product.BundleBreakdown ?? "A bundle of products.";

        public string? CategoryWarning(string? category)
            => CategoryLookup.IsOrphaned(category)
                ? $"\"{category}\" is no longer one of your categories"
                : null;

        public string GetCurrencySymbol(string currency)
        {
            return CurrencyConfiguration.GetCurrencySymbol(currency);
        }

        /// <summary>
        /// 051. "/ m²", "/ hrs" — or EMPTY for a plain unit, because
        /// "₹450 / unit" reads worse than "₹450" down a column of prices.
        /// </summary>
        public string PricedPer(string? unitOfMeasure)
        {
            var s = UnitsOfMeasure.ShortOf(unitOfMeasure);
            return string.IsNullOrEmpty(s) ? string.Empty : " / " + s;
        }
    }
}
