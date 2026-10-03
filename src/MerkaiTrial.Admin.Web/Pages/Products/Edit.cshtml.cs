// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Products/Edit.cshtml.cs
//
// COMPLETE FILE — 068.
//
// ONE CHANGE, with one wrinkle Create does not have: the Category
// dropdown comes from the workspace's own list instead of
// ProductCategoriesConfiguration, AND it keeps this product's existing
// category even when that is no longer one of them.
//
// Without that second part, opening a product filed under a category
// somebody removed would show a dropdown with nothing selected — and
// saving the form without touching it would silently clear the field.
// The product would lose its category because somebody edited its price.
//
// ── 069 ─────────────────────────────────────────────────────────────
//
// The form also carries PRICES IN OTHER CURRENCIES through
// _PriceGrid.cshtml, seeded from the product's own rows on GET.
//
// The same trap as the category, in a nastier form: the grid posts the
// COMPLETE list, so a grid that failed to load would post an empty one
// and DELETE every price on the product as a side effect of saving its
// name. Three things guard against that — the hidden field is seeded
// with the real prices rather than "[]" (see _PriceGrid), a malformed
// payload parses to null meaning "leave them alone" rather than to
// empty, and a POST failure rebuilds the grid from what was posted
// rather than from nothing.
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
using System.Text.Json;              // 069 — the price grid's hidden field

namespace MerkaiTrial.Admin.Web.Pages.Products
{
    public class EditModel : AuthorizedPageModel
    {
        private readonly IProductService _productService;
        private readonly IProductCategoryService _categoryService;   // 068
        private readonly ICurrentTenantService _tenantService;   // FIX: inject tenant service

        protected override string ModuleName => Modules.Products;

        public EditModel(
            IProductService productService,
            ICurrentTenantService tenantService,               // FIX: add to constructor
            IProductCategoryService categoryService,           // 068
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ILogger<EditModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _productService  = productService;
            _tenantService   = tenantService;
            _categoryService = categoryService;
        }

        /// <summary>
        /// 068. The workspace's active category names, in its own order.
        /// </summary>
        public List<string> CategoryNames { get; private set; } = new();

        /// <summary>069. What _PriceGrid.cshtml renders.</summary>
        public PriceGridVm PriceGrid { get; private set; } = new();
        /// <summary>070. What _BundleGrid.cshtml renders.</summary>
        public BundleGridVm BundleGrid { get; private set; } = new();

        /// <summary>
        /// 070. The bundle contents as they arrived on this POST, parsed
        /// once. Null before a POST, or when the field was absent or
        /// unreadable.
        /// </summary>
        private List<ProductBundleItemDto>? PostedBundle { get; set; }


        /// <summary>
        /// 069. The prices as they arrived on this POST, parsed once.
        /// Null before a POST, or when the field was absent or unreadable.
        /// </summary>
        private List<ProductPriceDto>? PostedPrices { get; set; }

        [BindProperty(SupportsGet = true)]
        public Guid Id { get; set; }

        [BindProperty]
        public UpdateProductDto Input { get; set; } = new();

        public string? ErrorMessage { get; set; }
        public Guid ProductId { get; set; }

        // FIX: expose tenant currency for the read-only display in the form
        public string TenantCurrencyCode   { get; private set; } = "";
        public string TenantCurrencySymbol { get; private set; } = "";
        public string TenantCurrencyName   { get; private set; } = "US Dollar";

        /// <summary>
        /// 068. Renamed and made async because it now loads the category
        /// list as well. A page model is a FRESH instance on every POST,
        /// so anything the view needs that the form does not carry has to
        /// be refilled here — the trap the currency and the tax-code
        /// labels were already in.
        /// </summary>
        private async Task LoadContextAsync()
        {
            LoadTenantCurrency();
            CategoryNames = await LoadCategoryNamesAsync();

            // 069. THE PRICES THE PERSON TYPED on this POST, not the
            // product's saved ones and not an empty grid. Rebuilding it
            // empty would show them a blank grid after a validation
            // failure and then delete their typing on the resubmit.
            PriceGrid = BuildPriceGrid(PostedPrices ?? new List<ProductPriceDto>());

            // 070. Same rule — what the person typed on this POST.
            BundleGrid = await BuildBundleGridAsync(
                CurrentUserService.GetCurrentTenantId(),
                Id,
                PostedBundle ?? new List<ProductBundleItemDto>());
        }

        private async Task<BundleGridVm> BuildBundleGridAsync(
            Guid tenantId, Guid selfId, List<ProductBundleItemDto> items) => new()
        {
            FormId             = "productForm",
            SelfProductId      = selfId,
            Items              = items,
            Choosable          = await LoadChoosableAsync(tenantId, selfId),
            CurrencySymbol     = TenantCurrencySymbol,
            BundlePrice        = Input?.ListPrice ?? 0m,
            ItemsJsonFieldName = "bundleJson"
        };

        /// <summary>
        /// 070. Read the bundle grid's hidden field. NULL means "said
        /// nothing about contents" and the API leaves them alone; an
        /// empty list means "no components", which the API REFUSES for a
        /// bundle.
        ///
        /// A malformed payload parses to null rather than to empty, so a
        /// parse failure cannot empty a bundle.
        /// </summary>
        private List<ProductBundleItemDto>? ParseBundle(string? bundleJson)
        {
            if (bundleJson is null) return null;
            if (string.IsNullOrWhiteSpace(bundleJson)) return new List<ProductBundleItemDto>();

            try
            {
                return JsonSerializer.Deserialize<List<ProductBundleItemDto>>(
                           bundleJson,
                           new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                       ?? new List<ProductBundleItemDto>();
            }
            catch (JsonException ex)
            {
                Logger.LogWarning(ex, "Could not read the bundle grid; leaving contents unchanged.");
                return null;
            }
        }

        /// <summary>
        /// 070. Everything that may go IN a bundle: this workspace's
        /// products, minus this product itself and minus every other
        /// bundle — a bundle cannot contain a bundle, and offering one in
        /// the picker would be offering a mistake.
        ///
        /// Inactive products are KEPT. A business stops selling a part
        /// separately and keeps it inside the package; the grid marks it
        /// rather than hiding it.
        /// </summary>
        private async Task<List<ProductListItem>> LoadChoosableAsync(Guid tenantId, Guid selfId)
        {
            try
            {
                var paginated = await _productService.GetAllAsync(
                    tenantId: tenantId, pageNumber: 1, pageSize: 1000,
                    category: null, isActive: null, searchTerm: null);

                return (paginated?.Items ?? new List<ProductListItem>())
                    .Where(p => p.Id != selfId && !p.IsBundle)
                    .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load the products a bundle can contain");
                return new List<ProductListItem>();
            }
        }


        private PriceGridVm BuildPriceGrid(List<ProductPriceDto> prices) => new()
        {
            FormId              = "productForm",
            HomeCurrencyCode    = TenantCurrencyCode,
            Prices              = prices,
            PricesJsonFieldName = "pricesJson"
        };

        /// <summary>
        /// 069. Read the grid's hidden field. NULL means "said nothing,
        /// leave the prices alone"; an empty list means "no other
        /// currencies" and does remove them.
        ///
        /// A malformed payload is read as "said nothing" rather than as
        /// "remove everything" — on this page the destructive reading of
        /// a parse failure would quietly wipe a product's prices.
        /// </summary>
        private List<ProductPriceDto>? ParsePrices(string? pricesJson)
        {
            if (pricesJson is null) return null;
            if (string.IsNullOrWhiteSpace(pricesJson)) return new List<ProductPriceDto>();

            try
            {
                return JsonSerializer.Deserialize<List<ProductPriceDto>>(
                           pricesJson,
                           new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                       ?? new List<ProductPriceDto>();
            }
            catch (JsonException ex)
            {
                Logger.LogWarning(ex, "Could not read the product price grid; leaving prices unchanged.");
                return null;
            }
        }

        /// <summary>
        /// 068. Active names, plus THE PRODUCT'S OWN CATEGORY even if it
        /// is no longer one — otherwise opening a product filed under a
        /// removed category would show a dropdown with nothing selected,
        /// and saving without touching it would silently clear the field.
        /// The extra entry says what it is.
        /// </summary>
        private async Task<List<string>> LoadCategoryNamesAsync()
        {
            var list = await _categoryService.GetAllAsync();

            var names = list.Categories
                .Where(c => c.IsActive)
                .OrderBy(c => c.SortOrder)
                .Select(c => c.Name)
                .ToList();

            var current = Input?.Category;
            if (!string.IsNullOrWhiteSpace(current) &&
                !names.Contains(current, StringComparer.OrdinalIgnoreCase))
            {
                names.Insert(0, current);
            }

            return names;
        }

        private void LoadTenantCurrency()
        {
            TenantCurrencyCode   = _tenantService.GetCurrencyCode();
            TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
            TenantCurrencyName   = CurrencyConfiguration.GetCurrencyName(TenantCurrencyCode);

            // 051: the tax code's wording follows the tenant.
            TaxCodeLabel = TaxCodes.LabelFor(TenantCurrencyCode);
            TaxCodeHint  = TaxCodes.HintFor(TenantCurrencyCode);
        }

        public async Task<IActionResult> OnGetAsync(Guid id)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                ProductId = id;
                LoadTenantCurrency();

                var tenantId = CurrentUserService.GetCurrentTenantId();
                var product  = await _productService.GetByIdAsync(tenantId, id);

                Input = new UpdateProductDto
                {
                    Name        = product.Name,
                    Description = product.Description,
                    Sku         = product.Sku,
                    Category    = product.Category,
                    Type        = product.Type,
                    UnitOfMeasure = product.UnitOfMeasure,   // 051
                    ListPrice   = product.ListPrice,
                    Currency    = TenantCurrencyCode,   // FIX: always use tenant currency
                    TaxRate     = product.TaxRate,
                    TaxCode     = product.TaxCode,           // 051
                    IsActive    = product.IsActive
                };

                // 068. AFTER Input, not before: the list includes this
                // product's own category when it is no longer one of the
                // workspace's, and that check reads Input.Category.
                CategoryNames = await LoadCategoryNamesAsync();

                // 069. Seeded from the product's own rows, so the grid
                // opens showing what is actually stored.
                PriceGrid = BuildPriceGrid(product.Prices ?? new List<ProductPriceDto>());

                // 070. Seeded from the bundle's own contents, and the
                // picker excludes this product so it cannot contain
                // itself.
                BundleGrid = await BuildBundleGridAsync(
                    tenantId, id, product.BundleItems ?? new List<ProductBundleItemDto>());

                return Page();
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Product not found.";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading product {ProductId}", id);
                TempData["ErrorMessage"] = "Failed to load product.";
                return RedirectToPage("./Index");
            }
        }

        /// <summary>
        /// 069: pricesJson is the grid's hidden field.
        /// </summary>
        public async Task<IActionResult> OnPostAsync(Guid id, string? pricesJson, string? bundleJson)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            ProductId = id;

            // 069. Parsed BEFORE LoadContextAsync, which rebuilds the
            // grid from it.
            PostedPrices = ParsePrices(pricesJson);
            PostedBundle = ParseBundle(bundleJson);   // 070

            // 068. The categories as well as the currency — a POST builds a
            // fresh page model, so a redisplay after a validation failure
            // would otherwise show an empty Category dropdown.
            await LoadContextAsync();

            if (!ModelState.IsValid)
                return Page();

            try
            {
                var tenantId = CurrentUserService.GetCurrentTenantId();
                // FIX: always enforce tenant currency regardless of what came in the form
                Input.Currency = TenantCurrencyCode;

                // 069. The complete list of other-currency prices, or
                // NULL when the field could not be read — which leaves
                // the product's existing prices untouched rather than
                // deleting them.
                Input.Prices = PostedPrices;

                // 070. The contents. The API refuses an empty bundle and
                // a bundle inside a bundle, and clears the contents
                // outright when the kind is not Bundle — so sending them
                // unconditionally is safe and keeps this page from having
                // to know the rules.
                Input.BundleItems = PostedBundle;

                await _productService.UpdateAsync(tenantId, id, Input);

                TempData["SuccessMessage"] = "Product updated successfully!";
                return RedirectToPage("./Index");
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Product not found.";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error updating product {ProductId}", id);
                ModelState.AddModelError(string.Empty, "Failed to update product. Please try again.");
                return Page();
            }
        }

        /// <summary>
        /// 068. The workspace's list (plus this product's own category if
        /// it has been removed since), not the eight C# constants. Loaded
        /// in OnGetAsync and refilled by LoadContextAsync on a POST — a
        /// page model cannot await inside a property the view binds to.
        /// </summary>
        public SelectList GetCategoryOptions()
            => new SelectList(CategoryNames, Input.Category);

        /// <summary>
        /// 051: from ProductKinds, not a local array. This method and its twin
        /// on the other page each had their own copy of
        /// { "Product", "Service", "Subscription" } — two places to get wrong,
        /// and nothing checking either.
        /// </summary>
        public SelectList GetTypeOptions()
            => new SelectList(ProductKinds.All, Input.Type);

        /// <summary>
        /// 051. Grouped, because twelve flat options is a scroll and
        /// "Time" / "Area and length" is how someone thinks about it.
        /// </summary>
        public SelectList GetUnitOptions()
            => new SelectList(
                UnitsOfMeasure.All.Select(u => new { u.Code, u.Label, u.Group }),
                "Code", "Label", Input.UnitOfMeasure, "Group");

        /// <summary>
        /// 051. One line saying what the selected kind means. Rendered
        /// server-side rather than swapped by a script, so the three strings
        /// live only in ProductKinds — putting a copy of them in JavaScript
        /// would recreate the duplication this round just removed.
        /// </summary>
        public string KindHint => ProductKinds.Hint(Input.Type);

        /// <summary>051. What to call the tax code field for this tenant.</summary>
        public string TaxCodeLabel { get; private set; } = "Tax classification code";
        public string TaxCodeHint  { get; private set; } = string.Empty;
        // FIX: GetCurrencyOptions() removed — currency is tenant-level, not per-product
    }
}
