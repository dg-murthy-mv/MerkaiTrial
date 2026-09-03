using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
        public string Status { get; set; } = "Captured";  // Pending, Captured, Failed, Refunded

        public string? ProviderTxnId { get; set; }
        public DateTime PaidAtUtc { get; set; }
        public string? Notes { get; set; }

        // Audit fields
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }

        // Navigation properties
        public Invoice? Invoice { get; set; }
    }

    
}
