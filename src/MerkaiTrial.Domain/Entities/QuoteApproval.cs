// =====================================================================
// QuoteApproval.cs
// Location: MerkaiTrial.Domain/Entities/QuoteApproval.cs
//
// COMPLETE FILE — replaces the 017 version.
//
//   QuoteApprovalSettings — one row per tenant. After 027 this is just
//     the master on/off switch plus the ORIGINAL limits, kept so the
//     migration has something to read and so an existing workspace can
//     see where its first rule came from. WHEN a quote needs approval is
//     now decided by ApprovalRules (see ApprovalRule.cs); nothing in the
//     engine reads MaxDiscountPercent or MaxQuoteTotal any more.
//
//   QuoteApprovalRequests — one row per "please approve" and its outcome.
//     After 027 a request walks a CHAIN: it records which rule it
//     matched, how many steps that rule has, and which step it is
//     waiting on. Each decision along the way gets its own
//     QuoteApprovalDecision row.
//
// WHO APPROVES (worked out when needed, not stored)
//   The rule's step at CurrentStepOrder says who. Four kinds:
//     • the deal owner's team managers  (what 017 always did)
//     • everyone holding a named role   (the Senior Manager case)
//     • one named person
//     • any workspace admin
//   A workspace admin can always decide, whatever the step says — that
//   is the escape hatch that stops a quote getting stuck behind somebody
//   on leave. When they do, the decision is marked as an override.
// =====================================================================

namespace MerkaiTrial.Domain.Entities;

public class QuoteApprovalSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>
    /// Master switch. Off = no quote ever needs approval, whatever the
    /// rules say. This is the one field on here the engine still reads.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// LEGACY (017). Superseded by ApprovalRule.DiscountOverPercent. Kept
    /// so 027's migration can carry a workspace's old limit into its first
    /// rule, and so the settings page can show where that rule came from.
    /// Nothing in the engine reads it.
    /// </summary>
    public decimal? MaxDiscountPercent { get; set; } = QuoteApprovalDefaults.MaxDiscountPercent;

    /// <summary>LEGACY (017). Superseded by ApprovalRule.TotalOverAmount.</summary>
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

    // ── 027: the chain ────────────────────────────────────────────────

    /// <summary>
    /// The rule this request is walking. Null for requests created before
    /// 027, and for a rule deleted since — which is why the name is
    /// snapshotted separately.
    /// </summary>
    public Guid? ApprovalRuleId { get; set; }

    /// <summary>
    /// The rule's name at the moment of submission. A snapshot on purpose:
    /// renaming or deleting a rule must not rewrite history.
    /// </summary>
    public string? RuleName { get; set; }

    /// <summary>Which step is waiting for a decision. 1-based.</summary>
    public int CurrentStepOrder { get; set; } = 1;

    /// <summary>
    /// How many steps this chain had when it started. Snapshotted, so
    /// adding a step to the rule tomorrow can't move the goalposts for a
    /// request already in flight.
    /// </summary>
    public int TotalSteps { get; set; } = 1;

    // ── the final decision (the last step's) ──────────────────────────

    public Guid? DecidedByUserId { get; set; }
    public string? DecidedByName { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionComment { get; set; }

    public Quote? Quote { get; set; }

    /// <summary>Every decision made along the chain, oldest first.</summary>
    public ICollection<QuoteApprovalDecision> Decisions { get; set; } = new List<QuoteApprovalDecision>();

    /// <summary>"Step 2 of 3" — or just "1 step" for a single-step chain.</summary>
    public string StepLabel =>
        TotalSteps <= 1 ? "1 step" : $"Step {CurrentStepOrder} of {TotalSteps}";

    /// <summary>True when approving the current step finishes the chain.</summary>
    public bool IsFinalStep => CurrentStepOrder >= TotalSteps;
}

public static class QuoteApprovalRequestStatus
{
    /// <summary>Waiting for a decision on CurrentStepOrder. At most one per quote (filtered unique index).</summary>
    public const string Pending = "Pending";

    /// <summary>Every step approved.</summary>
    public const string Approved = "Approved";

    /// <summary>An approver at ANY step sent it back to Draft with a comment.</summary>
    public const string ChangesRequested = "ChangesRequested";

    /// <summary>The requester (or an admin) withdrew it before a decision.</summary>
    public const string Recalled = "Recalled";

    /// <summary>It was approved, then the quote was edited — the approval no longer applies.</summary>
    public const string Superseded = "Superseded";
}
