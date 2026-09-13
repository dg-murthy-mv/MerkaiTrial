// =====================================================================
// ActivitiesController.cs
// Location: MerkaiTrial.WebAPI/Controllers/ActivitiesController.cs
//
// COMPLETE FILE — replaces the existing one.
//
// PERMISSION MODEL (agreed this round)
//
//   Activities, tasks and notes are NOT their own module. They inherit
//   from the record they hang off:
//
//     Read them          -> Leads.Read   / Deals.Read   / Contacts.Read
//     Add one            -> Leads.Update / Deals.Update / Contacts.Update
//     Complete a task    -> the above, OR being the assignee
//     Edit YOUR OWN      -> the Update permission
//     Edit SOMEONE ELSE'S-> tenant admin only
//     DELETE anything    -> Leads.Delete / Deals.Delete / Contacts.Delete
//                           AND (your own, or tenant admin)
//
//   Why delete is stricter than edit: erasing the record of a call is a
//   different kind of act from adding to it. Someone who can edit a lead
//   should not be able to quietly remove its history.
//
//   Why editing someone else's needs admin: rewriting another rep's
//   record of what they did is not the same as correcting your own typo.
//
// Tenant and user always come from the signed-in principal, never from
// the request body or query string.
// =====================================================================

using MerkaiTrial.Application.Commands.Activities;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebAPI.Controllers;

[ApiController]
[Authorize]
[Route("api/activities")]
public class ActivitiesController : ControllerBase
{
    private readonly CreateActivityHandler       _create;
    private readonly GetActivitiesHandler        _getForEntity;
    private readonly GetUpcomingTasksHandler     _getUpcoming;
    private readonly CompleteActivityHandler     _complete;
    private readonly SetActivityOutcomeHandler   _setOutcome;
    private readonly UpdateActivityHandler       _update;
    private readonly ReassignActivityHandler     _reassign;
    private readonly DeleteActivityHandler       _delete;
    private readonly GetActivityAccessHandler    _access;
    private readonly GetAssigneesHandler         _assignees;
    private readonly ICurrentUserService         _currentUser;
    private readonly IAuthorizationService       _auth;
    private readonly ILogger<ActivitiesController> _logger;

    public ActivitiesController(
        CreateActivityHandler     create,
        GetActivitiesHandler      getForEntity,
        GetUpcomingTasksHandler   getUpcoming,
        CompleteActivityHandler   complete,
        SetActivityOutcomeHandler setOutcome,
        UpdateActivityHandler     update,
        ReassignActivityHandler   reassign,
        DeleteActivityHandler     delete,
        GetActivityAccessHandler  access,
        GetAssigneesHandler       assignees,
        ICurrentUserService       currentUser,
        IAuthorizationService     auth,
        ILogger<ActivitiesController> logger)
    {
        _create       = create;
        _getForEntity = getForEntity;
        _getUpcoming  = getUpcoming;
        _complete     = complete;
        _setOutcome   = setOutcome;
        _update       = update;
        _reassign     = reassign;
        _delete       = delete;
        _access       = access;
        _assignees    = assignees;
        _currentUser  = currentUser;
        _auth         = auth;
        _logger       = logger;
    }

    // =================================================================
    // ASSIGNEES
    // =================================================================

    /// <summary>Active users in this workspace, for "assign to" dropdowns.</summary>
    [HttpGet("assignees")]
    public Task<IActionResult> GetAssignees(CancellationToken ct)
        => Run(async () =>
        {
            // Anyone who can see leads or deals can assign work on them.
            if (!await CanAsync(ActivityEntityType.Lead, "Read") &&
                !await CanAsync(ActivityEntityType.Deal, "Read"))
                return Forbid();

            var tenantId = _currentUser.GetCurrentTenantId();
            return Ok(await _assignees.Handle(tenantId, ct));
        }, "getting assignees");

    // =================================================================
    // CREATE / READ
    // =================================================================

    [HttpPost]
    public Task<IActionResult> Create([FromBody] CreateActivityDto dto, CancellationToken ct)
        => Run(async () =>
        {
            if (!ActivityEntityType.IsValid(dto.EntityType))
                return BadRequest(new { error = $"Unknown record type '{dto.EntityType}'." });

            if (!await CanAsync(dto.EntityType, "Update")) return Forbid();

            var (tenantId, userId) = Identity();
            var result = await _create.Handle(dto with { TenantId = tenantId, CreatedBy = userId }, ct);
            return Ok(result);
        }, "creating activity");

    [HttpGet]
    public Task<IActionResult> GetForEntity(
        [FromQuery] string entityType,
        [FromQuery] Guid   entityId,
        [FromQuery] bool   includeCompleted = true,
        [FromQuery] bool   tasksOnly        = false,
        CancellationToken  ct               = default)
        => Run(async () =>
        {
            if (!ActivityEntityType.IsValid(entityType))
                return BadRequest(new { error = $"Unknown record type '{entityType}'." });

            if (!await CanAsync(entityType, "Read")) return Forbid();

            var (tenantId, _) = Identity();
            var result = await _getForEntity.Handle(
                new GetActivitiesQuery(tenantId, entityType, entityId, includeCompleted, tasksOnly), ct);
            return Ok(result);
        }, "getting activities");

    [HttpGet("upcoming-tasks")]
    public Task<IActionResult> GetUpcomingTasks(
        [FromQuery] int     daysAhead        = 7,
        [FromQuery] string? assignedToUserId = null,
        CancellationToken   ct               = default)
        => Run(async () =>
        {
            var me = await _currentUser.GetCurrentUserAsync();

            // Non-admins see only their own tasks, whatever they ask for.
            // Silent rather than an error, same as ResolveTenant().
            var assignee = me.IsTenantAdmin ? assignedToUserId : me.UserId.ToString();

            var result = await _getUpcoming.Handle(
                new GetUpcomingTasksQuery(me.TenantId, assignee, Math.Clamp(daysAhead, 0, 90)), ct);
            return Ok(result);
        }, "getting tasks");

    // =================================================================
    // COMPLETE / OUTCOME
    // =================================================================

    [HttpPost("{id:guid}/complete")]
    public Task<IActionResult> Complete(Guid id, [FromBody] CompleteActivityDto dto, CancellationToken ct)
        => Run(async () =>
        {
            var (tenantId, userId) = Identity();

            var denied = await DenyUnlessAssigneeOrCanUpdateAsync(tenantId, id, userId, ct);
            if (denied != null) return denied;

            await _complete.Handle(dto with { TenantId = tenantId, ActivityId = id, CompletedBy = userId }, ct);
            return Ok();
        }, "completing activity");

    /// <summary>"How did it go?" — added after the fact, so completing is one click.</summary>
    [HttpPost("{id:guid}/outcome")]
    public Task<IActionResult> SetOutcome(Guid id, [FromBody] SetActivityOutcomeDto dto, CancellationToken ct)
        => Run(async () =>
        {
            var (tenantId, userId) = Identity();

            // Recording how your own task went is not "editing someone
            // else's record" — the assignee may always add an outcome.
            var denied = await DenyUnlessAssigneeOrCanUpdateAsync(tenantId, id, userId, ct);
            if (denied != null) return denied;

            await _setOutcome.Handle(dto with { TenantId = tenantId, ActivityId = id, UpdatedBy = userId }, ct);
            return Ok();
        }, "setting outcome");

    // =================================================================
    // EDIT / REASSIGN / DELETE
    // =================================================================

    [HttpPut("{id:guid}")]
    public Task<IActionResult> Update(Guid id, [FromBody] UpdateActivityDto dto, CancellationToken ct)
        => Run(async () =>
        {
            var (tenantId, userId) = Identity();

            var denied = await DenyUnlessOwnOrAdminAsync(tenantId, id, userId, "Update", ct);
            if (denied != null) return denied;

            await _update.Handle(dto with { TenantId = tenantId, ActivityId = id, UpdatedBy = userId }, ct);
            return Ok();
        }, "updating activity");

    [HttpPost("{id:guid}/reassign")]
    public Task<IActionResult> Reassign(Guid id, [FromBody] ReassignActivityDto dto, CancellationToken ct)
        => Run(async () =>
        {
            var (tenantId, userId) = Identity();

            var info = await _access.Handle(tenantId, id, ct);
            if (info is null) return NotFound(new { error = "Task not found." });

            if (!await CanAsync(info.EntityType, "Update")) return Forbid();

            // Handing YOUR task to someone else is normal delegation.
            // Taking someone else's task off them is a manager's action.
            var me = await _currentUser.GetCurrentUserAsync();
            var isMine = string.Equals(info.AssignedToUserId, userId, StringComparison.OrdinalIgnoreCase)
                      || string.Equals(info.CreatedBy, userId, StringComparison.OrdinalIgnoreCase);

            if (!isMine && !me.IsTenantAdmin)
                return Forbid();

            await _reassign.Handle(dto with { TenantId = tenantId, ActivityId = id, UpdatedBy = userId }, ct);
            return Ok();
        }, "reassigning task");

    [HttpDelete("{id:guid}")]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => Run(async () =>
        {
            var (tenantId, userId) = Identity();

            // Deleting history needs DELETE on the parent record, not Update.
            var denied = await DenyUnlessOwnOrAdminAsync(tenantId, id, userId, "Delete", ct);
            if (denied != null) return denied;

            await _delete.Handle(tenantId, id, userId, ct);
            return Ok();
        }, "deleting activity");

    // =================================================================
    // HELPERS
    // =================================================================

    private (Guid TenantId, string UserId) Identity()
        => (_currentUser.GetCurrentTenantId(), _currentUser.GetCurrentUserId().ToString());

    private static string ModuleFor(string entityType) => entityType switch
    {
        ActivityEntityType.Lead    => "Leads",
        ActivityEntityType.Deal    => "Deals",
        // No separate Companies module in the permission catalogue.
        ActivityEntityType.Contact => "Contacts",
        ActivityEntityType.Company => "Contacts",
        _ => throw new InvalidOperationException($"Unknown record type '{entityType}'.")
    };

    private async Task<bool> CanAsync(string entityType, string action)
    {
        if (!ActivityEntityType.IsValid(entityType)) return false;
        var result = await _auth.AuthorizeAsync(User, $"{ModuleFor(entityType)}.{action}");
        return result.Succeeded;
    }

    /// <summary>For completing and adding an outcome.</summary>
    private async Task<IActionResult?> DenyUnlessAssigneeOrCanUpdateAsync(
        Guid tenantId, Guid id, string userId, CancellationToken ct)
    {
        var info = await _access.Handle(tenantId, id, ct);
        if (info is null) return NotFound(new { error = "Activity not found." });

        if (string.Equals(info.AssignedToUserId, userId, StringComparison.OrdinalIgnoreCase))
            return null;

        return await CanAsync(info.EntityType, "Update") ? null : Forbid();
    }

    /// <summary>
    /// For editing and deleting. Needs the permission for the action, AND
    /// either authorship of the entry or tenant admin.
    /// </summary>
    private async Task<IActionResult?> DenyUnlessOwnOrAdminAsync(
        Guid tenantId, Guid id, string userId, string action, CancellationToken ct)
    {
        var info = await _access.Handle(tenantId, id, ct);
        if (info is null) return NotFound(new { error = "Activity not found." });

        if (!await CanAsync(info.EntityType, action)) return Forbid();

        if (string.Equals(info.CreatedBy, userId, StringComparison.OrdinalIgnoreCase))
            return null;

        var me = await _currentUser.GetCurrentUserAsync();
        if (me.IsTenantAdmin) return null;

        return Forbid();
    }

    private async Task<IActionResult> Run(Func<Task<IActionResult>> action, string what)
    {
        try
        {
            return await action();
        }
        catch (KeyNotFoundException ex)       { return NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex)  { return BadRequest(new { error = ex.Message }); }
        catch (UnauthorizedAccessException)   { return Unauthorized(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error {What}", what);
            return StatusCode(500, new { error = "Something went wrong. Please try again." });
        }
    }
}
