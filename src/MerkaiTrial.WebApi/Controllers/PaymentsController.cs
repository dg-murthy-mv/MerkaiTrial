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
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Database update failed.", detail: dbx.GetBaseException().Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during checkout for invoice {Invoice}", invoiceNumber);
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Checkout failed.");
        }
    }

    // POST /api/payments/webhook/{provider}
    [HttpPost("webhook/{provider}")]
    public async Task<IActionResult> Webhook(string provider, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "provider is required.");

        try
        {
            if (!Enum.TryParse<PaymentProviderKind>(provider, true, out var kind))
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Unknown provider '{provider}'.");

            IPaymentProvider p;
            try
            {
                p = _factory.Resolve(kind);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Webhook resolve failed for provider {Provider}", provider);
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Unsupported provider '{provider}'.");
            }

            var (ok, invoiceNumber) = await p.HandleWebhookAsync(Request, ct);

            if (!ok || string.IsNullOrWhiteSpace(invoiceNumber))
            {
                _logger.LogWarning("Webhook not processed (ok={Ok}) for provider {Provider}", ok, provider);
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Webhook not processed.");
            }

            try
            {
                await _invoices.MarkPaidAsync(invoiceNumber, ct);
            }
            catch (Exception markEx)
            {
                _logger.LogError(markEx, "MarkPaid failed for invoice {Invoice} via provider {Provider}", invoiceNumber, provider);
                // return 202 so provider doesn't retry endlessly if this is our internal issue
                return StatusCode(StatusCodes.Status202Accepted, new { status = "accepted", invoice = invoiceNumber });
            }

            return Ok(new { status = "ok", invoice = invoiceNumber });
        }
        catch (OperationCanceledException oce)
        {
            _logger.LogWarning(oce, "Webhook canceled for provider {Provider}", provider);
            return Problem(statusCode: StatusCodes.Status408RequestTimeout, title: "Request canceled or timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected webhook error for provider {Provider}", provider);
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Webhook failed.");
        }
    }
}
