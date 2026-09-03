// =====================================================================
// PlanDtos.cs
// Location: MerkaiTrial.Application/DTOs/PlanDtos.cs
// =====================================================================

using System.ComponentModel.DataAnnotations;
using System.Linq;
using MerkaiTrial.Application.Configuration;

namespace MerkaiTrial.Application.DTOs;

// ── Supported pricing currencies + the country metadata each maps to ──
// Mirrors what PlanPricing actually stores (CurrencyCode, CountryCode,
// CountryName, CurrencySymbol) so the admin form only has to submit a
// currency code and two numbers; the rest is looked up server-side.
//
// Symbols are sourced from CurrencyConfiguration (the single source of
// truth for currency metadata) rather than hardcoded here a second time —
// keeps this list and the general currency list from drifting apart.
public static class PlanCurrencies
{
    public record CurrencyInfo(string Code, string CountryCode, string CountryName, string Symbol);

    // Country/market mapping specific to plan pricing — CurrencyConfiguration
    // doesn't know about countries, only currencies, so that part stays here.
    private static readonly (string Code, string CountryCode, string CountryName)[] Markets =
    {
        ("USD", "*",  "International"),
        ("THB", "TH", "Thailand"),
        ("INR", "IN", "India"),
        ("PHP", "PH", "Philippines"),
        ("AED", "AE", "UAE"),
    };

    public static readonly CurrencyInfo[] All = Markets
        .Select(m => new CurrencyInfo(m.Code, m.CountryCode, m.CountryName, CurrencyConfiguration.GetCurrencySymbol(m.Code)))
        .ToArray();

    public static readonly string[] Codes = All.Select(c => c.Code).ToArray();

    public static CurrencyInfo? Find(string code) =>
        All.FirstOrDefault(c => c.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
}

// ── Per-currency pricing row (PlanPricings table) ──────────────────────
public record PlanPricingDto(
    Guid    Id,
    Guid    PlanId,
    string  CurrencyCode,     // USD / THB / INR / PHP / AED
    string  CountryCode,      // TH / IN / PH / AE / * (international)
    string  CountryName,      // "Thailand", "India", ... "International"
    string  CurrencySymbol,   // ฿, ₹, ₱, $, AED
    decimal MonthlyPrice,
    decimal AnnualPrice,
    bool    IsActive,
    int     SortOrder
);

// ── Input row used when creating/updating a plan's pricing ─────────────
// Admin only supplies currency + the two prices; country metadata and
// symbol are resolved from PlanCurrencies when persisted.
public record PlanPricingInput(
    [Required]
    [RegularExpression("^(USD|THB|INR|PHP|AED)$", ErrorMessage = "Unsupported currency")]
    string CurrencyCode,

    [Range(0, 1_000_000)]
    decimal MonthlyPrice,

    [Range(0, 1_000_000)]
    decimal AnnualPrice
);

// ── Admin read DTO (full detail for Super Admin pages) ────────────────
public record PlanDto(
    Guid    Id,
    string  Name,
    string  DisplayName,
    string? Description,
    int     MaxUsers,
    int     MaxLeads,
    int     MaxDeals,
    int     MaxContacts,
    int     MaxCompanies,
    long    StorageLimitBytes,
    string  StorageLimitDisplay,    // e.g. "5 GB" — computed
    decimal MonthlyPrice,           // base/USD price
    decimal AnnualPrice,            // base/USD price
    string? Features,               // raw JSON
    List<string> FeatureList,       // parsed for UI
    int     SortOrder,
    bool    IsHighlighted,
    string? BadgeText,
    bool    IsActive,
    bool    IsPublic,
    int     TenantCount,            // how many tenants are on this plan
    bool    IsTrial,                // trial plan flag
    int     TrialDurationDays,      // e.g. 45 — 0 when not a trial
    List<PlanPricingDto> PlanPricings,   // per-country/currency pricing
    DateTime  CreatedAtUtc,
    DateTime? UpdatedAtUtc
);

// ── Tenant-facing display DTO (pricing page / sign-up) ───────────────
public record PlanCardDto(
    Guid    Id,
    string  Name,
    string  DisplayName,
    string? Description,
    decimal MonthlyPrice,
    decimal AnnualPrice,
    List<string> FeatureList,
    int     MaxUsers,
    int     MaxLeads,
    int     MaxDeals,
    string  StorageLimitDisplay,
    bool    IsHighlighted,
    string? BadgeText,
    int     SortOrder,
    bool    IsTrial,
    int     TrialDurationDays,
    List<PlanPricingDto> PlanPricings
);

public record PlanTenantItem(
    Guid Id,
    string TenantKey,
    string Name,
    string DefaultCurrency,
    bool IsActive,
    DateTime CreatedAtUtc,
    string? CountryName
);
public class PlanDetailDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int MaxUsers { get; set; }
    public int MaxLeads { get; set; }
    public int MaxDeals { get; set; }
    public long StorageLimitBytes { get; set; }
    public decimal MonthlyPrice { get; set; }
    public string? Features { get; set; }
    public bool IsActive { get; set; }
    public bool IsPublic { get; set; }
    public bool IsTrial { get; set; }
    public int TrialDurationDays { get; set; }
    public List<PlanPricingDto> PlanPricings { get; set; } = new();
}
// ── List item (for Super Admin grid) ─────────────────────────────────
public record PlanListItem(
    Guid    Id,
    string  Name,
    string  DisplayName,
    decimal MonthlyPrice,
    decimal AnnualPrice,
    int     MaxUsers,
    int     MaxLeads,
    int     MaxDeals,
    long    StorageLimitBytes,
    string  StorageLimitDisplay,
    int     TenantCount,
    bool    IsActive,
    bool    IsPublic,
    bool    IsHighlighted,
    int     SortOrder,
    bool    IsTrial,
    int     TrialDurationDays,
    List<PlanPricingDto> PlanPricings
);

// ── Create ────────────────────────────────────────────────────────────
public record CreatePlanCommand(
    [Required]
    [StringLength(50, MinimumLength = 2)]
    [RegularExpression(@"^[a-z0-9_-]+$", ErrorMessage = "Name must be lowercase alphanumeric, hyphens or underscores only")]
    string Name,

    [Required]
    [StringLength(100, MinimumLength = 2)]
    string DisplayName,

    [StringLength(500)]
    string? Description,

    [Range(1, 10000)]
    int MaxUsers,

    [Range(1, 1000000)]
    int MaxLeads,

    [Range(1, 1000000)]
    int MaxDeals,

    [Range(1, 1000000)]
    int MaxContacts,

    [Range(1, 1000000)]
    int MaxCompanies,

    /// <summary>Storage limit in GB — converted to bytes internally</summary>
    [Range(1, 10000)]
    int StorageLimitGB,

    [Range(0, 100000)]
    decimal MonthlyPrice,

    [Range(0, 100000)]
    decimal AnnualPrice,

    /// <summary>JSON array string e.g. ["leads","deals","reports"]</summary>
    string? Features = null,

    int  SortOrder     = 99,
    bool IsHighlighted = false,
    bool IsPublic      = true,
    bool IsActive      = true,

    [StringLength(50)]
    string? BadgeText  = null,

    /// <summary>Marks this as a time-limited trial plan (e.g. 45 days, free)</summary>
    bool IsTrial = false,

    /// <summary>Trial length in days — required (> 0) when IsTrial is true</summary>
    [Range(0, 365)]
    int TrialDurationDays = 0,

    /// <summary>Per-currency pricing (USD/THB/INR/PHP/AED). One row per priced currency.</summary>
    List<PlanPricingInput>? PlanPricings = null,

    string CreatedBy   = "SuperAdmin"
);

// ── Update ────────────────────────────────────────────────────────────
public record UpdatePlanCommand(
    Guid    PlanId,

    [Required]
    [StringLength(100, MinimumLength = 2)]
    string DisplayName,

    [StringLength(500)]
    string? Description,

    [Range(1, 10000)]
    int MaxUsers,

    [Range(1, 1000000)]
    int MaxLeads,

    [Range(1, 1000000)]
    int MaxDeals,

    [Range(1, 1000000)]
    int MaxContacts,

    [Range(1, 1000000)]
    int MaxCompanies,

    [Range(1, 10000)]
    int StorageLimitGB,

    [Range(0, 100000)]
    decimal MonthlyPrice,

    [Range(0, 100000)]
    decimal AnnualPrice,

    string? Features,

    int  SortOrder,
    bool IsHighlighted,
    bool IsPublic,
    bool IsActive,

    [StringLength(50)]
    string? BadgeText,

    bool IsTrial = false,

    [Range(0, 365)]
    int TrialDurationDays = 0,

    List<PlanPricingInput>? PlanPricings = null,

    string UpdatedBy = "SuperAdmin"
);

// ── Tenant plan change (by Super Admin) ──────────────────────────────
public record ChangeTenantPlanCommand(
    Guid   TenantId,
    string NewPlanName,
    bool   ApplyLimitsImmediately = true,   // update TenantSettings snapshot
    string ChangedBy = "SuperAdmin"
);

// ── Result for plan change ────────────────────────────────────────────
public record ChangeTenantPlanResult(
    Guid   TenantId,
    string OldPlan,
    string NewPlan,
    bool   LimitsApplied
);

// ── Summary for dashboard widget ─────────────────────────────────────
public record PlanSummaryDto(
    string Name,
    string DisplayName,
    int    TenantCount,
    decimal MonthlyRevenue   // TenantCount * MonthlyPrice
);
