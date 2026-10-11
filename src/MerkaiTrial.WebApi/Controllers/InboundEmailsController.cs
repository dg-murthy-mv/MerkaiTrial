// =====================================================================
// InboundEmailsController.cs
// Location: MerkaiTrial.WebApi/Controllers/InboundEmailsController.cs
//
// NEW FILE (083). What Admin.Web calls for received email. Every route
// needs a signed-in user; the workspace comes from the TOKEN, never the
// query string.
//
//   GET    api/inbound-emails?status=&search=&skip=&take=   the Inbox        inbox.read
//   GET    api/inbound-emails/count                          nav badge        inbox.read
//   GET    api/inbound-emails/{id}                           one email        see below
//   GET    api/inbound-emails/for/{entityType}/{entityId}   a record's list  that record's read
//   GET    api/inbound-emails/{id}/link-targets?search=      Link search      inbox.update
//   POST   api/inbound-emails/{id}/link                      file it          inbox.update + target's update
//   POST   api/inbound-emails/{id}/unlink                    back to Inbox    inbox.update
//   POST   api/inbound-emails/{id}/dismiss                   not a customer   inbox.update
//   POST   api/inbound-emails/{id}/restore                   undo dismiss     inbox.update
//   DELETE api/inbound-emails/{id}                           delete           inbox.delete
//   GET    api/inbound-emails/settings                       settings page    settings.read
//   POST   api/inbound-emails/settings/new-address           new address      settings.update
//
// WHY THE PERMISSIONS ARE NOT [Authorize(Policy = …)] ATTRIBUTES
//   Reading one email is allowed by ANY of five permissions depending on
//   where it is filed (see InboundEmailHandlers' header), and a link needs
//   inbox.update AND the target record's update. Neither fits a static
//   attribute. So the caller's permissions are worked out once per request
//   (Access()) and the handler applies them. The routes that DO have one
//   fixed requirement still say so up front with an attribute, so the
//   rule is visible here as well.
//
// ERRORS reach the page as { error } — IApiService turns a 400/403 with
// that shape into an exception carrying the sentence. "Not found" is used
// for an email the caller may not see, so a 403 never confirms one exists.
// =====================================================================

using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.InboundEmails;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
    [Route("api/inbound-emails")]
    [Authorize]
    public class InboundEmailsController : ControllerBase
    {
        private readonly GetInboundEmailsHandler _list;
        private readonly GetInboundCountHandler _count;
        private readonly GetInboundEmailHandler _get;
        private readonly GetRecordEmailsHandler _forRecord;
        private readonly GetInboundLinkTargetsHandler _targets;
        private readonly InboundEmailActionsHandler _actions;
        private readonly InboundEmailSettingsHandler _settings;
        private readonly IAuthorizationService _authorization;
        private readonly ICurrentUserService _currentUser;
        private readonly ILogger<InboundEmailsController> _logger;

        public InboundEmailsController(
            GetInboundEmailsHandler list,
            GetInboundCountHandler count,
            GetInboundEmailHandler get,
            GetRecordEmailsHandler forRecord,
            GetInboundLinkTargetsHandler targets,
            InboundEmailActionsHandler actions,
            InboundEmailSettingsHandler settings,
            IAuthorizationService authorization,
            ICurrentUserService currentUser,
            ILogger<InboundEmailsController> logger)
        {
            _list = list;
            _count = count;
            _get = get;
            _forRecord = forRecord;
            _targets = targets;
            _actions = actions;
            _settings = settings;
            _authorization = authorization;
            _currentUser = currentUser;
            _logger = logger;
        }

        // ── Reading ───────────────────────────────────────────────────

        [HttpGet]
        [Authorize(Policy = Policies.InboxRead)]
        public Task<IActionResult> List(
            [FromQuery] string? status, [FromQuery] string? search,
            [FromQuery] int skip = 0, [FromQuery] int take = 25, CancellationToken ct = default)
            => Run(async a => Ok(await _list.Handle(a, status, search, skip, take, ct)), "list the inbox");

        [HttpGet("count")]
        [Authorize(Policy = Policies.InboxRead)]
        public Task<IActionResult> Count(CancellationToken ct = default)
            => Run(async a => Ok(await _count.Handle(a, ct)), "count the inbox");

        [HttpGet("{id:guid}")]
        public Task<IActionResult> Get(Guid id, CancellationToken ct = default)
            => Run(async a =>
            {
                var email = await _get.Handle(a, id, ct);
                return email is null
                    ? NotFound(new { error = "That email could not be found." })
                    : Ok(email);
            }, "load an email");

        [HttpGet("for/{entityType}/{entityId:guid}")]
        public Task<IActionResult> ForRecord(string entityType, Guid entityId, CancellationToken ct = default)
            => Run(async a =>
            {
                var result = await _forRecord.Handle(a, entityType, entityId, ct);
                return result is null
                    ? NotFound(new { error = "That record could not be found." })
                    : Ok(result);
            }, "list a record's emails");

        [HttpGet("{id:guid}/link-targets")]
        [Authorize(Policy = Policies.InboxUpdate)]
        public Task<IActionResult> LinkTargets(Guid id, [FromQuery] string? search, CancellationToken ct = default)
            => Run(async a => Ok(await _targets.Handle(a, id, search, ct)), "search for a record");

        // ── Filing ────────────────────────────────────────────────────

        [HttpPost("{id:guid}/link")]
        [Authorize(Policy = Policies.InboxUpdate)]
        public Task<IActionResult> Link(Guid id, [FromBody] LinkInboundEmailDto dto, CancellationToken ct = default)
            => Run(async a => { await _actions.LinkAsync(a, id, dto, ct); return Ok(new { ok = true }); }, "file an email");

        [HttpPost("{id:guid}/unlink")]
        [Authorize(Policy = Policies.InboxUpdate)]
        public Task<IActionResult> Unlink(Guid id, CancellationToken ct = default)
            => Run(async a => { await _actions.UnlinkAsync(a, id, ct); return Ok(new { ok = true }); }, "unfile an email");

        [HttpPost("{id:guid}/dismiss")]
        [Authorize(Policy = Policies.InboxUpdate)]
        public Task<IActionResult> Dismiss(Guid id, CancellationToken ct = default)
            => Run(async a => { await _actions.DismissAsync(a, id, ct); return Ok(new { ok = true }); }, "dismiss an email");

        [HttpPost("{id:guid}/restore")]
        [Authorize(Policy = Policies.InboxUpdate)]
        public Task<IActionResult> Restore(Guid id, CancellationToken ct = default)
            => Run(async a => { await _actions.RestoreAsync(a, id, ct); return Ok(new { ok = true }); }, "restore an email");

        [HttpDelete("{id:guid}")]
        [Authorize(Policy = Policies.InboxDelete)]
        public Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
            => Run(async a => { await _actions.DeleteAsync(a, id, ct); return Ok(new { ok = true }); }, "delete an email");

        // ── Settings ──────────────────────────────────────────────────

        [HttpGet("settings")]
        [Authorize(Policy = Policies.SettingsRead)]
        public Task<IActionResult> Settings(CancellationToken ct = default)
            => Run(async a => Ok(await _settings.GetAsync(a, ct)), "load inbound email settings");

        [HttpPost("settings/new-address")]
        [Authorize(Policy = Policies.SettingsUpdate)]
        public Task<IActionResult> NewAddress(CancellationToken ct = default)
            => Run(async a => Ok(await _settings.RegenerateAsync(a, ct)), "change the workspace address");

        // ── plumbing ──────────────────────────────────────────────────

        private async Task<IActionResult> Run(Func<InboundAccess, Task<IActionResult>> action, string what)
        {
            var tenantId = _currentUser.GetCurrentTenantId();

            try
            {
                return await action(await AccessAsync(tenantId));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to {What} for tenant {TenantId}", what, tenantId);
                return StatusCode(500, new { error = $"Could not {what}. Please try again." });
            }
        }

        private async Task<InboundAccess> AccessAsync(Guid tenantId)
        {
            async Task<bool> Can(string policy) => (await _authorization.AuthorizeAsync(User, policy)).Succeeded;

            var me = await _currentUser.GetCurrentUserAsync();

            return new InboundAccess(
                TenantId:       tenantId,
                UserId:         me?.UserId ?? Guid.Empty,
                UserName:       me?.FullName,
                InboxRead:      await Can(Policies.InboxRead),
                InboxUpdate:    await Can(Policies.InboxUpdate),
                InboxDelete:    await Can(Policies.InboxDelete),
                LeadsRead:      await Can(Policies.LeadsRead),
                LeadsUpdate:    await Can(Policies.LeadsUpdate),
                DealsRead:      await Can(Policies.DealsRead),
                DealsUpdate:    await Can(Policies.DealsUpdate),
                ContactsRead:   await Can(Policies.ContactsRead),
                ContactsUpdate: await Can(Policies.ContactsUpdate),
                CompaniesRead:  await Can(Policies.CompaniesRead),
                SettingsRead:   await Can(Policies.SettingsRead),
                SettingsUpdate: await Can(Policies.SettingsUpdate));
        }
    }
}
