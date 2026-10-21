// =====================================================================
// FILE: MerkaiTrial.WebApi/Controllers/ProductsController.cs
//
// COMPLETE FILE — 080.
//
// 1. THE WORKSPACE COMES FROM THE TOKEN, on every action. Until now each
//    action took tenantId from the QUERY STRING (and Create from the
//    request body), so the only thing stopping one workspace asking for
//    another's products was FlowDbContext's global query filter. That
//    filter still applies; the controller now also says what it means,
//    via ICurrentUserService.GetCurrentTenantId().
//
//    NO CALLER HAS TO CHANGE. A ?tenantId= still sent by an existing URL
//    is simply ignored by model binding, and CreateProductDto.TenantId is
//    overwritten with the token's workspace before the handler sees it.
//
// 2. SENTENCES REACH THE PAGE. Create and Update return a 400 with the
//    message for ProductValidationException (unknown category, a bad
//    price row, a bundle that contains itself, no name) and for
//    CustomFieldValidationException. Before this round every one of them
//    became "Failed to create product" and the person had no idea what
//    to fix. Delete returns a 400 the same way when the product is part
//    of a bundle (DeleteProductHandler refuses it). Any OTHER exception stays a logged 500 with a plain-string
//    body, so internal wording never reaches a page (the 078 rule:
//    IApiService turns an { error } body into an exception message).
//
// 3. GET api/products takes repeated ?cf=<fieldId>~<op>~<value> custom
//    field filters (CustomFieldFilterCodec), capped BEFORE decoding so a
//    request with ten thousand ?cf= values costs nothing. Unreadable ones
//    are skipped. Page and size are clamped by the handler (1–1000; the
//    catalogue callers need the large pages — see GetProductsHandler).
//
// 096 — GET api/products takes ?sort= and ?dir= (name, sku, category,
//   price, status, created), checked against a fixed list in the handler.
//   Without them the order is the catalogue order, as before.
//
// Policies unchanged: products.read / create / update / delete.
// =====================================================================

using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.CustomFields;   // 080 — CustomFieldValidationException
using MerkaiTrial.Application.Commands.Products;
using MerkaiTrial.Application.Configuration;           // 080 — CustomFieldFilterCodec, CustomFieldLimits
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;                // 080 — ICurrentUserService
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
        private readonly ICurrentUserService _currentUserService;   // 080
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
            ICurrentUserService currentUserService,                 // 080
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
            _currentUserService = currentUserService;
            _logger = logger;
        }

        /// <summary>080. The signed-in user's workspace — never the query string.</summary>
        private Guid TenantId => _currentUserService.GetCurrentTenantId();

        [HttpGet]
        [Authorize(Policy = Policies.ProductsRead)]
        public async Task<ActionResult<PaginatedResult<ProductListItem>>> GetAll(
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 25,
            [FromQuery] string? category = null,
            [FromQuery] bool? isActive = null,
            [FromQuery] string? search = null,
            // 069. The currency the returned prices should be denominated
            // in. Omitted means the workspace's own. The quote line editor
            // passes the QUOTE's currency — see ProductPricing.cs. Safe in
            // the query string: it changes which prices come back, never
            // WHOSE.
            [FromQuery] string? currency = null,
            [FromQuery(Name = "cf")] string[]? cf = null,          // 080
            [FromQuery] string? sort = null,                       // 096
            [FromQuery] string? dir = null,                        // 096
            CancellationToken cancellationToken = default)
        {
            var tenantId = TenantId;
            try
            {
                // 080 — custom field filters, capped before decoding.
                var customFilters = new List<CustomFieldFilter>();
                foreach (var raw in (cf ?? Array.Empty<string>()).Take(CustomFieldLimits.MaxFiltersPerQuery))
                    if (CustomFieldFilterCodec.TryDecode(raw, out var filter))
                        customFilters.Add(filter);

                var result = await _getProductsHandler.Handle(
                    tenantId, pageNumber, pageSize, category, isActive, search,
                    currency, customFilters, sort, dir, cancellationToken);   // 096
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get products for tenant {TenantId}", tenantId);
                return StatusCode(500, "Failed to retrieve products");
            }
        }

        // PATCH: api/products/{id}/toggle-active
        [HttpPatch("{id:guid}/toggle-active")]
        [Authorize(Policy = Policies.ProductsUpdate)]
        public async Task<IActionResult> ToggleActive([FromRoute] Guid id)
        {
            try
            {
                var isNowActive = await _toggleActiveHandler.Handle(TenantId, id);
                var msg = isNowActive ? "Product activated." : "Product deactivated.";
                return Ok(new { isActive = isNowActive, message = msg });
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = "Product not found." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to toggle product {ProductId}", id);
                return StatusCode(500, "Toggle failed");
            }
        }

        [HttpGet("stats")]
        [Authorize(Policy = Policies.ProductsRead)]
        public async Task<ActionResult<ProductStatsDto>> GetStats(CancellationToken cancellationToken = default)
        {
            try
            {
                var stats = await _getStatsHandler.Handle(TenantId, cancellationToken);
                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get product statistics");
                return StatusCode(500, "Failed to retrieve statistics");
            }
        }

        // GET: api/products/{id}
        [HttpGet("{id:guid}")]
        [Authorize(Policy = Policies.ProductsRead)]
        public async Task<ActionResult<ProductDto>> GetById([FromRoute] Guid id)
        {
            try
            {
                var product = await _getProductByIdHandler.Handle(TenantId, id);
                return Ok(product);
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = "Product not found." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get product {ProductId}", id);
                return StatusCode(500, "Failed to retrieve product");
            }
        }

        // POST: api/products
        [HttpPost]
        [Authorize(Policy = Policies.ProductsCreate)]
        public async Task<ActionResult<ProductDto>> Create([FromBody] CreateProductDto dto)
        {
            try
            {
                // 080 — whatever the body claimed, the product goes into
                // the signed-in user's workspace.
                dto.TenantId = TenantId;

                var product = await _createProductHandler.Handle(dto);
                return CreatedAtAction(nameof(GetById), new { id = product.Id }, product);
            }
            catch (ProductValidationException ex)          // 080 — a sentence for the person
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (CustomFieldValidationException ex)      // 080
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create product");
                return StatusCode(500, "Failed to create product");
            }
        }

        // PUT: api/products/{id}
        [HttpPut("{id:guid}")]
        [Authorize(Policy = Policies.ProductsUpdate)]
        public async Task<IActionResult> Update(
            [FromRoute] Guid id,
            [FromBody] UpdateProductDto dto)
        {
            try
            {
                await _updateProductHandler.Handle(TenantId, id, dto);
                return Ok(new { message = "Product updated successfully" });
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = "Product not found." });
            }
            catch (ProductValidationException ex)          // 080 — a sentence for the person
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (CustomFieldValidationException ex)      // 080
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update product {ProductId}", id);
                return StatusCode(500, "Failed to update product");
            }
        }

        // DELETE: api/products/{id}
        [HttpDelete("{id:guid}")]
        [Authorize(Policy = Policies.ProductsDelete)]
        public async Task<IActionResult> Delete([FromRoute] Guid id)
        {
            try
            {
                await _deleteProductHandler.Handle(TenantId, id);
                return Ok(new { message = "Product deleted successfully" });
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = "Product not found." });
            }
            catch (ProductValidationException ex)          // 080 — e.g. part of a bundle
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete product {ProductId}", id);
                return StatusCode(500, "Failed to delete product");
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
        // 080: the "tenantId from the query string" note that stood here
        // is resolved — see the header. Every action now takes the
        // workspace from the signed-in user's token.
        //
        // ── 068 ──────────────────────────────────────────────────────
        //
        // WHAT THIS ENDPOINT NOW ANSWERS, which is not what it used to.
        //
        // The handler behind it read DISTINCT Product.Category off the
        // products table, so it answered "which categories are IN USE"
        // while every caller was asking "which categories EXIST". A
        // workspace with no products got an empty list, and a category
        // nobody had used yet was invisible. It reads
        // dbo.ProductCategories now — see GetCategoriesHandler, which
        // spells out all three consequences.
        //
        // Still Products.Read, deliberately. Reading the names is part of
        // filing a product, so anybody who can add one needs it. EDITING
        // the list is configuring the workspace and lives on
        // ProductCategoriesController under Settings.*, where a
        // salesperson cannot reach it.
        //
        // Active categories only. An inactive one still prints on old
        // documents but nothing new should be filed under it; the Settings
        // page asks for the inactive rows through its own endpoint.
        [HttpGet("categories")]
        [Authorize(Policy = Policies.ProductsRead)]
        public async Task<ActionResult<List<string>>> GetCategories()
        {
            var tenantId = TenantId;
            try
            {
                var categories = await _getCategoriesHandler.Handle(tenantId);
                return Ok(categories);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get categories for tenant {TenantId}", tenantId);
                return StatusCode(500, "Failed to retrieve categories");
            }
        }
    }
}
