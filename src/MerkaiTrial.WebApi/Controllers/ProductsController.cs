using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Products;
using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
    [Route("api/products")]
    [Authorize]
    public class ProductsController : ControllerBase
    {
        private readonly GetProductsHandler _getProductsHandler;
        private readonly GetProductByIdHandler _getProductByIdHandler;
        private readonly CreateProductHandler _createProductHandler;
        private readonly UpdateProductHandler _updateProductHandler;
        private readonly DeleteProductHandler _deleteProductHandler;
        private readonly GetCategoriesHandler _getCategoriesHandler;
        private readonly GetProductStatsHandler _getStatsHandler;
        private readonly ToggleProductActiveHandler _toggleActiveHandler;
        private readonly ILogger<ProductsController> _logger;

        public ProductsController(
            GetProductsHandler getProductsHandler,
            GetProductByIdHandler getProductByIdHandler,
            GetProductStatsHandler getStatsHandler,
            CreateProductHandler createProductHandler,
            UpdateProductHandler updateProductHandler,
            DeleteProductHandler deleteProductHandler,
            GetCategoriesHandler getCategoriesHandler,
            ToggleProductActiveHandler toggleActiveHandler,
            ILogger<ProductsController> logger)
        {
            _getProductsHandler = getProductsHandler;
            _getProductByIdHandler = getProductByIdHandler;
            _createProductHandler = createProductHandler;
            _updateProductHandler = updateProductHandler;
            _deleteProductHandler = deleteProductHandler;
            _getCategoriesHandler = getCategoriesHandler;
            _toggleActiveHandler = toggleActiveHandler;
            _getStatsHandler = getStatsHandler;
            _logger = logger;
        }

        [HttpGet]
        [Authorize(Policy = Policies.ProductsRead)]
        public async Task<ActionResult<PaginatedResult<ProductListItem>>> GetAll(
        [FromQuery] Guid tenantId,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? category = null,
        [FromQuery] bool? isActive = null,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await _getProductsHandler.Handle(
                    tenantId, pageNumber, pageSize, category, isActive, search, cancellationToken);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get products for tenant {TenantId}", tenantId);
                return StatusCode(500, new { error = "Failed to retrieve products" });
            }
        }

        // PATCH: api/products/{id}/toggle-active?tenantId={guid}
        [HttpPatch("{id:guid}/toggle-active")]
        [Authorize(Policy = Policies.ProductsUpdate)]
        public async Task<IActionResult> ToggleActive(
            [FromRoute] Guid id,
            [FromQuery] Guid tenantId)
        {
            try
            {
                var isNowActive = await _toggleActiveHandler.Handle(tenantId, id);
                var msg = isNowActive ? "Product activated." : "Product deactivated.";
                return Ok(new { isActive = isNowActive, message = msg });
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Product {id} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to toggle product {ProductId}", id);
                return StatusCode(500, new { error = "Toggle failed" });
            }
        }
        [HttpGet("stats")]
        [Authorize(Policy = Policies.ProductsRead)]
        public async Task<ActionResult<ProductStatsDto>> GetStats(
        [FromQuery] Guid tenantId,
        CancellationToken cancellationToken = default)
        {
            try
            {
                var stats = await _getStatsHandler.Handle(tenantId, cancellationToken);
                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get product statistics");
                return StatusCode(500, new { error = "Failed to retrieve statistics" });
            }
        }

        // GET: api/products/{id}?tenantId={guid}
        [HttpGet("{id:guid}")]
        [Authorize(Policy = Policies.ProductsRead)]
        public async Task<ActionResult<ProductDto>> GetById(
            [FromRoute] Guid id,
            [FromQuery] Guid tenantId)
        {
            try
            {
                var product = await _getProductByIdHandler.Handle(tenantId, id);
                return Ok(product);
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Product {id} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get product {ProductId}", id);
                return StatusCode(500, new { error = "Failed to retrieve product" });
            }
        }

        // POST: api/products
        [HttpPost]
        [Authorize(Policy = Policies.ProductsCreate)]
        public async Task<ActionResult<ProductDto>> Create([FromBody] CreateProductDto dto)
        {
            try
            {
                var product = await _createProductHandler.Handle(dto);
                return CreatedAtAction(nameof(GetById), new { id = product.Id, tenantId = dto.TenantId }, product);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create product");
                return StatusCode(500, new { error = "Failed to create product" });
            }
        }

        // PUT: api/products/{id}?tenantId={guid}
        [HttpPut("{id:guid}")]
        [Authorize(Policy = Policies.ProductsUpdate)]
        public async Task<IActionResult> Update(
            [FromRoute] Guid id,
            [FromQuery] Guid tenantId,
            [FromBody] UpdateProductDto dto)
        {
            try
            {
                await _updateProductHandler.Handle(tenantId, id, dto);
                return Ok(new { message = "Product updated successfully" });
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Product {id} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update product {ProductId}", id);
                return StatusCode(500, new { error = "Failed to update product" });
            }
        }

        // DELETE: api/products/{id}?tenantId={guid}
        [HttpDelete("{id:guid}")]
        [Authorize(Policy = Policies.ProductsDelete)]
        public async Task<IActionResult> Delete(
            [FromRoute] Guid id,
            [FromQuery] Guid tenantId)
        {
            try
            {
                await _deleteProductHandler.Handle(tenantId, id);
                return Ok(new { message = "Product deleted successfully" });
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Product {id} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete product {ProductId}", id);
                return StatusCode(500, new { error = "Failed to delete product" });
            }
        }

        // GET: api/products/categories?tenantId={guid}
        //
        // 066: THE POLICY WAS MISSING. Every other action on this
        // controller carries one; this was the only one relying on the
        // class-level [Authorize], which asks for a valid token and
        // nothing else. So a user with no Products permission at all — a
        // viewer, someone whose role grants only Leads — could list the
        // workspace's product categories.
        //
        // Smaller than it looks, because Product carries a global query
        // filter: the handler's own WHERE and the filter are ANDed, so
        // passing another workspace's tenantId in the query string returns
        // nothing rather than their categories. That is the filter saving
        // this endpoint, though, not the endpoint being right.
        //
        // WORTH KNOWING, NOT CHANGED HERE: every action on this controller
        // takes tenantId from the QUERY STRING rather than the token. The
        // query filter is what makes that safe, which means the safety
        // depends on FlowDbContext rather than on the controller. Moving
        // them to _currentUserService.GetCurrentTenantId() would make the
        // controller say what it means — a worthwhile round, but it
        // changes every caller's URL and does not belong in a one-line fix.
        [HttpGet("categories")]
        [Authorize(Policy = Policies.ProductsRead)]
        public async Task<ActionResult<List<string>>> GetCategories([FromQuery] Guid tenantId)
        {
            try
            {
                var categories = await _getCategoriesHandler.Handle(tenantId);
                return Ok(categories);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get categories for tenant {TenantId}", tenantId);
                return StatusCode(500, new { error = "Failed to retrieve categories" });
            }
        }
    }
}

