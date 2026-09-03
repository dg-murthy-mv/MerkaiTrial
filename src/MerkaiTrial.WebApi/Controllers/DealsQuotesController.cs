using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MerkaiTrial.Infrastructure.Persistence;
using System.Data.Common;

namespace MerkaiTrial.WebApi.Controllers;

[ApiController]
[Route("api/deals")]
public class DealsQuotesController : ControllerBase
{
    public record ItemDto(string Description, decimal UnitPrice, int Quantity, decimal TaxRate);
    public record CreateQuoteRequest(IEnumerable<ItemDto> Items, string? Currency);
    public record QuoteDto(Guid Id, Guid DealId, string Number, DateTime IssueDateUtc,
                           string Currency, decimal Subtotal, decimal TaxTotal, decimal GrandTotal, string Status);

    private readonly FlowDbContext _db;
    private readonly ILogger<DealsQuotesController> _logger;
    private const int QuoteStatus_Draft = 0;
    public DealsQuotesController(FlowDbContext db, ILogger<DealsQuotesController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpPost("{dealId:guid}/quotes")]
    public async Task<ActionResult<QuoteDto>> CreateQuote(Guid dealId, [FromBody] CreateQuoteRequest body, CancellationToken ct)
    {
        // ---- Basic header & body validation ----
        var tenantHeader = Request.Headers["X-Tenant-Id"].FirstOrDefault()
                        ?? Request.Headers["x-tenant-id"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(tenantHeader))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Missing X-Tenant-Id header.");

        if (!Guid.TryParse(tenantHeader, out var tenantId) || tenantId == Guid.Empty)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid X-Tenant-Id header.");

        if (body is null)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Body required.");

        var items = body.Items?.ToList() ?? new List<ItemDto>();
        if (items.Count == 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "At least one item is required.");

        // Validate items
        foreach (var it in items)
        {
            if (string.IsNullOrWhiteSpace(it.Description))
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Item description is required.");
            if (it.Quantity <= 0)
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Item quantity must be > 0.");
            if (it.UnitPrice < 0)
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Item unit price must be >= 0.");
            if (it.TaxRate < 0)
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Item tax rate must be >= 0.");
        }

        try
        {
            // ---- Check that deal exists and belongs to tenant ----
            var deal = await _db.Deals.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == dealId && !d.IsDeleted, ct);
            if (deal is null)
                return NotFound(new { error = "Deal not found." });

            if (deal.TenantId != tenantId)
            {
                _logger.LogWarning("CreateQuote forbidden: deal.TenantId != header tenant (dealId={DealId}, dealTid={DealTid}, headerTid={HeaderTid})",
                    dealId, deal.TenantId, tenantId);
                return Forbid();
            }

            // ---- Currency & totals ----
            var currency = string.IsNullOrWhiteSpace(body.Currency)
                ? (string.IsNullOrWhiteSpace("deal.Company") ? "THB" : deal.Title!.Trim().ToUpperInvariant())
                : body.Currency!.Trim().ToUpperInvariant();

            if (currency.Length != 3)
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Currency must be a 3-letter ISO code.");

            var now = DateTime.UtcNow;
            var quoteId = Guid.NewGuid();
            var number = $"Q-{now:yyyyMM}-{Random.Shared.Next(1000, 9999)}";

            decimal subtotal = 0m, tax = 0m;
            foreach (var it in items)
            {
                var line = it.UnitPrice * it.Quantity;
                subtotal += line;
                tax += Math.Round(line * it.TaxRate, 2, MidpointRounding.AwayFromZero);
            }
            var grand = subtotal + tax;

            // ---- Transaction (atomic: quote header + items + move deal stage) ----
            await using IDbContextTransaction tx = await _db.Database.BeginTransactionAsync(ct);
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            // Insert into Quotes
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx.GetDbTransaction();
                cmd.CommandText =
                    @"INSERT INTO dbo.Quotes
                        (Id, DealId, Number, IssueDateUtc, ExpiresAtUtc, Currency, Status,
                         Subtotal, DiscountTotal, TaxTotal, GrandTotal,
                         PaymentLinkUrl, PdfUrl,
                         CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, TenantId, IsDeleted)
                      VALUES
                        (@id,@deal,@num,@issue,@exp,@cur,@status,
                         @sub,0,@tax,@grand,
                         NULL,NULL,
                         SYSUTCDATETIME(),@createdBy,NULL,NULL,@tenantId,0)";
                Add(cmd, "@id", quoteId);
                Add(cmd, "@deal", dealId);
                Add(cmd, "@num", number);
                Add(cmd, "@issue", now);
                Add(cmd, "@exp", now.AddDays(14));
                Add(cmd, "@cur", currency);
                Add(cmd, "@status", QuoteStatus_Draft); // NVARCHAR status
                Add(cmd, "@sub", subtotal);
                Add(cmd, "@tax", tax);
                Add(cmd, "@grand", grand);
                Add(cmd, "@createdBy", tenantId.ToString());
                Add(cmd, "@tenantId", tenantId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Insert items
            foreach (var it in items)
            {
                await using var cmd2 = conn.CreateCommand();
                cmd2.Transaction = tx.GetDbTransaction();
                cmd2.CommandText =
                    @"INSERT INTO dbo.QuoteItems
                        (Id, QuoteId, ProductId, Description, UnitPrice, Quantity,
                         LineDiscount, TaxRate, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted)
                      VALUES
                        (@id,@qid,NULL,@desc,@price,@qty,
                         0,@rate,SYSUTCDATETIME(),@createdBy,NULL,NULL,0)";
                Add(cmd2, "@id", Guid.NewGuid());
                Add(cmd2, "@qid", quoteId);
                Add(cmd2, "@desc", it.Description);
                Add(cmd2, "@price", it.UnitPrice);
                Add(cmd2, "@qty", it.Quantity);
                Add(cmd2, "@rate", it.TaxRate);
                Add(cmd2, "@createdBy", tenantId.ToString());
                await cmd2.ExecuteNonQueryAsync(ct);
            }

            // Move deal to "Quote Sent"
            await using (var cmd3 = conn.CreateCommand())
            {
                cmd3.Transaction = tx.GetDbTransaction();
                cmd3.CommandText = @"
UPDATE dbo.Deals
SET Stage = N'Quote Sent', UpdatedAtUtc = SYSUTCDATETIME(), UpdatedBy = @by
WHERE Id = @dealId AND IsDeleted = 0;";
                Add(cmd3, "@by", tenantId.ToString());
                Add(cmd3, "@dealId", dealId);
                await cmd3.ExecuteNonQueryAsync(ct);
            }


            await tx.CommitAsync(ct);

            return Ok(new QuoteDto(
                quoteId, dealId, number, now, currency,
                subtotal, tax, grand, "Draft"));
        }
        catch (OperationCanceledException oce)
        {
            _logger.LogWarning(oce, "POST /api/deals/{DealId}/quotes cancelled", dealId);
            return Problem(statusCode: StatusCodes.Status408RequestTimeout, title: "Request canceled or timed out.");
        }
        catch (DbUpdateException dbx)
        {
            _logger.LogError(dbx, "DB update error while creating quote for deal {DealId}", dealId);
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Database update failed.", detail: dbx.GetBaseException().Message);
        }
        catch (DbException dbex)
        {
            _logger.LogError(dbex, "DB error while creating quote for deal {DealId}", dealId);
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Database error.", detail: dbex.GetBaseException().Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while creating quote for deal {DealId}", dealId);
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Failed to create quote.");
        }
    }

    private static void Add(DbCommand c, string n, object? v)
    {
        var p = c.CreateParameter();
        p.ParameterName = n;
        p.Value = v ?? DBNull.Value;
        c.Parameters.Add(p);
    }
}
