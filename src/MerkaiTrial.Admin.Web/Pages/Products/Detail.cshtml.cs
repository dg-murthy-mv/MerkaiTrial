using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Services.Products;
using MerkaiTrial.Application.Authorization;
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
        protected override string ModuleName => Modules.Products;

        public DetailModel(
            IProductService productService,
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            ILogger<DetailModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _productService = productService;
            _tenantService = tenantService;
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

        public string GetCategoryIcon()
        {
            return ProductCategoriesConfiguration.GetCategoryIcon(Product.Category ?? string.Empty);
        }

        public string GetCategoryColor()
        {
            return ProductCategoriesConfiguration.GetCategoryColor(Product.Category ?? string.Empty);
        }

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
