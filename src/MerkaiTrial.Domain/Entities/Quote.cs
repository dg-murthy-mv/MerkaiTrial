using MerkaiTrial.Domain.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace MerkaiTrial.Domain.Entities
{
    public class Quote
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid DealId { get; set; }

        // Quote Information
        public string Number { get; set; } = string.Empty;  // QUO-001, QUO-002, etc.
        public DateTime IssueDateUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public string Currency { get; set; } = "USD";
        public QuoteStatus Status { get; set; } = QuoteStatus.Draft;

        // Financial Totals
        public decimal Subtotal { get; set; }
        public decimal DiscountTotal { get; set; }
        public decimal TaxTotal { get; set; }
        public decimal GrandTotal { get; set; }

        // Optional Links
        public string? PaymentLinkUrl { get; set; }
        public string? PublicLinkToken { get; set; }
        public string? PdfUrl { get; set; }

        // Audit Fields
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }

        // Navigation Properties
        public Deal Deal { get; set; } = null!;
        public ICollection<QuoteItem> Items { get; set; } = new List<QuoteItem>();
    }
}
