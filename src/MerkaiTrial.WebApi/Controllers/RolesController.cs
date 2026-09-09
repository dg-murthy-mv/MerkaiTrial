// =====================================================================
// FILE: MerkaiTrial.WebApi/Controllers/RolesController.cs
//
// STEP 3 CHANGE: every action now carries a permission policy.
// STEP 4 CHANGE: KeyNotFoundException is caught everywhere it can now be
//                thrown, so cross-tenant access returns 404 instead of 500.
//
// WHY STEP 4 MATTERS: the scoped handlers throw KeyNotFoundException for
// "another tenant's role" as well as for "no such role" — deliberately, so
// the two are indistinguishable from outside. But GetRoleUsers, GetLookup
// and the others caught only Exception and returned 500. A 500 is a server
// error: it fills your logs with false alarms, and it tells a prober that
// something unusual happened rather than simply "not found".
//
// The catch order matters: KeyNotFoundException and InvalidOperationException
// must come BEFORE the general Exception catch, or they never match.
// =====================================================================

using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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

        /// <summary>Get paginated roles with filtering.</summary>
        [HttpGet("paginated")]
        [Authorize(Policy = Policies.RolesRead)]
        public async Task<ActionResult<PaginatedRolesResponse>> GetPaginated(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 25,
            [FromQuery] string? search = null,
            [FromQuery] bool? isSystemRole = null)
        {
            try
            {
                return Ok(await _getPaginatedHandler.Handle(page, pageSize, search, isSystemRole));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get paginated roles");
                return StatusCode(500, "Failed to retrieve roles");
            }
        }

        /// <summary>Get all roles (no pagination).</summary>
        [HttpGet]
        [Authorize(Policy = Policies.RolesRead)]
        public async Task<ActionResult<List<RoleListItem>>> GetAll()
        {
            try
            {
                return Ok(await _getAllHandler.Handle());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get all roles");
                return StatusCode(500, "Failed to retrieve roles");
            }
        }

        /// <summary>Get role by ID.</summary>
        [HttpGet("{id}")]
        [Authorize(Policy = Policies.RolesRead)]
        public async Task<ActionResult<RoleDto>> GetById(Guid id)
        {
            try
            {
                return Ok(await _getDetailHandler.Handle(id));
            }
            catch (KeyNotFoundException)
            {
                // Covers both "no such role" and "belongs to another tenant".
                // Logged at Debug, not Warning: with scoping in place this is
                // an ordinary outcome, not an incident.
                _logger.LogDebug("Role {RoleId} not visible to caller", id);
                return NotFound();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get role {RoleId}", id);
                return StatusCode(500, "Failed to retrieve role");
            }
        }

        /// <summary>Create a new role. It is stamped with the caller's tenant.</summary>
        [HttpPost]
        [Authorize(Policy = Policies.RolesCreate)]
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

        /// <summary>Update a role. System roles and other tenants' roles are rejected.</summary>
        [HttpPut("{id}")]
        [Authorize(Policy = Policies.RolesUpdate)]
        public async Task<IActionResult> Update(Guid id, [FromBody] UpdateRoleCommand command)
        {
            try
            {
                if (id != command.RoleId)
                    return BadRequest("Role ID mismatch");

                await _updateHandler.Handle(command);
                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                _logger.LogDebug("Role {RoleId} not visible to caller for update", id);
                return NotFound();
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

        /// <summary>Delete a role (soft delete).</summary>
        [HttpDelete("{id}")]
        [Authorize(Policy = Policies.RolesDelete)]
        public async Task<IActionResult> Delete(Guid id)
        {
            try
            {
                await _deleteHandler.Handle(id);
                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                _logger.LogDebug("Role {RoleId} not visible to caller for delete", id);
                return NotFound();
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

        /// <summary>Roles lookup for dropdowns.</summary>
        [HttpGet("lookup")]
        [Authorize(Policy = Policies.RolesRead)]
        public async Task<ActionResult<List<RoleLookupDto>>> GetLookup([FromQuery] Guid? tenantId = null)
        {
            try
            {
                // Scoped the same way UsersController does it: a super admin may
                // ask about any tenant, everyone else silently gets their own.
                // Without this, a tenant admin could enumerate another tenant's
                // role names — minor on its own, but it is the same door.
                Guid? scoped = null;

                if (tenantId.HasValue)
                {
                    var isSuperAdmin = User.HasClaim("IsSuperAdmin", "true")
                                    && !User.HasClaim("ViewingAs", "true");

                    scoped = isSuperAdmin
                        ? tenantId
                        : (Guid.TryParse(User.FindFirst("TenantId")?.Value, out var own) ? own : null);
                }

                return Ok(await _getLookupHandler.Handle(scoped));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get roles lookup");
                return StatusCode(500, "Failed to retrieve roles");
            }
        }

        /// <summary>
        /// Users assigned to a role — the caller's tenant only.
        ///
        /// This previously returned the names and emails of matching users in
        /// EVERY tenant, because roles were shared and nothing filtered by
        /// tenant. The handler is now scoped; this catch makes an unreachable
        /// role a clean 404.
        /// </summary>
        [HttpGet("{id}/users")]
        [Authorize(Policy = Policies.RolesRead)]
        public async Task<ActionResult<List<UserLookupDto>>> GetRoleUsers(Guid id)
        {
            try
            {
                return Ok(await _getRoleUsersHandler.Handle(id));
            }
            catch (KeyNotFoundException)
            {
                _logger.LogDebug("Role {RoleId} not visible to caller for user list", id);
                return NotFound();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get users for role {RoleId}", id);
                return StatusCode(500, "Failed to retrieve role users");
            }
        }

        /// <summary>Role statistics, scoped to the caller's tenant.</summary>
        [HttpGet("stats")]
        [Authorize(Policy = Policies.RolesRead)]
        public async Task<ActionResult<RoleStatsDto>> GetStats()
        {
            try
            {
                return Ok(await _getStatsHandler.Handle());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get role stats");
                return StatusCode(500, "Failed to retrieve statistics");
            }
        }
    }
}
