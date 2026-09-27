// =====================================================================
// Country.cs
// Location: MerkaiTrial.Domain/Entities/Country.cs
// Updated: Added CurrencySymbol, CurrencyDecimals, NumberFormat,
//          DateFormat, TimeFormat, Timezone for localization support
//
// 035: Added FiscalYearStartMonth.
//
//      India's financial year runs April to March; Thailand, the
//      Philippines and the UAE default to the calendar year. This column
//      is the DEFAULT for a market — a tenant can override it in
//      TenantSettings, because a Thai subsidiary of an Indian group may
//      genuinely want April, and companies in TH/PH/AE are permitted to
//      choose a non-calendar accounting period.
// =====================================================================

namespace MerkaiTrial.Domain.Entities;

public class Country
{
    public Guid Id { get; set; }

    // ── Identity ──────────────────────────────────────────────────────
    public string Code { get; set; } = string.Empty;   // ISO 3166-1 alpha-2: IN, TH, PH, AE
    public string Name { get; set; } = string.Empty;   // India, Thailand, Philippines, UAE

    // ── Phone ─────────────────────────────────────────────────────────
    public string? DialCode { get; set; }              // +91, +66, +63, +971

    // ── Currency ──────────────────────────────────────────────────────
    public string? CurrencyCode    { get; set; }       // INR, THB, PHP, AED
    public string? CurrencySymbol  { get; set; }       // ₹, ฿, ₱, د.إ  ← NEW
    public int     CurrencyDecimals { get; set; } = 2; // 2 for all 4 target countries ← NEW

    // ── Tax ───────────────────────────────────────────────────────────
    public string?  TaxLabel      { get; set; }        // GST, VAT, VAT, VAT
    public decimal? DefaultTaxRate { get; set; }       // 18, 7, 12, 5

    // ── Localization ──────────────────────────────────────────────────
    public string? NumberFormat { get; set; }          // en-IN, th-TH, en-PH, ar-AE  ← NEW
    public string? DateFormat   { get; set; }          // dd/MM/yyyy or MM/dd/yyyy     ← NEW
    public string? TimeFormat   { get; set; }          // HH:mm or hh:mm tt           ← NEW
    public string? Timezone     { get; set; }          // IANA: Asia/Kolkata etc.      ← NEW

    // ── Fiscal year ───────────────────────────────────────────────────
    /// <summary>
    /// The month the financial year STARTS in, 1-12. India = 4 (April to
    /// March), Thailand / Philippines / UAE = 1 (calendar year).
    ///
    /// This is the market default. TenantSettings.FiscalYearStartMonth
    /// overrides it per workspace when set.
    ///
    /// Reporting only: no Lead, Deal, Quote or Invoice stores a financial
    /// year. The period a record falls in is DERIVED from its date, because
    /// a deal created in March and won in May belongs to different years
    /// depending on which question is being asked. The one exception is
    /// invoice numbering, which needs a stored year code because Indian GST
    /// requires a serial that is unique within the financial year.
    /// </summary>
    public int FiscalYearStartMonth { get; set; } = 1;

    // ── Status ────────────────────────────────────────────────────────
    public bool IsActive      { get; set; } = true;
    public int  DisplayOrder  { get; set; } = 999;

    // ── Audit ─────────────────────────────────────────────────────────
    public DateTime  CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public bool      IsDeleted    { get; set; } = false;
}
