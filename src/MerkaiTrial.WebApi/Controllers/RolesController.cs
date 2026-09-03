// =====================================================================
// ROLES CONTROLLER - Updated with Pagination
// Location: MerkaiTrial.WebApi/Controllers/RolesController.cs
// =====================================================================

using Microsoft.AspNetCore.Mvc;
using MerkaiTrial.Application.Commands;
using MerkaiTrial.Application.Queries;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
    [Route("api/roles")]
    public class RolesController : ControllerBase
    {
        private readonly GetPaginatedRolesHandler _getPaginatedHandler;
        private readonly GetAllRolesHandler _getAllHandler;
        private readonly GetRoleDetailHandler _getDetailHandler;
        private readonly CreateRoleHandler _createHandler;
        private readonly UpdateRoleHandler _updateHandler;
        private readonly DeleteRoleHandler _deleteHandler;
        private readonly GetRolesLookupHandler _getLookupHandler;
        private readonly GetRoleUsersHandler _getRoleUsersHandler;        
        private readonly GetRoleStatsHandler _getStatsHandler;
        private readonly ILogger<RolesController> _logger;

        public RolesController(
            GetPaginatedRolesHandler getPaginatedHandler,
            GetAllRolesHandler getAllHandler,
            GetRoleDetailHandler getDetailHandler,
            CreateRoleHandler createHandler,
            UpdateRoleHandler updateHandler,
            DeleteRoleHandler deleteHandler,
            GetRolesLookupHandler getLookupHandler,
            GetRoleUsersHandler getRoleUsersHandler,
            GetRoleStatsHandler getStatsHandler,
            ILogger<RolesController> logger)
        {
            _getPaginatedHandler = getPaginatedHandler;
            _getAllHandler = getAllHandler;
            _getDetailHandler = getDetailHandler;
            _createHandler = createHandler;
            _updateHandler = updateHandler;
            _deleteHandler = deleteHandler;
            _getLookupHandler = getLookupHandler;
            _getRoleUsersHandler = getRoleUsersHandler;
            _getStatsHandler = getStatsHandler;
            _logger = logger;
        }

        /// <summary>
        /// Get paginated roles with filtering
        /// </summary>
        [HttpGet("paginated")]
        public async Task<ActionResult<PaginatedRolesResponse>> GetPaginated(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 25,
            [FromQuery] string? search = null,
            [FromQuery] bool? isSystemRole = null)
        {
            try
            {
                var result = await _getPaginatedHandler.Handle(page, pageSize, search, isSystemRole);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get paginated roles");
                return StatusCode(500, "Failed to retrieve roles");
            }
        }

        /// <summary>
        /// Get all roles (no pagination)
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<List<RoleListItem>>> GetAll()
        {
            try
            {
                var roles = await _getAllHandler.Handle();
                return Ok(roles);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get all roles");
                return StatusCode(500, "Failed to retrieve roles");
            }
        }

        /// <summary>
        /// Get role by ID
        /// </summary>
        [HttpGet("{id}")]
        public async Task<ActionResult<RoleDto>> GetById(Guid id)
        {
            try
            {
                var role = await _getDetailHandler.Handle(id);
                return Ok(role);
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogWarning(ex, "Role {RoleId} not found", id);
                return NotFound(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get role {RoleId}", id);
                return StatusCode(500, "Failed to retrieve role");
            }
        }

        /// <summary>
        /// Create new role
        /// </summary>
        [HttpPost]
        public async Task<ActionResult<RoleDto>> Create([FromBody] CreateRoleCommand command)
        {
            try
            {
                var role = await _createHandler.Handle(command);
                return CreatedAtAction(nameof(GetById), new { id = role.Id }, role);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Failed to create role: {Message}", ex.Message);
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create role");
                return StatusCode(500, "Failed to create role");
            }
        }

        /// <summary>
        /// Update role
        /// </summary>
        [HttpPut("{id}")]
        public async Task<IActionResult> Update(Guid id, [FromBody] UpdateRoleCommand command)
        {
            try
            {
                if (id != command.RoleId)
                    return BadRequest("Role ID mismatch");

                await _updateHandler.Handle(command);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogWarning(ex, "Role {RoleId} not found", id);
                return NotFound(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Failed to update role: {Message}", ex.Message);
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update role {RoleId}", id);
                return StatusCode(500, "Failed to update role");
            }
        }

        /// <summary>
        /// Delete role
        /// </summary>
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(Guid id)
        {
            try
            {
                await _deleteHandler.Handle(id);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogWarning(ex, "Role {RoleId} not found", id);
                return NotFound(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Failed to delete role: {Message}", ex.Message);
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete role {RoleId}", id);
                return StatusCode(500, "Failed to delete role");
            }
        }

        /// <summary>
        /// Get roles lookup (for dropdowns)
        /// </summary>
        [HttpGet("lookup")]
        public async Task<ActionResult<List<RoleLookupDto>>> GetLookup()
        {
            try
            {
                var roles = await _getLookupHandler.Handle();
                return Ok(roles);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get roles lookup");
                return StatusCode(500, "Failed to retrieve roles");
            }
        }

        /// <summary>
        /// Get users assigned to a role
        /// </summary>
        [HttpGet("{id}/users")]
        public async Task<ActionResult<List<UserLookupDto>>> GetRoleUsers(Guid id)
        {
            try
            {
                var users = await _getRoleUsersHandler.Handle(id);
                return Ok(users);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get users for role {RoleId}", id);
                return StatusCode(500, "Failed to retrieve role users");
            }
        }

        /// <summary>
        /// Get role statistics
        /// </summary>
        [HttpGet("stats")]
        public async Task<ActionResult<RoleStatsDto>> GetStats()
        {
            try
            {
                var stats = await _getStatsHandler.Handle();
                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get role stats");
                return StatusCode(500, "Failed to retrieve statistics");
            }
        }
    }
}
