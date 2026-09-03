using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using Microsoft.AspNetCore.Http;

namespace MerkaiTrial.Infrastructure.Payments;
public record CheckoutResult(string Provider, string ProviderRef, string CheckoutUrl);

public interface IPaymentProvider
{
    PaymentProviderKind Kind { get; }
    Task<CheckoutResult> CreateCheckoutAsync(Invoice invoice, CancellationToken ct = default);
    Task<(bool ok, string invoiceNumber)> HandleWebhookAsync(HttpRequest request, CancellationToken ct = default);
}
