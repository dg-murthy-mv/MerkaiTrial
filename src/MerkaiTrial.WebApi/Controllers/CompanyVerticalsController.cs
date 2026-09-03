// =====================================================================
// COMPANY VERTICALS CONTROLLER - UPDATED with Stats Endpoint
// Location: MerkaiTrial.WebApi/Controllers/CompanyVerticalsController.cs
// =====================================================================

using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Mvc;
using MerkaiTrial.Application.Commands.Verticals;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
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
                    PageNumber = pageNumber,
                    PageSize = pageSize
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
                    PageNumber = pageNumber,
                    PageSize = pageSize,
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
                    CreatedBy = dto.CreatedBy
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
                    UpdatedBy = dto.UpdatedBy
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
