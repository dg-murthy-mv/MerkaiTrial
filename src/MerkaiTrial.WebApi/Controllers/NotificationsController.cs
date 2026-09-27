// =====================================================================
// NotificationsController.cs
// Location: MerkaiTrial.WebApi/Controllers/NotificationsController.cs
//
// NEW FILE (037).
//
//   GET  api/notifications/summary            bell: count + newest 6
//   GET  api/notifications                    the page, paged
//   POST api/notifications/{id}/read          returns the item, for its link
//   POST api/notifications/read-all
//   POST api/notifications/{id}/dismiss
//   POST api/notifications/dismiss-read
//
// [Authorize] ONLY — NO MODULE POLICY. Deliberate, and the one design
// decision in this file worth arguing about.
//
//   Notifications are personal. A user whose role grants almost nothing
//   still needs to be told when an approval is waiting on them — in fact
//   they need it most, because they have no list page to go and check.
//   Gating the bell behind, say, Quotes.Read would hide it from exactly
//   those people.
//
//   The protection is not a policy, it is scope: every handler filters on
//   RecipientUserId == the caller. There is no route that takes a user id,
//   so there is nothing to tamper with, and no admin override — a workspace
//   admin who needs to know what happened has the audit log.
//
// The summary endpoint is called by every open browser every 45 seconds,
// which makes it the busiest route in the app. It is two indexed counts and
// a Take(6); keep it that way.
// =====================================================================

using MerkaiTrial.Application.Commands.Notifications;
using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers;

[ApiController]
[Authorize]
[Route("api/notifications")]
public class NotificationsController : ControllerBase
{
    private readonly GetNotificationSummaryHandler _summary;
    private readonly GetNotificationsHandler _list;
    private readonly MarkNotificationReadHandler _markRead;
    private readonly MarkAllNotificationsReadHandler _markAllRead;
    private readonly DismissNotificationHandler _dismiss;
    private readonly DismissReadNotificationsHandler _dismissRead;
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(
        GetNotificationSummaryHandler summary,
        GetNotificationsHandler list,
        MarkNotificationReadHandler markRead,
        MarkAllNotificationsReadHandler markAllRead,
        DismissNotificationHandler dismiss,
        DismissReadNotificationsHandler dismissRead,
        ILogger<NotificationsController> logger)
    {
        _summary = summary;
        _list = list;
        _markRead = markRead;
        _markAllRead = markAllRead;
        _dismiss = dismiss;
        _dismissRead = dismissRead;
        _logger = logger;
    }

    // ── The bell ──────────────────────────────────────────────────────

    [HttpGet("summary")]
    public Task<IActionResult> Summary(CancellationToken ct)
        => Run(async () => Ok(await _summary.Handle(ct)), "reading the notification summary");

    // ── The page ──────────────────────────────────────────────────────

    [HttpGet]
    public Task<IActionResult> List(
        [FromQuery] bool unreadOnly = false,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 25,
        CancellationToken ct = default)
        => Run(async () => Ok(await _list.Handle(unreadOnly, skip, take, ct)),
               "reading notifications");

    // ── Read ──────────────────────────────────────────────────────────

    /// <summary>
    /// Marks one read and returns it, so the caller can follow its link.
    /// A notification that is not this person's returns 204 rather than 404:
    /// clicking a stale item from a tab left open overnight should quietly
    /// do nothing, not show an error page.
    /// </summary>
    [HttpPost("{id:guid}/read")]
    public Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
        => Run(async () =>
        {
            var dto = await _markRead.Handle(id, ct);
            return dto is null ? NoContent() : Ok(dto);
        }, "marking a notification read");

    [HttpPost("read-all")]
    public Task<IActionResult> MarkAllRead(CancellationToken ct)
        => Run(async () => Ok(new { marked = await _markAllRead.Handle(ct) }),
               "marking all notifications read");

    // ── Dismiss ───────────────────────────────────────────────────────

    [HttpPost("{id:guid}/dismiss")]
    public Task<IActionResult> Dismiss(Guid id, CancellationToken ct)
        => Run(async () =>
        {
            await _dismiss.Handle(id, ct);
            return Ok();
        }, "dismissing a notification");

    [HttpPost("dismiss-read")]
    public Task<IActionResult> DismissRead(CancellationToken ct)
        => Run(async () => Ok(new { dismissed = await _dismissRead.Handle(ct) }),
               "clearing read notifications");

    // ── Plumbing ──────────────────────────────────────────────────────

    // Same shape as QuoteApprovalsController: refusals become 400 { error },
    // which the Admin.Web ApiService turns into InvalidOperationException
    // carrying the message, so a page can show it unchanged.
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
