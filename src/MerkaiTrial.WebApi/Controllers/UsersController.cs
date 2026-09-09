// =====================================================================
// FILE: MerkaiTrial.WebApi/Controllers/UsersController.cs
//
// SECURITY FIX. Replace the controller. The handlers are unchanged.
//
// WHAT WAS WRONG
//
// Every endpoint took tenantId from the query string or request body, and
// the controller had no [Authorize] attribute at all:
//
//     GET /api/users/paginated?tenantId=<any-other-tenant>
//
// returned that tenant's users — names, emails, phone numbers. Create
// took TenantId from the body, so a caller could add a user to someone
// else's workspace. Delete and role assignment were the same.
//
// This is exactly what the EF global query filters were added to prevent,
// but Users is deliberately EXEMPT from those filters (login has to find
// a user before there is a tenant context). So Users has no automatic
// protection and needs it enforced explicitly, here.
//
// THE RULE
//
//   A super admin may pass any tenantId.
//   Everyone else gets THEIR OWN tenant, taken from the token, and
//   whatever they sent is ignored.
//
// Not rejected — ignored. Rejecting tells a prober that the tenant
// exists; silently scoping to their own tells them nothing.
// =====================================================================

using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Users;
using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers;

[ApiController]
[Route("api/users")]
[Authorize]   // ← was missing entirely: every endpoint below was anonymous
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

    // =================================================================
    // TENANT RESOLUTION — the heart of the fix
    // =================================================================

    private bool IsSuperAdmin =>
        User.HasClaim("IsSuperAdmin", "true") && !User.HasClaim("ViewingAs", "true");

    private Guid CallerTenantId =>
        Guid.TryParse(User.FindFirst("TenantId")?.Value, out var id)
            ? id
            : throw new UnauthorizedAccessException("No tenant in token.");

    /// <summary>
    /// A super admin may act on any tenant. Everyone else is silently
    /// scoped to their own, whatever they asked for.
    ///
    /// Silently, not with an error: refusing a specific tenant id confirms
    /// that tenant exists. Scoping to their own reveals nothing and still
    /// returns a sensible result.
    /// </summary>
    private Guid ResolveTenant(Guid requested)
    {
        if (IsSuperAdmin) return requested == Guid.Empty ? CallerTenantId : requested;

        var own = CallerTenantId;

        if (requested != Guid.Empty && requested != own)
        {
            _logger.LogWarning(
                "User {UserId} in tenant {Own} requested tenant {Requested} on {Path} — scoped to their own.",
                User.FindFirst("UserId")?.Value, own, requested, Request.Path);
        }

        return own;
    }

    // =================================================================
    // READS
    // =================================================================

    [HttpGet("paginated")]
    [Authorize(Policy = Policies.UsersRead)]
    public async Task<ActionResult<PaginatedUsersResponse>> GetPaginated(
        [FromQuery] Guid tenantId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? search = null)
    {
        try
        {
            var result = await _getPaginatedHandler.Handle(
                ResolveTenant(tenantId), page, pageSize, search);
            return Ok(result);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get paginated users");
            return StatusCode(500, new { error = "Failed to retrieve users" });
        }
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Policies.UsersRead)]
    public async Task<ActionResult<UserDto>> GetById(
        [FromRoute] Guid id, [FromQuery] Guid tenantId)
    {
        try
        {
            var result = await _getDetailHandler.Handle(ResolveTenant(tenantId), id);
            return Ok(result);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get user {UserId}", id);
            return StatusCode(500, new { error = "Failed to retrieve user" });
        }
    }

    [HttpGet("{id:guid}/roles")]
    [Authorize(Policy = Policies.UsersRead)]
    public async Task<ActionResult<List<UserRoleDto>>> GetRoles(
        [FromRoute] Guid id, [FromQuery] Guid tenantId)
    {
        try
        {
            var result = await _getUserRolesHandler.Handle(ResolveTenant(tenantId), id);
            return Ok(result);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get user roles {UserId}", id);
            return StatusCode(500, new { error = "Failed to retrieve roles" });
        }
    }

    [HttpGet("stats")]
    [Authorize(Policy = Policies.UsersRead)]
    public async Task<ActionResult<UserStatsDto>> GetStats([FromQuery] Guid tenantId)
    {
        try
        {
            return Ok(await _getStatsHandler.Handle(ResolveTenant(tenantId)));
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get user stats");
            return StatusCode(500, new { error = "Failed to retrieve stats" });
        }
    }

    // Lookup and sales-team feed owner dropdowns on lead and deal forms.
    // Any signed-in user needs them, so no users.read policy — but they
    // are still tenant-scoped, which is what actually matters.
    [HttpGet("lookup")]
    public async Task<ActionResult<List<UserLookupDto>>> GetLookup([FromQuery] Guid tenantId)
    {
        try
        {
            return Ok(await _getLookupHandler.Handle(ResolveTenant(tenantId)));
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get users lookup");
            return StatusCode(500, new { error = "Failed to retrieve users" });
        }
    }

    [HttpGet("sales-team")]
    public async Task<ActionResult<List<SalesTeamMemberDto>>> GetSalesTeam([FromQuery] Guid tenantId)
    {
        try
        {
            return Ok(await _getSalesTeamHandler.Handle(ResolveTenant(tenantId)));
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get sales team");
            return StatusCode(500, new { error = "Failed to retrieve sales team" });
        }
    }

    // =================================================================
    // WRITES
    // =================================================================

    [HttpPost]
    [Authorize(Policy = Policies.UsersCreate)]
    public async Task<ActionResult<UserDto>> Create([FromBody] CreateUserCommand cmd)
    {
        try
        {
            // TenantId arrives in the BODY here, so it is overwritten rather
            // than trusted. A tenant admin posting another tenant's id gets
            // a user in their own workspace, not the other one.
            var scoped = cmd with { TenantId = ResolveTenant(cmd.TenantId) };

            var result = await _createHandler.Handle(scoped);
            return Ok(result);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create user");
            return StatusCode(500, new { error = "Failed to create user" });
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.UsersUpdate)]
    public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] UpdateUserCommand cmd)
    {
        try
        {
            if (id != cmd.UserId) return BadRequest(new { error = "User ID mismatch" });

            // UpdateUserCommand carries no TenantId, so the handler must be
            // checked: if it loads the user by id alone, a tenant admin can
            // edit ANY user by guessing an id. See the note at the bottom.

            var scoped = cmd with { TenantId = ResolveTenant(cmd.TenantId) };
            await _updateHandler.Handle(scoped);
            return NoContent();
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update user {UserId}", id);
            return StatusCode(500, new { error = "Failed to update user" });
        }
    }

    [HttpPatch("{id:guid}/status")]
    [Authorize(Policy = Policies.UsersUpdate)]
    public async Task<IActionResult> UpdateStatus(
        [FromRoute] Guid id, [FromBody] UpdateUserStatusCommand cmd)
    {
        try
        {
            if (id != cmd.UserId) return BadRequest(new { error = "User ID mismatch" });

            var scoped = cmd with { TenantId = ResolveTenant(cmd.TenantId) };
            await _updateStatusHandler.Handle(scoped);
            return NoContent();
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update user status {UserId}", id);
            return StatusCode(500, new { error = "Failed to update status" });
        }
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.UsersDelete)]
    public async Task<IActionResult> Delete([FromRoute] Guid id, [FromQuery] Guid tenantId)
    {
        try
        {
            var scopedTenant = ResolveTenant(tenantId);

            // You cannot delete yourself. Otherwise a tenant's only admin can
            // lock the whole workspace out with one click, and recovering it
            // means you editing the database by hand.
            if (Guid.TryParse(User.FindFirst("UserId")?.Value, out var me) && me == id)
                return BadRequest(new { error = "You cannot delete your own account." });

            await _deleteHandler.Handle(scopedTenant, id);
            return NoContent();
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete user {UserId}", id);
            return StatusCode(500, new { error = "Failed to delete user" });
        }
    }

    [HttpPost("{id:guid}/roles")]
    [Authorize(Policy = Policies.UsersUpdate)]
    public async Task<IActionResult> AssignRoles(
        [FromRoute] Guid id, [FromBody] AssignUserRolesCommand cmd)
    {
        try
        {
            if (id != cmd.UserId) return BadRequest(new { error = "User ID mismatch" });

            var scoped = cmd with { TenantId = ResolveTenant(cmd.TenantId) };
            await _assignRolesHandler.Handle(scoped);
            return NoContent();
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to assign roles to user {UserId}", id);
            return StatusCode(500, new { error = "Failed to assign roles" });
        }
    }

    [HttpPost("bulk/status")]
    [Authorize(Policy = Policies.UsersUpdate)]
    public async Task<ActionResult<BulkOperationResult>> BulkUpdateStatus(
        [FromBody] BulkUpdateUserStatusCommand cmd)
    {
        try
        {
            var scoped = cmd with { TenantId = ResolveTenant(cmd.TenantId) };
            return Ok(await _bulkUpdateStatusHandler.Handle(scoped));
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to bulk update user status");
            return StatusCode(500, new { error = "Failed to update users" });
        }
    }
}


