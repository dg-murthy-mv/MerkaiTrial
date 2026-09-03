using MerkaiTrial.Infrastructure.Tenancy;
using Microsoft.Extensions.Configuration;

namespace MerkaiTrial.Admin.Web.Services
{
    public sealed class TenantUiOptions
    {
        public string Country { get; init; } = "TH";
        public string DefaultCulture { get; init; } = "en";
        public string[] AllowedCultures { get; init; } = new[] { "en", "th" };
        public string DefaultCurrency { get; init; } = "THB";
        public string TaxProfile { get; init; } = "TH_VAT_7";
        public string[] PaymentProviders { get; init; } = System.Array.Empty<string>();
        public string InvoiceTemplate { get; init; } = "TH";
    }

    public interface ITenantUiService
    {
        TenantUiOptions Get();
    }

    public sealed class TenantUiService : ITenantUiService
    {
        private readonly ITenantContext _tenant;
        private readonly IConfiguration _cfg;

        public TenantUiService(ITenantContext tenant, IConfiguration cfg)
        {
            _tenant = tenant;
            _cfg = cfg;
        }

        public TenantUiOptions Get()
        {
            var id = _tenant.TenantId.ToString();
            var section = _cfg.GetSection($"Tenants:{id}");
            var specific = section.Exists() ? section.Get<TenantUiOptions>() : null;

            if (specific is not null) return specific;

            var fallback = _cfg.GetSection("Tenants:Default").Get<TenantUiOptions>();
            return fallback ?? new TenantUiOptions();
        }
    }
}
