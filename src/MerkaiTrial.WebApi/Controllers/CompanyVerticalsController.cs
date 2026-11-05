// =====================================================================
// COMPANY VERTICALS CONTROLLER - UPDATED with Stats Endpoint
// Location: MerkaiTrial.WebApi/Controllers/CompanyVerticalsController.cs
// =====================================================================

using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Mvc;
using MerkaiTrial.Application.Commands.Verticals;
using MerkaiTrial.WebApi.Authorization;   // 119 — [PlatformAdminOnly]
using Microsoft.AspNetCore.Authorization;

namespace MerkaiTrial.WebApi.Controllers
{
    // 119 — [Authorize] added, and adding / changing / deleting a vertical is
    // platform administration ([PlatformAdminOnly]). Until now any signed-in
    // person could do all three — and Create took the workspace from the
    // request body, so they could add one to SOMEONE ELSE's workspace.
    [ApiController]
    [Authorize]
    [Route("api/company-verticals")]
    public class CompanyVerticalsController : ControllerBase
    {
        private readonly GetCompanyVerticalsHandler _getVerticals;
        private readonly GetCompanyVerticalByIdHandler _getById;
        private readonly CreateCompanyVerticalHandler _createVertical;
        private readonly UpdateCompanyVerticalHandler _updateVertical;
        private readonly DeleteCompanyVerticalHandler _deleteVertical;
        private readonly GetVerticalStatsHandler _getStats;  // ✅ NEW
        private readonly ILogger<CompanyVerticalsController> _logger;

        public CompanyVerticalsController(
            GetCompanyVerticalsHandler getVerticals,
            GetCompanyVerticalByIdHandler getById,
            CreateCompanyVerticalHandler createVertical,
            UpdateCompanyVerticalHandler updateVertical,
            DeleteCompanyVerticalHandler deleteVertical,
            GetVerticalStatsHandler getStats,  // ✅ NEW
            ILogger<CompanyVerticalsController> logger)
        {
            _getVerticals = getVerticals;
            _getById = getById;
            _createVertical = createVertical;
            _updateVertical = updateVertical;
            _deleteVertical = deleteVertical;
            _getStats = getStats;  // ✅ NEW
            _logger = logger;
        }

        /// <summary>120 — who is doing this, from the sign-in (was taken from the request body).</summary>
        private string Who =>
            User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? User.Identity?.Name ?? "super admin";

        // ==================== GET STATISTICS (NEW!) ====================
        [HttpGet("stats")]
        [ProducesResponseType(typeof(VerticalStatsDto), 200)]
        public async Task<IActionResult> GetStats(CancellationToken cancellationToken = default)
        {
            try
            {
                var stats = await _getStats.Handle(cancellationToken);
                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting vertical statistics");
                return StatusCode(500, "An error occurred");
            }
        }

        [HttpGet("available")]
        [ProducesResponseType(typeof(PaginatedResult<CompanyVerticalListItem>), 200)]
        public async Task<IActionResult> GetAvailable(
            [FromQuery] Guid tenantId,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 1000,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var query = new GetCompanyVerticalsQuery
                {
                    TenantId = tenantId,
                    IncludeSystem = true,
                    PageNumber = Math.Max(1, pageNumber),          // 120 — page 0 was a negative skip (a 500)
                    PageSize = Math.Clamp(pageSize, 1, 1000)   // 120 — dropdowns ask for up to 1,000
                };

                var result = await _getVerticals.Handle(query, cancellationToken);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting available verticals");
                return StatusCode(500, "An error occurred");
            }
        }

        [HttpGet]
        [ProducesResponseType(typeof(PaginatedResult<CompanyVerticalListItem>), 200)]
        public async Task<IActionResult> GetAll(
            [FromQuery] Guid? tenantId = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string? searchTerm = null,
            [FromQuery] bool showSystemOnly = false,
            [FromQuery] bool showCustomOnly = false,
            [FromQuery] bool showAllVerticals = false,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var query = new GetCompanyVerticalsQuery
                {
                    TenantId = tenantId,
                    IncludeSystem = tenantId.HasValue,
                    PageNumber = Math.Max(1, pageNumber),          // 120 — page 0 was a negative skip (a 500)
                    PageSize = Math.Clamp(pageSize, 1, 1000),   // 120 — dropdowns ask for up to 1,000
                    SearchTerm = searchTerm,
                    ShowSystemOnly = showSystemOnly,
                    ShowCustomOnly = showCustomOnly,
                    ShowAllVerticals = showAllVerticals
                };

                var result = await _getVerticals.Handle(query, cancellationToken);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting verticals");
                return StatusCode(500, "An error occurred");
            }
        }

        [HttpGet("{id}")]
        [ProducesResponseType(typeof(CompanyVerticalDto), 200)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken = default)
        {
            try
            {
                var query = new GetCompanyVerticalByIdQuery { Id = id };
                var vertical = await _getById.Handle(query, cancellationToken);
                return Ok(vertical);
            }
            catch (KeyNotFoundException)
            {
                return NotFound($"Vertical {id} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting vertical {Id}", id);
                return StatusCode(500, "An error occurred");
            }
        }

        [HttpPost]
        [PlatformAdminOnly]   // 119
        [ProducesResponseType(typeof(CompanyVerticalDto), 201)]
        [ProducesResponseType(400)]
        public async Task<IActionResult> Create([FromBody] CreateCompanyVerticalDto dto, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var command = new CreateCompanyVerticalCommand
                {
                    TenantId = dto.TenantId,
                    Name = dto.Name,
                    Description = dto.Description,
                    Icon = dto.Icon,
                    Color = dto.Color,
                    CreatedBy = Who   // 120 — the signed-in person, not what the request says
                };

                var vertical = await _createVertical.Handle(command, cancellationToken);
                return CreatedAtAction(nameof(GetById), new { id = vertical.Id }, vertical);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating vertical");
                return StatusCode(500, "An error occurred");
            }
        }

        [HttpPut("{id}")]
        [PlatformAdminOnly]   // 119
        [ProducesResponseType(204)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCompanyVerticalDto dto, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var command = new UpdateCompanyVerticalCommand
                {
                    Id = id,
                    Name = dto.Name,
                    Description = dto.Description,
                    Icon = dto.Icon,
                    Color = dto.Color,
                    UpdatedBy = Who   // 120
                };

                await _updateVertical.Handle(command, cancellationToken);
                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound($"Vertical {id} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating vertical {Id}", id);
                return StatusCode(500, "An error occurred");
            }
        }

        [HttpDelete("{id}")]
        [PlatformAdminOnly]   // 119
        [ProducesResponseType(204)]
        [ProducesResponseType(404)]
        [ProducesResponseType(400)]
        public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken = default)
        {
            try
            {
                var command = new DeleteCompanyVerticalCommand { Id = id };
                await _deleteVertical.Handle(command, cancellationToken);
                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound($"Vertical {id} not found");
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting vertical {Id}", id);
                return StatusCode(500, "An error occurred");
            }
        }
    }
}
