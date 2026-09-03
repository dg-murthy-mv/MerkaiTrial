// =====================================================================
// PlanPricing.cs — Domain Entity (NEW)
// Location: MerkaiTrial.Domain/Entities/PlanPricing.cs
//
// Stores country/currency-specific pricing for each plan.
// One row per plan per currency.
// CountryCode = "*" means default/international pricing (USD).
// =====================================================================

namespace MerkaiTrial.Domain.Entities;

public class PlanPricing
{
    public Guid   Id             { get; set; } = Guid.NewGuid();

    /// <summary>FK to Plans.Id</summary>
    public Guid   PlanId         { get; set; }

    /// <summary>ISO currency code — USD, THB, INR, PHP, AED</summary>
    public string CurrencyCode   { get; set; } = "USD";

    /// <summary>ISO country code — TH, IN, PH, AE, or * for international default</summary>
    public string CountryCode    { get; set; } = "*";

    /// <summary>Display name — "Thailand", "India", "International"</summary>
    public string CountryName    { get; set; } = string.Empty;

    /// <summary>Currency symbol for display — $, ฿, ₹, ₱, AED</summary>
    public string CurrencySymbol { get; set; } = "$";

    /// <summary>Monthly price in local currency</summary>
    public decimal MonthlyPrice  { get; set; } = 0;

    /// <summary>Annual price in local currency (billed yearly)</summary>
    public decimal AnnualPrice   { get; set; } = 0;

    public bool   IsActive       { get; set; } = true;
    public int    SortOrder      { get; set; } = 99;

    public DateTime  CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string?   CreatedBy    { get; set; }

    // ── Navigation ────────────────────────────────────────────────────
    public Plan? Plan { get; set; }

    // ── Computed helpers ──────────────────────────────────────────────
    /// <summary>Annual savings vs 12x monthly</summary>
    public decimal AnnualSavings => (MonthlyPrice * 12) - AnnualPrice;

    /// <summary>True if annual plan offers any discount</summary>
    public bool HasAnnualDiscount => AnnualPrice > 0 && AnnualSavings > 0;

    /// <summary>Annual saving as percentage</summary>
    public int AnnualDiscountPercent =>
        MonthlyPrice > 0
            ? (int)Math.Round(AnnualSavings / (MonthlyPrice * 12) * 100)
            : 0;
}
