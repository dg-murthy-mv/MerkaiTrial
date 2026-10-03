// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Products/Create.cshtml.cs
//
// COMPLETE FILE — 068.
//
// ONE CHANGE: the Category dropdown was built from
// ProductCategoriesConfiguration.GetCategoryNames(), a static list of
// eight C# constants. It is the workspace's own category list now.
//
// The list is loaded on GET and reloaded on every POST failure path,
// through ReloadContextAsync — a page model is a FRESH instance on every
// POST, so anything the view needs that the form does not carry has to
// be refilled by hand. That is the same trap the 051 note below
// describes for the currency and the tax-code labels, and the categories
// would have been the third thing to forget.
//
// AN EMPTY LIST IS POSSIBLE AND IS HANDLED IN THE VIEW: a workspace
// whose categories have all been removed, or an API that could not be
// reached. The form says so and points at Settings, rather than showing
// a dropdown with nothing in it and failing on save.
//
// ── 069 ─────────────────────────────────────────────────────────────
//
// The form now also carries PRICES IN OTHER CURRENCIES, through
// _PriceGrid.cshtml. They are on CREATE as well as Edit on purpose: a
// product's non-home prices are part of deciding what the product is,
// and making somebody save it first and come back would mean the first
// quote raised in USD against a brand-new product has no price — which
// is the exact hole this round closes.
//
// The grid posts one hidden field of JSON (pricesJson) rather than
// indexed form fields. _PriceGrid's own header says why: removing a
// middle row gaps the indexes and the model binder silently drops
// everything after the gap.
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
    public class CreateModel : AuthorizedPageModel
    {
        private readonly IProductService _productService;
        private readonly ICurrentTenantService _tenantService;   // FIX: inject tenant service
        private readonly IProductCategoryService _categoryService;   // 068

        protected override string ModuleName => Modules.Products;

        public CreateModel(
            IProductService productService,
            ICurrentTenantService tenantService,               // FIX: add to constructor
            IProductCategoryService categoryService,           // 068
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ILogger<CreateModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _productService  = productService;
            _tenantService   = tenantService;
            _categoryService = categoryService;
        }

        /// <summary>
        /// 068. The workspace's active category names, in its own display
        /// order. EMPTY is a real state the view handles — see the header.
        /// </summary>
        public List<string> CategoryNames { get; private set; } = new();

        /// <summary>
        /// 069. What _PriceGrid.cshtml renders. Rebuilt on every POST
        /// failure path too — a page model is a fresh instance on each
        /// POST, so the grid would otherwise come back empty and a
        /// resubmit would delete the prices the person had typed.
        /// </summary>
        public PriceGridVm PriceGrid { get; private set; } = new();
        /// <summary>070. What _BundleGrid.cshtml renders.</summary>
        public BundleGridVm BundleGrid { get; private set; } = new();

        /// <summary>
        /// 070. The bundle contents as they arrived on this POST, parsed
        /// once. Null before a POST, or when the field was absent or
        /// unreadable.
        /// </summary>
        private List<ProductBundleItemDto>? PostedBundle { get; set; }


        [BindProperty]
        public CreateProductDto Input { get; set; } = new();

        public string? ErrorMessage { get; set; }

        // FIX: expose tenant currency for the read-only display in the form
        public string TenantCurrencyCode   { get; private set; } = "USD";
        public string TenantCurrencySymbol { get; private set; } = "$";
        public string TenantCurrencyName   { get; private set; } = "US Dollar";

        public async Task<IActionResult> OnGetAsync()
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Create);
            if (permissionCheck != null) return permissionCheck;

            // FIX: currency comes from tenant — not hardcoded "USD"
            TenantCurrencyCode   = _tenantService.GetCurrencyCode();
            TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
            TenantCurrencyName   = CurrencyConfiguration.GetCurrencyName(TenantCurrencyCode);

            // 051: the tax code's wording follows the tenant.
            TaxCodeLabel = TaxCodes.LabelFor(TenantCurrencyCode);
            TaxCodeHint  = TaxCodes.HintFor(TenantCurrencyCode);

            // 068: the workspace's own categories.
            CategoryNames = await LoadCategoryNamesAsync();

            // 069: an empty grid — a new product has no prices yet.
            PriceGrid = BuildPriceGrid(new List<ProductPriceDto>());

            // 070: likewise empty, and the picker of what can go in it.
            BundleGrid = await BuildBundleGridAsync(
                CurrentUserService.GetCurrentTenantId(),
                Guid.Empty,
                new List<ProductBundleItemDto>());

            Input = new CreateProductDto
            {
                TenantId = CurrentUserService.GetCurrentTenantId(),
                IsActive = true,
                Currency = TenantCurrencyCode,   // FIX: was hardcoded "USD"
                TaxRate  = 0,

                // 051. "unit" prints as nothing beside a quantity, which is
                // the right default: most catalogues sell countable things
                // and "3 units" reads worse than "3".
                Type          = ProductKinds.Product,
                UnitOfMeasure = UnitsOfMeasure.Unit
            };

            return Page();
        }

        /// <summary>
        /// 069: pricesJson is the grid's hidden field — see
        /// _PriceGrid.cshtml for why it is one JSON field and not
        /// indexed inputs.
        /// </summary>
        public async Task<IActionResult> OnPostAsync(string? pricesJson, string? bundleJson)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Create);
            if (permissionCheck != null) return permissionCheck;

            // 069. Parsed BEFORE the ModelState check, so a validation
            // failure can rebuild the grid from what the person typed
            // instead of showing them an empty one.
            PostedPrices = ParsePrices(pricesJson);
            PostedBundle = ParseBundle(bundleJson);   // 070

            if (!ModelState.IsValid)
            {
                await ReloadContextAsync();   // 051, 068, 069
                return Page();
            }

            try
            {
                Input.TenantId = CurrentUserService.GetCurrentTenantId();
                // FIX: always enforce tenant currency regardless of what came in the form
                Input.Currency = _tenantService.GetCurrencyCode();

                // 069. The other-currency prices. The API refuses a row
                // in the workspace's own currency and normalises the
                // codes, so what comes back may not be what went in.
                Input.Prices = PostedPrices;

                // 070. The contents. The API refuses an empty bundle and
                // refuses a bundle inside a bundle, and it clears the
                // contents outright when the kind is not Bundle — so
                // sending them unconditionally is safe and keeps this
                // page from having to know the rules.
                Input.BundleItems = PostedBundle;

                await _productService.CreateAsync(Input);

                TempData["SuccessMessage"] = "Product created successfully!";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error creating product");
                await ReloadContextAsync();   // 051, 068
                ModelState.AddModelError(string.Empty, "Failed to create product. Please try again.");
                return Page();
            }
        }

        /// <summary>
        /// 068. The workspace's list, not the eight C# constants. Loaded
        /// in OnGetAsync and refilled by ReloadContextAsync, so this stays
        /// a pure selector over what is already on the model — a page
        /// model cannot await inside a property the view binds to.
        /// </summary>
        public SelectList GetCategoryOptions()
            => new SelectList(CategoryNames, Input.Category);

        /// <summary>
        /// 051. Everything the view needs that a POST does not carry. A page
        /// model is a FRESH instance on every POST, so these had to be refilled
        /// by hand in two separate places before — and the tax code labels
        /// would have been a third.
        /// </summary>
        private async Task ReloadContextAsync()
        {
            TenantCurrencyCode   = _tenantService.GetCurrencyCode();
            TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
            TenantCurrencyName   = CurrencyConfiguration.GetCurrencyName(TenantCurrencyCode);
            TaxCodeLabel         = TaxCodes.LabelFor(TenantCurrencyCode);
            TaxCodeHint          = TaxCodes.HintFor(TenantCurrencyCode);

            // 068. Async now, which is why the method was renamed — a
            // ReloadTenantContext() that silently did not reload the
            // categories would leave an empty dropdown on exactly the
            // screen somebody is already annoyed with.
            CategoryNames = await LoadCategoryNamesAsync();

            // 069. THE PRICES THE PERSON TYPED, not an empty grid.
            // Rebuilding it empty would show them a blank grid after a
            // validation failure and then delete their typing on the
            // resubmit — the posted JSON is the only record of it at this
            // point, so it is what the grid has to be rebuilt from.
            PriceGrid = BuildPriceGrid(PostedPrices ?? new List<ProductPriceDto>());

            // 070. Same rule — what the person typed, not an empty grid.
            BundleGrid = await BuildBundleGridAsync(
                CurrentUserService.GetCurrentTenantId(),
                Guid.Empty,
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
        /// 070. Read the bundle grid's hidden field.
        ///
        /// NULL means "said nothing about contents" and the API leaves
        /// them alone; an empty list means "no components", which the API
        /// REFUSES for a bundle ("A bundle has to contain something").
        ///
        /// A malformed payload parses to null rather than to empty, so a
        /// parse failure cannot empty a bundle — the same rule the price
        /// grid follows, and the same reason: the destructive reading of
        /// a parse failure is the wrong one.
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
        /// active products, minus this product itself and minus every
        /// other bundle.
        ///
        /// NO BUNDLES, because a bundle cannot contain a bundle — the
        /// API refuses it, and offering it in the picker would be
        /// offering a mistake. ProductBundles.ValidateAsync has the
        /// reasoning: one level keeps the breakdown finite, the rollup
        /// one query deep, and a quote line's description from nesting.
        ///
        /// Inactive products are KEPT, deliberately. A business stops
        /// selling a part separately and keeps it inside the package; the
        /// grid marks it rather than hiding it.
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


        /// <summary>
        /// 069. The prices as they arrived on this POST, parsed once.
        /// Null before a POST, or when the field was absent.
        /// </summary>
        private List<ProductPriceDto>? PostedPrices { get; set; }

        private PriceGridVm BuildPriceGrid(List<ProductPriceDto> prices) => new()
        {
            FormId              = "productForm",
            HomeCurrencyCode    = TenantCurrencyCode,
            Prices              = prices,
            PricesJsonFieldName = "pricesJson"
        };

        /// <summary>
        /// 069. Read the grid's hidden field.
        ///
        /// Returns NULL when the field is absent entirely, which the API
        /// reads as "the caller said nothing about prices, leave them
        /// alone". Returns an EMPTY LIST for "[]", which means "no other
        /// currencies" and does remove them. The grid always posts one or
        /// the other, so the distinction only matters for an API client
        /// — but the parse has to preserve it or the form could never
        /// clear a price.
        ///
        /// A malformed payload is treated as "said nothing" rather than
        /// as "remove everything": the destructive reading of a parse
        /// failure is the wrong one.
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
        /// 068. Active category names. Never throws: the service already
        /// degrades a failed read to an empty list, and this page is more
        /// useful with every other field intact than it is replaced by an
        /// error screen.
        /// </summary>
        private async Task<List<string>> LoadCategoryNamesAsync()
        {
            var list = await _categoryService.GetAllAsync();
            return list.Categories
                .Where(c => c.IsActive)
                .OrderBy(c => c.SortOrder)
                .Select(c => c.Name)
                .ToList();
        }

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
