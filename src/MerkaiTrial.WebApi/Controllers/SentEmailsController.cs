// =====================================================================
// SentEmailsController.cs
// Location: MerkaiTrial.WebApi/Controllers/SentEmailsController.cs
//
// NEW FILE (085). What Admin.Web calls for email WE sent — the twin of
// InboundEmailsController. Every route needs a signed-in user; the
// workspace comes from the TOKEN, never the query string.
//
//   GET  api/sent-emails/for-entity/{quote|invoice}/{id}   the document's card
//   GET  api/sent-emails/for/{deal|contact|lead}/{id}      a record's Emails tab
//   GET  api/sent-emails/{id}                              one email in full
//   POST api/sent-emails/{id}/retry                        put a failed one back in the queue
//
// WHY NO [Authorize(Policy = …)] ON THE ROUTES
//   Who may see an email depends on WHAT it is about: a quote email needs
//   quotes.read and that quote visible, an invoice email invoices.read and
//   that invoice visible, our own notification emails workspace admin.
//   No static attribute can say that, so the caller's permissions are
//   worked out once per request (AccessAsync) and SentEmailHandlers
//   applies them. Anything the caller may not see is 404, never 403, so a
//   403 never confirms an email exists.
//
// ERRORS reach the page as { error } — IApiService turns a 400/403 with
// that shape into an exception carrying the sentence.
// =====================================================================

using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Emails;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
    [Route("api/sent-emails")]
    [Authorize]
    public class SentEmailsController : ControllerBase
    {
        private readonly GetSentEmailsForEntityHandler _forEntity;
        private readonly GetSentEmailsForRecordHandler _forRecord;
        private readonly GetSentEmailHandler _get;
        private readonly RetrySentEmailHandler _retry;
        private readonly IAuthorizationService _authorization;
        private readonly ICurrentUserService _currentUser;
        private readonly ILogger<SentEmailsController> _logger;

        public SentEmailsController(
            GetSentEmailsForEntityHandler forEntity,
            GetSentEmailsForRecordHandler forRecord,
            GetSentEmailHandler get,
            RetrySentEmailHandler retry,
            IAuthorizationService authorization,
            ICurrentUserService currentUser,
            ILogger<SentEmailsController> logger)
        {
            _forEntity = forEntity;
            _forRecord = forRecord;
            _get = get;
            _retry = retry;
            _authorization = authorization;
            _currentUser = currentUser;
            _logger = logger;
        }

        [HttpGet("for-entity/{entityType}/{entityId:guid}")]
        public Task<IActionResult> ForEntity(string entityType, Guid entityId, CancellationToken ct = default)
            => Run(async a =>
            {
                var result = await _forEntity.Handle(a, entityType, entityId, ct);
                return result is null
                    ? NotFound(new { error = "That record could not be found." })
                    : Ok(result);
            }, "list a document's emails");

        [HttpGet("for/{entityType}/{entityId:guid}")]
        public Task<IActionResult> ForRecord(string entityType, Guid entityId, CancellationToken ct = default)
            => Run(async a =>
            {
                var result = await _forRecord.Handle(a, entityType, entityId, ct);
                return result is null
                    ? NotFound(new { error = "That record could not be found." })
                    : Ok(result);
            }, "list a record's sent emails");

        [HttpGet("{id:guid}")]
        public Task<IActionResult> Get(Guid id, CancellationToken ct = default)
            => Run(async a =>
            {
                var email = await _get.Handle(a, id, ct);
                return email is null
                    ? NotFound(new { error = "That email could not be found." })
                    : Ok(email);
            }, "load a sent email");

        [HttpPost("{id:guid}/retry")]
        public Task<IActionResult> Retry(Guid id, CancellationToken ct = default)
            => Run(async a =>
            {
                await _retry.Handle(a, id, ct);
                return Ok(new { ok = true });
            }, "send the email again");

        // ── plumbing ──────────────────────────────────────────────────

        private async Task<IActionResult> Run(Func<OutboundAccess, Task<IActionResult>> action, string what)
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

        private async Task<OutboundAccess> AccessAsync(Guid tenantId)
        {
            async Task<bool> Can(string policy) => (await _authorization.AuthorizeAsync(User, policy)).Succeeded;

            var me = await _currentUser.GetCurrentUserAsync();

            return new OutboundAccess(
                TenantId:       tenantId,
                UserId:         me?.UserId ?? Guid.Empty,
                UserName:       me?.FullName,
                IsAdmin:        me?.IsTenantAdmin ?? false,
                QuotesRead:     await Can(Policies.QuotesRead),
                QuotesUpdate:   await Can(Policies.QuotesUpdate),
                InvoicesRead:   await Can(Policies.InvoicesRead),
                InvoicesUpdate: await Can(Policies.InvoicesUpdate),
                DealsRead:      await Can(Policies.DealsRead),
                ContactsRead:   await Can(Policies.ContactsRead),
                LeadsRead:      await Can(Policies.LeadsRead));
        }
    }
}
