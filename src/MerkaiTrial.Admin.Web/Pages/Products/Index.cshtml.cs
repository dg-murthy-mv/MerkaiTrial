// UPDATE: Pages/Products/Index.cshtml.cs - Policy-Based

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

        protected override string ModuleName => Modules.Products;

        public IndexModel(
            IProductService productService,
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            ILogger<IndexModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _productService = productService;
            _tenantService = tenantService;

        }

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



        public SelectList GetCategoryFilterOptions()
        {
            var items = new List<SelectListItem>
            {
                new SelectListItem { Value = "", Text = "All Categories" }
            };
            items.AddRange(ProductCategoriesConfiguration.GetCategoryNames()
                .Select(c => new SelectListItem { Value = c, Text = c }));
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
        public string GetCategoryIcon(string? category)
        {
            if (string.IsNullOrEmpty(category)) return "bi-box";
            return ProductCategoriesConfiguration.GetCategoryIcon(category);
        }

        public string GetCategoryColor(string? category)
        {
            if (string.IsNullOrEmpty(category)) return "#6b7280";
            return ProductCategoriesConfiguration.GetCategoryColor(category);
        }

        public string GetCurrencySymbol(string currency)
        {
            return CurrencyConfiguration.GetCurrencySymbol(currency);
        }
    }
}
