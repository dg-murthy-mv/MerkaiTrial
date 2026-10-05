// =====================================================================
// Quote.cs
// Location: MerkaiTrial.Domain/Entities/Quote.cs
//
// CHANGES (071 — partial invoicing / milestone billing)
//   ✅ ONE LINE ADDED: the Milestones navigation collection. Nothing
//      else in this file moves, and no column is added to dbo.Quotes —
//      the schedule lives in its own table (dbo.QuoteMilestones), so a
//      quote with no schedule is a quote with no rows, which is exactly
//      what every existing quote is.
//
//   An empty collection means "invoice the whole thing once": the
//   behaviour the product had before this round, and still the default.
// =====================================================================

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

        /// <summary>
        /// 071. The billing schedule — "30% on signing, 40% on delivery,
        /// 30% on completion". EMPTY IS THE NORMAL STATE and means
        /// "invoice the whole thing once", which is what the product did
        /// before this round and still does for every quote nobody gives
        /// a schedule to.
        ///
        /// Ordered by SortOrder wherever it is read; EF does not order a
        /// collection for you, and the order of a billing schedule is
        /// the order the work happens in.
        /// </summary>
        public ICollection<QuoteMilestone> Milestones { get; set; } = new List<QuoteMilestone>();
    }
}
