// =====================================================================
// ProductCategoriesController.cs
// Location: MerkaiTrial.WebApi/Controllers/ProductCategoriesController.cs
//
// NEW FILE (068).
//
// ─────────────────────────────────────────────────────────────────────
// TENANT ID COMES FROM THE TOKEN, NOT THE QUERY STRING
//
// ProductsController takes `[FromQuery] Guid tenantId` on every action
// and leans on FlowDbContext's query filter to make that safe — its own
// comment at the bottom of that file says so, and calls it a worthwhile
// round to fix. This controller does not inherit the problem: every
// action below reads the tenant from the signed token via
// ICurrentUserService.GetCurrentTenantId(), the way TenantsController's
// company-profile endpoints do since 064.
//
// It matters more here than it does for products. These endpoints WRITE
// to a shared-or-tenant table, and a tenantId a caller could choose is
// a caller choosing which workspace's category list to rename.
//
// AUTHORIZATION: WRITES are Settings.*, the READ is Products.Read.
//
//   The writes first. Editing the category list is configuring the
//   workspace — the same kind of act as setting tax rates or filling in
//   the company profile — and not something every salesperson who can
//   add a product should be able to do. So create, update, remove and
//   reorder are Settings.Create / Update / Delete / Update.
//
//   THE READ IS DELIBERATELY NOT Settings.Read, and getting this wrong
//   is a bug worth describing because it would have been invisible:
//
//     The products list, the product page and both product forms need
//     this list — for the filter dropdown, the Category field, and the
//     icon and colour on every row. Those pages are used by anybody
//     with products.read, who typically does NOT hold settings.read.
//     Under Settings.Read this endpoint would have returned 403 to them,
//     ProductCategoryService would have degraded it to an empty list
//     exactly as designed, and every product in the catalogue would have
//     rendered with a grey box and an empty category dropdown. No error,
//     no log line anybody would look at.
//
//     Reading the shape of the catalogue is a catalogue operation.
//     Deciding it is configuration.
//
//   The Settings PAGE is still Settings-gated — its nav entry is behind
//   `settings` and its OnGetAsync calls ValidatePermissionAsync against
//   Modules.Settings — so a rep who can call this endpoint still cannot
//   open the screen that edits it.
//
//   PermissionHandler bypasses on IsTenantAdmin only, NOT IsSuperAdmin,
//   so a Module.Action policy locks super admins OUT. That is correct
//   here: a product category belongs to a workspace, and a platform
//   admin has no business renaming one.
// ─────────────────────────────────────────────────────────────────────
// =====================================================================

using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Products;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
    [Route("api/product-categories")]
    [Authorize]   // explicit, rather than relying on the FallbackPolicy
    public class ProductCategoriesController : ControllerBase
    {
        private readonly GetProductCategoriesHandler _get;
        private readonly CreateProductCategoryHandler _create;
        private readonly UpdateProductCategoryHandler _update;
        private readonly DeleteProductCategoryHandler _delete;
        private readonly ReorderProductCategoriesHandler _reorder;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<ProductCategoriesController> _logger;

        public ProductCategoriesController(
            GetProductCategoriesHandler get,
            CreateProductCategoryHandler create,
            UpdateProductCategoryHandler update,
            DeleteProductCategoryHandler delete,
            ReorderProductCategoriesHandler reorder,
            ICurrentUserService currentUserService,
            ILogger<ProductCategoriesController> logger)
        {
            _get                = get;
            _create             = create;
            _update             = update;
            _delete             = delete;
            _reorder            = reorder;
            _currentUserService = currentUserService;
            _logger             = logger;
        }

        /// <summary>
        /// The effective list for this workspace.
        ///
        /// includeInactive=true is what the Settings page sends — it has to
        /// show a switched-off category in order to switch it back on.
        /// The Products pages send false, and also pass true when they only
        /// need the icon and colour for a product already filed under a
        /// retired category.
        ///
        /// PRODUCTS.READ, not Settings.Read — the long note at the top of
        /// this file explains why, and what breaks silently if it changes.
        /// </summary>
        [HttpGet]
        [Authorize(Policy = Policies.ProductsRead)]
        public async Task<ActionResult<ProductCategoryListDto>> GetAll(
            [FromQuery] bool includeInactive = false,
            CancellationToken ct = default)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();

            try
            {
                return Ok(await _get.Handle(tenantId, includeInactive, ct));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list product categories for {TenantId}", tenantId);
                return StatusCode(500, new { error = "Failed to load product categories" });
            }
        }

        [HttpPost]
        [Authorize(Policy = Policies.SettingsCreate)]
        public async Task<ActionResult<ProductCategoryWriteResult>> Create(
            [FromBody] CreateProductCategoryDto dto,
            CancellationToken ct)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();

            try
            {
                return Ok(await _create.Handle(tenantId, dto, ct));
            }
            catch (InvalidOperationException ex)
            {
                // A name clash or a bad colour. The message is written for
                // the person, and IApiService turns a 400 body of
                // { "error": "..." } into exactly that message on the page.
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create a product category for {TenantId}", tenantId);
                return StatusCode(500, new { error = "Failed to add the category" });
            }
        }

        /// <summary>
        /// Rename, recolour, re-icon or switch a category on and off.
        ///
        /// A RENAME REWRITES Product.Category ON EVERY AFFECTED PRODUCT —
        /// see UpdateProductCategoryHandler. The result carries the count
        /// so the page can say how many rather than just "Saved".
        /// </summary>
        [HttpPut("{id:guid}")]
        [Authorize(Policy = Policies.SettingsUpdate)]
        public async Task<ActionResult<ProductCategoryWriteResult>> Update(
            Guid id,
            [FromBody] UpdateProductCategoryDto dto,
            CancellationToken ct)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();

            try
            {
                return Ok(await _update.Handle(tenantId, id, dto, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update product category {Id} for {TenantId}", id, tenantId);
                return StatusCode(500, new { error = "Failed to save the category" });
            }
        }

        /// <summary>
        /// Remove a category, saying where its products go.
        ///
        /// WHY POST {id}/remove AND NOT DELETE {id}
        ///
        ///   Because the destination for the products is part of the
        ///   instruction, not a filter on it: "remove this and move its
        ///   products to Hardware" is ONE operation, and splitting it into
        ///   a reassign call followed by a delete call leaves a window
        ///   where a crash has moved the products and left the category
        ///   standing.
        ///
        ///   So it needs a body. DELETE with a body is legal but widely
        ///   mishandled — by proxies, by HttpClient helpers, and by
        ///   IApiService, which has no DeleteAsync overload that carries
        ///   one. The alternative was a query parameter, which would put a
        ///   category name into the URL and therefore into the request
        ///   log; round 063 went to real trouble to keep names and tokens
        ///   out of those, and adding one back for tidiness would undo it.
        ///
        ///   Still SettingsDelete. The verb in the URL does not change
        ///   what the action is.
        /// </summary>
        [HttpPost("{id:guid}/remove")]
        [Authorize(Policy = Policies.SettingsDelete)]
        public async Task<ActionResult<ProductCategoryWriteResult>> Remove(
            Guid id,
            [FromBody] DeleteProductCategoryDto? dto,
            CancellationToken ct)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();

            try
            {
                // No body at all means "leave the products uncategorized",
                // which is the same thing an explicit null ReassignTo
                // means. Product.Category is nullable by design.
                return Ok(await _delete.Handle(tenantId, id, dto ?? new DeleteProductCategoryDto(), ct));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete product category {Id} for {TenantId}", id, tenantId);
                return StatusCode(500, new { error = "Failed to remove the category" });
            }
        }

        /// <summary>
        /// The whole display order at once. Under SettingsUpdate, not
        /// SettingsCreate: nothing is created and nothing is destroyed.
        /// </summary>
        [HttpPut("order")]
        [Authorize(Policy = Policies.SettingsUpdate)]
        public async Task<IActionResult> Reorder(
            [FromBody] ReorderProductCategoriesDto dto,
            CancellationToken ct)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();

            try
            {
                var moved = await _reorder.Handle(tenantId, dto, ct);
                return Ok(new { moved });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reorder product categories for {TenantId}", tenantId);
                return StatusCode(500, new { error = "Failed to save the new order" });
            }
        }
    }
}
