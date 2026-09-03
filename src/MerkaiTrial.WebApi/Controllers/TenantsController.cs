// =====================================================================
// TENANTS CONTROLLER - Complete with Users Endpoint
// Location: MerkaiTrial.WebApi/Controllers/TenantsController.cs
// =====================================================================

using MerkaiTrial.Application.Commands.Tenants;
using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers;

[ApiController]
[Route("api/tenants")]
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
        _logger = logger;
    }

    // ==================== GET PAGINATED TENANTS ====================
    [HttpGet("paginated")]
    public async Task<ActionResult<PaginatedTenantsResponse>> GetPaginated(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? search = null,
        [FromQuery] bool? isActive = null)
    {
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
    [HttpGet("{id:guid}/settings")]
    public async Task<ActionResult<TenantSettingsDto>> GetSettings([FromRoute] Guid id)
    {
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
    [HttpPut("{id:guid}/settings")]
    public async Task<IActionResult> UpdateSettings([FromRoute] Guid id, [FromBody] UpdateTenantSettingsCommand cmd)
    {
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

    // ==================== GET TENANT USERS (NEW!) ====================
    [HttpGet("{id:guid}/users")]
    public async Task<ActionResult<List<UserListItem>>> GetUsers([FromRoute] Guid id)
    {
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
    [HttpGet("lookup")]
    public async Task<ActionResult<List<TenantLookupDto>>> GetLookup()
    {
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

    // ==================== GET COUNTRIES FOR DROPDOWN (NEW!) ====================
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

    // ==================== GET TIMEZONES (NEW!) ====================
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

    // ==================== GET PLANS (NEW!) ====================
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
}
