// =====================================================================
// ApprovalRule.cs
// Location: MerkaiTrial.Domain/Entities/ApprovalRule.cs
//
// NEW FILE (027). Three tables, one idea: a quote that breaks a rule
// walks that rule's chain of steps, in order, and is only approved when
// the last step says yes.
//
//   ApprovalRule   — a named rule with conditions. FIRST MATCH WINS, in
//                    SortOrder, so the tightest rule goes first. One
//                    quote is never governed by two rules at once: that
//                    is what made the old single-setting model
//                    impossible to explain.
//
//   ApprovalStep   — step 1, step 2, step 3 … Each names WHO signs it,
//                    by kind. Step 2 is only asked once step 1 approves.
//
//   QuoteApprovalDecision — one row per decision, so the history reads
//                    "Priya approved step 1, then Anand approved step 2"
//                    instead of collapsing to a single verdict.
//
// WHY A ROLE IS AN APPROVER KIND
//   The old engine worked approvers out at run time as "managers of the
//   deal owner's team, plus workspace admins". That is a good default
//   and it survives as ApproverKind.TeamManagers. But it could not
//   express "the Senior Manager signs off deals over 5 lakh", because a
//   role could not be named anywhere. ApproverKind.Role is that.
// =====================================================================

namespace MerkaiTrial.Domain.Entities;

/// <summary>How the conditions on a rule combine.</summary>
public enum ApprovalConditionMode
{
    /// <summary>Either condition is enough to trigger the rule. The usual case.</summary>
    Any = 0,

    /// <summary>Every condition that is set must be met. "Big AND heavily discounted".</summary>
    All = 1
}

/// <summary>Who is asked to sign one step.</summary>
public enum ApproverKind
{
    /// <summary>
    /// Managers of the deal owner's team — what the app did before 027,
    /// and still the sensible default. Falls back to workspace admins when
    /// the team has no manager who can sign in.
    /// </summary>
    TeamManagers = 0,

    /// <summary>Everyone holding a named role. The "Senior Manager" case.</summary>
    Role = 1,

    /// <summary>One named person.</summary>
    User = 2,

    /// <summary>Any workspace admin.</summary>
    WorkspaceAdmin = 3
}

public class ApprovalRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Ascending. The first ACTIVE rule that matches is the one that applies.</summary>
    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Matches when any line is priced more than this many percent below
    /// its list price. Null = this rule doesn't look at discount.
    /// </summary>
    public decimal? DiscountOverPercent { get; set; }

    /// <summary>
    /// Matches when the quote's grand total is above this. Null = this rule
    /// doesn't look at size.
    /// </summary>
    public decimal? TotalOverAmount { get; set; }

    public ApprovalConditionMode ConditionMode { get; set; } = ApprovalConditionMode.Any;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }

    public ICollection<ApprovalStep> Steps { get; set; } = new List<ApprovalStep>();

    /// <summary>
    /// A rule with no condition at all matches every quote. That is a
    /// legitimate thing to want ("everything goes past the manager"), but
    /// it is also what an unfinished rule looks like, so the page says so
    /// before you save it.
    /// </summary>
    public bool IsCatchAll => DiscountOverPercent is null && TotalOverAmount is null;
}

public class ApprovalStep
{
    /// <summary>
    /// Ten is far past anything a sales process needs, and it stops a
    /// runaway form creating a chain nobody can ever finish. The database
    /// enforces it too.
    /// </summary>
    public const int MaxStepsPerRule = 10;

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ApprovalRuleId { get; set; }

    /// <summary>1-based. Asked in ascending order.</summary>
    public int StepOrder { get; set; }

    /// <summary>Optional label, e.g. "Sales manager sign-off".</summary>
    public string? Name { get; set; }

    public ApproverKind ApproverKind { get; set; } = ApproverKind.TeamManagers;

    /// <summary>Set only when <see cref="ApproverKind"/> is Role.</summary>
    public Guid? ApproverRoleId { get; set; }

    /// <summary>Set only when <see cref="ApproverKind"/> is User.</summary>
    public Guid? ApproverUserId { get; set; }

    /// <summary>
    /// Off by default — nobody signs off their own discount. Worth having
    /// for a one-person workspace, where the alternative is a quote that
    /// can never be sent.
    /// </summary>
    public bool AllowSelfApproval { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }

    public ApprovalRule? Rule { get; set; }

    /// <summary>What to call this step when it has no name of its own.</summary>
    public string EffectiveName =>
        string.IsNullOrWhiteSpace(Name) ? $"Step {StepOrder}" : Name!;
}

/// <summary>
/// One decision on one step. Rows are never edited after the fact — this
/// is the audit trail the customer reads when they ask who approved what.
/// </summary>
public class QuoteApprovalDecision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid RequestId { get; set; }

    public int StepOrder { get; set; }

    /// <summary>Snapshot, so the history still reads right after the rule is edited.</summary>
    public string? StepName { get; set; }

    /// <summary>QuoteApprovalDecisionKind.*</summary>
    public string Decision { get; set; } = QuoteApprovalDecisionKind.Approved;

    public Guid DecidedByUserId { get; set; }
    public string DecidedByName { get; set; } = string.Empty;
    public DateTime DecidedAtUtc { get; set; } = DateTime.UtcNow;
    public string? Comment { get; set; }

    /// <summary>
    /// True when a workspace admin signed a step that named somebody else.
    /// "The Senior Manager step was signed by the admin" is a different
    /// fact from "the Senior Manager signed it", and the history says which.
    /// </summary>
    public bool WasAdminOverride { get; set; }

    public QuoteApprovalRequest? Request { get; set; }
}

public static class QuoteApprovalDecisionKind
{
    public const string Approved = "Approved";
    public const string ChangesRequested = "ChangesRequested";
}
