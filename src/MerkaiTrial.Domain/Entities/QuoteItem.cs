// =====================================================================
// QuoteItem.cs
// Location: MerkaiTrial.Domain/Entities/QuoteItem.cs
//
// COMPLETE FILE — 052 (Phase B of the catalogue).
//
// TWO CHANGES, AND THEY BELONG TOGETHER
//
//   1. Quantity is now DECIMAL, not int.
//   2. UnitOfMeasure is snapshotted onto the line.
//
// WHY 1 — an integer quantity quietly forbids most of what Merkai's
// markets sell. An interiors firm quoting 12.5 m² of flooring, a
// consultancy billing 3.5 days, a freight line of 0.75 kg: none of them
// could be written down. What people actually did was fudge the unit
// price until the line total came out right, which makes the TOTAL correct
// and everything else wrong — every report of what was sold, every
// per-unit margin, every "how much flooring did we quote this quarter".
//
// WHY 2 — a bare number means nothing. "12.5" against a flooring line is
// 12.5 of something, and until now the something lived in the description
// as prose. The unit is SNAPSHOTTED, not read through ProductId, for the
// same reason Name and UnitPrice are snapshotted: a quote is a document
// sent to a customer, and re-pricing or re-labelling history because
// somebody edited the catalogue afterwards would be wrong. A custom line
// with no product still gets a unit.
//
// Codes come from UnitsOfMeasure (MerkaiTrial.Application/Configuration/
// ProductCatalog.cs, added in 051). Stored lowercase, 16 chars is plenty.
//
// 055 adds DiscountPercent — see LineDiscounts.cs.
//
// Columns are widened / added by Sql/052_LineQuantityAndUnits.sql and
// Sql/055_LineDiscountPercent.sql.
// InvoiceLine in Invoice.cs gets exactly the same two changes in the same
// round — a quote that can say 12.5 against an invoice that rounds it to
// 12 would be worse than neither.
// =====================================================================

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

        /// <summary>
        /// 052: DECIMAL(18,4). 12.5 m², 3.5 days, 0.75 kg. Whole numbers are
        /// still whole numbers — nothing about the common case changes.
        /// </summary>
        public decimal Quantity { get; set; }

        /// <summary>
        /// 052: what the quantity MEANS — a code from UnitsOfMeasure
        /// ("unit", "hour", "day", "sqm", "user"…). Snapshotted from the
        /// product when the line was added, so editing the catalogue later
        /// never rewrites a quote that has already gone out.
        /// </summary>
        public string UnitOfMeasure { get; set; } = "unit";

        /// <summary>
        /// The discount AMOUNT on this line. Still the only thing any total
        /// is worked out from — 055 did not change one subtraction.
        /// </summary>
        public decimal LineDiscount { get; set; }

        /// <summary>
        /// 055: DECIMAL(5,2), nullable. Set when the discount was agreed as
        /// a PERCENTAGE — "10% off" — and NULL when it was typed as a flat
        /// amount, which is not the same thing and should not look it.
        ///
        /// LineDiscount is recomputed from this on save (LineDiscounts.
        /// Resolve), so the two can never disagree.
        /// </summary>
        public decimal? DiscountPercent { get; set; }

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
        // Unchanged: decimal * decimal was already the arithmetic here, so
        // widening Quantity changes nothing about how a total is worked out.
        public decimal LineTotal => (UnitPrice * Quantity) - LineDiscount;
        public decimal LineTax => LineTotal * TaxRate;
        public decimal LineGrandTotal => LineTotal + LineTax;
    }
}
