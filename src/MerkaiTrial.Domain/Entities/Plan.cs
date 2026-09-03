// =====================================================================
// Plan.cs — Domain Entity
// Location: MerkaiTrial.Domain/Entities/Plan.cs
//
// Global catalog of subscription plans managed by Super Admin.
// Tenant.Plan (string) references Plan.Name loosely — intentional,
// allows grandfathering tenants on old plans and custom enterprise deals.
// TenantSettings is the *applied snapshot* populated from this on creation.
// =====================================================================

namespace MerkaiTrial.Domain.Entities;

public class Plan
{
    public Guid Id { get; set; }

    /// <summary>
    /// Internal code key — "trial", "starter", "professional", "enterprise".
    /// Tenant.Plan stores this value. Lowercase, no spaces.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Human-readable label — "Free Trial", "Starter", "Professional"</summary>
    public string DisplayName { get; set; } = string.Empty;

    public string? Description { get; set; }

    // ── Usage Limits ─────────────────────────────────────────────────
    public int MaxUsers { get; set; }
    public int MaxLeads { get; set; }
    public int MaxDeals { get; set; }
    public int MaxContacts { get; set; }
    public int MaxCompanies { get; set; }

    /// <summary>Storage limit in bytes</summary>
    public long StorageLimitBytes { get; set; }

    // ── Pricing (USD base — country pricing in PlanPricings) ─────────
    /// <summary>Base monthly price in USD</summary>
    public decimal MonthlyPrice { get; set; }

    /// <summary>Base annual price in USD (typically ~2 months free)</summary>
    public decimal AnnualPrice { get; set; }

    // ── Trial ─────────────────────────────────────────────────────────
    /// <summary>True only for the trial plan — affects signup and expiry logic</summary>
    public bool IsTrial { get; set; } = false;

    /// <summary>How many days the trial lasts. 0 for non-trial plans.</summary>
    public int TrialDurationDays { get; set; } = 0;

    // ── Feature Flags ─────────────────────────────────────────────────
    /// <summary>
    /// JSON array of enabled feature keys.
    /// e.g. ["leads","deals","reports","api_access","custom_roles"]
    /// Checked by ICurrentTenantService.HasFeature(key)
    /// </summary>
    public string? Features { get; set; }

    // ── Display / Ordering ────────────────────────────────────────────
    public int SortOrder { get; set; }
    public bool IsHighlighted { get; set; } = false;
    public string? BadgeText { get; set; }

    // ── Lifecycle ─────────────────────────────────────────────────────
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// If false, plan is hidden from new signups but existing tenants keep it.
    /// Trial plan has IsPublic = false — only SuperAdmin can assign it.
    /// </summary>
    public bool IsPublic { get; set; } = true;

    // ── Audit ─────────────────────────────────────────────────────────
    public DateTime CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }

    // ── Navigation ────────────────────────────────────────────────────
    public ICollection<Tenant> Tenants { get; set; } = new List<Tenant>();
    public ICollection<PlanPricing> PlanPricings { get; set; } = new List<PlanPricing>();
}

