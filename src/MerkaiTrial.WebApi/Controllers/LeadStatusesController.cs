// =====================================================================
// LeadStatusesController.cs
// Location: MerkaiTrial.WebApi/Controllers/LeadStatusesController.cs
//
// NEW FILE. Mirrors PipelineStagesController.
//
// PERMISSIONS (024 — this is the change)
//
//   READING is leads.read. Unchanged, and it must stay that way: every
//   lead page needs the status list to render a dropdown or a badge.
//
//   WRITING is settings.*. It was leads.update — the permission every
//   Sales Rep needs to work a lead — so any rep could rename the
//   statuses, reorder them, change which one new leads start in, and
//   rewrite the SCORE each one is worth. Lead scoring orders every rep's
//   queue, not just their own, which makes this the quieter of the two
//   holes and the more unpleasant one.
//
//   Unlike the pipeline pages, nothing hid this: the Lead Statuses link
//   was visible in the sidebar to any rep, and the page let them save.
// =====================================================================

using MerkaiTrial.Application.Authorization;
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
    private readonly MoveStatusLeadsHandler _moveLeads;          // 025
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
        MoveStatusLeadsHandler moveLeads,                        // 025
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
        _moveLeads   = moveLeads;
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
    /// <param name="detail">
    /// The TRUE lead counts and the delete/retire reasons. Costs an extra
    /// round trip, so only the settings page asks for it — every lead page
    /// and list reads this endpoint for a dropdown or a badge and must not
    /// pay for numbers it will not display.
    /// </param>
    [HttpGet]
    public Task<IActionResult> Get(
        [FromQuery] bool selectableOnly = false,
        [FromQuery] bool detail = false,
        CancellationToken ct = default)
        => Run(Policies.LeadsRead, async () =>
        {
            var tenantId = _currentUser.GetCurrentTenantId();
            return Ok(await _get.Handle(tenantId, selectableOnly, detail, ct));
        }, "reading lead statuses");

    [HttpPost]
    public Task<IActionResult> Create([FromBody] CreateLeadStatusDto dto, CancellationToken ct)
        => Run(Policies.SettingsUpdate, async () =>
        {
            var (tenantId, userId) = Identity();
            return Ok(await _create.Handle(dto with { TenantId = tenantId, CreatedBy = userId }, ct));
        }, "creating lead status");

    [HttpPut("{id:guid}")]
    public Task<IActionResult> Update(Guid id, [FromBody] UpdateLeadStatusDefDto dto, CancellationToken ct)
        => Run(Policies.SettingsUpdate, async () =>
        {
            var (tenantId, userId) = Identity();
            await _update.Handle(dto with { TenantId = tenantId, StatusId = id, UpdatedBy = userId }, ct);
            return Ok();
        }, "updating lead status");

    [HttpPost("reorder")]
    public Task<IActionResult> Reorder([FromBody] ReorderLeadStatusesDto dto, CancellationToken ct)
        => Run(Policies.SettingsUpdate, async () =>
        {
            var (tenantId, userId) = Identity();
            await _reorder.Handle(dto with { TenantId = tenantId, UpdatedBy = userId }, ct);
            return Ok();
        }, "reordering lead statuses");

    [HttpPost("{id:guid}/default")]
    public Task<IActionResult> SetDefault(Guid id, CancellationToken ct)
        => Run(Policies.SettingsUpdate, async () =>
        {
            var (tenantId, userId) = Identity();
            await _setDefault.Handle(tenantId, id, userId, ct);
            return Ok();
        }, "setting default lead status");

    /// <summary>
    /// Sends every lead in this status to another status of the same
    /// kind, and optionally retires this one afterwards.
    ///
    /// settings.update, not settings.delete: it deletes nothing. It
    /// rewrites the status of every lead in one status, which is the
    /// largest thing settings.update can be asked to do — and requiring
    /// delete would mean someone allowed to reshape the lead pipeline
    /// could not empty a status in order to do so.
    /// </summary>
    [HttpPost("{id:guid}/move-leads")]
    public Task<IActionResult> MoveLeads(Guid id, [FromBody] MoveStatusLeadsDto dto, CancellationToken ct)
        => Run(Policies.SettingsUpdate, async () =>
        {
            var (tenantId, userId) = Identity();
            var result = await _moveLeads.Handle(
                dto with { TenantId = tenantId, FromStatusId = id, MovedBy = userId }, ct);
            return Ok(result);
        }, "moving leads between statuses");

    [HttpDelete("{id:guid}")]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => Run(Policies.SettingsDelete, async () =>
        {
            var tenantId = _currentUser.GetCurrentTenantId();
            await _delete.Handle(tenantId, id, ct);
            return Ok();
        }, "deleting lead status");

    // =================================================================

    private (Guid TenantId, string UserId) Identity()
        => (_currentUser.GetCurrentTenantId(), _currentUser.GetCurrentUserId().ToString());

    /// <summary>
    /// 024: takes the whole policy name rather than an action appended to
    /// a hardcoded "Leads.". Reading and writing this controller are now
    /// two different modules, so there is no single prefix to assume —
    /// and a helper that builds the module name for you is how every
    /// endpoint in a file silently inherits the wrong one.
    /// </summary>
    private async Task<IActionResult> Run(string policy, Func<Task<IActionResult>> body, string what)
    {
        var allowed = await _auth.AuthorizeAsync(User, policy);
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
