using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace MerkaiTrial.Infrastructure.Payments;
public class XenditProvider : IPaymentProvider
{
    private readonly IConfiguration _cfg;
    public XenditProvider(IConfiguration cfg) => _cfg = cfg;
    public PaymentProviderKind Kind => PaymentProviderKind.Xendit;

    public Task<CheckoutResult> CreateCheckoutAsync(Invoice inv, CancellationToken ct = default)
    {
        var url = $"https://pay.example/xendit/{inv.Number}";
        return Task.FromResult(new CheckoutResult(Kind.ToString(), $"xnd_{inv.Id:N}", url));
    }

    public Task<(bool ok, string invoiceNumber)> HandleWebhookAsync(HttpRequest request, CancellationToken ct = default)
    {
        var invNo = request.Query["invoice"].ToString();
        return Task.FromResult((true, invNo));
    }
}
