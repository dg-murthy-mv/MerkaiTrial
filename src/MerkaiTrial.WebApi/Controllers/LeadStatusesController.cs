// =====================================================================
// LeadStatusesController.cs
// Location: MerkaiTrial.WebApi/Controllers/LeadStatusesController.cs
//
// NEW FILE. Mirrors PipelineStagesController.
//
// PERMISSIONS: reading is Leads.Read — every lead page needs the status
// list to render a dropdown or a badge. Changing them is Leads.Update,
// because the shape of the lead pipeline is a sales-management decision.
// =====================================================================

using MerkaiTrial.Application.Commands.LeadStatuses;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers;

[ApiController]
[Authorize]
[Route("api/lead-statuses")]
public class LeadStatusesController : ControllerBase
{
    private readonly GetLeadStatusesHandler _get;
    private readonly CreateLeadStatusHandler _create;
    private readonly UpdateLeadStatusDefHandler _update;
    private readonly ReorderLeadStatusesHandler _reorder;
    private readonly SetDefaultLeadStatusHandler _setDefault;
    private readonly DeleteLeadStatusHandler _delete;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuthorizationService _auth;
    private readonly ILogger<LeadStatusesController> _logger;

    public LeadStatusesController(
        GetLeadStatusesHandler get,
        CreateLeadStatusHandler create,
        UpdateLeadStatusDefHandler update,
        ReorderLeadStatusesHandler reorder,
        SetDefaultLeadStatusHandler setDefault,
        DeleteLeadStatusHandler delete,
        ICurrentUserService currentUser,
        IAuthorizationService auth,
        ILogger<LeadStatusesController> logger)
    {
        _get         = get;
        _create      = create;
        _update      = update;
        _reorder     = reorder;
        _setDefault  = setDefault;
        _delete      = delete;
        _currentUser = currentUser;
        _auth        = auth;
        _logger      = logger;
    }

    /// <param name="selectableOnly">
    /// True for dropdowns — excludes retired statuses and the system
    /// Converted status, which is set by conversion and never chosen.
    /// False for settings and for resolving an existing lead's status.
    /// </param>
    [HttpGet]
    public Task<IActionResult> Get([FromQuery] bool selectableOnly = false, CancellationToken ct = default)
        => Run("Read", async () =>
        {
            var tenantId = _currentUser.GetCurrentTenantId();
            return Ok(await _get.Handle(tenantId, selectableOnly, ct));
        }, "reading lead statuses");

    [HttpPost]
    public Task<IActionResult> Create([FromBody] CreateLeadStatusDto dto, CancellationToken ct)
        => Run("Update", async () =>
        {
            var (tenantId, userId) = Identity();
            return Ok(await _create.Handle(dto with { TenantId = tenantId, CreatedBy = userId }, ct));
        }, "creating lead status");

    [HttpPut("{id:guid}")]
    public Task<IActionResult> Update(Guid id, [FromBody] UpdateLeadStatusDefDto dto, CancellationToken ct)
        => Run("Update", async () =>
        {
            var (tenantId, userId) = Identity();
            await _update.Handle(dto with { TenantId = tenantId, StatusId = id, UpdatedBy = userId }, ct);
            return Ok();
        }, "updating lead status");

    [HttpPost("reorder")]
    public Task<IActionResult> Reorder([FromBody] ReorderLeadStatusesDto dto, CancellationToken ct)
        => Run("Update", async () =>
        {
            var (tenantId, userId) = Identity();
            await _reorder.Handle(dto with { TenantId = tenantId, UpdatedBy = userId }, ct);
            return Ok();
        }, "reordering lead statuses");

    [HttpPost("{id:guid}/default")]
    public Task<IActionResult> SetDefault(Guid id, CancellationToken ct)
        => Run("Update", async () =>
        {
            var (tenantId, userId) = Identity();
            await _setDefault.Handle(tenantId, id, userId, ct);
            return Ok();
        }, "setting default lead status");

    [HttpDelete("{id:guid}")]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => Run("Delete", async () =>
        {
            var tenantId = _currentUser.GetCurrentTenantId();
            await _delete.Handle(tenantId, id, ct);
            return Ok();
        }, "deleting lead status");

    // =================================================================

    private (Guid TenantId, string UserId) Identity()
        => (_currentUser.GetCurrentTenantId(), _currentUser.GetCurrentUserId().ToString());

    private async Task<IActionResult> Run(string action, Func<Task<IActionResult>> body, string what)
    {
        var allowed = await _auth.AuthorizeAsync(User, $"Leads.{action}");
        if (!allowed.Succeeded) return Forbid();

        try
        {
            return await body();
        }
        catch (KeyNotFoundException ex)      { return NotFound(new { error = ex.Message }); }
        // The handlers' refusals are written for the user to read — "This is
        // your only qualified status" is more use than "Bad Request".
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (UnauthorizedAccessException)  { return Unauthorized(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error {What}", what);
            return StatusCode(500, new { error = "Something went wrong. Please try again." });
        }
    }
}
