// =====================================================================
// Product.cs
// Location: MerkaiTrial.Domain/Entities/Product.cs
//
// COMPLETE FILE — 070 adds TWO MORE navigation properties, BundleItems
// and PartOfBundles. 069 added Prices.
//
// ListPrice BELOW IS THE PRICE IN THE WORKSPACE'S OWN CURRENCY, and
// always has been — there has never been a currency column beside it and
// every screen resolves the symbol from ICurrentTenantService. 069
// writes that rule down and adds the other currencies beside it in
// dbo.ProductPrices; see ProductPrice.cs for why that is a bug fix and
// not just a feature.
// =====================================================================

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
        /// <summary>
        /// THE PRICE IN THE WORKSPACE'S OWN CURRENCY. There is no
        /// currency column beside it and never has been: every screen
        /// resolves the symbol from ICurrentTenantService, so this number
        /// has always been denominated in whatever the workspace's
        /// country uses.
        ///
        /// 069 does not change that. Prices in OTHER currencies live in
        /// dbo.ProductPrices, and the application refuses a row there in
        /// the workspace's own currency — two places to store the home
        /// price would disagree, and nothing would say which one a quote
        /// had used.
        ///
        /// One consequence: changing a workspace's country silently
        /// re-denominates this number. That is true today, and it is why
        /// changing a country is a platform operation rather than a
        /// settings screen.
        /// </summary>
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

        /// <summary>
        /// 069. Prices in currencies OTHER than the workspace's own. The
        /// home-currency price is ListPrice above, not a row in here.
        ///
        /// Never read directly to price a quote line — go through
        /// ProductPricing.ResolveAsync, which applies the one rule in one
        /// place and tells the caller when a currency has no price rather
        /// than quietly handing back the home-currency number labelled as
        /// something else. That substitution is the defect this round
        /// exists to fix, and doing it by hand here would reintroduce it.
        /// </summary>
        public ICollection<ProductPrice> Prices { get; set; } = new List<ProductPrice>();

        /// <summary>
        /// 070. What this product CONTAINS, when its Type is "Bundle".
        /// Empty for every other kind.
        ///
        /// A LIVE REFERENCE, not a snapshot: a bundle is a definition,
        /// so renaming a component changes what the catalogue says the
        /// bundle contains. The quote line's DESCRIPTION is where the
        /// breakdown gets frozen, at the moment the line is added — see
        /// ProductBundleItem.cs. Definition live, document frozen.
        /// </summary>
        public ICollection<ProductBundleItem> BundleItems { get; set; } = new List<ProductBundleItem>();

        /// <summary>
        /// 070. The bundles this product is PART OF — the inverse side.
        ///
        /// Needed, not decorative. ProductBundleItem has two foreign keys
        /// to Products, and EF pairs a navigation with a foreign key by
        /// convention when it can. With only one inverse collection it
        /// guesses, and when it guesses wrong the symptom is a query that
        /// joins a bundle to itself. Naming both inverses in
        /// ProductBundleItemConfiguration is what removes the guess.
        ///
        /// Also the read behind "this product is used in 3 bundles" on
        /// the product page, which is the warning somebody needs before
        /// they deactivate it.
        /// </summary>
        public ICollection<ProductBundleItem> PartOfBundles { get; set; } = new List<ProductBundleItem>();
    }
}
