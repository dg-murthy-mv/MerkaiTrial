using MerkaiTrial.Domain.Entities;

namespace MerkaiTrial.Infrastructure.Tax;
// Simplified VAT 12%
public class PhilippinesTaxCalculator : ITaxCalculator
{
    public Task<QuoteTotals> CalculateAsync(Quote q, CancellationToken ct = default)
    {
        var subtotal = q.Items.Sum(i => i.LineTotal);
        var tax = Math.Round(subtotal * 0.12m, 2, MidpointRounding.AwayFromZero);
        return Task.FromResult(new QuoteTotals(subtotal, tax, subtotal + tax));
    }
}
