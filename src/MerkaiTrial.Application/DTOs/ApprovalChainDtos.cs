// =====================================================================
// ApprovalChainDtos.cs
// Location: MerkaiTrial.Application/DTOs/ApprovalChainDtos.cs
//
// NEW FILE (027). Everything the approval-rules screen and the approval
// queue need, in one place.
//
// A NEW FILE ON PURPOSE. The 017 quote-approval DTOs
// (QuoteApprovalSettingsDto, QuoteApprovalStateDto,
// PendingQuoteApprovalDto, QuoteApprovalRequestDto,
// SaveQuoteApprovalSettingsDto, QuoteApprovalActionDto) live in their own
// file and are NOT touched by this round — every one of them keeps the
// exact shape it had, so Pages/Quotes/Detail.cshtml.cs and anything else
// holding them still compiles untouched. The chain is expressed in the
// new records below instead.
// =====================================================================

using MerkaiTrial.Domain.Entities;

namespace MerkaiTrial.Application.DTOs;

// ── Reading: the rules screen ─────────────────────────────────────────

/// <summary>One step of a chain, with the approver already resolved to a name.</summary>
public record ApprovalStepDto(
    Guid Id,
    int StepOrder,
    string? Name,
    string EffectiveName,
    ApproverKind ApproverKind,
    Guid? ApproverRoleId,
    Guid? ApproverUserId,
    bool AllowSelfApproval,
    // "Everyone with the role Senior Manager (3 people)" — written out so
    // the page doesn't have to reassemble it from four fields.
    string ApproverSummary,
    // How many people this step actually resolves to TODAY. Zero is the
    // number that matters: a step nobody can sign strands the quote.
    int ApproverCount,
    // Set when the step points at something that no longer works — a
    // deleted role, a deactivated person, a team with no manager.
    string? Problem = null);

public record ApprovalRuleDto(
    Guid Id,
    string Name,
    string? Description,
    int SortOrder,
    bool IsActive,
    decimal? DiscountOverPercent,
    decimal? TotalOverAmount,
    ApprovalConditionMode ConditionMode,
    bool IsCatchAll,
    List<ApprovalStepDto> Steps,
    // "A line more than 10% below list, or a total above ฿500,000"
    string ConditionSummary,
    // "This rule matches every quote" / "No steps — a quote matching this
    // rule could never be approved"
    string? Warning = null,
    // Approval requests part-way through this rule's chain right now.
    // Editing or deleting the rule changes what THEY are asked for from
    // their next step onward, so the page has to be able to warn.
    int InFlightCount = 0);

/// <summary>A role that can be named as an approver.</summary>
public record ApproverRoleDto(Guid Id, string DisplayName, bool IsSystemRole, int UserCount);

/// <summary>A person who can be named as an approver.</summary>
public record ApproverUserDto(Guid Id, string FullName, string Email, bool IsTenantAdmin);

/// <summary>Everything the rules screen draws, in one call.</summary>
public record ApprovalRulesPageDto(
    // The master switch. Off = no quote ever needs approval, whatever the
    // rules say.
    bool IsEnabled,
    List<ApprovalRuleDto> Rules,
    // The pickers for "named role" and "named person" steps.
    List<ApproverRoleDto> Roles,
    List<ApproverUserDto> Users,
    DateTime? UpdatedAtUtc,
    string? UpdatedBy,
    // True when this workspace still has no rule at all, so every quote
    // sends freely. Worth saying out loud.
    bool HasNoRules);

// ── Writing ───────────────────────────────────────────────────────────

public record SaveApprovalStepDto(
    string? Name,
    ApproverKind ApproverKind,
    Guid? ApproverRoleId,
    Guid? ApproverUserId,
    bool AllowSelfApproval);

/// <summary>
/// One rule and its whole chain, saved together. Id null = create.
/// The steps replace whatever the rule had: a chain is only meaningful
/// as a whole, and diffing step-by-step would let a half-applied save
/// leave a gap in the order.
/// </summary>
public record SaveApprovalRuleDto(
    Guid? Id,
    string Name,
    string? Description,
    bool IsActive,
    decimal? DiscountOverPercent,
    decimal? TotalOverAmount,
    ApprovalConditionMode ConditionMode,
    List<SaveApprovalStepDto> Steps);

public record ReorderApprovalRulesDto(List<Guid> RuleIdsInOrder);

public record SetApprovalsEnabledDto(bool IsEnabled);

/// <summary>
/// How many approval requests are mid-chain on a rule, so the page can
/// warn before a delete rather than after. A named record rather than an
/// anonymous object, because Admin.Web deserialises it.
/// </summary>
public record InFlightCountDto(int Count);

// ── The queue, and what happens after a decision ──────────────────────

/// <summary>
/// One request waiting for me, with the chain context the old
/// PendingQuoteApprovalDto had no room for: which step this is, what it
/// is called, and whether I'm being asked as the named approver or
/// standing in as a workspace admin.
/// </summary>
public record PendingApprovalChainDto(
    Guid RequestId,
    Guid QuoteId,
    string QuoteNumber,
    string DealTitle,
    string ContactName,
    string RequestedByName,
    DateTime RequestedAtUtc,
    string? RequestComment,
    decimal QuoteTotal,
    string Currency,
    decimal MaxLineDiscountPercent,
    List<string> Reasons,
    // ── chain ──
    string? RuleName,
    int CurrentStepOrder,
    int TotalSteps,
    string StepName,
    string ApproverSummary,
    // True when the only reason I can decide this is that I'm a workspace
    // admin — the step names somebody else. The page says so, because
    // signing on somebody's behalf should be a deliberate act.
    bool IsAdminOverride,
    // Decisions already taken on earlier steps, oldest first.
    List<ApprovalDecisionDto> SoFar);

public record ApprovalDecisionDto(
    int StepOrder,
    string? StepName,
    string Decision,
    string DecidedByName,
    DateTime DecidedAtUtc,
    string? Comment,
    bool WasAdminOverride);

/// <summary>
/// What an approve/request-changes call did. Returned so the page can say
/// "approved — now with Anand for step 2 of 3" instead of the flatly
/// wrong "the rep can send it now".
///
/// Returning a value where 017 returned void is source-compatible: an
/// existing `await _approvals.ApproveAsync(...)` that ignores the result
/// still compiles unchanged.
/// </summary>
public record ApprovalOutcomeDto(
    // True when the chain is finished and the quote is Approved.
    bool ChainComplete,
    // True when it was sent back to the rep.
    bool SentBack,
    int StepJustDecided,
    int TotalSteps,
    // Only when the chain moves on.
    int? NextStepOrder,
    string? NextStepName,
    List<string> NextApprovers,
    // Ready-made sentence for the banner.
    string Message);
