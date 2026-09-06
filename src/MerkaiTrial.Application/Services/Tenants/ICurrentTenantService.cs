// =====================================================================
// FILE: MerkaiTrial.Application/Services/Tenants/ICurrentTenantService.cs
// CHANGES: Added plan/quota methods — reads from TenantSettings table
//          which is already seeded per tenant. No Plans table join needed.
// =====================================================================

using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Services.Tenants;

// ═════════════════════════════════════════════════════════════════════
// INTERFACE
// ═════════════════════════════════════════════════════════════════════

public interface ICurrentTenantService
{
    // ── Identity ──────────────────────────────────────────────────────
    Guid GetTenantId();
    Guid GetUserId();
    string GetUserEmail();
    bool IsInRole(string roleName);

    // ── Country ───────────────────────────────────────────────────────
    string GetCountryCode();
    string GetCountryName();

    // ── Currency ──────────────────────────────────────────────────────
    string GetCurrencyCode();
    string GetCurrencySymbol();
    int GetCurrencyDecimals();
    /// <summary>Tenant-aware money. Pass decimals to override Country.CurrencyDecimals (e.g. 0 for compact KPI tiles).</summary>
    string FormatCurrency(decimal amount, int? decimals = null);

    // ── Tax ───────────────────────────────────────────────────────────
    Task<decimal> GetDefaultTaxRateAsync(CancellationToken ct = default);
    string GetTaxLabel();

    // ── Localization ──────────────────────────────────────────────────
    string GetTimezone();
    string GetDateFormat();
    string GetTimeFormat();

    // ── Timezone helpers ──────────────────────────────────────────────
    DateTime UtcToLocal(DateTime utcDateTime);
    string FormatDate(DateTime utcDateTime);
    string FormatDateTime(DateTime utcDateTime);
    string GetTenantName();
    string GetNumberFormat();

    // ── ✅ NEW: Plan & Quota ──────────────────────────────────────────
    /// <summary>Raw plan name from Tenants.Plan e.g. "starter"</summary>
    string GetPlanName();

    /// <summary>Display name e.g. "Starter", "Professional", "Enterprise"</summary>
    string GetPlanDisplayName();

    /// <summary>Check if a feature key exists in TenantSettings.FeatureFlags JSON array</summary>
    bool HasFeature(string featureKey);

    /// <summary>Max leads allowed — from TenantSettings.MaxLeads</summary>
    int GetMaxLeads();

    /// <summary>Max deals allowed — from TenantSettings.MaxDeals</summary>
    int GetMaxDeals();

    /// <summary>Max users allowed — from TenantSettings.MaxUsers</summary>
    int GetMaxUsers();

    int GetMaxContacts();
    int GetMaxCompanies();

    /// <summary>Storage limit in bytes — from TenantSettings.StorageLimit</summary>
    long GetStorageLimitBytes();

    /// <summary>All feature keys for this tenant</summary>
    IReadOnlyList<string> GetFeatures();

    // ── ADD TO INTERFACE ──────────────────────────────────────────────────

    /// <summary>True when tenant is on trial plan AND trial has not expired</summary>
    bool IsOnTrial();

    /// <summary>True when trial plan has passed its expiry date — account locked</summary>
    bool IsTrialExpired();

    /// <summary>Days remaining in trial. 0 if not on trial or expired.</summary>
    int GetTrialDaysRemaining();

    /// <summary>Trial expiry date. Null if not on trial.</summary>
    DateTime? GetTrialExpiresAt();

    /// <summary>
    /// True when tenant can use the app.
    /// False when trial expired — all create/update operations should block.
    /// </summary>
    bool IsAccountActive();
}

// ═════════════════════════════════════════════════════════════════════
// IMPLEMENTATION
// ═════════════════════════════════════════════════════════════════════

public class CurrentTenantService : ICurrentTenantService
{
    private readonly IHttpContextAccessor _http;
    private readonly FlowDbContext _db;
    private readonly ILogger<CurrentTenantService> _logger;

    // ── Per-request cache ─────────────────────────────────────────────
    private Tenant? _tenant;
    private Country? _country;
    private TenantSettings? _settings;
    private List<string>? _features;
    private bool _loaded = false;
    private Plan? _plan;
    public CurrentTenantService(
        IHttpContextAccessor httpContextAccessor,
        FlowDbContext db,
        ILogger<CurrentTenantService> logger)
    {
        _http = httpContextAccessor;
        _db = db;
        _logger = logger;
    }

    // ── PRIVATE: Load tenant + country + settings once per request ────
    private async Task<(Tenant tenant, Country country, TenantSettings? settings)> LoadAsync()
    {
        if (_loaded && _tenant != null && _country != null)
            return (_tenant, _country, _settings);

        var tenantId = GetTenantId();

        // 1. Load tenant
        _tenant = await _db.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId && !t.IsDeleted);

        if (_tenant == null)
        {
            _logger.LogError("Tenant {TenantId} not found", tenantId);
            throw new UnauthorizedAccessException($"Tenant {tenantId} not found");
        }

        // 2. Load country
        if (_tenant.CountryId.HasValue)
        {
            _country = await _db.Countries.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == _tenant.CountryId.Value && c.IsActive);
        }

        if (_country == null && !string.IsNullOrEmpty(_tenant.DefaultCurrency))
        {
            _country = await _db.Countries.AsNoTracking()
                .FirstOrDefaultAsync(c => c.CurrencyCode == _tenant.DefaultCurrency && c.IsActive);
        }

        if (_country == null)
        {
            _logger.LogWarning("No country for tenant {TenantId} — using IN defaults", tenantId);
            _country = new Country
            {
                Code = "IN",
                Name = "India",
                CurrencyCode = "INR",
                CurrencySymbol = "₹",
                CurrencyDecimals = 2,
                TaxLabel = "GST",
                DefaultTaxRate = 18,
                NumberFormat = "en-IN",
                DateFormat = "dd/MM/yyyy",
                TimeFormat = "HH:mm",
                Timezone = "Asia/Kolkata",
            };
        }

        // 3. ✅ Load TenantSettings (plan limits + features)
        _settings = await _db.TenantSettings.AsNoTracking()
            .FirstOrDefaultAsync(ts => ts.TenantId == tenantId);

        if (_settings == null)
            _logger.LogWarning("No TenantSettings for tenant {TenantId} — quota/features unavailable", tenantId);

        // Load Plan for limits not in TenantSettings (MaxContacts, MaxCompanies)
        if (!string.IsNullOrEmpty(_tenant.Plan))
        {
            _plan = await _db.Plans.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Name == _tenant.Plan && p.IsActive);

            if (_plan == null)
                _logger.LogWarning("Plan '{Plan}' not found for tenant {TenantId}",
                    _tenant.Plan, tenantId);
        }

        // 4. Parse features JSON once and cache
        _features = ParseFeatures(_settings?.FeatureFlags);

        _loaded = true;
        _logger.LogDebug("Tenant {TenantId} loaded — Plan={Plan} MaxLeads={MaxLeads}",
            tenantId, _tenant.Plan, _settings?.MaxLeads);

        return (_tenant, _country, _settings);
    }

    private (Tenant t, Country c, TenantSettings? s) Load()
        => LoadAsync().GetAwaiter().GetResult();

    private static List<string> ParseFeatures(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    // ── IDENTITY ──────────────────────────────────────────────────────

    public Guid GetTenantId()
    {
        // Previously short-circuited to _devCtx.Value.TenantId whenever
        // DevelopmentTenantContext.Enabled=true, completely bypassing the
        // ViewAs cookie/header override — DemoAuthenticationHandler already
        // resolves the correct value (ViewAs override, or the appsettings
        // default when no override is active) and puts it in the TenantId
        // claim, so there's no need for a second, independent fallback here.
        var claim = _http.HttpContext?.User?.FindFirst("TenantId")?.Value;
        if (Guid.TryParse(claim, out var id)) return id;

        // Only reached if something is authenticated with no TenantId claim
        // at all (shouldn't happen under the Demo scheme, but keep a clear
        // failure rather than silently resolving to the wrong tenant).
        throw new UnauthorizedAccessException("TenantId claim missing from authenticated user");
    }

    public Guid GetUserId()
    {
        var claim = _http.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(claim, out var id)) return id;
        throw new UnauthorizedAccessException("UserId claim missing from authenticated user");
    }

    public string GetUserEmail()
        => _http.HttpContext?.User?.FindFirst(ClaimTypes.Email)?.Value ?? string.Empty;

    public bool IsInRole(string roleName)
        => _http.HttpContext?.User?.IsInRole(roleName) ?? false;

    // ── COUNTRY ───────────────────────────────────────────────────────

    public string GetCountryCode() => Load().c.Code;
    public string GetCountryName() => Load().c.Name;
    public string GetTenantName() => Load().t.Name ?? "Your Company";
    public string GetNumberFormat() => Load().c.NumberFormat ?? "en-US";

    // ── CURRENCY ──────────────────────────────────────────────────────

    public string GetCurrencyCode()
    {
        var (tenant, country, _) = Load();
        return !string.IsNullOrEmpty(tenant.DefaultCurrency)
            ? tenant.DefaultCurrency
            : country.CurrencyCode ?? "INR";
    }

    public string GetCurrencySymbol() => Load().c.CurrencySymbol ?? "₹";
    public int GetCurrencyDecimals() => Load().c.CurrencyDecimals;

    // ── ✅ NUMBER FORMATTING ──────────────────────────────────────────
    // CORRECTION to the previous fix. I claimed grouping size could live in
    // Countries.NumberFormat as a pattern ("#,##,##0.00" for Indian lakhs).
    // That is WRONG. In .NET custom numeric format strings a ',' between digit
    // placeholders only ENABLES grouping — the actual group SIZES come from the
    // culture's NumberFormatInfo.NumberGroupSizes. InvariantCulture is {3}, so
    // "#,##,##0.00" rendered 332,155.84 rather than 3,32,155.84.
    //
    // Indian grouping needs a culture whose NumberGroupSizes is {3,2}, i.e.
    // en-IN. So the ORIGINAL code's intent — CultureInfo from the column — was
    // right; only the DATA was wrong (it held patterns, not culture names).
    //
    // This handles BOTH conventions so a half-migrated Countries table can't
    // break, and never throws:
    //   "en-IN"      -> culture   -> ToString("N{decimals}", culture)
    //   "#,##0.00"   -> pattern   -> ToString(pattern, InvariantCulture)
    //   null / junk  -> fallback  -> ToString("N{decimals}", InvariantCulture)
    //
    // Resolved cultures are cached per process, and a bad value logs ONCE
    // rather than on every call (the old code threw 23-37 times per page).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CultureInfo?>
        _cultureCache = new();

    private static bool LooksLikePattern(string v)
        => v.IndexOf('#') >= 0 || v.IndexOf('0') >= 0;

    private CultureInfo? ResolveCulture(string name)
    {
        return _cultureCache.GetOrAdd(name, n =>
        {
            try
            {
                return CultureInfo.GetCultureInfo(n);
            }
            catch (CultureNotFoundException)
            {
                _logger.LogWarning(
                    "Countries.NumberFormat {Value} is neither a valid .NET culture name " +
                    "nor a numeric pattern — falling back to InvariantCulture. " +
                    "Logged once per process per value.", n);
                return null;
            }
        });
    }

    public string FormatCurrency(decimal amount, int? decimals = null)
    {
        var (_, country, _) = Load();

        var places = decimals ?? country.CurrencyDecimals;
        if (places < 0 || places > 15) places = 2;

        var raw = country.NumberFormat?.Trim();
        string formatted;

        if (string.IsNullOrEmpty(raw))
        {
            formatted = amount.ToString($"N{places}", CultureInfo.InvariantCulture);
        }
        else if (LooksLikePattern(raw))
        {
            // Legacy pattern data. Western grouping only — a pattern cannot
            // express lakh grouping. Migrate the row to a culture name.
            formatted = amount.ToString(raw, CultureInfo.InvariantCulture);
        }
        else
        {
            var culture = ResolveCulture(raw);
            formatted = amount.ToString($"N{places}", culture ?? CultureInfo.InvariantCulture);
        }

        // ✅ Was `?? "₹"` — a hardcoded rupee on the 43 countries with no symbol,
        // which is the wrong currency rather than a missing one. ISO code is
        // unstyled but never misleading.
        var symbol = !string.IsNullOrWhiteSpace(country.CurrencySymbol)
            ? country.CurrencySymbol
            : country.CurrencyCode ?? string.Empty;

        return $"{symbol}{formatted}";
    }

    // ── TAX ───────────────────────────────────────────────────────────

    public async Task<decimal> GetDefaultTaxRateAsync(CancellationToken ct = default)
    {
        var (_, country, _) = await LoadAsync();

        var rate = await _db.TaxRates.AsNoTracking()
            .Where(t => t.TenantId == GetTenantId()
                     && t.CountryCode == country.Code
                     && t.IsDefault
                     && t.IsActive
                     && !t.IsDeleted
                     && (t.EffectiveFrom == null || t.EffectiveFrom <= DateTime.UtcNow)
                     && (t.EffectiveTo == null || t.EffectiveTo >= DateTime.UtcNow))
            .Select(t => (decimal?)t.Rate)
            .FirstOrDefaultAsync(ct);

        if (rate.HasValue) return rate.Value;

        _logger.LogWarning("No TaxRate for tenant {TenantId} — using country default", GetTenantId());
        return country.DefaultTaxRate ?? 0m;
    }

    public string GetTaxLabel() => Load().c.TaxLabel ?? "Tax";

    // ── LOCALIZATION ──────────────────────────────────────────────────

    public string GetTimezone()
    {
        var (tenant, country, _) = Load();
        return !string.IsNullOrEmpty(tenant.Timezone)
            ? tenant.Timezone
            : country.Timezone ?? "Asia/Kolkata";
    }

    public string GetDateFormat() => Load().c.DateFormat ?? "dd/MM/yyyy";
    public string GetTimeFormat() => Load().c.TimeFormat ?? "HH:mm";

    public DateTime UtcToLocal(DateTime utcDateTime)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(GetTimezone());
            return TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc), tz);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Timezone conversion failed for {Tz}", GetTimezone());
            return utcDateTime;
        }
    }

    public string FormatDate(DateTime utcDateTime)
        => UtcToLocal(utcDateTime).ToString(GetDateFormat());

    public string FormatDateTime(DateTime utcDateTime)
        => UtcToLocal(utcDateTime).ToString($"{GetDateFormat()} {GetTimeFormat()}");

    // ── ✅ PLAN & QUOTA ───────────────────────────────────────────────

    public string GetPlanName()
    {
        var (tenant, _, _) = Load();
        return (tenant.Plan ?? "starter").ToLowerInvariant();
    }

    public string GetPlanDisplayName()
    {
        if (!_loaded) Load();
        // Read from the Plans table rather than a hardcoded switch. The switch
        // had no "trial" case and fell through to "Starter", so a trial tenant
        // displayed as Starter everywhere — including on the badge a prospect
        // sees every day of their evaluation.
        return _plan?.DisplayName ?? GetPlanName();
    }

    public bool HasFeature(string featureKey)
    {
        // Ensure features are loaded
        if (_features == null) Load();
        return _features?.Contains(featureKey, StringComparer.OrdinalIgnoreCase) ?? false;
    }

    public IReadOnlyList<string> GetFeatures()
    {
        if (_features == null) Load();
        return _features ?? new List<string>();
    }

    public int GetMaxLeads()
    {
        var (_, _, settings) = Load();
        return settings?.MaxLeads ?? 100; // Starter default fallback
    }

    public int GetMaxDeals()
    {
        var (_, _, settings) = Load();
        return settings?.MaxDeals ?? 50; // Starter default fallback
    }

    public int GetMaxUsers()
    {
        var (_, _, settings) = Load();
        return settings?.MaxUsers ?? 5; // Starter default fallback
    }
    public int GetMaxContacts()
    {
        if (!_loaded) Load();
        return _plan?.MaxContacts ?? 500;    // Starter default fallback
    }

    public int GetMaxCompanies()
    {
        if (!_loaded) Load();
        return _plan?.MaxCompanies ?? 100;   // Starter default fallback
    }
    public long GetStorageLimitBytes()
    {
        var (_, _, settings) = Load();
        return settings?.StorageLimit ?? 5_368_709_120L; // 5 GB Starter default
    }

    public bool IsOnTrial()
    {
        var (tenant, _, _) = Load();
        return tenant.IsOnTrial();
    }

    public bool IsTrialExpired()
    {
        var (tenant, _, _) = Load();
        return tenant.IsTrialExpired();
    }

    public int GetTrialDaysRemaining()
    {
        var (tenant, _, _) = Load();
        return tenant.TrialDaysRemaining;
    }

    public DateTime? GetTrialExpiresAt()
    {
        var (tenant, _, _) = Load();
        return tenant.TrialExpiresAt;
    }

    public bool IsAccountActive()
    {
        var (tenant, _, _) = Load();

        // Trial expired → account locked
        if (tenant.IsTrialExpired()) return false;

        // Active tenant
        return tenant.IsActive;
    }
}
