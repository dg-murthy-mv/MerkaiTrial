// =====================================================================
// Payment.cs
// Location: MerkaiTrial.Domain/Entities/Payment.cs
//
// CHANGES (018)
//   ✅ A payment recorded by mistake is REVERSED, not deleted: it stays in
//      the history with Status = "Reversed", who reversed it, when and why,
//      and stops counting towards the invoice. ReversedAtUtc / ReversedBy /
//      ReversalReason are added by 018_InvoiceWorkflow.sql.
// =====================================================================

namespace MerkaiTrial.Domain.Entities
{
    public class Payment
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid InvoiceId { get; set; }

        public decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";
        public string Method { get; set; } = "BankTransfer";  // BankTransfer, CreditCard, Cash, Check, PromptPay
        public string Status { get; set; } = PaymentStatusNames.Captured;  // Captured, Reversed (Pending, Failed, Refunded kept for gateways)

        public string? ProviderTxnId { get; set; }
        public DateTime PaidAtUtc { get; set; }
        public string? Notes { get; set; }

        // ── Reversal (018) ────────────────────────────────────────────
        public DateTime? ReversedAtUtc { get; set; }
        public string? ReversedBy { get; set; }
        public string? ReversalReason { get; set; }

        // Audit fields
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }

        // Navigation properties
        public Invoice? Invoice { get; set; }
    }

    /// <summary>Payment.Status values the code writes.</summary>
    public static class PaymentStatusNames
    {
        public const string Captured = "Captured";

        /// <summary>Recorded by mistake and taken back out. Kept for the audit trail; never counted.</summary>
        public const string Reversed = "Reversed";
    }
}
