using MerkaiTrial.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace MerkaiTrial.Infrastructure.Tax;
public interface ITaxCalculatorFactory
{
    ITaxCalculator Resolve(CountryCode country);
}
public class TaxCalculatorFactory : ITaxCalculatorFactory
{
    private readonly IServiceProvider _sp;
    public TaxCalculatorFactory(IServiceProvider sp) => _sp = sp;

    public ITaxCalculator Resolve(CountryCode c) => c switch
    {
        CountryCode.IN => _sp.GetRequiredService<IndiaTaxCalculator>(),
        CountryCode.TH => _sp.GetRequiredService<ThailandTaxCalculator>(),
        CountryCode.PH => _sp.GetRequiredService<PhilippinesTaxCalculator>(),
        _ => throw new NotSupportedException()
    };
}
