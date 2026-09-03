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
    public class CreateModel : AuthorizedPageModel
    {
        private readonly IProductService _productService;
        private readonly ICurrentTenantService _tenantService;   // FIX: inject tenant service

        protected override string ModuleName => Modules.Products;

        public CreateModel(
            IProductService productService,
            ICurrentTenantService tenantService,               // FIX: add to constructor
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ILogger<CreateModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _productService = productService;
            _tenantService  = tenantService;
        }

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

            Input = new CreateProductDto
            {
                TenantId = CurrentUserService.GetCurrentTenantId(),
                IsActive = true,
                Currency = TenantCurrencyCode,   // FIX: was hardcoded "USD"
                TaxRate  = 0
            };

            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Create);
            if (permissionCheck != null) return permissionCheck;

            if (!ModelState.IsValid)
            {
                TenantCurrencyCode   = _tenantService.GetCurrencyCode();
                TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
                TenantCurrencyName   = CurrencyConfiguration.GetCurrencyName(TenantCurrencyCode);
                return Page();
            }

            try
            {
                Input.TenantId = CurrentUserService.GetCurrentTenantId();
                // FIX: always enforce tenant currency regardless of what came in the form
                Input.Currency = _tenantService.GetCurrencyCode();

                await _productService.CreateAsync(Input);

                TempData["SuccessMessage"] = "Product created successfully!";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error creating product");
                TenantCurrencyCode   = _tenantService.GetCurrencyCode();
                TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
                TenantCurrencyName   = CurrencyConfiguration.GetCurrencyName(TenantCurrencyCode);
                ModelState.AddModelError(string.Empty, "Failed to create product. Please try again.");
                return Page();
            }
        }

        public SelectList GetCategoryOptions()
        {
            return new SelectList(
                ProductCategoriesConfiguration.GetCategoryNames(),
                Input.Category);
        }

        public SelectList GetTypeOptions()
        {
            var types = new[] { "Product", "Service", "Subscription" };
            return new SelectList(types);
        }
        // FIX: GetCurrencyOptions() removed — currency is tenant-level, not per-product
    }
}
