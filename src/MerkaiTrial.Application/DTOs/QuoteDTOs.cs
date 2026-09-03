using MerkaiTrial.Application.Services.Pdf;

namespace MerkaiTrial.Application.DTOs
{
    public class QuotePdfModel
    {
        public QuoteDto Quote { get; set; } = null!;
        public string CurrencySymbol { get; set; } = string.Empty;
        public string CurrencyCode { get; set; } = string.Empty;   // ← ADD: "INR","THB","PHP","AED"
        public string DateFormat { get; set; } = "dd/MM/yyyy";
        public string TaxLabel { get; set; } = "Tax";
        public string NumberFormat { get; set; } = "N2";
        public TenantPdfInfo Tenant { get; set; } = new();
    }
    public class QuoteDto
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid DealId { get; set; }
        public string DealTitle { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public string Number { get; set; } = string.Empty;
        public DateTime IssueDateUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public string Currency { get; set; } = string.Empty;   // ✅ No default — tenant sets this
        public string Status { get; set; } = "Draft";
        public decimal Subtotal { get; set; }
        public decimal DiscountTotal { get; set; }
        public decimal TaxTotal { get; set; }
        public decimal GrandTotal { get; set; }
        public string? PaymentLinkUrl { get; set; }
        public string? PdfUrl { get; set; }
        public string? PublicLinkToken { get; set; }   // ← ADD THIS

        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        // ✅ From linked Deal — carried through for display
        public string? VerticalName { get; set; }
        public List<QuoteItemDto> Items { get; set; } = new();
    }

    public record QuoteListItem(
        Guid Id,
        Guid? DealId,
        string Number,
        string DealTitle,
        string CompanyName,
        DateTime IssueDateUtc,
        DateTime ExpiresAtUtc,
        string Currency,
        string Status,
        decimal GrandTotal,
        int ItemCount,
        DateTime CreatedAtUtc
    );

    public class QuoteItemDto
    {
        public Guid Id { get; set; }
        public Guid QuoteId { get; set; }
        public Guid? ProductId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public decimal UnitPrice { get; set; }
        public int Quantity { get; set; }
        public decimal LineDiscount { get; set; }
        public decimal TaxRate { get; set; }        // Decimal fraction: 0.18 for 18%
        public decimal LineTotal { get; set; }
        public decimal LineTax { get; set; }
        public decimal LineGrandTotal { get; set; }
    }

    public class CreateQuoteDto
    {
        public Guid TenantId { get; set; }
        public Guid DealId { get; set; }
        public DateTime IssueDateUtc { get; set; } = DateTime.UtcNow;
        public DateTime ExpiresAtUtc { get; set; }
        public string Currency { get; set; } = string.Empty;   // ✅ Set by tenant service
        public string? CreatedBy { get; set; }
        public List<CreateQuoteItemDto> Items { get; set; } = new();
    }

    public class CreateQuoteItemDto
    {
        public Guid? ProductId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public decimal UnitPrice { get; set; }
        public int Quantity { get; set; }
        public decimal LineDiscount { get; set; }
        public decimal TaxRate { get; set; }   // Decimal fraction: 0.18 for 18%
    }

    public class UpdateQuoteDto
    {
        public DateTime IssueDateUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public string Currency { get; set; } = string.Empty;   // ✅ No default
        public List<UpdateQuoteItemDto> Items { get; set; } = new();
    }

    public class UpdateQuoteItemDto
    {
        public Guid? Id { get; set; }
        public Guid? ProductId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public decimal UnitPrice { get; set; }
        public int Quantity { get; set; }
        public decimal LineDiscount { get; set; }
        public decimal TaxRate { get; set; }   // Decimal fraction: 0.18 for 18%
    }

    public class UpdateQuoteStatusDto
    {
        public string Status { get; set; } = "Draft";  // Draft, Sent, Accepted, Rejected, Expired
        public string? UpdatedBy { get; set; }
        public string? BaseUrl { get; set; }
    }

    public class QuoteStatisticsDto
    {
        public int TotalQuotes { get; set; }
        public int DraftQuotes { get; set; }
        public int SentQuotes { get; set; }
        public int AcceptedQuotes { get; set; }
        public int RejectedQuotes { get; set; }
        public int ExpiredQuotes { get; set; }
        public decimal TotalValue { get; set; }
        public decimal AcceptedValue { get; set; }
        public decimal PendingValue { get; set; }
    }
}
