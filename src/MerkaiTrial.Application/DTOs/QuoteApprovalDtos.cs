// =====================================================================
// QuoteApprovalDtos.cs
// Location: MerkaiTrial.Application/DTOs/QuoteApprovalDtos.cs
//
// NEW FILE (017). Shared by the WebApi and Admin.Web.
// Nothing here carries a TenantId — the API always uses the caller's.
// =====================================================================

namespace MerkaiTrial.Application.DTOs;

/// <summary>The tenant's approval rules, as shown on Settings → Quote approvals.</summary>
public record QuoteApprovalSettingsDto(
    bool IsEnabled,
    decimal? MaxDiscountPercent,
    decimal? MaxQuoteTotal,
    bool IsDefault,
    DateTime? UpdatedAtUtc,
    string? UpdatedBy);

public record SaveQuoteApprovalSettingsDto(
    bool IsEnabled,
    decimal? MaxDiscountPercent,
    decimal? MaxQuoteTotal);

/// <summary>One approval request and its outcome.</summary>
///
/// (033) The last three carry the CHAIN. QuoteApprovalRequest has held
/// RuleName, CurrentStepOrder and TotalSteps since 027, but ToDto never
/// passed them on, so the quote Detail panel could not say "Step 2 of 3"
/// and had to hedge: "if your workspace's rule has more than one step…".
///
/// They are OPTIONAL TRAILING parameters on purpose. Every existing
/// positional `new QuoteApprovalRequestDto(...)` still compiles untouched,
/// which is the same rule round 026 followed when it widened TeamDto.
///
/// The defaults describe a pre-027 request honestly: one step, no rule.
public record QuoteApprovalRequestDto(
    Guid Id,
    Guid QuoteId,
    string Status,
    string RequestedByName,
    DateTime RequestedAtUtc,
    string? RequestComment,
    List<string> Reasons,
    decimal QuoteTotal,
    decimal MaxLineDiscountPercent,
    string Currency,
    string? DecidedByName,
    DateTime? DecidedAtUtc,
    string? DecisionComment,

    /// <summary>The rule's name when the request was raised — a snapshot.</summary>
    string? RuleName = null,

    /// <summary>Which step is (or was) waiting for a decision. 1-based.</summary>
    int CurrentStepOrder = 1,

    /// <summary>How many steps the chain had when it started.</summary>
    int TotalSteps = 1)
{
    /// <summary>"Step 2 of 3", or null for a single-step chain — there is
    /// nothing to say about step 1 of 1, and saying it is noise.</summary>
    public string? StepLabel => TotalSteps > 1 ? $"Step {CurrentStepOrder} of {TotalSteps}" : null;

    /// <summary>True when approving the current step finishes the chain.</summary>
    public bool IsFinalStep => CurrentStepOrder >= TotalSteps;
}

/// <summary>
/// Everything the quote Detail page needs to draw the approval panel —
/// and what the current user is allowed to do about it.
/// </summary>
public record QuoteApprovalStateDto(
    Guid QuoteId,
    string QuoteStatus,

    // The tenant has approval rules switched on.
    bool RulesEnabled,

    // The quote as it stands breaks at least one rule.
    bool RequiresApproval,

    // Why — one line per rule broken.
    List<string> Reasons,

    // The deepest line discount on the quote, in percent.
    decimal MaxLineDiscountPercent,

    // The current user is a workspace admin, so the rules don't apply to them.
    bool IsExempt,

    bool CanSubmit,
    bool CanApprove,
    bool CanRecall,

    // The quote may go to the customer now (subject to Quotes.Update).
    bool CanSend,

    // Who can approve this quote (never includes the requester).
    List<string> ApproverNames,

    QuoteApprovalRequestDto? Pending,
    List<QuoteApprovalRequestDto> History);

public record QuoteApprovalActionDto(string? Comment);

/// <summary>A row on the "Waiting for my approval" page.</summary>
public record PendingQuoteApprovalDto(
    Guid RequestId,
    Guid QuoteId,
    string QuoteNumber,
    string DealTitle,
    string CustomerName,
    string RequestedByName,
    DateTime RequestedAtUtc,
    string? RequestComment,
    decimal QuoteTotal,
    string Currency,
    decimal MaxLineDiscountPercent,
    List<string> Reasons);
