using MerkaiTrial.Domain.Enums;

namespace MerkaiTrial.Infrastructure.Payments;
public interface IPaymentProviderFactory
{
    IPaymentProvider Resolve(PaymentProviderKind kind);
}
public class PaymentProviderFactory : IPaymentProviderFactory
{
    private readonly IEnumerable<IPaymentProvider> _providers;
    public PaymentProviderFactory(IEnumerable<IPaymentProvider> providers) => _providers = providers;
    public IPaymentProvider Resolve(PaymentProviderKind k) =>
        _providers.First(p => p.Kind == k);
}
