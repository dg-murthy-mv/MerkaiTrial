using MerkaiTrial.Domain.Entities;

namespace MerkaiTrial.Infrastructure.Tax;
public record QuoteTotals(decimal Subtotal, decimal TaxAmount, decimal Total);
public interface ITaxCalculator
{
    Task<QuoteTotals> CalculateAsync(Quote quote, CancellationToken ct = default);
}
