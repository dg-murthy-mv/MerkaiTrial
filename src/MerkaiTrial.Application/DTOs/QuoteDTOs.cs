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
//
// 067 — TAX CLASSIFICATION ON THE LINE
//
// All three line shapes gain TaxCode, exactly as 052 gave them
// UnitOfMeasure and for the same reason: the product has carried a tax
// code since 051 and the line it ends up on had no field to put it in, so
// a GST invoice could not print the HSN / SAC code it is legally required
// to show against every line.
//
// Nullable everywhere, and NULL is the normal state in the markets that
// ask for nothing. On the two WRITE shapes, leaving it null with a
// ProductId set makes the handler snapshot the product's own code — the
// same arrangement UnitOfMeasure has, so a caller that has never heard of
// tax codes still stores the right one.
// =====================================================================

using MerkaiTrial.Application.Common;                 // 055: LineDiscounts
using MerkaiTrial.Application.Configuration;          // 052: UnitsOfMeasure
using MerkaiTrial.Application.Services.Pdf;
using System.Collections.Generic;                     // 065: QuoteSellerDto
using System.Linq;                                    // 065: QuoteSellerDto

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

        /// <summary>
        /// 065: WHO THE QUOTE IS FROM.
        ///
        /// The public quote page is [AllowAnonymous] and a plain PageModel
        /// — ICurrentTenantService cannot resolve anything there, because
        /// the customer has no tenant claim. So the seller's details have
        /// to travel ON the quote, resolved server-side where the tenant
        /// IS known.
        ///
        /// Set by GetQuoteByTokenHandler. Null on the other read paths,
        /// which do not need it: the internal pages already have the
        /// workspace in their own context.
        /// </summary>
        public QuoteSellerDto? Seller { get; set; }

        /// <summary>
        /// 071: THE PAYMENT SCHEDULE, for the two documents a CUSTOMER
        /// sees — the quote PDF and the public /q/{token} page.
        ///
        /// EMPTY IS THE NORMAL STATE and means "payable in full"; both
        /// surfaces print nothing at all in that case, so a quote raised
        /// before this round looks exactly as it did.
        ///
        /// Set by GetQuoteByTokenHandler and GenerateQuotePdfHandler.
        /// Null-free but empty on the internal read paths, which do not
        /// need it: the quote page loads the schedule separately through
        /// IQuoteMilestoneService, because it also needs what has been
        /// invoiced against each stage and that is not part of the quote.
        ///
        /// ⚠ BOTH SETTERS BUILD THEIR OWN QuoteDto BY HAND. That is the
        /// hazard the 065 and 067 notes in GenerateQuotePdfHandler
        /// describe having been bitten by twice — a field added to one
        /// read path and not the others fails silently, by printing a
        /// document with a section missing.
        /// </summary>
        public List<MilestoneRowDto> PaymentStages { get; set; } = new();

        /// <summary>071: true when this quote is billed in stages.</summary>
        public bool HasPaymentSchedule => PaymentStages.Count > 0;

        public List<QuoteItemDto> Items { get; set; } = new();
    }

    /// <summary>
    /// 065: the seller's letterhead, for the page a CUSTOMER opens.
    ///
    /// Deliberately a flat snapshot rather than a reference to the tenant:
    /// this is serialised over HTTP to an anonymous page, so it carries
    /// exactly what gets printed and nothing else — no ids, no plan, no
    /// email the workspace sends FROM.
    ///
    /// Mirrors TenantPdfInfo, including the three layout helpers, so the
    /// web page and the PDF assemble the block the same way. Two documents
    /// of the same quote disagreeing about where a comma goes is the sort
    /// of thing a customer notices and nobody else does.
    /// </summary>
    public class QuoteSellerDto
    {
        /// <summary>What the workspace is called in the app.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>The registered entity, when it differs from Name.</summary>
        public string? LegalName { get; set; }

        public string? AddressLine1 { get; set; }
        public string? AddressLine2 { get; set; }
        public string? City { get; set; }
        public string? State { get; set; }
        public string? PostalCode { get; set; }
        public string? CountryName { get; set; }

        public string? Phone { get; set; }

        /// <summary>Where a reply should go — ReplyToEmail, not FromEmail.</summary>
        public string? Email { get; set; }

        public string? Website { get; set; }

        public string? TaxNumber { get; set; }
        public string? TaxNumberLabel { get; set; }

        /// <summary>
        /// The country's word for the tax itself — "VAT", "GST". Used for
        /// the Tax column header and the totals row, which until 065 said
        /// the generic "Tax" on a document where India expects "GST".
        /// Distinct from TaxNumberLabel, which names the NUMBER.
        /// </summary>
        public string TaxLabel { get; set; } = "Tax";

        /// <summary>
        /// True when there is enough to be worth drawing a block. A name
        /// alone is not a letterhead, and an empty bordered box looks more
        /// broken than no box at all. Same test as
        /// Tenant.HasCompanyProfile and the Company Profile page's preview.
        /// </summary>
        public bool HasDetails =>
            !string.IsNullOrWhiteSpace(AddressLine1)
            || !string.IsNullOrWhiteSpace(TaxNumber);

        public string DisplayName =>
            string.IsNullOrWhiteSpace(LegalName) ? Name : LegalName!;

        /// <summary>The address, one printable line per element.</summary>
        public IReadOnlyList<string> AddressLines()
        {
            var lines = new List<string>();

            if (!string.IsNullOrWhiteSpace(AddressLine1)) lines.Add(AddressLine1!.Trim());
            if (!string.IsNullOrWhiteSpace(AddressLine2)) lines.Add(AddressLine2!.Trim());

            var town = string.Join(" ", new[] { City, State, PostalCode }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim()));

            if (town.Length > 0) lines.Add(town);

            if (!string.IsNullOrWhiteSpace(CountryName)) lines.Add(CountryName!.Trim());

            return lines;
        }

        /// <summary>Phone · email · website, or null when there is none.</summary>
        public string? ContactLine()
        {
            var parts = new[] { Phone, Email, Website }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim())
                .ToArray();

            return parts.Length == 0 ? null : string.Join("  ·  ", parts);
        }

        /// <summary>"VAT No.: 0105558012345", or null.</summary>
        public string? TaxLine()
        {
            if (string.IsNullOrWhiteSpace(TaxNumber)) return null;

            var label = string.IsNullOrWhiteSpace(TaxNumberLabel)
                ? "Tax No."
                : TaxNumberLabel!.Trim();

            return $"{label}: {TaxNumber!.Trim()}";
        }
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

        /// <summary>
        /// 067: the tax classification this line was quoted under — HSN /
        /// SAC in India, the local equivalent elsewhere, NULL where none
        /// applies. Snapshotted onto the line, not joined to the product.
        /// </summary>
        public string? TaxCode { get; set; }

        /// <summary>
        /// 067: true when this line has a code to print. Saves every view
        /// from writing its own IsNullOrWhiteSpace check, and keeps "no
        /// code" meaning one thing across six screens.
        /// </summary>
        public bool HasTaxCode => !string.IsNullOrWhiteSpace(TaxCode);

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

        /// <summary>
        /// 067. Left blank with a ProductId set, the handler snapshots the
        /// product's own code — same arrangement as UnitOfMeasure above.
        /// Trimmed, and blank stored as NULL (TaxCodes.Normalise).
        /// </summary>
        public string? TaxCode { get; set; }
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

        /// <summary>
        /// 067. Blank + ProductId → snapshotted from the product. On an
        /// EDIT the handler keeps whatever the line already had rather than
        /// clearing it, so a page that posts nothing never silently wipes a
        /// code somebody typed by hand.
        /// </summary>
        public string? TaxCode { get; set; }
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
