using MerkaiTrial.Domain.Enums;
namespace MerkaiTrial.Domain.Entities
{
    public class Invoice
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid? QuoteId { get; set; }
        public Guid? DealId { get; set; }

        public string Number { get; set; } = string.Empty;  // INV-0001, INV-0002, etc.
        public DateTime IssueDateUtc { get; set; }
        public DateTime? DueDateUtc { get; set; }
        public string Currency { get; set; } = "USD";
        public InvoiceStatus Status { get; set; }

        // Breakdown amounts (matching Quotes structure)
        public decimal Subtotal { get; set; }
        public decimal DiscountTotal { get; set; }
        public decimal TaxTotal { get; set; }
        public decimal Total { get; set; }  // Subtotal + TaxTotal (kept for compatibility)
        public decimal Balance { get; set; }  // Remaining unpaid amount

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

        // Calculated properties
        public decimal TotalPaid => Payments?.Where(p => !p.IsDeleted).Sum(p => p.Amount) ?? 0;
        public bool IsFullyPaid => Balance <= 0;
        public bool IsOverdue => DueDateUtc.HasValue && DueDateUtc.Value < DateTime.UtcNow && !IsFullyPaid && Status != InvoiceStatus.Cancelled;
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

    
}
