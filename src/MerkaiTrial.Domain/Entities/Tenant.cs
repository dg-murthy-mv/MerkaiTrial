// =====================================================================
// Tenant.cs — FINAL (matches DB022626Ver1.sql exactly)
// Location: MerkaiTrial.Domain/Entities/Tenant.cs
// Columns: Id, TenantKey, Name, DefaultCurrency, PreferredLanguage,
//          Timezone, Plan, IsActive, Domain, FromEmail, ReplyToEmail,
//          PaymentProvider, PaymentConfigJson, Phone, CountryId,
//          PublicLinkSecret,
//          CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted
// =====================================================================

namespace MerkaiTrial.Domain.Entities;

public class Tenant
{
    public Guid Id { get; set; }
    public string TenantKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    // ── Country ───────────────────────────────────────────────────────
    public Guid? CountryId { get; set; }
    public Country? Country { get; set; }

    // ── Currency + Locale ─────────────────────────────────────────────
    public string DefaultCurrency { get; set; } = string.Empty;
    public string? Timezone { get; set; }
    public string PreferredLanguage { get; set; } = "en";

    // ── Plan ──────────────────────────────────────────────────────────
    /// <summary>References Plan.Name — "trial", "starter", "professional", "enterprise"</summary>
    public string Plan { get; set; } = "starter";
    public bool IsActive { get; set; } = true;

    // ── Trial fields ──────────────────────────────────────────────────
    /// <summary>When SuperAdmin activated the trial for this tenant</summary>
    public DateTime? TrialStartedAt { get; set; }

    /// <summary>When the trial expires — set to TrialStartedAt + 45 days</summary>
    public DateTime? TrialExpiresAt { get; set; }

    /// <summary>Email/name of SuperAdmin who activated the trial</summary>
    public string? TrialActivatedBy { get; set; }

    // ── Contact / Domain ──────────────────────────────────────────────
    public string? Domain { get; set; }
    public string? FromEmail { get; set; }
    public string? ReplyToEmail { get; set; }
    public string? Phone { get; set; }

    // ── Public link ───────────────────────────────────────────────────
    public string? PublicLinkSecret { get; set; }

    // ── Payment ───────────────────────────────────────────────────────
    public string? PaymentProvider { get; set; }
    public string? PaymentConfigJson { get; set; }

    // ── Audit ─────────────────────────────────────────────────────────
    public DateTime? CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; } = false;

    // ── Computed trial helpers ─────────────────────────────────────────
    /// <summary>True when on trial plan AND trial has not yet expired</summary>
    public bool IsOnTrial()
        => string.Equals(Plan, "trial", StringComparison.OrdinalIgnoreCase)
           && TrialExpiresAt.HasValue
           && TrialExpiresAt.Value > DateTime.UtcNow;

    /// <summary>True when on trial plan AND trial has expired — account should be locked</summary>
    public bool IsTrialExpired()
        => string.Equals(Plan, "trial", StringComparison.OrdinalIgnoreCase)
           && TrialExpiresAt.HasValue
           && TrialExpiresAt.Value <= DateTime.UtcNow;

    /// <summary>Days remaining in trial. 0 if expired or not on trial.</summary>
    public int TrialDaysRemaining
    {
        get
        {
            if (!IsOnTrial() || !TrialExpiresAt.HasValue) return 0;
            return Math.Max(0, (int)(TrialExpiresAt.Value - DateTime.UtcNow).TotalDays);
        }
    }

    // ── Trial lifecycle (migration 002) ───────────────────────────────
    /// <summary>Active | Expired | Converted | Suspended. Set at
    /// provisioning; the read-only enforcement uses TrialExpiresAt, so
    /// this is a status label rather than the thing being checked.</summary>
    public string? TrialStatus { get; set; }

    /// <summary>Manual cut-off, independent of the trial clock. Lets a
    /// workspace be stopped immediately without touching TrialExpiresAt.</summary>
    public bool IsSuspended { get; set; }

    /// <summary>Plan name they moved to when the trial converted, kept for
    /// history — Plan itself changes to the new value.</summary>
    public string? ConvertedToPlan { get; set; }
}
