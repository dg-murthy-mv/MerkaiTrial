// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Services/Products/ProductService.cs
//
// COMPLETE FILE — 080.
//
// 1. GetListAsync is NEW, for the Products list page. It takes the
//    custom field filters (repeated ?cf=, each escaped as a whole) and it
//    THROWS on failure, so the page can say "the list could not be
//    loaded" instead of showing "No products yet" to a workspace that has
//    hundreds.
//
//    GetAllAsync is UNCHANGED in behaviour: it still returns an empty
//    result on failure. The bundle picker and the quote pages call it and
//    were written expecting that, so changing it under them would turn a
//    quietly empty dropdown into an error page. Both build the URL with
//    the same private BuildListUrl, so they cannot drift.
//
// 2. The category filter is escaped. "Hardware & Tools" used to cut the
//    query string at the "&" and filter by "Hardware ".
//
// 3. Create, Update and Delete let an InvalidOperationException through
//    WITHOUT logging it as an error: it is the API's 400 sentence (an
//    unknown category, a bad price row, a custom field value, a product
//    that is part of a bundle) and the page shows it. Anything else is
//    still logged and rethrown.
//
// 4. A 404 from GetById / Update / Delete becomes KeyNotFoundException,
//    which the pages already turn into "Product not found."
//
// 5. The ?tenantId= on every URL is kept for older API builds; the API
//    now takes the workspace from the token and ignores it.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.Configuration;   // 080 — CustomFieldFilterCodec
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.Products
{
    public interface IProductService
    {
        /// <summary>
        /// 069. <paramref name="currency"/> is new and TRAILING, so every
        /// existing call still compiles and still means "the workspace's
        /// own currency".
        ///
        /// Pass the QUOTE's currency when building a quote: the returned
        /// ListPrice is then a price in that currency, and
        /// HasPriceInCurrency is false on the products that have none.
        /// See ProductPricing.cs.
        ///
        /// 080: returns an EMPTY result on failure, as it always has — the
        /// catalogue callers (bundle picker, quote pages) rely on it. The
        /// Products list uses GetListAsync, which throws.
        /// </summary>
        Task<PaginatedResult<ProductListItem>> GetAllAsync(
             Guid tenantId,
             int pageNumber = 1,
             int pageSize = 25,
             string? category = null,
             bool? isActive = null,
             string? searchTerm = null,
             string? currency = null);

        /// <summary>
        /// 080. The Products list page's read: same filters plus custom
        /// field filters, and it THROWS when the list cannot be loaded.
        /// </summary>
        Task<PaginatedResult<ProductListItem>> GetListAsync(
             Guid tenantId,
             int pageNumber,
             int pageSize,
             string? category,
             bool? isActive,
             string? searchTerm,
             IReadOnlyList<CustomFieldFilter>? customFilters);

        Task<ProductStatsDto> GetStatsAsync(Guid tenantId);
        Task<ProductDto> GetByIdAsync(Guid tenantId, Guid productId);
        Task<ProductDto> CreateAsync(CreateProductDto dto);
        Task UpdateAsync(Guid tenantId, Guid productId, UpdateProductDto dto);
        Task DeleteAsync(Guid tenantId, Guid productId);
        Task<bool> ToggleActiveAsync(Guid tenantId, Guid productId);
        Task<List<string>> GetCategoriesAsync(Guid tenantId);
    }

    public class ProductService : IProductService
    {
        private readonly IApiService _apiService;
        private readonly ILogger<ProductService> _logger;

        public ProductService(
            IApiService apiService,
            ILogger<ProductService> logger)
        {
            _apiService = apiService;
            _logger = logger;
        }

        private static bool IsNotFound(HttpRequestException ex)
            => ex.Message.Contains("404") || ex.Message.Contains("NotFound");

        /// <summary>080. The one place the list URL is built.</summary>
        private static string BuildListUrl(
            Guid tenantId, int pageNumber, int pageSize, string? category, bool? isActive,
            string? searchTerm, string? currency, IReadOnlyList<CustomFieldFilter>? customFilters)
        {
            var url = $"api/products?tenantId={tenantId}&pageNumber={pageNumber}&pageSize={pageSize}";

            // 080 — escaped; "&" in a category name used to cut the URL.
            if (!string.IsNullOrWhiteSpace(category))
                url += $"&category={Uri.EscapeDataString(category.Trim())}";

            if (isActive.HasValue)
                url += $"&isActive={(isActive.Value ? "true" : "false")}";

            if (!string.IsNullOrWhiteSpace(searchTerm))
                url += $"&search={Uri.EscapeDataString(searchTerm.Trim())}";

            // 069. Escaped like the search term. This value can arrive from
            // Deals.Currency — an nvarchar an import can put anything into.
            if (!string.IsNullOrEmpty(currency))
                url += $"&currency={Uri.EscapeDataString(currency)}";

            // 080 — each filter escaped as a whole; the value part is
            // free text and may contain anything.
            foreach (var f in customFilters ?? Array.Empty<CustomFieldFilter>())
                url += $"&cf={Uri.EscapeDataString(CustomFieldFilterCodec.Encode(f))}";

            return url;
        }

        public async Task<PaginatedResult<ProductListItem>> GetAllAsync(
           Guid tenantId,
           int pageNumber = 1,
           int pageSize = 25,
           string? category = null,
           bool? isActive = null,
           string? searchTerm = null,
           string? currency = null)   // 069
        {
            try
            {
                var url = BuildListUrl(tenantId, pageNumber, pageSize, category, isActive,
                                       searchTerm, currency, null);
                return await _apiService.GetAsync<PaginatedResult<ProductListItem>>(url);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get products for tenant {TenantId}", tenantId);
                return new PaginatedResult<ProductListItem>();
            }
        }

        public async Task<PaginatedResult<ProductListItem>> GetListAsync(
            Guid tenantId,
            int pageNumber,
            int pageSize,
            string? category,
            bool? isActive,
            string? searchTerm,
            IReadOnlyList<CustomFieldFilter>? customFilters)
        {
            try
            {
                var url = BuildListUrl(tenantId, pageNumber, pageSize, category, isActive,
                                       searchTerm, null, customFilters);
                return await _apiService.GetAsync<PaginatedResult<ProductListItem>>(url)
                       ?? new PaginatedResult<ProductListItem>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load the products list for tenant {TenantId}", tenantId);
                throw;
            }
        }

        public async Task<bool> ToggleActiveAsync(Guid tenantId, Guid productId)
        {
            try
            {
                var url = $"api/products/{productId}/toggle-active?tenantId={tenantId}";
                await _apiService.PatchVoidAsync(url, new { });
                // PatchVoidAsync returns no result, so read the product back
                // to report its new state.
                var product = await GetByIdAsync(tenantId, productId);
                return product.IsActive;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to toggle product {ProductId}", productId);
                throw;
            }
        }

        public async Task<ProductStatsDto> GetStatsAsync(Guid tenantId)
        {
            try
            {
                return await _apiService.GetAsync<ProductStatsDto>($"api/products/stats?tenantId={tenantId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting product statistics");
                throw;
            }
        }

        public async Task<ProductDto> GetByIdAsync(Guid tenantId, Guid productId)
        {
            try
            {
                var url = $"api/products/{productId}?tenantId={tenantId}";
                return await _apiService.GetAsync<ProductDto>(url);
            }
            catch (HttpRequestException ex) when (IsNotFound(ex))
            {
                _logger.LogWarning("Product {ProductId} not found", productId);
                throw new KeyNotFoundException($"Product {productId} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get product {ProductId}", productId);
                throw;
            }
        }

        public async Task<ProductDto> CreateAsync(CreateProductDto dto)
        {
            try
            {
                return await _apiService.PostAsync<ProductDto>("api/products", dto);
            }
            catch (InvalidOperationException)
            {
                throw;   // 080 — the API's sentence for the person; see note 3
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create product");
                throw;
            }
        }

        public async Task UpdateAsync(Guid tenantId, Guid productId, UpdateProductDto dto)
        {
            try
            {
                var url = $"api/products/{productId}?tenantId={tenantId}";
                await _apiService.PutAsync<ProductDto>(url, dto);
            }
            catch (HttpRequestException ex) when (IsNotFound(ex))
            {
                throw new KeyNotFoundException($"Product {productId} not found");
            }
            catch (InvalidOperationException)
            {
                throw;   // 080 — the API's sentence for the person; see note 3
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update product {ProductId}", productId);
                throw;
            }
        }

        public async Task DeleteAsync(Guid tenantId, Guid productId)
        {
            try
            {
                var url = $"api/products/{productId}?tenantId={tenantId}";
                await _apiService.DeleteAsync(url);
            }
            catch (HttpRequestException ex) when (IsNotFound(ex))
            {
                throw new KeyNotFoundException($"Product {productId} not found");
            }
            catch (InvalidOperationException)
            {
                throw;   // 080 — the API's refusal, e.g. "part of a bundle"; see note 3
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete product {ProductId}", productId);
                throw;
            }
        }

        public async Task<List<string>> GetCategoriesAsync(Guid tenantId)
        {
            try
            {
                var url = $"api/products/categories?tenantId={tenantId}";
                var categories = await _apiService.GetAsync<List<string>>(url);
                return categories ?? new List<string>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get categories for tenant {TenantId}", tenantId);
                return new List<string>();
            }
        }
    }
}
