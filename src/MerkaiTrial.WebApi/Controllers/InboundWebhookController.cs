// =====================================================================
// InboundWebhookController.cs
// Location: MerkaiTrial.WebApi/Controllers/InboundWebhookController.cs
//
// NEW FILE (083). The address Resend calls when an email arrives —
// and, 085, when something happens to an email we SENT (delivered,
// bounced, marked as spam). Same address, same signature check:
//
//     POST /api/inbound/resend
//
// [AllowAnonymous] — Resend has no login, so this cannot sit behind the
// JWT. It is listed in Program.cs's footer with the other two anonymous
// endpoints. What protects it instead:
//
//   • Resend's signature (svix-* headers), checked against the signing
//     secret BEFORE a byte of the body is believed — see
//     ResendWebhookVerifier. No secret configured = every call refused.
//   • A 256 KB body limit. The event is metadata only (no body, no
//     files), a few KB at most; anything bigger is not Resend.
//   • No tenant from the caller. The workspace comes from the address the
//     email was sent TO, looked up on our side.
//
// THE TRIAL FILTER lets it through (anonymous endpoints always pass), which
// is right: a customer's reply should not bounce because the workspace's
// trial expired — it is filed, and readable, like all their other data.
//
// ANSWERS — chosen for what Resend does with them:
//   200  accepted, duplicate, or genuine-but-not-for-us. Resend stops.
//   400  signed but unreadable. Resend's retry will not fix it either.
//   401  bad or missing signature.
//   503  receiving not configured here. Resend retries later — so mail
//        that arrives while you are still setting up is delivered once
//        the secret is in place, not lost.
//   503  085: also a delivery event for an email we sent seconds ago
//        and have not finished recording — Resend retries it shortly.
//   500  anything unexpected (the database was down). Resend retries.
// =====================================================================

using System.Text;
using MerkaiTrial.Application.Services.Inbound;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
    [Route("api/inbound")]
    public class InboundWebhookController : ControllerBase
    {
        private const int MaxBodyBytes = 256 * 1024;

        private readonly IInboundEmailReceiver _receiver;
        private readonly ILogger<InboundWebhookController> _logger;

        public InboundWebhookController(IInboundEmailReceiver receiver, ILogger<InboundWebhookController> logger)
        {
            _receiver = receiver;
            _logger = logger;
        }

        [HttpPost("resend")]
        [AllowAnonymous]
        [RequestSizeLimit(MaxBodyBytes)]
        [ApiExplorerSettings(IgnoreApi = true)]
        public async Task<IActionResult> Resend(CancellationToken ct)
        {
            // The RAW body, exactly as sent: the signature covers these
            // bytes, and model binding would re-serialise them.
            string body;
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync(ct);
            }

            if (Encoding.UTF8.GetByteCount(body) > MaxBodyBytes)
                return StatusCode(413);

            var headers = new InboundWebhookHeaders(
                Request.Headers["svix-id"].FirstOrDefault(),
                Request.Headers["svix-timestamp"].FirstOrDefault(),
                Request.Headers["svix-signature"].FirstOrDefault());

            try
            {
                var result = await _receiver.ReceiveAsync(body, headers, ct);

                return result.Outcome switch
                {
                    InboundReceiveOutcome.Accepted      => Ok(new { status = "accepted" }),
                    InboundReceiveOutcome.Duplicate     => Ok(new { status = "duplicate" }),
                    InboundReceiveOutcome.Ignored       => Ok(new { status = "ignored" }),
                    InboundReceiveOutcome.Unauthorised  => Unauthorized(),
                    InboundReceiveOutcome.NotConfigured => StatusCode(503, new { error = result.Detail }),
                    InboundReceiveOutcome.Malformed     => BadRequest(new { error = result.Detail }),
                    InboundReceiveOutcome.RetryLater    => StatusCode(503, new { error = result.Detail }),
                    _                                   => StatusCode(500)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inbound webhook failed — Resend will retry");
                return StatusCode(500, new { error = "Could not record the email just now." });
            }
        }
    }
}
