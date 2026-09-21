// =====================================================================
// InvoiceStatusRules.cs
// Location: MerkaiTrial.Application/Commands/Invoices/InvoiceStatusRules.cs
//
// UPDATED (018). Who sets which status:
//
//   Draft → Sent        ISSUE only (IssueInvoiceHandler): number, date, lock
//   Sent  → Viewed      by hand (the customer opened it)
//   → Cancelled ("Void") VOID only (VoidInvoiceHandler): needs a reason,
//                        refused while payments are recorded
//   Paid / PartiallyPaid DERIVED from the payment ledger — never by hand
//   Overdue             computed from the due date when shown, not stored
//
// Before 018 a person could set Draft, Sent or Cancelled directly, so an
// invoice could be "cancelled" with money against it, or skip the number.
// =====================================================================

using MerkaiTrial.Domain.Enums;

namespace MerkaiTrial.Application.Commands.Invoices;

public static class InvoiceStatusRules
{
    /// <summary>The only status a person may set through the status endpoint.</summary>
    private static readonly HashSet<InvoiceStatus> ManuallySettable = new()
    {
        InvoiceStatus.Viewed,
    };

    /// <summary>Derived from the payment ledger.</summary>
    private static readonly HashSet<InvoiceStatus> DerivedFromPayments = new()
    {
        InvoiceStatus.Paid,
        InvoiceStatus.PartiallyPaid,
    };

    public static bool IsManuallySettable(InvoiceStatus status) => ManuallySettable.Contains(status);

    public static bool IsDerived(InvoiceStatus status) => DerivedFromPayments.Contains(status);

    /// <summary>A message the user can act on when the move isn't theirs to make; null = allowed.</summary>
    public static string? RejectionReason(InvoiceStatus current, InvoiceStatus requested)
    {
        if (IsDerived(requested))
            return requested == InvoiceStatus.Paid
                ? "An invoice is marked paid by recording a payment, not by changing its status. Record the payment instead."
                : "Partial payment is set automatically when a payment is recorded.";

        if (requested == InvoiceStatus.Sent)
            return current == InvoiceStatus.Draft
                ? null   // handled as Issue by the caller
                : "This invoice has already been issued.";

        if (requested == InvoiceStatus.Cancelled)
            return "Use Void — it asks for a reason and keeps the invoice on record.";

        if (!IsManuallySettable(requested))
            return $"'{requested}' is worked out automatically and can't be set by hand.";

        if (requested == InvoiceStatus.Viewed && current is not (InvoiceStatus.Sent or InvoiceStatus.Unpaid or InvoiceStatus.Overdue))
            return "Only an issued, unpaid invoice can be marked as viewed.";

        return null;
    }

    /// <summary>
    /// The status an invoice SHOULD have given what has been paid. Called
    /// after every payment change (record or reverse) so the two can never
    /// drift.
    /// </summary>
    public static InvoiceStatus DeriveFromPayments(InvoiceStatus current, decimal invoiceTotal, decimal totalPaid)
    {
        // Void stays void; a draft has no payments by rule.
        if (current is InvoiceStatus.Cancelled or InvoiceStatus.Draft) return current;

        if (totalPaid <= 0)
            return IsDerived(current) ? InvoiceStatus.Sent : current;

        if (totalPaid >= invoiceTotal) return InvoiceStatus.Paid;

        return InvoiceStatus.PartiallyPaid;
    }
}
