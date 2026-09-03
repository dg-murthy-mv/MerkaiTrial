using System.ComponentModel.DataAnnotations.Schema;

namespace MerkaiTrial.Domain.Entities
{
    public class QuoteItem
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid QuoteId { get; set; }
        public Guid? ProductId { get; set; }  // Optional - can be custom item

        // Item Details
        public string Name { get; set; } = string.Empty;  // Cached from Product or custom
        public string Description { get; set; } = string.Empty;
        public decimal UnitPrice { get; set; }
        public int Quantity { get; set; }
        public decimal LineDiscount { get; set; }  // Amount discount on this line
        public decimal TaxRate { get; set; }  // 0.18 for 18%

        // Audit Fields
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }

        // Navigation Properties
        public Quote Quote { get; set; } = null!;
        public Product? Product { get; set; }

        // Calculated Properties
        public decimal LineTotal => (UnitPrice * Quantity) - LineDiscount;
        public decimal LineTax => LineTotal * TaxRate;
        public decimal LineGrandTotal => LineTotal + LineTax;
    }
}
