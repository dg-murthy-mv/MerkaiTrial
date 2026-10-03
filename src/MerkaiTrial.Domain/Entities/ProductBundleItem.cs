// =====================================================================
// ProductBundleItem.cs
// Location: MerkaiTrial.Domain/Entities/ProductBundleItem.cs
//
// NEW FILE (070). One thing that is in a bundle, and how much of it.
//
// ─────────────────────────────────────────────────────────────────────
// WHAT A BUNDLE IS
//
//   A product whose Type is "Bundle" — a value of the existing column,
//   beside Product, Service and Subscription. There is no new column on
//   dbo.Products at all.
//
//   "Starter package — ₹45,000" containing 1 × Onboarding setup,
//   3 × Training day, 1 × Support (12 months).
//
// ON A QUOTE IT IS ONE LINE, at the bundle's own price, with its
// contents printed underneath. Not one line per component.
//
//   The customer sees a price they can say yes to, and nothing in the
//   line maths, the tax maths, the approval rules, the PDF layout or
//   CreateInvoiceFromQuoteHandler changes — a bundle line is an ordinary
//   line with a longer description.
//
//   The cost, stated on the product form because somebody will hit it: a
//   quote line carries ONE tax rate and ONE tax code, so a bundle mixing
//   5% goods with 18% services cannot be quoted as a single line under
//   Indian GST. Quote those components separately.
//
// THE PRICE IS THE BUNDLE'S OWN, not the sum of its parts
//
//   A bundle exists in order to cost less than its parts. If the price
//   were computed, editing one component would silently reprice the
//   bundle and every quote drafted from it afterwards.
//   Products.ListPrice on the bundle row is the price; 069's
//   ProductPrices rows give it a price per currency like any other
//   product. The component total is a HINT on the form — "components
//   total ₹52,000, this bundle is 13% below" — computed, never stored.
//
// COMPONENTS ARE A LIVE REFERENCE, NOT A SNAPSHOT
//
//   The opposite of QuoteItem.UnitOfMeasure and .TaxCode, on purpose. A
//   bundle is a DEFINITION: renaming "Training day" should change what
//   the catalogue says the package contains. A quote is a DOCUMENT, so
//   the breakdown is snapshotted into the quote line's DESCRIPTION the
//   moment it is added and a quote already sent never changes.
//
//   Definition live, document frozen. Both halves matter; getting
//   either backwards is a bug somebody notices months later.
//
// NO BUNDLES INSIDE BUNDLES — refused by ProductBundles.Validate, not
// by the schema, because a CHECK constraint cannot read another row's
// Type. One level keeps the breakdown finite and the rollup one query
// deep.
//
// Table created by Sql/070_ProductBundles.sql.
// =====================================================================

namespace MerkaiTrial.Domain.Entities
{
    public class ProductBundleItem
    {
        public Guid Id { get; set; }

        /// <summary>
        /// NOT nullable. A bundle's contents are never shared across
        /// workspaces — strict filter, like ProductPrice and unlike
        /// ProductCategory.
        /// </summary>
        public Guid TenantId { get; set; }

        /// <summary>The product whose Type is "Bundle".</summary>
        public Guid BundleProductId { get; set; }

        /// <summary>
        /// What is in it. Must NOT itself be a bundle; the application
        /// refuses that, since a CHECK cannot read another row's Type.
        /// </summary>
        public Guid ComponentProductId { get; set; }

        /// <summary>
        /// DECIMAL(18,4), the same shape as QuoteItem.Quantity. Four
        /// places because a component can be 0.5 days of training or
        /// 12.5 m² of material — a bundle that could only hold whole
        /// numbers would recreate the exact problem 052 fixed.
        ///
        /// Must be above zero. "In the bundle, none of it" is not a
        /// thing; remove the row.
        /// </summary>
        public decimal Quantity { get; set; } = 1m;

        /// <summary>
        /// The order the breakdown prints in. Hand-set: "Onboarding,
        /// then Training, then Support" is the order the work happens
        /// in, and alphabetical would scramble it.
        /// </summary>
        public int SortOrder { get; set; }

        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }

        // Navigation
        public Product? BundleProduct { get; set; }
        public Product? ComponentProduct { get; set; }
    }
}
