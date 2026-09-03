using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace MerkaiTrial.Infrastructure.Payments;
public class PromptPayProvider : IPaymentProvider
{
    private readonly IConfiguration _cfg;
    public PromptPayProvider(IConfiguration cfg) => _cfg = cfg;
    public PaymentProviderKind Kind => PaymentProviderKind.PromptPay;

    public Task<CheckoutResult> CreateCheckoutAsync(Invoice inv, CancellationToken ct = default)
    {
        // Typically you'll render a QR (EMVCo) page; we link to a placeholder route
        var url = $"https://pay.example/promptpay/{inv.Number}";
        return Task.FromResult(new CheckoutResult(Kind.ToString(), $"pp_{inv.Id:N}", url));
    }

    public Task<(bool ok, string invoiceNumber)> HandleWebhookAsync(HttpRequest request, CancellationToken ct = default)
        => Task.FromResult((true, request.Query["invoice"].ToString()));
}
