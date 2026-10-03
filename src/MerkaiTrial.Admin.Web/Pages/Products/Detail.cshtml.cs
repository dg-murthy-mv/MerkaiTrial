// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Products/Detail.cshtml.cs
//
// COMPLETE FILE — 068.
//
// The icon and colour at the top of the page came from
// ProductCategoriesConfiguration, a static list of eight C# constants.
// They come from the workspace's own category list now, through
// ProductCategoryLookup — which falls back to the Merkai default of
// that name and then to a grey box, so a product filed under a category
// somebody removed still opens.
//
// IsCategoryOrphaned goes with it. A grey box on its own reads as a
// styling bug; the badge beside it now says what actually happened.
//
// ── 069 ─────────────────────────────────────────────────────────────
//
// The page also shows PRICES IN OTHER CURRENCIES, when there are any.
// They come with the product — GetProductByIdHandler reads them — so
// there is no second call.
//
// FormatInCurrency is deliberately NOT FormatCurrency. The base helper
// formats in the WORKSPACE's currency, which is right for the main
// price and wrong for every row in the other-currency table: it would
// print a USD figure with a rupee sign in front of it, which is the
// exact confusion this round exists to remove.
//
// ── 070 ─────────────────────────────────────────────────────────────
//
// A bundle also shows WHAT IT CONTAINS, with the components' own prices
// and the total beside them — this page is for the person who sells the
// package, not for the customer. The breakdown that goes onto a QUOTE
// carries names and quantities only; printing component prices there
// would invite the customer to add them up and find a different number
// from the one they are being charged.
//
// The components' prices are formatted with FormatCurrency, not
// FormatInCurrency: ComponentListPrice is Product.ListPrice on each
// component, which is a HOME-currency figure by definition.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Services.Products;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Products;   // 070 — ProductBundles
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.Admin.Web.Pages.Products
{
    public class DetailModel : AuthorizedPageModel
    {
        private readonly IProductService _productService;
        private readonly ICurrentTenantService _tenantService;
        private readonly IProductCategoryService _categoryService;   // 068
        protected override string ModuleName => Modules.Products;

        public DetailModel(
            IProductService productService,
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            IProductCategoryService categoryService,                 // 068
            ILogger<DetailModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _productService  = productService;
            _tenantService   = tenantService;
            _categoryService = categoryService;
        }

        /// <summary>
        /// 068. Name -> icon and colour. Never null, and answers for a
        /// category that no longer has a row.
        /// </summary>
        public ProductCategoryLookup CategoryLookup { get; private set; } =
            new ProductCategoryLookup(null);

        /// <summary>
        /// 069. Prices in currencies OTHER than the workspace's own, in
        /// CurrencyConfiguration's order. Empty for the single-currency
        /// workspace that most of them are, and the view shows nothing at
        /// all in that case.
        /// </summary>
        public List<ProductPriceDto> OtherPrices =>
            Product?.Prices ?? new List<ProductPriceDto>();

        /// <summary>070. True when this product is a bundle.</summary>
        public bool IsBundle => ProductKinds.IsBundle(Product?.Type);

        /// <summary>
        /// 070. What the bundle contains, in display order. Empty for
        /// every other kind, and the view renders nothing in that case.
        /// </summary>
        public List<ProductBundleItemDto> BundleItems =>
            Product?.BundleItems ?? new List<ProductBundleItemDto>();

        /// <summary>
        /// 070. The components added up at their own list prices.
        ///
        /// A FIGURE FOR THIS PAGE, not a price. The bundle's price is
        /// Product.ListPrice; if this were the price, editing one
        /// component would silently reprice the bundle and every quote
        /// drafted from it afterwards. ProductBundles.ComponentsTotal has
        /// the full argument.
        ///
        /// A home-currency figure, because ComponentListPrice is
        /// Product.ListPrice on each component. Never shown beside a 069
        /// other-currency row: adding up USD prices for some components
        /// and rupee prices for the rest would produce a number that
        /// means nothing.
        /// </summary>
        public decimal ComponentsTotal => ProductBundles.ComponentsTotal(BundleItems);

        /// <summary>
        /// 070. How far below the components' total the bundle sits, as a
        /// percentage. NULL when there is nothing to compare. NEGATIVE
        /// when the bundle costs more than its parts — unusual, legal,
        /// and said plainly rather than hidden, because it is usually a
        /// typo in the price.
        /// </summary>
        public decimal? BundleDiscountPercent =>
            ProductBundles.DiscountPercent(Product?.ListPrice ?? 0m, ComponentsTotal);

        /// <summary>
        /// 069. "US Dollar (USD)" — the same shape the Currency field
        /// above the table uses, so the two read as one page.
        /// </summary>
        public string CurrencyLabel(string? code)
            => $"{CurrencyConfiguration.GetCurrencyName(code)} ({CurrencyConfiguration.NormaliseCode(code) ?? code})";

        /// <summary>
        /// 069. An amount in a GIVEN currency, with that currency's own
        /// symbol and decimals.
        ///
        /// NOT FormatCurrency, which formats in the WORKSPACE's currency.
        /// Using it here would print a USD price behind a ₹ — the exact
        /// confusion this round exists to remove.
        ///
        /// The culture is left as the workspace's on purpose: digit
        /// GROUPING should follow the reader, not the money. Somebody in
        /// India reading a USD price still reads 1,00,000 more easily
        /// than 100,000, and the symbol is what says which currency it
        /// is.
        /// </summary>
        public string FormatInCurrency(decimal amount, string? code)
        {
            var symbol   = CurrencyConfiguration.GetCurrencySymbol(code);
            var decimals = CurrencyConfiguration.GetCurrencyDecimals(code);
            return symbol + amount.ToString("N" + decimals.ToString());
        }

        public ProductDto Product { get; set; } = null!;
        public string TenantCurrencySymbol { get; private set; } = "";
        public string TenantCurrencyCode { get; private set; } = "";
        public string TenantCurrencyName { get; private set; } = "";


        public async Task<IActionResult> OnGetAsync(Guid id)
        {
            TenantCurrencyCode = _tenantService.GetCurrencyCode();
            TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
            TenantCurrencyName = CurrencyConfiguration.GetCurrencyName(TenantCurrencyCode);

            // ✅ Check READ permission
            var permissionCheck = await ValidatePermissionAsync(Actions.Read);
            if (permissionCheck != null) return permissionCheck;

            // Initialize permissions for UI buttons
            await InitializePermissionsAsync();

            try
            {
                var tenantId = CurrentUserService.GetCurrentTenantId();
                Product = await _productService.GetByIdAsync(tenantId, id);

                // 068. After the product, because nothing above needs it
                // and a category read that fails should not stop the
                // product loading.
                CategoryLookup = await _categoryService.GetLookupAsync();

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

        public async Task<IActionResult> OnPostDeleteAsync(Guid id)
        {
            // ✅ Check DELETE permission
            var permissionCheck = await ValidatePermissionAsync(Actions.Delete);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var tenantId = CurrentUserService.GetCurrentTenantId();
                await _productService.DeleteAsync(tenantId, id);

                TempData["SuccessMessage"] = "Product deleted successfully!";
                return RedirectToPage("./Index");
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Product not found.";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error deleting product {ProductId}", id);
                TempData["ErrorMessage"] = "Failed to delete product.";
                return RedirectToPage("./Index");
            }
        }

        /// <summary>068 — the workspace's own icon, with fallbacks.</summary>
        public string GetCategoryIcon()
            => CategoryLookup.IconFor(Product?.Category);

        /// <summary>068 — see GetCategoryIcon.</summary>
        public string GetCategoryColor()
            => CategoryLookup.ColorFor(Product?.Category);

        /// <summary>
        /// 068. True when this product's category no longer has a row, so
        /// the page can say so rather than leaving a grey box to be read
        /// as a styling bug.
        /// </summary>
        public bool IsCategoryOrphaned()
            => CategoryLookup.IsOrphaned(Product?.Category);

        public string GetCurrencySymbol()
        {
            return CurrencyConfiguration.GetCurrencySymbol(Product.Currency);
        }

        // ── 051 ──────────────────────────────────────────────────────

        /// <summary>"Square metre", "Hour", "Unit / piece".</summary>
        public string UnitLabel => UnitsOfMeasure.LabelOf(Product?.UnitOfMeasure);

        /// <summary>
        /// The suffix after a price: "/ m²". EMPTY for a plain unit, because
        /// "₹450 / unit" reads worse than "₹450" and adds nothing.
        /// </summary>
        public string PricedPer
        {
            get
            {
                var s = UnitsOfMeasure.ShortOf(Product?.UnitOfMeasure);
                return string.IsNullOrEmpty(s) ? string.Empty : "/ " + s;
            }
        }

        /// <summary>What this tenant's tax authority calls the code.</summary>
        public string TaxCodeLabel => TaxCodes.LabelFor(TenantCurrencyCode);

        public string GetCurrencyName()
        {
            return CurrencyConfiguration.GetCurrencyName(Product.Currency);
        }
    }
}
