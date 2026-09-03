using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace MerkaiTrial.Infrastructure.Payments;
public class RazorpayProvider : IPaymentProvider
{
    private readonly IConfiguration _cfg;
    public RazorpayProvider(IConfiguration cfg) => _cfg = cfg;
    public PaymentProviderKind Kind => PaymentProviderKind.Razorpay;

    public Task<CheckoutResult> CreateCheckoutAsync(Invoice inv, CancellationToken ct = default)
    {
        // TODO: use Razorpay SDK; this is a placeholder URL
        var url = $"https://pay.example/razorpay/{inv.Number}";
        return Task.FromResult(new CheckoutResult(Kind.ToString(), $"rzp_{inv.Id:N}", url));
    }

    public Task<(bool ok, string invoiceNumber)> HandleWebhookAsync(HttpRequest request, CancellationToken ct = default)
    {
        // TODO: verify signature & parse payload
        var invNo = request.Query["invoice"].ToString();
        return Task.FromResult((true, invNo));
    }
}
