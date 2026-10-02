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
    public class EditModel : AuthorizedPageModel
    {
        private readonly IProductService _productService;
        private readonly ICurrentTenantService _tenantService;   // FIX: inject tenant service

        protected override string ModuleName => Modules.Products;

        public EditModel(
            IProductService productService,
            ICurrentTenantService tenantService,               // FIX: add to constructor
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ILogger<EditModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _productService = productService;
            _tenantService  = tenantService;
        }

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

        public async Task<IActionResult> OnPostAsync(Guid id)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            ProductId = id;
            LoadTenantCurrency();

            if (!ModelState.IsValid)
                return Page();

            try
            {
                var tenantId = CurrentUserService.GetCurrentTenantId();
                // FIX: always enforce tenant currency regardless of what came in the form
                Input.Currency = TenantCurrencyCode;

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

        public SelectList GetCategoryOptions()
        {
            return new SelectList(
                ProductCategoriesConfiguration.GetCategoryNames(),
                Input.Category);
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
