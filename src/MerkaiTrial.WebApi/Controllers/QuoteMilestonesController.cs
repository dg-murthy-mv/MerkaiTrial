// =====================================================================
// QuoteMilestonesController.cs
// Location: MerkaiTrial.WebApi/Controllers/QuoteMilestonesController.cs
//
// NEW FILE (071). Two endpoints: read the billing schedule, save it.
//
// ─────────────────────────────────────────────────────────────────────
// TENANT ID COMES FROM THE TOKEN, NOT THE QUERY STRING
//
//   Same position as ProductCategoriesController took in 068, and for
//   a sharper reason: this endpoint decides HOW MUCH MONEY gets
//   invoiced. A tenantId a caller could choose would be a caller
//   choosing whose payment terms to re-cut. Read from the signed token
//   via ICurrentUserService.GetCurrentTenantId(), and nowhere else.
//
// AUTHORIZATION
//
//   READ  → Policies.QuotesRead
//   WRITE → Policies.QuotesUpdate
//
//   NOT Invoices.*, and the distinction is the whole design: the
//   SCHEDULE is part of the quote — it is a commercial term the customer
//   agrees to, it prints on the quote PDF, and it is settled before any
//   invoice exists. Raising the invoice against a stage is a separate
//   act with a separate gate (Invoices.Create, on the invoice page).
//
//   So a rep who can quote can set the terms; a rep who can invoice can
//   bill them. Those are genuinely different jobs in an SME, and in a
//   one-person business the same user holds both anyway.
//
//   The READ is Quotes.Read and not Invoices.Read for the same reason
//   068 made its category read Products.Read: the quote page, the quote
//   PDF and the schedule editor all need this, and they are used by
//   people who hold quotes.read. Gating it on Invoices.Read would have
//   returned 403 to them, the service would have degraded to "no
//   schedule" exactly as designed, and the quote page would have shown
//   a quote with no payment terms — no error, no log line anybody looks
//   at. The invoice-create page also reads it, and anyone who can raise
//   an invoice from a quote already holds quotes.read (the page's own
//   OnGetAsync requires it, since 018).
//
//   PermissionHandler bypasses on IsTenantAdmin only, NOT IsSuperAdmin,
//   so a Module.Action policy locks super admins OUT. Correct here: a
//   payment schedule is a workspace's agreement with its customer, and
//   a platform admin has no business editing one.
//
// RECORD VISIBILITY is not checked here — it is checked in the
// handlers, against the quote's DEAL, because that is where the
// ownership lives and where every other quote path checks it. A
// controller-level check would be a second copy of a rule that is
// already enforced one layer down.
// ─────────────────────────────────────────────────────────────────────
// =====================================================================

using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Quotes;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
    [Route("api/quotes/{quoteId:guid}/billing-schedule")]
    [Authorize]   // explicit, rather than relying on the FallbackPolicy
    public class QuoteMilestonesController : ControllerBase
    {
        private readonly GetBillingScheduleHandler _get;
        private readonly SaveBillingScheduleHandler _save;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<QuoteMilestonesController> _logger;

        public QuoteMilestonesController(
            GetBillingScheduleHandler get,
            SaveBillingScheduleHandler save,
            ICurrentUserService currentUserService,
            ILogger<QuoteMilestonesController> logger)
        {
            _get = get;
            _save = save;
            _currentUserService = currentUserService;
            _logger = logger;
        }

        /// <summary>
        /// The schedule, with every stage's computed amount and what has
        /// been invoiced against it, plus the agreed / invoiced /
        /// outstanding rollup.
        ///
        /// A quote with NO schedule returns Rows = [] and HasSchedule =
        /// false. That is a normal, successful answer — not a 404 — and
        /// it is what every quote written before 071 returns.
        /// </summary>
        [HttpGet]
        [Authorize(Policy = Policies.QuotesRead)]
        public async Task<ActionResult<BillingScheduleDto>> Get(
            Guid quoteId,
            CancellationToken ct = default)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();

            try
            {
                return Ok(await _get.Handle(tenantId, quoteId, ct));
            }
            catch (KeyNotFoundException ex)
            {
                // Also the answer for a quote on a deal this user cannot
                // see — the handler deliberately does not distinguish, so
                // an invisible deal never reveals itself by answering
                // differently.
                return NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load the billing schedule for quote {QuoteId}", quoteId);
                return StatusCode(500, new { error = "Failed to load the payment schedule" });
            }
        }

        /// <summary>
        /// Replace the schedule. The whole schedule, every time — this is
        /// a PUT and not a collection of POST / DELETE calls, because the
        /// stages only make sense together: they have to add up to the
        /// quote, and "add a 40% stage" is not a valid instruction on its
        /// own.
        ///
        /// THE NULL-VS-EMPTY RULE (fourth outing — 067 TaxCode, 069
        /// prices, 070 bundle contents):
        ///   Milestones == null → leave the schedule alone.
        ///   Milestones == []   → remove it; the quote goes back to being
        ///                        invoiced once, for the whole amount.
        /// The page always sends a list, so on that path the two cases
        /// cannot be confused.
        /// </summary>
        [HttpPut]
        [Authorize(Policy = Policies.QuotesUpdate)]
        public async Task<ActionResult<BillingScheduleDto>> Save(
            Guid quoteId,
            [FromBody] SaveBillingScheduleDto dto,
            CancellationToken ct = default)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();

            // The route is authoritative. A body that disagrees with the
            // URL is a bug in the caller at best, and an attempt to write
            // to another quote at worst — so the route wins and the body's
            // copies are overwritten rather than trusted.
            dto.TenantId = tenantId;
            dto.QuoteId = quoteId;

            try
            {
                return Ok(await _save.Handle(dto, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                // "The stages come to 90% of the quote", "Stage 2's amount
                // cannot change — invoice INV-0042 has been raised against
                // this schedule". Written for the person; IApiService turns
                // a 400 body of { "error": "..." } into exactly that
                // sentence on the page.
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save the billing schedule for quote {QuoteId}", quoteId);
                return StatusCode(500, new { error = "Failed to save the payment schedule" });
            }
        }
    }
}
