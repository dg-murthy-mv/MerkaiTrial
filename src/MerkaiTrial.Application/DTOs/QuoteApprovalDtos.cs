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
    string? DecisionComment);

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
