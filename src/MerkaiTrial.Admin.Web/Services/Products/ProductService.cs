using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.Products
{
    public interface IProductService
    {
        Task<PaginatedResult<ProductListItem>> GetAllAsync(
             Guid tenantId,
             int pageNumber = 1,
             int pageSize = 25,
             string? category = null,
             bool? isActive = null,
             string? searchTerm = null);

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

        public async Task<PaginatedResult<ProductListItem>> GetAllAsync(
           Guid tenantId,
           int pageNumber = 1,
           int pageSize = 25,
           string? category = null,
           bool? isActive = null,
           string? searchTerm = null)
        {
            try
            {
                var url = $"api/products?tenantId={tenantId}&pageNumber={pageNumber}&pageSize={pageSize}";

                if (!string.IsNullOrEmpty(category))
                    url += $"&category={category}";

                if (isActive.HasValue)
                    url += $"&isActive={isActive.Value}";

                if (!string.IsNullOrEmpty(searchTerm))
                    url += $"&search={Uri.EscapeDataString(searchTerm)}";

                return await _apiService.GetAsync<PaginatedResult<ProductListItem>>(url);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get products for tenant {TenantId}", tenantId);
                return new PaginatedResult<ProductListItem>();
            }
        }

        public async Task<bool> ToggleActiveAsync(Guid tenantId, Guid productId)
        {
            try
            {
                var url = $"api/products/{productId}/toggle-active?tenantId={tenantId}";
                await _apiService.PatchVoidAsync(url, new { });
                // Since PatchVoidAsync returns no result, you need to fetch the updated product to check its status
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
