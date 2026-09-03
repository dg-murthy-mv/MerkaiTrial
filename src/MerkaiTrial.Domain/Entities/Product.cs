using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Domain.Entities
{
    public class Product
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }

        // Basic Information
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string Sku { get; set; } = string.Empty;

        // Categorization
        public string? Category { get; set; }  // "Services", "Products", "Software"
        public string Type { get; set; } = "Product";  // "Product", "Service", "Subscription"

        // Pricing
        public decimal ListPrice { get; set; }
       
        public decimal TaxRate { get; set; } = 0;  // 18.00 for 18% GST/VAT

        // Status
        public bool IsActive { get; set; } = true;

        // Audit Fields
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }
        public DateTime? DeletedAtUtc { get; set; }
        public string? DeletedBy { get; set; }

        // Navigation Properties
       
    }
}
