// =====================================================================
// InvoiceWorkflowDtos.cs
// Location: MerkaiTrial.Application/DTOs/InvoiceWorkflowDtos.cs
//
// NEW FILE (018). What the invoice Detail/Index pages need to know about
// where an invoice is in its life and what the current user may do next.
// Kept separate from InvoiceDto so nothing that already uses InvoiceDto
// changes shape.
// =====================================================================

namespace MerkaiTrial.Application.DTOs;

public record InvoiceWorkflowDto(
    Guid InvoiceId,
    string Status,

    // Draft = still has a DRAFT-XXXXXXXX placeholder, not yet a tax invoice.
    bool IsDraft,

    // Void = issued, then cancelled with a reason. Stored status Cancelled.
    bool IsVoid,

    // Raised from an accepted quote — lines follow the quote and can't be edited.
    bool FromQuote,

    DateTime? IssuedAtUtc,
    string? IssuedBy,
    DateTime? VoidedAtUtc,
    string? VoidedBy,
    string? VoidReason,

    bool CanEdit,
    bool CanEditLines,

    bool CanIssue,
    // Why Issue isn't available, in words for the user (null when it is).
    string? IssueBlockedReason,
    // Manual invoices over the workspace's limits: which, and who may issue.
    List<string> IssueRuleReasons,
    List<string> IssueApproverNames,

    bool CanVoid,
    string? VoidBlockedReason,

    bool CanDelete,
    bool CanRecordPayment,

    // Payments that can still be reversed (captured, not already reversed).
    List<Guid> ReversiblePaymentIds);

public record VoidInvoiceDto(string Reason);

public record ReversePaymentDto(string Reason);
