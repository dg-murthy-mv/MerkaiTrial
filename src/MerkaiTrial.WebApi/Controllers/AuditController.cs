// =====================================================================
// AuditController.cs
// Location: MerkaiTrial.WebApi/Controllers/AuditController.cs
//
// NEW FILE. Read-only by design — nothing edits or deletes an audit row
// through the API. An audit trail that can be altered from the product
// it audits is not an audit trail.
// =====================================================================

using MerkaiTrial.Application.Queries;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers;

[ApiController]
[Authorize]
[Route("api/audit")]
public class AuditController : ControllerBase
{
    private readonly GetAuditLogsHandler _getLogs;
    private readonly GetAuditFiltersHandler _getFilters;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuthorizationService _auth;
    private readonly ILogger<AuditController> _logger;

    public AuditController(
        GetAuditLogsHandler getLogs,
        GetAuditFiltersHandler getFilters,
        ICurrentUserService currentUser,
        IAuthorizationService auth,
        ILogger<AuditController> logger)
    {
        _getLogs     = getLogs;
        _getFilters  = getFilters;
        _currentUser = currentUser;
        _auth        = auth;
        _logger      = logger;
    }

    // GET api/audit
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? search = null,
        [FromQuery] string? entityType = null,
        [FromQuery] string? action = null,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        if (!await CanReadAsync()) return Forbid();

        try
        {
            // Tenant comes from the signed-in user, never the request.
            // Without this, any authenticated user could read another
            // tenant's entire history — AuditLogs has no query filter.
            var tenantId = _currentUser.GetCurrentTenantId();

            var result = await _getLogs.Handle(
                new AuditLogFilter(tenantId, search, entityType, action, fromUtc, toUtc, page, pageSize), ct);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading audit log");
            return StatusCode(500, new { error = "Something went wrong. Please try again." });
        }
    }

    // GET api/audit/filters
    [HttpGet("filters")]
    public async Task<IActionResult> GetFilters(CancellationToken ct)
    {
        if (!await CanReadAsync()) return Forbid();

        try
        {
            var tenantId = _currentUser.GetCurrentTenantId();
            var (entityTypes, actions) = await _getFilters.Handle(tenantId, ct);
            return Ok(new { entityTypes, actions });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading audit filters");
            return StatusCode(500, new { error = "Something went wrong. Please try again." });
        }
    }

    private async Task<bool> CanReadAsync()
    {
        var result = await _auth.AuthorizeAsync(User, "audit.read");
        return result.Succeeded;
    }
}
