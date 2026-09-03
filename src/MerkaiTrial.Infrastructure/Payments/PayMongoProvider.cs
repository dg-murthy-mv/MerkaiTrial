using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using Microsoft.AspNetCore.Http;

namespace MerkaiTrial.Infrastructure.Payments;
public class PayMongoProvider : IPaymentProvider
{
    public PaymentProviderKind Kind => PaymentProviderKind.PayMongo;
    public Task<CheckoutResult> CreateCheckoutAsync(Invoice inv, CancellationToken ct = default)
        => Task.FromResult(new CheckoutResult(Kind.ToString(), $"pm_{inv.Id:N}", $"https://pay.example/paymongo/{inv.Number}"));

    public Task<(bool ok, string invoiceNumber)> HandleWebhookAsync(HttpRequest request, CancellationToken ct = default)
        => Task.FromResult((true, request.Query["invoice"].ToString()));
}
