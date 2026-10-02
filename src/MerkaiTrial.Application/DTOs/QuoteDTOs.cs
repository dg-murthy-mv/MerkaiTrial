// =====================================================================
// QuoteDTOs.cs
// Location: MerkaiTrial.Application/DTOs/QuoteDTOs.cs
//
// COMPLETE FILE — 052 (Phase B of the catalogue).
//
// Quantity becomes DECIMAL on all three line shapes, and each gains
// UnitOfMeasure. The reasoning is in QuoteItem.cs; the short version is
// that an integer quantity with no unit cannot express 12.5 m² of
// flooring or 3.5 consulting days, and the workaround people reach for —
// fudging the unit price — makes the line total right and every report
// about WHAT was sold wrong.
//
// UnitOfMeasure is a CODE from UnitsOfMeasure (Configuration/
// ProductCatalog.cs): "unit", "hour", "day", "sqm", "user"… The default
// is "unit" everywhere, so a caller that never sets it behaves exactly as
// before.
//
// QuoteItemDto also carries QuantityDisplay — "12.5 m²", "3", "2 days" —
// worked out once on the server rather than in four different views. Use
// it wherever a quantity is PRINTED; use Quantity for arithmetic.
// =====================================================================

using MerkaiTrial.Application.Common;                 // 055: LineDiscounts
using MerkaiTrial.Application.Configuration;          // 052: UnitsOfMeasure
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

        /// <summary>
        /// 053: the CUSTOMER'S COMPANY — Deal.Company, falling back to the
        /// contact's. It used to hold "FirstName LastName", so the quote PDF
        /// printed a person under a heading that reads QUOTE TO. Empty when
        /// the sale is genuinely person-to-person; show ContactName then.
        /// </summary>
        public string CompanyName { get; set; } = string.Empty;

        /// <summary>
        /// 053: the person at that company. New — QuoteDto had nowhere to put
        /// them, which is why CompanyName was being used for it. InvoiceDto
        /// has had both fields all along.
        /// </summary>
        public string? ContactName { get; set; }

        /// <summary>
        /// 061: the contact's email address, so the quote page can say
        /// "this contact has no email address" BEFORE the rep presses
        /// Email to customer, rather than failing after.
        ///
        /// Set by GetQuoteByIdHandler only. The public-token read leaves it
        /// NULL on purpose — an anonymous endpoint should carry the least
        /// it can, and the customer has no use for their own address being
        /// read back to them.
        /// </summary>
        public string? ContactEmail { get; set; }

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

        /// <summary>052: decimal. 12.5, 3.5, 0.75 — not just whole numbers.</summary>
        public decimal Quantity { get; set; }

        /// <summary>052: code from UnitsOfMeasure. "unit" when nothing else applies.</summary>
        public string UnitOfMeasure { get; set; } = UnitsOfMeasure.Unit;

        /// <summary>
        /// 052: the quantity as it should be PRINTED — "12.5 m²", "3",
        /// "2 days". Trailing zeros trimmed, so a decimal column does not
        /// make everyone else's quotes uglier. Display only; never parse it.
        /// </summary>
        public string QuantityDisplay => UnitsOfMeasure.Describe(Quantity, UnitOfMeasure);

        public decimal LineDiscount { get; set; }

        /// <summary>055: set when the discount was agreed as a percentage.</summary>
        public decimal? DiscountPercent { get; set; }

        /// <summary>
        /// 055: "-10%" when it was agreed as a percentage, null otherwise —
        /// so a view can print the percentage and fall back to the amount
        /// without repeating the rule.
        /// </summary>
        public string? DiscountLabel => LineDiscounts.PercentLabel(DiscountPercent);

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

        /// <summary>052: decimal.</summary>
        public decimal Quantity { get; set; }

        /// <summary>
        /// 052. Left blank with a ProductId set, the handler snapshots the
        /// product's own unit — so an API caller that knows nothing about
        /// units still stores the right one.
        /// </summary>
        public string? UnitOfMeasure { get; set; }

        public decimal LineDiscount { get; set; }

        /// <summary>055. Authoritative when set — see LineDiscounts.cs.</summary>
        public decimal? DiscountPercent { get; set; }

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

        /// <summary>052: decimal.</summary>
        public decimal Quantity { get; set; }

        /// <summary>052. Blank + ProductId → snapshotted from the product.</summary>
        public string? UnitOfMeasure { get; set; }

        public decimal LineDiscount { get; set; }

        /// <summary>
        /// 055. When this is set the handler RECOMPUTES LineDiscount from it
        /// and ignores whatever amount came with it — see LineDiscounts.cs.
        /// </summary>
        public decimal? DiscountPercent { get; set; }

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
