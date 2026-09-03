using System.Globalization;
using MerkaiTrial.Domain.Entities;

namespace MerkaiTrial.Infrastructure.Tax;
public class IndiaTaxCalculator : ITaxCalculator
{
    // Simplified: 18% GST for SaaS
    public Task<QuoteTotals> CalculateAsync(Quote q, CancellationToken ct = default)
    {
        var subtotal = q.Items.Sum(i => i.LineTotal);
        var tax = Math.Round(subtotal * 0.18m, 2, MidpointRounding.AwayFromZero);
        return Task.FromResult(new QuoteTotals(subtotal, tax, subtotal + tax));
    }
}
