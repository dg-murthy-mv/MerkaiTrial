using Microsoft.EntityFrameworkCore;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using MerkaiTrial.Infrastructure.Tax;
using System;

namespace MerkaiTrial.WebApi.Services;
public class QuoteService
{
    private readonly FlowDbContext _db;
    private readonly ITaxCalculatorFactory _tax;
    public QuoteService(FlowDbContext db, ITaxCalculatorFactory tax)
    { _db = db; _tax = tax; }

    public async Task<Quote> CreateAsync(Guid tenantId, Guid dealId, string currency, IEnumerable<(string name, int qty, decimal unitPrice)> items, string lang, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FindAsync(new object?[] { tenantId }, ct) ?? throw new("Tenant not found");
        var q = new Quote
        {
            TenantId = tenantId,
            DealId = dealId,
            Currency = currency,
            
            Number = $"Q-{DateTime.UtcNow:yyyy}-{Random.Shared.Next(1000, 9999)}"
        };
        foreach (var it in items)
            q.Items.Add(new QuoteItem { Name = it.name, Quantity = it.qty, UnitPrice = it.unitPrice });

        //var calc = _tax.Resolve(tenant.Country.GetValueOrDefault());
        //var totals = await calc.CalculateAsync(q, ct);
        //q.Subtotal = totals.Subtotal; q.TaxTotal = totals.TaxAmount; q.GrandTotal = totals.Total;

        _db.Quotes.Add(q);
        await _db.SaveChangesAsync(ct);
        return q;
    }
}
