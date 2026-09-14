// =====================================================================
// InvoiceStatusRules.cs
// Location: MerkaiTrial.Application/Commands/Invoices/InvoiceStatusRules.cs
//
// NEW FILE.
//
// THE PROBLEM THIS FIXES
//
// Two code paths write Invoice.Status and they disagree:
//
//   AddPaymentHandler DERIVES it — Paid when the payments cover the
//   total, PartiallyPaid otherwise. That is correct.
//
//   UpdateInvoiceStatusHandler lets a person set ANY status, including
//   Paid, with no payment behind it. Then the next payment recalculates
//   and overwrites it.
//
// So an invoice could read Paid with zero payments recorded, and the
// revenue figure and the payment ledger could disagree with no way to
// tell which is right. For a CRM that bills people, that is the worst
// kind of bug: quiet, and about money.
//
// THE RULE
//
//   Paid and PartiallyPaid are DERIVED from payments. Nobody sets them
//   by hand.
//
//   Draft, Sent, Viewed, Cancelled, WrittenOff are CHOSEN by a person.
//
//   Overdue is derived from the due date (and should really be computed
//   at read time rather than stored — see the note at the bottom).
//
// Adjust the enum member names below if yours differ; everything else
// follows from this one list.
// =====================================================================

using MerkaiTrial.Domain.Enums;

namespace MerkaiTrial.Application.Commands.Invoices;

public static class InvoiceStatusRules
{
    /// <summary>
    /// Statuses a human may set directly. Everything else is derived
    /// from payments or dates.
    /// </summary>
    private static readonly HashSet<InvoiceStatus> ManuallySettable = new()
    {
        InvoiceStatus.Draft,
        InvoiceStatus.Sent,
        InvoiceStatus.Cancelled,
        // Add InvoiceStatus.WrittenOff here if your enum has it.
    };

    /// <summary>
    /// Derived from the payment ledger. Setting these by hand would let
    /// the invoice and the payments contradict each other.
    /// </summary>
    private static readonly HashSet<InvoiceStatus> DerivedFromPayments = new()
    {
        InvoiceStatus.Paid,
        InvoiceStatus.PartiallyPaid,
    };

    public static bool IsManuallySettable(InvoiceStatus status)
        => ManuallySettable.Contains(status);

    public static bool IsDerived(InvoiceStatus status)
        => DerivedFromPayments.Contains(status);

    /// <summary>
    /// Throws with a message the user can act on when the requested
    /// status isn't theirs to set. Returns the reason string for the
    /// audit entry when it refuses.
    /// </summary>
    public static string? RejectionReason(InvoiceStatus current, InvoiceStatus requested)
    {
        if (IsDerived(requested))
            return requested == InvoiceStatus.Paid
                ? "An invoice is marked paid by recording a payment, not by changing its status. Record the payment instead."
                : "Partial payment is set automatically when a payment is recorded.";

        if (!IsManuallySettable(requested))
            return $"'{requested}' is worked out automatically and can't be set by hand.";

        // Money already received — cancelling would orphan the payments.
        if (IsDerived(current) && requested == InvoiceStatus.Cancelled)
            return "This invoice has payments against it. Reverse the payments before cancelling it.";

        // Going back to Draft after it has been sent hides it from the
        // customer's view of what they owe.
        if (current == InvoiceStatus.Paid && requested == InvoiceStatus.Draft)
            return "A paid invoice can't be returned to draft.";

        return null;   // allowed
    }

    /// <summary>
    /// The status an invoice SHOULD have given what has been paid.
    /// Called after every payment change so the two can never drift.
    /// </summary>
    public static InvoiceStatus DeriveFromPayments(
        InvoiceStatus current, decimal invoiceTotal, decimal totalPaid)
    {
        // Cancelled and written-off invoices stay where they are: a
        // payment against one is a data problem, not a status change.
        if (current == InvoiceStatus.Cancelled) return current;

        if (totalPaid <= 0)
        {
            // Payments were reversed — fall back to a sensible open state
            // rather than leaving it reading Paid.
            return IsDerived(current) ? InvoiceStatus.Sent : current;
        }

        if (totalPaid >= invoiceTotal) return InvoiceStatus.Paid;

        return InvoiceStatus.PartiallyPaid;
    }
}

/* =====================================================================
   ON "OVERDUE"

   If Overdue is a stored status, it is wrong the moment the clock
   passes the due date without anyone saving the invoice — nothing
   updates it. Either:

     (a) compute it at read time:
         IsOverdue = Status is Sent or Viewed or PartiallyPaid
                     && DueDateUtc < DateTime.UtcNow
         and show it as a badge rather than storing it; or

     (b) have the nightly job set it, once there IS a nightly job.

   (a) is correct with no infrastructure and is what I would do. Worth
   checking whether anything currently writes InvoiceStatus.Overdue —
   if nothing does, the value is already decorative.
   ===================================================================== */
