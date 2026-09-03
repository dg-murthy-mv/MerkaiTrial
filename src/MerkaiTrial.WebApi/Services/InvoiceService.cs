using Microsoft.EntityFrameworkCore;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using MerkaiTrial.Domain.Enums;
namespace MerkaiTrial.WebApi.Services
{
    public class InvoiceService
    {
        private readonly FlowDbContext _db;
        public InvoiceService(FlowDbContext db) => _db = db;

        public async Task<Invoice> CreateFromQuoteAsync(Guid quoteId, DateTime dueDateUtc, CancellationToken ct)
        {
            // Project only the columns we actually need to avoid Status (INT) -> string casts etc.
            var q = await _db.Quotes.AsNoTracking()
                .Where(x => x.Id == quoteId)
                .Select(x => new
                {
                    x.Id,
                    x.TenantId,
                    x.Currency,
                    GrandTotal = x.GrandTotal // entity Total is mapped to DB [GrandTotal]
                })
                .SingleAsync(ct);

            var now = DateTime.UtcNow;
            var amount = q.GrandTotal;

            var inv = new Invoice
            {
                Id = Guid.NewGuid(),
                TenantId = q.TenantId,
                QuoteId = q.Id,
                Number = await GenerateUniqueInvoiceNumberAsync(ct),
                Currency = q.Currency,

                // Fill all money fields your table has
                Total = amount,    // maps to [Total]
                Amount = amount,   // maps to [Amount]
                Balance = amount,  // maps to [Balance]

                IssueDateUtc = now,
                DueDateUtc = dueDateUtc,
                Status = InvoiceStatus.Unpaid,

                CreatedAtUtc = now,
                CreatedBy = "api",
                UpdatedAtUtc = null,
                UpdatedBy = null,
                IsDeleted = false
            };

            _db.Invoices.Add(inv);
            await _db.SaveChangesAsync(ct);
            return inv;
        }

        public async Task MarkPaidAsync(string invoiceNumber, CancellationToken ct)
        {
            var inv = await _db.Invoices.FirstOrDefaultAsync(i => i.Number == invoiceNumber, ct);
            if (inv is null) return;

            inv.Status = InvoiceStatus.Paid; 
            inv.Balance = 0m;
            inv.UpdatedAtUtc = DateTime.UtcNow;
            inv.UpdatedBy = "api";
            await _db.SaveChangesAsync(ct);
        }

        // Simple unique-ish generator; replace with your preferred numbering scheme if needed.
        private async Task<string> GenerateUniqueInvoiceNumberAsync(CancellationToken ct)
        {
            for (var i = 0; i < 3; i++)
            {
                var candidate = $"INV-{DateTime.UtcNow:yyyyMMddHHmmss}-{Random.Shared.Next(1000, 9999)}";
                var exists = await _db.Invoices.AsNoTracking().AnyAsync(x => x.Number == candidate, ct);
                if (!exists) return candidate;
                await Task.Delay(10, ct);
            }
            return $"INV-{Guid.NewGuid():N}".ToUpperInvariant();
        }
    }
}
