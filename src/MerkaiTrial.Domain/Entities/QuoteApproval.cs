// =====================================================================
// QuoteApproval.cs
// Location: MerkaiTrial.Domain/Entities/QuoteApproval.cs
//
// NEW FILE (017). Two tables:
//
//   QuoteApprovalSettings — one row per tenant: WHEN a quote needs
//     approval. No row = the defaults below (on, discount over 10%).
//
//   QuoteApprovalRequests — one row per "please approve" and its outcome.
//     This is the approval history: who asked, why it was needed, who
//     decided, what they said. Rows are never deleted or edited after the
//     decision.
//
// WHO APPROVES (worked out when needed, not stored)
//   • Managers of the deal owner's team (Settings → Record visibility →
//     "Manages"). If the deal has no owner, the requester's team.
//   • Workspace admins — always, and the fallback when a team has no
//     manager.
//   • Never the person who asked.
//   Admins are exempt from the rules: an admin can send any quote.
// =====================================================================

namespace MerkaiTrial.Domain.Entities;

public class QuoteApprovalSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>Master switch. Off = no quote ever needs approval.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// A quote needs approval when ANY line is priced more than this many
    /// percent below its list price (catalog items) or has a line discount
    /// above this percentage (custom items). Null = no discount rule.
    /// </summary>
    public decimal? MaxDiscountPercent { get; set; } = QuoteApprovalDefaults.MaxDiscountPercent;

    /// <summary>A quote whose grand total is above this needs approval. Null = no limit.</summary>
    public decimal? MaxQuoteTotal { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
}

public static class QuoteApprovalDefaults
{
    public const bool IsEnabled = true;
    public const decimal MaxDiscountPercent = 10m;
}

public class QuoteApprovalRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid QuoteId { get; set; }

    /// <summary>QuoteApprovalRequestStatus.*</summary>
    public string Status { get; set; } = QuoteApprovalRequestStatus.Pending;

    public Guid RequestedByUserId { get; set; }
    public string RequestedByName { get; set; } = string.Empty;
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public string? RequestComment { get; set; }

    /// <summary>Why approval was needed, one reason per line — a snapshot, so it still reads right after the rules change.</summary>
    public string Reasons { get; set; } = string.Empty;

    /// <summary>Snapshot of the quote when it was submitted.</summary>
    public decimal QuoteTotal { get; set; }
    public decimal MaxLineDiscountPercent { get; set; }
    public string Currency { get; set; } = string.Empty;

    public Guid? DecidedByUserId { get; set; }
    public string? DecidedByName { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionComment { get; set; }

    public Quote? Quote { get; set; }
}

public static class QuoteApprovalRequestStatus
{
    /// <summary>Waiting for a decision. At most one per quote (filtered unique index).</summary>
    public const string Pending = "Pending";

    public const string Approved = "Approved";

    /// <summary>Approver sent it back to Draft with a comment.</summary>
    public const string ChangesRequested = "ChangesRequested";

    /// <summary>The requester (or an admin) withdrew it before a decision.</summary>
    public const string Recalled = "Recalled";

    /// <summary>It was approved, then the quote was edited — the approval no longer applies.</summary>
    public const string Superseded = "Superseded";
}
