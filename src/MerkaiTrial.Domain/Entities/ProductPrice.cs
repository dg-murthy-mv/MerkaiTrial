// =====================================================================
// ProductPrice.cs
// Location: MerkaiTrial.Domain/Entities/ProductPrice.cs
//
// NEW FILE (069). What a product costs in a currency that is NOT the
// workspace's own.
//
// ─────────────────────────────────────────────────────────────────────
// THE RULE, AND IT IS THE WHOLE DESIGN
//
//   Products.ListPrice   = the price in the WORKSPACE'S OWN currency.
//   ProductPrices rows   = the price in any OTHER currency.
//
//   ListPrice has never had a currency column beside it; every screen
//   resolves the symbol from ICurrentTenantService. So it has always
//   been denominated in the workspace currency — this round writes that
//   down rather than changing it, which is why nothing is backfilled and
//   no existing read path moves.
//
//   The application REFUSES a row in the workspace's own currency. Two
//   places to store the home price would be worse than none: they would
//   disagree, and nothing would say which one a quote used.
// ─────────────────────────────────────────────────────────────────────
//
// WHY THIS EXISTS AT ALL — it is a bug fix
//
//   A quote's currency comes from its DEAL (Quotes/Create.cshtml.cs,
//   ResolveCurrencyAsync), and Deals.Currency is a real per-deal column.
//   The catalogue had one price with no stated currency, and the line
//   editor was handed the WORKSPACE currency for its labels. So a deal
//   in USD against an INR workspace produced a quote stored as USD
//   containing INR numbers, with the editor saying INR while the rep
//   typed. Quotes/Detail warned about the mismatch afterwards, which is
//   the wrong end of the problem.
//
// WHY A TYPED PRICE AND NOT A CONVERSION RATE
//   An SME does not sell at the interbank rate. A ₹45,000 package is
//   $549 in the US, not $540.62, and next month it is still $549. A rate
//   needs a source, a refresh and an as-of date, and it silently
//   reprices last week's quote. A price somebody typed is a decision;
//   a converted price is an estimate pretending to be one.
//
// STRICTLY TENANT-OWNED, unlike ProductCategory. TenantId is NOT
// nullable and there is no shared row: Merkai has no business publishing
// what anybody's product costs. It goes in FlowDbContext's strict filter
// group.
//
// Table created by Sql/069_ProductPrices.sql.
// =====================================================================

namespace MerkaiTrial.Domain.Entities
{
    public class ProductPrice
    {
        public Guid Id { get; set; }

        /// <summary>
        /// NOT nullable. A price is never shared across workspaces — see
        /// the header. Strict filter, not shared-or-tenant.
        /// </summary>
        public Guid TenantId { get; set; }

        public Guid ProductId { get; set; }

        /// <summary>
        /// NVARCHAR(8). An ISO 4217 code from
        /// CurrencyConfiguration.Currencies, stored upper-case.
        ///
        /// 8 rather than 3 to match Deals.Currency and Quotes.Currency,
        /// which are both nvarchar(8). A narrower column here would be
        /// the one place in the chain a currency code could not
        /// round-trip.
        /// </summary>
        public string CurrencyCode { get; set; } = string.Empty;

        /// <summary>
        /// DECIMAL(18,2), the same shape as Products.ListPrice. Zero is
        /// legal — a free component, a promotional line. Negative is not,
        /// and the database has a CHECK saying so.
        /// </summary>
        public decimal ListPrice { get; set; }

        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }

        // Navigation
        public Product? Product { get; set; }
    }
}
