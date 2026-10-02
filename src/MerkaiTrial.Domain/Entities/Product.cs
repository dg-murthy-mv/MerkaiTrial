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

        /// <summary>
        /// Product / Service / Subscription. The values live in
        /// ProductKinds (Application/Configuration/ProductCatalog.cs) — they
        /// used to be a string array written out separately in the Create and
        /// Edit page models, with nothing checking either copy.
        /// </summary>
        public string Type { get; set; } = "Product";

        /// <summary>
        /// 051. What a quantity of this product MEANS: unit, hour, day, sqm,
        /// user, kg… The codes are in UnitsOfMeasure.
        ///
        /// Until this existed a quote line's quantity was a bare integer with
        /// no unit, so an interiors firm quoting 12.5 m² of flooring had
        /// nowhere to put the 12.5 OR the m² — the real quantity went into the
        /// description as prose and the unit price was fudged to make the line
        /// total come out right. The total was correct; everything else about
        /// what was sold was not.
        ///
        /// Defaults to "unit", which is what every existing row becomes and
        /// which prints as nothing at all.
        /// </summary>
        public string UnitOfMeasure { get; set; } = "unit";

        // Pricing
        public decimal ListPrice { get; set; }

        /// <summary>
        /// Percentage — 18.00 for 18%. NOTE the two other conventions this
        /// meets on its way to a quote: QuoteItem.TaxRate is a FRACTION
        /// (0.18) and QuoteItemData (the editor's shape) is a percentage
        /// again. Each boundary converts; none of them is wrong, but the
        /// three together are worth knowing about before changing any of them.
        ///
        /// Until 050 this value was never used — the quote line always took
        /// the workspace default, so a 5% product in a 7% workspace produced
        /// a 7% line.
        /// </summary>
        public decimal TaxRate { get; set; } = 0;

        /// <summary>
        /// 051. The tax classification an invoice must carry in some markets —
        /// HSN for goods and SAC for services under Indian GST.
        ///
        /// Free text, not a validated list: the code sets are long, they
        /// change, and they differ by country. What Merkai can usefully do is
        /// carry it, print it on the invoice and not lose it.
        ///
        /// Nullable, and it stays empty for tenants whose tax authority does
        /// not ask for one.
        /// </summary>
        public string? TaxCode { get; set; }

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
