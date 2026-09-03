using MerkaiTrial.Application.Commands.Users;
using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
    [Route("api/users")]
    public class UsersController : ControllerBase
    {
        private readonly GetUsersPaginatedHandler _getPaginatedHandler;
        private readonly GetUserDetailHandler _getDetailHandler;
        private readonly CreateUserHandler _createHandler;
        private readonly UpdateUserHandler _updateHandler;
        private readonly UpdateUserStatusHandler _updateStatusHandler;
        private readonly DeleteUserHandler _deleteHandler;
        private readonly AssignUserRolesHandler _assignRolesHandler;
        private readonly GetUserRolesHandler _getUserRolesHandler;
        private readonly GetUsersLookupHandler _getLookupHandler;
        private readonly GetUserStatsHandler _getStatsHandler;
        private readonly BulkUpdateUserStatusHandler _bulkUpdateStatusHandler;
        private readonly GetSalesTeamHandler _getSalesTeamHandler;
        private readonly ILogger<UsersController> _logger;

        public UsersController(
            GetUsersPaginatedHandler getPaginatedHandler,
            GetUserDetailHandler getDetailHandler,
            CreateUserHandler createHandler,
            UpdateUserHandler updateHandler,
            UpdateUserStatusHandler updateStatusHandler,
            DeleteUserHandler deleteHandler,
            AssignUserRolesHandler assignRolesHandler,
            GetUserRolesHandler getUserRolesHandler,
            GetUsersLookupHandler getLookupHandler,
            GetUserStatsHandler getStatsHandler,
            BulkUpdateUserStatusHandler bulkUpdateStatusHandler,
            GetSalesTeamHandler getSalesTeamHandler,
            ILogger<UsersController> logger)
        {
            _getPaginatedHandler = getPaginatedHandler;
            _getDetailHandler = getDetailHandler;
            _createHandler = createHandler;
            _updateHandler = updateHandler;
            _updateStatusHandler = updateStatusHandler;
            _deleteHandler = deleteHandler;
            _assignRolesHandler = assignRolesHandler;
            _getUserRolesHandler = getUserRolesHandler;
            _getLookupHandler = getLookupHandler;
            _getStatsHandler = getStatsHandler;
            _bulkUpdateStatusHandler = bulkUpdateStatusHandler;
            _getSalesTeamHandler = getSalesTeamHandler;
            _logger = logger;
        }

        [HttpGet("paginated")]
        public async Task<ActionResult<PaginatedUsersResponse>> GetPaginated(
            [FromQuery] Guid tenantId,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 25,
            [FromQuery] string? search = null)
        {
            try
            {
                var result = await _getPaginatedHandler.Handle(tenantId, page, pageSize, search);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get paginated users");
                return StatusCode(500, new { error = "Failed to retrieve users" });
            }
        }

        [HttpGet("{id:guid}")]
        public async Task<ActionResult<UserDto>> GetById([FromRoute] Guid id, [FromQuery] Guid tenantId)
        {
            try
            {
                var result = await _getDetailHandler.Handle(tenantId, id);
                return Ok(result);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get user {UserId}", id);
                return StatusCode(500, new { error = "Failed to retrieve user" });
            }
        }

        [HttpPost]
        public async Task<ActionResult<UserDto>> Create([FromBody] CreateUserCommand cmd)
        {
            try
            {
                var result = await _createHandler.Handle(cmd);
                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create user");
                return StatusCode(500, new { error = "Failed to create user" });
            }
        }

        [HttpPut("{id:guid}")]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] UpdateUserCommand cmd)
        {
            try
            {
                if (id != cmd.UserId)
                    return BadRequest(new { error = "User ID mismatch" });

                await _updateHandler.Handle(cmd);
                return NoContent();
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
                _logger.LogError(ex, "Failed to update user {UserId}", id);
                return StatusCode(500, new { error = "Failed to update user" });
            }
        }

        [HttpPatch("{id:guid}/status")]
        public async Task<IActionResult> UpdateStatus([FromRoute] Guid id, [FromBody] UpdateUserStatusCommand cmd)
        {
            try
            {
                if (id != cmd.UserId)
                    return BadRequest(new { error = "User ID mismatch" });

                await _updateStatusHandler.Handle(cmd);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update user status {UserId}", id);
                return StatusCode(500, new { error = "Failed to update status" });
            }
        }

        [HttpDelete("{id:guid}")]
        public async Task<IActionResult> Delete([FromRoute] Guid id, [FromQuery] Guid tenantId)
        {
            try
            {
                await _deleteHandler.Handle(tenantId, id);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete user {UserId}", id);
                return StatusCode(500, new { error = "Failed to delete user" });
            }
        }

        [HttpPost("{id:guid}/roles")]
        public async Task<IActionResult> AssignRoles([FromRoute] Guid id, [FromBody] AssignUserRolesCommand cmd)
        {
            try
            {
                if (id != cmd.UserId)
                    return BadRequest(new { error = "User ID mismatch" });

                await _assignRolesHandler.Handle(cmd);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to assign roles to user {UserId}", id);
                return StatusCode(500, new { error = "Failed to assign roles" });
            }
        }

        [HttpGet("{id:guid}/roles")]
        public async Task<ActionResult<List<UserRoleDto>>> GetRoles([FromRoute] Guid id, [FromQuery] Guid tenantId)
        {
            try
            {
                var result = await _getUserRolesHandler.Handle(tenantId, id);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get user roles {UserId}", id);
                return StatusCode(500, new { error = "Failed to retrieve roles" });
            }
        }

        [HttpGet("lookup")]
        public async Task<ActionResult<List<UserLookupDto>>> GetLookup([FromQuery] Guid tenantId)
        {
            try
            {
                var result = await _getLookupHandler.Handle(tenantId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get users lookup");
                return StatusCode(500, new { error = "Failed to retrieve users" });
            }
        }

        [HttpGet("stats")]
        public async Task<ActionResult<UserStatsDto>> GetStats([FromQuery] Guid tenantId)
        {
            try
            {
                var result = await _getStatsHandler.Handle(tenantId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get user stats");
                return StatusCode(500, new { error = "Failed to retrieve stats" });
            }
        }

        [HttpPost("bulk/status")]
        public async Task<ActionResult<BulkOperationResult>> BulkUpdateStatus([FromBody] BulkUpdateUserStatusCommand cmd)
        {
            try
            {
                var result = await _bulkUpdateStatusHandler.Handle(cmd);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to bulk update user status");
                return StatusCode(500, new { error = "Failed to update users" });
            }
        }

        // File: MerkaiTrial.WebApi/Controllers/UsersController.cs (ADD THIS ENDPOINT)
        [HttpGet("sales-team")]
        public async Task<ActionResult<List<SalesTeamMemberDto>>> GetSalesTeam(
            [FromQuery] Guid tenantId)
        {
            try
            {
                /*var handler = new GetSalesTeamHandler(_db); */// Inject FlowDbContext in constructor
                var result = await _getSalesTeamHandler.Handle(tenantId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get sales team for tenant {TenantId}", tenantId);
                return StatusCode(500, new { error = "Failed to retrieve sales team" });
            }
        }
    }
}
