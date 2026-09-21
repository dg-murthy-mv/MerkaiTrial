// =====================================================================
// Invoice.cs
// Location: MerkaiTrial.Domain/Entities/Invoice.cs
//
// CHANGES (018 — invoice workflow)
//   ✅ IssuedAtUtc / IssuedBy — when the invoice was issued (left Draft).
//   ✅ VoidedAtUtc / VoidedBy / VoidReason — an issued invoice is never
//      deleted or edited; it is VOIDED, with a reason. (Stored status stays
//      InvoiceStatus.Cancelled; the screens call it "Void".)
//   ✅ IsDraftNumber — a draft carries a placeholder "DRAFT-XXXXXXXX"; the
//      real INV-nnnn is given out only when it is issued, so deleting a
//      draft never leaves a gap in the tax invoice sequence.
//   ✅ TotalPaid counts CAPTURED payments only (reversed ones never).
//   ✅ IsOverdue ignores drafts (a draft isn't owed yet).
//   Columns are added by 018_InvoiceWorkflow.sql.
// =====================================================================

using MerkaiTrial.Domain.Enums;

namespace MerkaiTrial.Domain.Entities
{
    public class Invoice
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid? QuoteId { get; set; }
        public Guid? DealId { get; set; }

        public string Number { get; set; } = string.Empty;  // INV-0001 once issued; DRAFT-XXXXXXXX before
        public DateTime IssueDateUtc { get; set; }
        public DateTime? DueDateUtc { get; set; }
        public string Currency { get; set; } = "USD";
        public InvoiceStatus Status { get; set; }

        // Breakdown amounts (matching Quotes structure)
        public decimal Subtotal { get; set; }       // GROSS — before line discounts
        public decimal DiscountTotal { get; set; }
        public decimal TaxTotal { get; set; }
        public decimal Total { get; set; }          // Subtotal − DiscountTotal + TaxTotal
        public decimal Balance { get; set; }        // Total − captured payments

        // Legacy fields (kept for compatibility with existing schema)
        public decimal Amount { get; set; }  // Same as Total

        // Payment gateway fields (existing)
        public string? PromptPayQrImageUrl { get; set; }
        public string? GatewayRef { get; set; }
        public string? Provider { get; set; }
        public string? ProviderRef { get; set; }
        public string? CheckoutUrl { get; set; }

        // Additional fields
        public string? PdfUrl { get; set; }
        public string? Notes { get; set; }

        // ── Workflow (018) ────────────────────────────────────────────
        public DateTime? IssuedAtUtc { get; set; }
        public string? IssuedBy { get; set; }
        public DateTime? VoidedAtUtc { get; set; }
        public string? VoidedBy { get; set; }
        public string? VoidReason { get; set; }

        // Audit fields
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }

        // Navigation properties
        public Quote? Quote { get; set; }
        public Deal? Deal { get; set; }
        public ICollection<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();
        public ICollection<Payment> Payments { get; set; } = new List<Payment>();

        // Calculated properties (not mapped — no setter)
        // Captured payments only — the same rule the handlers, queries and
        // reports use. Reversed / pending / failed never count.
        public decimal TotalPaid => Payments?
            .Where(p => !p.IsDeleted && p.Status == PaymentStatusNames.Captured)
            .Sum(p => p.Amount) ?? 0;

        public bool IsFullyPaid => Balance <= 0;

        public bool IsDraftNumber => Number?.StartsWith(InvoiceNumbering.DraftPrefix, StringComparison.Ordinal) == true;

        public bool IsOverdue =>
            DueDateUtc.HasValue && DueDateUtc.Value < DateTime.UtcNow && !IsFullyPaid &&
            Status != InvoiceStatus.Cancelled && Status != InvoiceStatus.Draft;
    }

    public class InvoiceLine
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid InvoiceId { get; set; }
        public Guid? ProductId { get; set; }

        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public decimal UnitPrice { get; set; }
        public int Quantity { get; set; }
        public decimal LineDiscount { get; set; }
        public decimal TaxRate { get; set; }  // 0.18 for 18%

        // Legacy field (kept for compatibility)
        public decimal Amount { get; set; }  // Total line amount (calculated)

        // Audit fields
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }

        // Navigation properties
        public Invoice? Invoice { get; set; }
        public Product? Product { get; set; }

        // Calculated properties (same as QuoteItem)
        public decimal LineTotal => (UnitPrice * Quantity) - LineDiscount;
        public decimal LineTax => LineTotal * TaxRate;
        public decimal LineGrandTotal => LineTotal + LineTax;
    }

    /// <summary>Invoice numbers — the placeholder a draft carries, and the real sequence.</summary>
    public static class InvoiceNumbering
    {
        public const string DraftPrefix = "DRAFT-";
        public const string IssuedPrefix = "INV-";

        /// <summary>"DRAFT-3F9A1C2B" — unique enough per tenant; the unique index is the backstop.</summary>
        public static string NewDraftNumber()
            => DraftPrefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    }
}
