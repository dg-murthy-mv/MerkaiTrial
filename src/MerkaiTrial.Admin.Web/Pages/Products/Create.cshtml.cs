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

            // 051: the tax code's wording follows the tenant.
            TaxCodeLabel = TaxCodes.LabelFor(TenantCurrencyCode);
            TaxCodeHint  = TaxCodes.HintFor(TenantCurrencyCode);

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

        public async Task<IActionResult> OnPostAsync()
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Create);
            if (permissionCheck != null) return permissionCheck;

            if (!ModelState.IsValid)
            {
                ReloadTenantContext();   // 051
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
                ReloadTenantContext();   // 051
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

        /// <summary>
        /// 051. Everything the view needs that a POST does not carry. A page
        /// model is a FRESH instance on every POST, so these had to be refilled
        /// by hand in two separate places before — and the tax code labels
        /// would have been a third.
        /// </summary>
        private void ReloadTenantContext()
        {
            TenantCurrencyCode   = _tenantService.GetCurrencyCode();
            TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
            TenantCurrencyName   = CurrencyConfiguration.GetCurrencyName(TenantCurrencyCode);
            TaxCodeLabel         = TaxCodes.LabelFor(TenantCurrencyCode);
            TaxCodeHint          = TaxCodes.HintFor(TenantCurrencyCode);
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
