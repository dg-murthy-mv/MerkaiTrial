using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Application.DTOs
{
    public class InvoiceDto
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid? QuoteId { get; set; }
        public Guid? DealId { get; set; }

        public string Number { get; set; } = string.Empty;
        public DateTime IssueDateUtc { get; set; }
        public DateTime? DueDateUtc { get; set; }
        public string Currency { get; set; } = string.Empty;  // ✅ Set from tenant
        public string Status { get; set; } = "Draft";
        public string? VerticalName { get; set; }

        // Breakdown amounts
        public decimal Subtotal { get; set; }
        public decimal DiscountTotal { get; set; }
        public decimal TaxTotal { get; set; }
        public decimal GrandTotal { get; set; }
        public decimal Balance { get; set; }
        public decimal TotalPaid { get; set; }

        // Related data
        public string? QuoteNumber { get; set; }
        public string? DealTitle { get; set; }
        public string? CompanyName { get; set; }
        public string? ContactName { get; set; }

        public string? PdfUrl { get; set; }
        public string? Notes { get; set; }

        // Payment gateway
        public string? PromptPayQrImageUrl { get; set; }
        public string? CheckoutUrl { get; set; }

        // Items and payments
        public List<InvoiceLineDto> Lines { get; set; } = new();
        public List<PaymentDto> Payments { get; set; } = new();

        // Audit
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }

        // Calculated
        public bool IsOverdue { get; set; }
        public bool IsFullyPaid { get; set; }
        public int DaysUntilDue { get; set; }
        public int ItemCount => Lines.Count;
    }

    public class InvoiceListItem
    {
        public Guid Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public DateTime IssueDateUtc { get; set; }
        public DateTime? DueDateUtc { get; set; }
        public string Currency { get; set; } = string.Empty;  // ✅ Set from tenant
        public string Status { get; set; } = "Draft";

        public decimal GrandTotal { get; set; }
        public decimal Balance { get; set; }

        public string CompanyName { get; set; } = string.Empty;
        public string? DealTitle { get; set; }
        public string? QuoteNumber { get; set; }

        public int ItemCount { get; set; }
        public bool IsOverdue { get; set; }

        public DateTime CreatedAtUtc { get; set; }
    }

    public class CreateInvoiceDto
    {
        public Guid TenantId { get; set; }
        public Guid? QuoteId { get; set; }
        public Guid? DealId { get; set; }

        public DateTime IssueDateUtc { get; set; }
        public DateTime? DueDateUtc { get; set; }
        public string Currency { get; set; } = string.Empty;  // ✅ Set from tenant
        public string? Notes { get; set; }

        public List<CreateInvoiceLineDto> Lines { get; set; } = new();

        public string CreatedBy { get; set; } = string.Empty;

        // Optional: Send immediately after creation
        public bool SendImmediately { get; set; }
    }

    public class CreateInvoiceFromQuoteDto
    {
        public Guid TenantId { get; set; }
        public Guid QuoteId { get; set; }

        public DateTime IssueDateUtc { get; set; }
        public DateTime? DueDateUtc { get; set; }
        public string? Notes { get; set; }

        public string CreatedBy { get; set; } = string.Empty;
        public bool SendImmediately { get; set; }
    }

    public class UpdateInvoiceDto
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }

        public DateTime? DueDateUtc { get; set; }
        public string? Notes { get; set; }

        public List<CreateInvoiceLineDto> Lines { get; set; } = new();

        public string UpdatedBy { get; set; } = string.Empty;
    }

    public class UpdateInvoiceStatusDto
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string UpdatedBy { get; set; } = string.Empty;
    }

    public class InvoiceLineDto
    {
        public Guid Id { get; set; }
        public Guid? ProductId { get; set; }

        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public decimal UnitPrice { get; set; }
        public int Quantity { get; set; }
        public decimal LineDiscount { get; set; }
        public decimal TaxRate { get; set; }

        // Calculated
        public decimal LineTotal { get; set; }
        public decimal LineTax { get; set; }
        public decimal LineGrandTotal { get; set; }

        public string? ProductName { get; set; }
        public string? ProductSku { get; set; }
    }

    public class CreateInvoiceLineDto
    {
        public Guid? ProductId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public decimal UnitPrice { get; set; }
        public int Quantity { get; set; }
        public decimal LineDiscount { get; set; }
        public decimal TaxRate { get; set; }  // Decimal format (0.18 for 18%)
    }

    public class InvoiceStatisticsDto
    {
        public int TotalInvoices { get; set; }
        public int DraftInvoices { get; set; }
        public int SentInvoices { get; set; }
        public int PaidInvoices { get; set; }
        public int OverdueInvoices { get; set; }

        public decimal TotalAmount { get; set; }
        public decimal PaidAmount { get; set; }
        public decimal UnpaidAmount { get; set; }
        public decimal OverdueAmount { get; set; }

        public string Currency { get; set; } = string.Empty;  // ✅ Set from tenant
    }

    public class PaymentDto
    {
        public Guid Id { get; set; }
        public Guid InvoiceId { get; set; }

        public decimal Amount { get; set; }
        public string Currency { get; set; } = string.Empty;  // ✅ Set from tenant
        public string Method { get; set; } = "BankTransfer";
        public string Status { get; set; } = "Captured";

        public DateTime PaidAtUtc { get; set; }
        public string? Notes { get; set; }
        public string? ProviderTxnId { get; set; }

        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
    }

    public class CreatePaymentDto
    {
        public Guid TenantId { get; set; }
        public Guid InvoiceId { get; set; }

        public decimal Amount { get; set; }
        public string Currency { get; set; } = string.Empty;  // ✅ Set from tenant
        public string Method { get; set; } = "BankTransfer";
        public DateTime PaidAtUtc { get; set; }
        public string? Notes { get; set; }
        public string? ProviderTxnId { get; set; }

        public string CreatedBy { get; set; } = string.Empty;
    }
}
