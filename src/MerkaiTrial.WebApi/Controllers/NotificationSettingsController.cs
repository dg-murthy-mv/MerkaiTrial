// =====================================================================
// NotificationSettingsController.cs
// Location: MerkaiTrial.WebApi/Controllers/NotificationSettingsController.cs
//
// NEW FILE (038).
//
//   GET  api/notification-settings/preferences      anyone signed in
//   PUT  api/notification-settings/preferences      anyone signed in
//
//   GET  api/notification-settings/defaults         workspace admin only
//   PUT  api/notification-settings/defaults         workspace admin only
//
//   GET  api/notification-settings/outbound         workspace admin only
//   POST api/notification-settings/outbound/{id}/retry    admin only
//   POST api/notification-settings/outbound/{id}/cancel   admin only
//
// TWO DIFFERENT SCOPES IN ONE CONTROLLER, ON PURPOSE
//   They are the two tabs of one settings page, so one controller keeps
//   the feature together. The authorisation is not uniform and should not
//   be:
//
//   Preferences are PERSONAL — [Authorize] only, no module policy, same
//   reasoning as 037's notifications controller. A read-only user still
//   needs to choose whether approval requests reach them by email, and
//   gating that behind a module permission would deny exactly the people
//   who most need it. There is no user id in any route, and every handler
//   scopes to the caller, so there is nothing to tamper with.
//
//   The outbound log is the WORKSPACE's outgoing mail — every colleague's
//   notification emails, with subjects. That is an admin screen, checked
//   in the handler rather than by policy because "workspace admin" is a
//   property of the user row, not a module permission.
// =====================================================================

using MerkaiTrial.Application.Commands.Notifications;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers;

[ApiController]
[Authorize]
[Route("api/notification-settings")]
public class NotificationSettingsController : ControllerBase
{
    private readonly GetNotificationPreferencesHandler _getPrefs;
    private readonly SaveNotificationPreferencesHandler _savePrefs;
    private readonly GetTenantNotificationDefaultsHandler _getDefaults;   // 040
    private readonly SaveTenantNotificationDefaultsHandler _saveDefaults; // 040
    private readonly GetOutboundMessagesHandler _getOutbound;
    private readonly RetryOutboundMessageHandler _retry;
    private readonly CancelOutboundMessageHandler _cancel;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<NotificationSettingsController> _logger;

    public NotificationSettingsController(
        GetNotificationPreferencesHandler getPrefs,
        SaveNotificationPreferencesHandler savePrefs,
        GetTenantNotificationDefaultsHandler getDefaults,
        SaveTenantNotificationDefaultsHandler saveDefaults,
        GetOutboundMessagesHandler getOutbound,
        RetryOutboundMessageHandler retry,
        CancelOutboundMessageHandler cancel,
        ICurrentUserService currentUser,
        ILogger<NotificationSettingsController> logger)
    {
        _getPrefs = getPrefs;
        _savePrefs = savePrefs;
        _getDefaults = getDefaults;
        _saveDefaults = saveDefaults;
        _getOutbound = getOutbound;
        _retry = retry;
        _cancel = cancel;
        _currentUser = currentUser;
        _logger = logger;
    }

    // ── Preferences: personal ─────────────────────────────────────────

    [HttpGet("preferences")]
    public Task<IActionResult> GetPreferences(CancellationToken ct)
        => Run(async () => Ok(await _getPrefs.Handle(ct)), "reading notification preferences");

    [HttpPut("preferences")]
    public Task<IActionResult> SavePreferences(
        [FromBody] SaveNotificationPreferencesDto dto, CancellationToken ct)
        => Run(async () =>
        {
            await _savePrefs.Handle(dto, ct);
            return Ok();
        }, "saving notification preferences");

    // ── Workspace defaults: admins only (040) ─────────────────────────

    [HttpGet("defaults")]
    public Task<IActionResult> GetDefaults(CancellationToken ct)
        => Run(async () =>
        {
            var denied = await RequireAdminAsync();
            if (denied is not null) return denied;

            return Ok(await _getDefaults.Handle(ct));
        }, "reading the workspace notification defaults");

    [HttpPut("defaults")]
    public Task<IActionResult> SaveDefaults(
        [FromBody] SaveTenantNotificationDefaultsDto dto, CancellationToken ct)
        => Run(async () =>
        {
            var denied = await RequireAdminAsync();
            if (denied is not null) return denied;

            await _saveDefaults.Handle(dto, ct);
            return Ok();
        }, "saving the workspace notification defaults");

    // ── The email log: workspace admins ───────────────────────────────

    [HttpGet("outbound")]
    public Task<IActionResult> GetOutbound(
        [FromQuery] string? status = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 25,
        CancellationToken ct = default)
        => Run(async () =>
        {
            var denied = await RequireAdminAsync();
            if (denied is not null) return denied;

            return Ok(await _getOutbound.Handle(status, skip, take, ct));
        }, "reading the email log");

    [HttpPost("outbound/{id:guid}/retry")]
    public Task<IActionResult> Retry(Guid id, CancellationToken ct)
        => Run(async () =>
        {
            var denied = await RequireAdminAsync();
            if (denied is not null) return denied;

            await _retry.Handle(id, ct);
            return Ok();
        }, "retrying a message");

    [HttpPost("outbound/{id:guid}/cancel")]
    public Task<IActionResult> Cancel(Guid id, CancellationToken ct)
        => Run(async () =>
        {
            var denied = await RequireAdminAsync();
            if (denied is not null) return denied;

            await _cancel.Handle(id, ct);
            return Ok();
        }, "cancelling a message");

    // ── Plumbing ──────────────────────────────────────────────────────

    /// <summary>
    /// Null when they may proceed, otherwise the 403 to return. Checked
    /// here rather than with a policy because workspace-admin is a property
    /// of the user row, not a module permission.
    /// </summary>
    private async Task<IActionResult?> RequireAdminAsync()
    {
        var me = await _currentUser.GetCurrentUserAsync();

        return me.IsTenantAdmin
            ? null
            : StatusCode(403, new
            {
                error = "Only workspace admins can change notification settings for the workspace."
            });
    }

    private async Task<IActionResult> Run(Func<Task<IActionResult>> body, string what)
    {
        try
        {
            return await body();
        }
        catch (KeyNotFoundException ex)        { return NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex)   { return BadRequest(new { error = ex.Message }); }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error {What}", what);
            return StatusCode(500, new { error = "Something went wrong. Please try again." });
        }
    }
}
