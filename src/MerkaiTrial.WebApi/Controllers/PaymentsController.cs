// =====================================================================
// PaymentsController.cs
// Location: MerkaiTrial.WebApi/Controllers/PaymentsController.cs
//
// 100 — PERMISSION ADDED. Both actions now need Invoices.Update.
//
//   Before this round the controller had no [Authorize] of its own, so
//   the API's fallback policy ("signed in") was all that stood in front
//   of it: a Viewer could start a checkout on an invoice (which writes
//   Provider / ProviderRef / CheckoutUrl onto it) and could post to the
//   webhook, which marks an invoice paid by its number. The record-a-
//   payment button already needs Invoices.Update; these now match it.
//
//   The webhook was never reachable by a real payment provider: a
//   provider sends no Merkai sign-in, so the fallback policy has always
//   answered it with 401. That is unchanged. When online payments are
//   switched on for real, the webhook needs its own round — anonymous,
//   checked by the provider's signature instead (the way the Resend
//   webhook is), and recording a Payment row through AddPaymentHandler
//   rather than marking the invoice paid directly.
//
//   Nothing else in this file changed.
//
// 105 — the webhook is switched off (answers 404 to everyone, changes
//   nothing) until a provider's signature is really checked — see the
//   note on the action. And a failed checkout save no longer sends SQL
//   Server's own error text back.
// =====================================================================

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MerkaiTrial.Infrastructure.Payments;
using MerkaiTrial.Infrastructure.Persistence;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.WebApi.Services;
using MerkaiTrial.Domain.Entities;

namespace MerkaiTrial.WebApi.Controllers;

[ApiController]
[Route("api/payments")]
[Authorize(Policy = "Invoices.Update")]      // 100 — see the header
public class PaymentsController : ControllerBase
{
    private readonly FlowDbContext _db;
    private readonly IPaymentProviderFactory _factory;
    private readonly InvoiceService _invoices;
    private readonly ILogger<PaymentsController> _logger;

    public PaymentsController(
        FlowDbContext db,
        IPaymentProviderFactory factory,
        InvoiceService invoices,
        ILogger<PaymentsController> logger)
    {
        _db = db;
        _factory = factory;
        _invoices = invoices;
        _logger = logger;
    }

    public record CheckoutDto(PaymentProviderKind Provider);

    // POST /api/payments/checkout/{invoiceNumber}
    [HttpPost("checkout/{invoiceNumber}")]
    public async Task<IActionResult> Checkout(string invoiceNumber, [FromBody] CheckoutDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(invoiceNumber))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "invoiceNumber is required.");

        if (dto is null)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Body required.");

        try
        {
            var inv = await _db.Invoices.FirstOrDefaultAsync(i => i.Number == invoiceNumber, ct);
            if (inv is null)
                return NotFound(new { error = $"Invoice '{invoiceNumber}' not found." });

            // Optional guard: prevent duplicate checkout after Paid
            if (string.Equals(inv.Status.ToString(), "Paid", StringComparison.OrdinalIgnoreCase))
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "Invoice already paid.");

            IPaymentProvider provider;
            try
            {
                provider = _factory.Resolve(dto.Provider);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unsupported provider {Provider} for invoice {Invoice}", dto.Provider, invoiceNumber);
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Unsupported provider '{dto.Provider}'.");
            }

            var result = await provider.CreateCheckoutAsync(inv, ct);

            inv.Provider = result.Provider;
            inv.ProviderRef = result.ProviderRef;
            inv.CheckoutUrl = result.CheckoutUrl;

            await _db.SaveChangesAsync(ct);

            return Ok(new { inv.Number, inv.CheckoutUrl, inv.Provider });
        }
        catch (OperationCanceledException oce)
        {
            _logger.LogWarning(oce, "Checkout canceled for invoice {Invoice}", invoiceNumber);
            return Problem(statusCode: StatusCodes.Status408RequestTimeout, title: "Request canceled or timed out.");
        }
        catch (DbUpdateException dbx)
        {
            _logger.LogError(dbx, "DB update failed during checkout for invoice {Invoice}", invoiceNumber);
            // 105 — VUL-12: was detail: dbx.GetBaseException().Message, which is
            // SQL Server's own text (table, column and constraint names).
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "The invoice could not be updated just now.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during checkout for invoice {Invoice}", invoiceNumber);
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Checkout failed.");
        }
    }

    // POST /api/payments/webhook/{provider}
    //
    // 105 — VUL-19: SWITCHED OFF. Every call is answered 404 and nothing
    // changes.
    //
    // It used to mark an invoice paid from nothing but its number in the
    // address: /api/payments/webhook/razorpay?invoice=INV-0042. None of the
    // four providers checked a signature (Razorpay's said "TODO"), the
    // invoice was set to Paid with a zero balance directly — no Payment
    // row, so none of the payment rules, history or idempotency applied —
    // and anyone allowed to edit invoices could call it. A real provider
    // never could: it has no Merkai sign-in.
    //
    // When online payments are switched on for real, this becomes an
    // anonymous endpoint per provider that checks THAT provider's
    // signature over the raw body, rejects replays by the provider's event
    // id, and records the money through AddPaymentHandler like the Record
    // payment button. Until then it must do nothing at all.
    [HttpPost("webhook/{provider}")]
    [AllowAnonymous]
    [ApiExplorerSettings(IgnoreApi = true)]
    public IActionResult Webhook(string provider)
    {
        _logger.LogWarning(
            "Refused a call to the payment webhook ({Provider}): online payments are not switched on",
            provider.Length <= 30 ? provider : provider[..30]);
        return NotFound();
    }
}
