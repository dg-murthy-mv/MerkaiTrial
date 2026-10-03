// =====================================================================
// ProductDtos.cs
// Location: MerkaiTrial.Application/DTOs/ProductDtos.cs
//
// 051: every shape gains UnitOfMeasure and TaxCode.
//
//   UnitOfMeasure — what a quantity MEANS (unit, hour, day, sqm, user…).
//                   Codes in UnitsOfMeasure.
//   TaxCode       — HSN / SAC, or whatever the tenant's tax authority
//                   wants printed on the invoice. Nullable.
//
// ProductListItem is a POSITIONAL record and is the one to be careful
// with: it is constructed positionally in GetProductsHandler and consumed
// by the quote line editor, both quote page models and the products list.
// The two new members are TRAILING and have DEFAULTS, so every existing
// construction site still compiles unchanged.
//
// ─────────────────────────────────────────────────────────────────────
// 069: A PRICE PER CURRENCY
//
// ListPrice on every shape here is THE PRICE IN THE WORKSPACE'S OWN
// CURRENCY — which is what it has always been, since there has never
// been a currency column beside it. 069 writes that down rather than
// changing it.
//
// ProductPriceDto rows carry the OTHER currencies. The application
// refuses a row in the workspace's own currency: two places to store the
// home price would disagree, and nothing would say which one a quote
// had used.
//
// ProductListItem gains three TRAILING members with defaults, after the
// two 051 added, for the same reason — it is constructed positionally
// and the quote editor consumes it. They answer the question the quote
// line editor actually has, which is not "what does this cost" but
// "what does this cost IN THE CURRENCY OF THE QUOTE I AM BUILDING":
//
//   PriceCurrency       which currency ListPrice is in, for this read
//   HasPriceInCurrency  false = nothing priced in it; do NOT print
//                       ListPrice, which is zero in that case
//   OtherCurrencyCount  how many additional currencies exist, for the
//                       badge on the products list
//
// HasPriceInCurrency is the one that matters. Before 069 a quote in a
// deal's currency was populated from the workspace's prices with no
// conversion and no warning — see ProductPricing.cs.
//
// ─────────────────────────────────────────────────────────────────────
// 070: BUNDLES
//
// A bundle is a product whose Type is "Bundle". What it contains is a
// list of ProductBundleItemDto; its PRICE is still ListPrice, exactly
// like any other product, and 069's Prices rows still give it a price
// per currency.
//
// ProductListItem gains three more TRAILING members, which is now six
// since 051. They exist so the quote line editor can turn a bundle into
// ONE line with its contents in the description without a second call:
//
//   IsBundle             the kind, pre-answered and normalised
//   BundleBreakdown      "Includes: 1 × Onboarding; 3 × Training day"
//   BundleComponentCount for the badge on the products list
//
// BundleBreakdown is a STRING and not a list on purpose. It gets
// snapshotted into QuoteItem.Description the moment a line is added, so
// a quote already sent never changes when somebody edits the bundle —
// while the bundle itself stays a live reference everywhere else.
// Definition live, document frozen; ProductBundleItem.cs has the full
// argument.
// ─────────────────────────────────────────────────────────────────────
// =====================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MerkaiTrial.Application.Configuration;   // 070 — UnitsOfMeasure, for the
                                               // breakdown's quantity formatting

namespace MerkaiTrial.Application.DTOs
{
    public class ProductDto
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Sku { get; set; }
        public string? Category { get; set; }
        public string Type { get; set; } = "Product";

        /// <summary>051. Code from UnitsOfMeasure — "unit", "hour", "sqm"…</summary>
        public string UnitOfMeasure { get; set; } = "unit";

        /// <summary>
        /// The price in the WORKSPACE'S OWN currency. Other currencies
        /// are in Prices below.
        /// </summary>
        public decimal ListPrice { get; set; }

        public string Currency { get; set; } = "USD";
        public decimal? TaxRate { get; set; }

        /// <summary>051. HSN / SAC or equivalent. Null where none applies.</summary>
        public string? TaxCode { get; set; }

        /// <summary>
        /// 069. Prices in currencies OTHER than the workspace's own, in
        /// CurrencyConfiguration's order. Empty for the single-currency
        /// workspace that most of them are.
        /// </summary>
        public List<ProductPriceDto> Prices { get; set; } = new();

        /// <summary>
        /// 070. What this bundle CONTAINS, in display order. Empty for
        /// every kind other than Bundle.
        /// </summary>
        public List<ProductBundleItemDto> BundleItems { get; set; } = new();

        /// <summary>
        /// 070. How many bundles this product is PART OF.
        ///
        /// The product page's warning before somebody deactivates it:
        /// "used in 2 bundles" is the thing they need to know, and
        /// nothing else in the product would tell them.
        /// </summary>
        public int UsedInBundleCount { get; set; }

        public bool IsActive { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
    }

    /// <summary>
    /// 051: UnitOfMeasure and TaxCode added as TRAILING parameters with
    /// defaults. GetProductsHandler constructs this positionally and the
    /// quote editor reads it — trailing defaults mean neither breaks.
    /// </summary>
    public record ProductListItem(
        Guid Id,
        string Name,
        string? Description,
        string? Sku,
        string? Category,
        string Type,
        decimal ListPrice,
        string Currency,
        decimal? TaxRate,
        bool IsActive,
        DateTime? CreatedAtUtc,
        string UnitOfMeasure = "unit",
        string? TaxCode = null,

        // ── 069, all three trailing with defaults ────────────────────
        //
        // PLAIN // COMMENTS, NOT ///. A documentation comment is only
        // valid immediately before a declaration; inside a parameter
        // list the compiler raises CS1587 and drops it, which is a
        // warning normally and a build failure under
        // TreatWarningsAsErrors. The <param> tags on the record above
        // are where these belong if they are ever wanted in IntelliSense.

        // Which currency ListPrice is in FOR THIS READ. Normally the
        // workspace's; the quote line editor asks for the quote's
        // instead, and then ListPrice is the price in that one.
        string? PriceCurrency = null,

        // FALSE means nothing is priced in PriceCurrency and ListPrice is
        // ZERO — a placeholder, not a price. Do not print it.
        //
        // Defaults to TRUE so every construction site written before 069
        // keeps meaning what it meant.
        bool HasPriceInCurrency = true,

        // How many additional currencies this product is priced in, for
        // the badge on the products list. Not the prices themselves — the
        // list renders one row per product and has no use for them.
        int OtherCurrencyCount = 0,

        // ── 070, all three trailing with defaults ────────────────────
        //
        // Plain // comments again, not ///: a documentation comment
        // inside a parameter list raises CS1587 and is dropped, which is
        // a build failure under TreatWarningsAsErrors.

        // The kind, pre-answered and normalised, so the catalogue in the
        // quote line editor does not each have to remember to call
        // ProductKinds.IsBundle — where forgetting to normalise would
        // make "bundle" in lower case behave as an ordinary product and
        // silently drop its contents.
        bool IsBundle = false,

        // "Includes: 1 × Onboarding setup; 3 × Training day". A STRING,
        // because this is what gets snapshotted into the quote line's
        // description; see the header. Empty for a non-bundle.
        string? BundleBreakdown = null,

        // For the badge on the products list.
        int BundleComponentCount = 0
    );

    public record ProductStatsDto(
        int TotalProducts,
        int ActiveProducts,
        decimal TotalValue,
        int CategoriesCount
    );

    public class CreateProductDto
    {
        public Guid TenantId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Sku { get; set; }
        public string? Category { get; set; }
        public string Type { get; set; } = "Product";

        /// <summary>051.</summary>
        public string UnitOfMeasure { get; set; } = "unit";

        /// <summary>The price in the workspace's own currency.</summary>
        public decimal ListPrice { get; set; }

        public string Currency { get; set; } = "USD";
        public decimal? TaxRate { get; set; }

        /// <summary>051.</summary>
        public string? TaxCode { get; set; }

        /// <summary>
        /// 069. Prices in OTHER currencies. NULL and EMPTY mean different
        /// things, as they do for TaxCode on a quote line:
        ///
        ///   null  — the caller said nothing about prices. Nothing is
        ///           touched. This is what an API client written before
        ///           069 sends, and it must not wipe a product's
        ///           currencies as a side effect of renaming it.
        ///   empty — "no other currencies", and any existing rows go.
        ///
        /// A row in the workspace's own currency is REFUSED; the home
        /// price is ListPrice above.
        /// </summary>
        public List<ProductPriceDto>? Prices { get; set; }

        /// <summary>
        /// 070. What this bundle contains. Only meaningful when Type is
        /// "Bundle"; the handler clears any contents on a product of
        /// another kind, so changing a bundle into a Service empties it
        /// rather than leaving orphaned rows behind.
        ///
        /// NULL and EMPTY mean different things, as they do for Prices
        /// above: null is "the caller said nothing" and touches nothing,
        /// empty is "no components". For a BUNDLE an empty list is
        /// REFUSED — a bundle with nothing in it is a price with no
        /// explanation.
        /// </summary>
        public List<ProductBundleItemDto>? BundleItems { get; set; }

        public bool IsActive { get; set; } = true;
        public string? CreatedBy { get; set; }
    }

    public class UpdateProductDto
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Sku { get; set; }
        public string? Category { get; set; }
        public string Type { get; set; } = "Product";

        /// <summary>051.</summary>
        public string UnitOfMeasure { get; set; } = "unit";

        /// <summary>The price in the workspace's own currency.</summary>
        public decimal ListPrice { get; set; }

        public string Currency { get; set; } = "USD";
        public decimal? TaxRate { get; set; }

        /// <summary>051.</summary>
        public string? TaxCode { get; set; }

        /// <summary>069 — see CreateProductDto.Prices. Null vs empty matters.</summary>
        public List<ProductPriceDto>? Prices { get; set; }

        /// <summary>070 — see CreateProductDto.BundleItems.</summary>
        public List<ProductBundleItemDto>? BundleItems { get; set; }

        public bool IsActive { get; set; } = true;
    }

    /// <summary>
    /// 069. One product, one currency, one price. The currency is always
    /// one OTHER than the workspace's own — the home price is
    /// Product.ListPrice.
    /// </summary>
    public class ProductPriceDto
    {
        /// <summary>
        /// An ISO code from CurrencyConfiguration. Stored upper-cased
        /// through CurrencyConfiguration.NormaliseCode, so one product
        /// can never hold both "usd" and "USD" — which the unique index
        /// would allow and which would give it two prices in one
        /// currency.
        /// </summary>
        public string CurrencyCode { get; set; } = string.Empty;

        /// <summary>
        /// DECIMAL(18,2). Zero is legal — a free component, a
        /// promotional line. Negative is refused.
        /// </summary>
        public decimal ListPrice { get; set; }
    }

    /// <summary>
    /// 070. One thing inside a bundle, and how much of it.
    ///
    /// Carries the component's name, unit and price alongside its id,
    /// read LIVE from the component product — a bundle is a definition,
    /// so renaming "Training day" changes what the catalogue says the
    /// package contains. The quote line's description is where the
    /// breakdown gets frozen.
    ///
    /// On a WRITE only ComponentProductId and Quantity are read; the
    /// rest is ignored, because a client does not get to assert what
    /// another product is called or what it costs.
    /// </summary>
    public class ProductBundleItemDto
    {
        public Guid ComponentProductId { get; set; }

        /// <summary>Read-only on a write. "(removed product)" when the component is gone.</summary>
        public string ComponentName { get; set; } = string.Empty;

        /// <summary>Read-only on a write.</summary>
        public string? ComponentSku { get; set; }

        /// <summary>
        /// The component's own unit, read-only on a write. Used to print
        /// "3 days" rather than "3.0000" in the breakdown.
        /// </summary>
        public string UnitOfMeasure { get; set; } = UnitsOfMeasure.Unit;

        /// <summary>
        /// DECIMAL(18,4), matching QuoteItem.Quantity. Must be above
        /// zero — "in the bundle, none of it" is not a thing.
        /// </summary>
        public decimal Quantity { get; set; } = 1m;

        /// <summary>Display order. Renumbered by the handler from the posted order.</summary>
        public int SortOrder { get; set; }

        /// <summary>
        /// The component's list price in the WORKSPACE'S currency,
        /// read-only on a write. Feeds the "components total" hint on the
        /// bundle form, which is computed and never stored — see
        /// ProductBundles.ComponentsTotal.
        /// </summary>
        public decimal ComponentListPrice { get; set; }

        /// <summary>
        /// False when the component product has been deactivated.
        ///
        /// NOT an error and not refused: a business stops selling a part
        /// separately and keeps it inside the package. The form flags it
        /// so nobody is surprised.
        /// </summary>
        public bool ComponentIsActive { get; set; } = true;

        /// <summary>
        /// "3 days", "12.5 m²", "1" — the quantity as it should be
        /// PRINTED, trimmed of trailing zeros and carrying the unit. The
        /// same helper QuoteItemDto.QuantityDisplay has used since 052,
        /// so a bundle's breakdown and a quote line's quantity cannot
        /// format the same number differently.
        /// </summary>
        public string QuantityDisplay => UnitsOfMeasure.Describe(Quantity, UnitOfMeasure);
    }
}
