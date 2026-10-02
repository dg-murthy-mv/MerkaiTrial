// =====================================================================
// TENANTS CONTROLLER
// Location: MerkaiTrial.WebApi/Controllers/TenantsController.cs
//
// =====================================================================
// 064 — THIS CONTROLLER HAD NO AUTHORIZATION AT ALL
// =====================================================================
//
// There was no [Authorize] on the class and none on any action. The only
// thing standing in front of it was the FallbackPolicy in
// ApiAuthenticationSetup, which requires A valid token — ANY valid
// token, from any workspace, held by any user of any role.
//
// So a sales rep at one workspace, with a perfectly ordinary login,
// could call:
//
//     GET    /api/tenants/paginated     list every workspace in the system
//     GET    /api/tenants/{id}          read any of them
//     PUT    /api/tenants/{id}          rename one, change its currency
//     PATCH  /api/tenants/{id}/status   deactivate one
//     DELETE /api/tenants/{id}          delete one
//     GET    /api/tenants/lookup        every workspace's id and name
//     GET    /api/tenants/{id}/users    every user in any workspace
//     PUT    /api/tenants/{id}/settings raise their own quotas
//
// Every one of those is a PLATFORM operation. They are now guarded.
//
// WHY THE CHECK IS A CLAIM AND NOT A POLICY. PermissionHandler bypasses
// on IsTenantAdmin, NOT on IsSuperAdmin, so a "tenants.read" policy would
// lock super admins OUT of their own console while letting every tenant
// admin in — exactly backwards. Platform endpoints read the IsSuperAdmin
// claim directly, the same way 056's TaxRatesController does.
//
// =====================================================================
// 064 — AND THE COMPANY PROFILE
// =====================================================================
//
// Two new endpoints, and they are deliberately shaped differently from
// everything else here:
//
//     GET /api/tenants/company-profile
//     PUT /api/tenants/company-profile
//
// NO {id} IN THE ROUTE. The workspace comes from the caller's token, so
// there is no identifier for anyone to change and a workspace can only
// ever read or write its own. Guarded by Settings.Read / Settings.Update
// — the tenant's OWN admin fills in their own letterhead, which is not
// something a super admin should have to do for them.
// =====================================================================

using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Tenants;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers;

[ApiController]
[Route("api/tenants")]
[Authorize]   // 064. Explicit rather than relying on the FallbackPolicy —
              // the per-action guards below are what actually matter, but
              // a controller this dangerous should say so at the top.
public class TenantsController : ControllerBase
{
    private readonly GetTenantsPaginatedHandler _getPaginatedHandler;
    private readonly GetTenantDetailHandler _getDetailHandler;
    private readonly CreateTenantHandler _createHandler;
    private readonly UpdateTenantHandler _updateHandler;
    private readonly UpdateTenantStatusHandler _updateStatusHandler;
    private readonly DeleteTenantHandler _deleteHandler;
    private readonly GetTenantSettingsHandler _getSettingsHandler;
    private readonly UpdateTenantSettingsHandler _updateSettingsHandler;
    private readonly GetTenantStatsHandler _getStatsHandler;
    private readonly GetAllTenantsStatsHandler _getAllStatsHandler;
    private readonly GetTenantUsersHandler _getUsersHandler;
    private readonly GetTenantsLookupHandler _getLookupHandler;
    private readonly GetCountriesForDropdownHandler _getCountriesHandler;

    // 064 — the workspace's own letterhead
    private readonly GetCompanyProfileHandler _getCompanyProfileHandler;
    private readonly UpdateCompanyProfileHandler _updateCompanyProfileHandler;
    private readonly ICurrentUserService _currentUserService;

    private readonly ILogger<TenantsController> _logger;

    public TenantsController(
        GetTenantsPaginatedHandler getPaginatedHandler,
        GetTenantDetailHandler getDetailHandler,
        CreateTenantHandler createHandler,
        UpdateTenantHandler updateHandler,
        UpdateTenantStatusHandler updateStatusHandler,
        DeleteTenantHandler deleteHandler,
        GetTenantSettingsHandler getSettingsHandler,
        UpdateTenantSettingsHandler updateSettingsHandler,
        GetTenantStatsHandler getStatsHandler,
        GetAllTenantsStatsHandler getAllStatsHandler,
        GetTenantUsersHandler getUsersHandler,
        GetTenantsLookupHandler getLookupHandler,
        GetCountriesForDropdownHandler getCountriesHandler,
        GetCompanyProfileHandler getCompanyProfileHandler,        // 064
        UpdateCompanyProfileHandler updateCompanyProfileHandler,  // 064
        ICurrentUserService currentUserService,                   // 064
        ILogger<TenantsController> logger)
    {
        _getPaginatedHandler = getPaginatedHandler;
        _getDetailHandler = getDetailHandler;
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _updateStatusHandler = updateStatusHandler;
        _deleteHandler = deleteHandler;
        _getSettingsHandler = getSettingsHandler;
        _updateSettingsHandler = updateSettingsHandler;
        _getStatsHandler = getStatsHandler;
        _getAllStatsHandler = getAllStatsHandler;
        _getUsersHandler = getUsersHandler;
        _getLookupHandler = getLookupHandler;
        _getCountriesHandler = getCountriesHandler;
        _getCompanyProfileHandler = getCompanyProfileHandler;
        _updateCompanyProfileHandler = updateCompanyProfileHandler;
        _currentUserService = currentUserService;
        _logger = logger;
    }

    // =================================================================
    // 064 — THE PLATFORM GUARD
    //
    // Returns null when the caller may proceed, or the response to send
    // back when they may not. Called as the FIRST line of every platform
    // action, before any handler runs.
    //
    // 403, not 404: the route is public knowledge and pretending it does
    // not exist would also hide a genuine misconfiguration from whoever
    // has to debug it. The message says what is wrong without saying
    // anything about what is behind it.
    // =================================================================
    private bool IsSuperAdmin => User.HasClaim("IsSuperAdmin", "true");

    private IActionResult? RequireSuperAdmin(string operation)
    {
        if (IsSuperAdmin) return null;

        // Worth a log line: under the old code this call would have
        // SUCCEEDED, so the first few of these are how you find out who
        // was reaching the platform API by accident — a stale page, a
        // bookmarked URL, a script.
        _logger.LogWarning(
            "Refused platform operation {Operation} for non-super-admin {User}",
            operation,
            User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "unknown");

        return StatusCode(StatusCodes.Status403Forbidden, new
        {
            error = "This is a platform administration endpoint."
        });
    }

    // ==================== GET PAGINATED TENANTS ====================
    [HttpGet("paginated")]
    public async Task<ActionResult<PaginatedTenantsResponse>> GetPaginated(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? search = null,
        [FromQuery] bool? isActive = null)
    {
        var denied = RequireSuperAdmin("tenants.list");
        if (denied is not null) return new ObjectResult(denied) { StatusCode = (denied as ObjectResult)?.StatusCode ?? 403 };

        try
        {
            var result = await _getPaginatedHandler.Handle(page, pageSize, search, isActive);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get paginated tenants");
            return StatusCode(500, new { error = "Failed to retrieve tenants" });
        }
    }

    // ==================== GET TENANT BY ID ====================
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TenantDto>> GetById([FromRoute] Guid id)
    {
        var denied = RequireSuperAdmin("tenants.read");
        if (denied is not null) return new ObjectResult(denied) { StatusCode = (denied as ObjectResult)?.StatusCode ?? 403 };

        try
        {
            var result = await _getDetailHandler.Handle(id);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get tenant {TenantId}", id);
            return StatusCode(500, new { error = "Failed to retrieve tenant" });
        }
    }

    // ==================== CREATE TENANT ====================
    [HttpPost]
    [ProducesResponseType(typeof(TenantDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TenantDto>> Create([FromBody] CreateTenantCommand cmd)
    {
        var denied = RequireSuperAdmin("tenants.create");
        if (denied is not null) return new ObjectResult(denied) { StatusCode = (denied as ObjectResult)?.StatusCode ?? 403 };

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
            _logger.LogError(ex, "Failed to create tenant");
            return StatusCode(500, new { error = "Failed to create tenant" });
        }
    }

    // ==================== UPDATE TENANT ====================
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] UpdateTenantCommand cmd)
    {
        var denied = RequireSuperAdmin("tenants.update");
        if (denied is not null) return denied;

        try
        {
            if (id != cmd.TenantId)
                return BadRequest(new { error = "Tenant ID mismatch" });

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
            _logger.LogError(ex, "Failed to update tenant {TenantId}", id);
            return StatusCode(500, new { error = "Failed to update tenant" });
        }
    }

    // ==================== UPDATE TENANT STATUS ====================
    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> UpdateStatus([FromRoute] Guid id, [FromQuery] bool isActive)
    {
        var denied = RequireSuperAdmin("tenants.status");
        if (denied is not null) return denied;

        try
        {
            await _updateStatusHandler.Handle(id, isActive);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update tenant status {TenantId}", id);
            return StatusCode(500, new { error = "Failed to update status" });
        }
    }

    // ==================== DELETE TENANT ====================
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete([FromRoute] Guid id)
    {
        var denied = RequireSuperAdmin("tenants.delete");
        if (denied is not null) return denied;

        try
        {
            await _deleteHandler.Handle(id);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete tenant {TenantId}", id);
            return StatusCode(500, new { error = "Failed to delete tenant" });
        }
    }

    // ==================== GET TENANT SETTINGS ====================
    // Quotas and feature flags — what a workspace is ALLOWED, not what it
    // has configured. Platform.
    [HttpGet("{id:guid}/settings")]
    public async Task<ActionResult<TenantSettingsDto>> GetSettings([FromRoute] Guid id)
    {
        var denied = RequireSuperAdmin("tenants.settings.read");
        if (denied is not null) return new ObjectResult(denied) { StatusCode = (denied as ObjectResult)?.StatusCode ?? 403 };

        try
        {
            var result = await _getSettingsHandler.Handle(id);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get tenant settings {TenantId}", id);
            return StatusCode(500, new { error = "Failed to retrieve settings" });
        }
    }

    // ==================== UPDATE TENANT SETTINGS ====================
    // The one that mattered most. Unguarded, any signed-in user could
    // raise their own MaxUsers, MaxLeads, MaxDeals and StorageLimit, and
    // hand themselves feature flags, by calling this with their own id.
    [HttpPut("{id:guid}/settings")]
    public async Task<IActionResult> UpdateSettings([FromRoute] Guid id, [FromBody] UpdateTenantSettingsCommand cmd)
    {
        var denied = RequireSuperAdmin("tenants.settings.update");
        if (denied is not null) return denied;

        try
        {
            if (id != cmd.TenantId)
                return BadRequest(new { error = "Tenant ID mismatch" });

            await _updateSettingsHandler.Handle(cmd);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update tenant settings {TenantId}", id);
            return StatusCode(500, new { error = "Failed to update settings" });
        }
    }

    // ==================== GET TENANT STATISTICS ====================
    [HttpGet("{id:guid}/stats")]
    public async Task<ActionResult<TenantStatsDto>> GetStats([FromRoute] Guid id)
    {
        var denied = RequireSuperAdmin("tenants.stats");
            if (denied is not null) return new ObjectResult(denied) { StatusCode = (denied as ObjectResult)?.StatusCode ?? 403 };

        try
        {
            var result = await _getStatsHandler.Handle(id);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get tenant stats {TenantId}", id);
            return StatusCode(500, new { error = "Failed to retrieve stats" });
        }
    }

    // ==================== GET ALL TENANTS STATS (AGGREGATE) ====================
    [HttpGet("stats/all")]
    public async Task<ActionResult<TenantStatsDto>> GetAllStats()
    {
        var denied = RequireSuperAdmin("tenants.stats.all");
        if (denied is not null) return new ObjectResult(denied) { StatusCode = (denied as ObjectResult)?.StatusCode ?? 403 };

        try
        {
            var result = await _getAllStatsHandler.Handle();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get all tenants stats");
            return StatusCode(500, new { error = "Failed to retrieve stats" });
        }
    }

    // ==================== GET TENANT USERS ====================
    // Every user in a named workspace, with their email. Platform.
    [HttpGet("{id:guid}/users")]
    public async Task<ActionResult<List<UserListItem>>> GetUsers([FromRoute] Guid id)
    {
        var denied = RequireSuperAdmin("tenants.users");
        if (denied is not null) return new ObjectResult(denied) { StatusCode = (denied as ObjectResult)?.StatusCode ?? 403 }; ;

        try
        {
            var result = await _getUsersHandler.Handle(id);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get users for tenant {TenantId}", id);
            return StatusCode(500, new { error = "Failed to retrieve users" });
        }
    }

    // ==================== GET TENANTS LOOKUP ====================
    // Every workspace's id and name — the ViewAs picker. Platform.
    [HttpGet("lookup")]
    public async Task<ActionResult<List<TenantLookupDto>>> GetLookup()
    {
        var denied = RequireSuperAdmin("tenants.lookup");
        if (denied is not null) return new ObjectResult(denied) { StatusCode = (denied as ObjectResult)?.StatusCode ?? 403 };

        try
        {
            var result = await _getLookupHandler.Handle();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get tenants lookup");
            return StatusCode(500, new { error = "Failed to retrieve tenants" });
        }
    }

    // =================================================================
    // REFERENCE DATA — any signed-in user
    //
    // Countries, timezones and plan names are the same for everybody and
    // say nothing about any workspace. They stay open to any authenticated
    // caller because tenant-facing forms read them; locking them to super
    // admins would break those forms and protect nothing.
    // =================================================================

    [HttpGet("countries")]
    public async Task<ActionResult<List<CountryDropdownDto>>> GetCountries()
    {
        try
        {
            var result = await _getCountriesHandler.Handle();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get countries");
            return StatusCode(500, new { error = "Failed to retrieve countries" });
        }
    }

    [HttpGet("timezones")]
    public ActionResult<List<TimezoneDto>> GetTimezones([FromQuery] bool commonOnly = false)
    {
        try
        {
            var timezones = commonOnly
                ? TimezoneHelper.GetCommonTimezones()
                : TimezoneHelper.GetAllTimezones();
            return Ok(timezones);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get timezones");
            return StatusCode(500, new { error = "Failed to retrieve timezones" });
        }
    }

    [HttpGet("plans")]
    public ActionResult<List<string>> GetPlans()
    {
        try
        {
            var plans = PlanHelper.GetAllPlans();
            return Ok(plans);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get plans");
            return StatusCode(500, new { error = "Failed to retrieve plans" });
        }
    }

    // =================================================================
    // 064 — COMPANY PROFILE: THE WORKSPACE'S OWN LETTERHEAD
    //
    // NO {id} IN EITHER ROUTE. The workspace is taken from the caller's
    // token, so there is nothing to tamper with and no way to reach
    // another workspace's profile — which is why these two are safe under
    // Settings.* rather than needing the platform guard above.
    // =================================================================

    [HttpGet("company-profile")]
    [Authorize(Policy = Policies.SettingsRead)]
    public async Task<ActionResult<CompanyProfileDto>> GetCompanyProfile(CancellationToken ct)
    {
        var tenantId = _currentUserService.GetCurrentTenantId();

        try
        {
            var result = await _getCompanyProfileHandler.HandleAsync(tenantId, ct);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get company profile for {TenantId}", tenantId);
            return StatusCode(500, new { error = "Failed to retrieve the company profile" });
        }
    }

    [HttpPut("company-profile")]
    [Authorize(Policy = Policies.SettingsUpdate)]
    public async Task<IActionResult> UpdateCompanyProfile(
        [FromBody] UpdateCompanyProfileCommand cmd,
        CancellationToken ct)
    {
        var tenantId = _currentUserService.GetCurrentTenantId();

        try
        {
            // Who changed it, resolved server-side. Never from the body —
            // the DTO carries UpdatedBy for the handler's convenience, not
            // as something a caller gets to assert about themselves.
            string? updatedBy = null;
            try
            {
                var user = await _currentUserService.GetCurrentUserAsync();
                updatedBy = user?.FullName;
            }
            catch
            {
                // For the audit row only. Never fail a save over a name.
            }

            await _updateCompanyProfileHandler.HandleAsync(
                tenantId, cmd with { UpdatedBy = updatedBy }, ct);

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
            _logger.LogError(ex, "Failed to update company profile for {TenantId}", tenantId);
            return StatusCode(500, new { error = "Failed to save the company profile" });
        }
    }
}
