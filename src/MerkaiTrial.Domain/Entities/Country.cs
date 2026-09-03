// =====================================================================
// Country.cs
// Location: MerkaiTrial.Domain/Entities/Country.cs
// Updated: Added CurrencySymbol, CurrencyDecimals, NumberFormat,
//          DateFormat, TimeFormat, Timezone for localization support
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

    // ── Status ────────────────────────────────────────────────────────
    public bool IsActive      { get; set; } = true;
    public int  DisplayOrder  { get; set; } = 999;

    // ── Audit ─────────────────────────────────────────────────────────
    public DateTime  CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public bool      IsDeleted    { get; set; } = false;
}
