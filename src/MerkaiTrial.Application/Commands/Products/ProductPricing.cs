// =====================================================================
// ProductPricing.cs
// Location: MerkaiTrial.Application/Commands/Products/ProductPricing.cs
//
// NEW FILE (069). The one place that answers "what does this product
// cost in this currency", and the one place that writes a price.
//
// Shaped like LineUnits and LineTaxCodes in QuotesCommandHandler.cs —
// a LookupAsync that reads every product's price in ONE query, and a
// Resolve that decides for one product — for the same reason: the quote
// line editor, the products list, the product page and the forms all
// need the same answer, and four copies of the rule would be four
// chances to get it wrong in different ways.
//
// ─────────────────────────────────────────────────────────────────────
// THE RULE
//
//   The price in the WORKSPACE'S OWN currency is Product.ListPrice.
//   The price in any OTHER currency is a dbo.ProductPrices row.
//   A currency with no row has NO PRICE — it does not fall back.
//
// THAT LAST LINE IS THE WHOLE POINT, so it is worth saying why.
//
//   Falling back to ListPrice would hand the caller a number in the
//   wrong currency, and every caller would then print it next to a "$".
//   That is precisely the defect this round exists to fix: a deal in USD
//   against an INR workspace was being quoted from INR numbers labelled
//   USD, and nothing anywhere said so.
//
//   So ResolvedPrice carries HasPrice, and the callers are expected to
//   do something visible with it. The quote line editor shows the
//   product in the catalogue with "no USD price" instead of a figure and
//   leaves the unit price at zero — where the editor's existing
//   "needs a unit price above zero" check stops the save until a human
//   types one. That is the correct outcome: a missing price is a
//   decision somebody has to make, not a rounding problem.
// ─────────────────────────────────────────────────────────────────────
// =====================================================================

using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.Products
{
    /// <summary>
    /// What one product costs in one currency, and whether that is a
    /// real answer.
    /// </summary>
    /// <param name="Price">
    /// The amount. ZERO WHEN <paramref name="HasPrice"/> IS FALSE — do
    /// not print it in that case; it is a placeholder, not a price.
    /// </param>
    /// <param name="CurrencyCode">The currency asked for, normalised.</param>
    /// <param name="IsHomeCurrency">
    /// True when this came from Product.ListPrice rather than a
    /// ProductPrices row. The product forms use it to show the base
    /// price field as the source instead of a grid row.
    /// </param>
    /// <param name="HasPrice">
    /// False when nothing has been priced in this currency. The caller
    /// must say so rather than showing Price.
    /// </param>
    public sealed record ResolvedPrice(
        decimal Price,
        string CurrencyCode,
        bool IsHomeCurrency,
        bool HasPrice);

    public static class ProductPricing
    {
        /// <summary>
        /// productId → price, for every product that has one in
        /// <paramref name="currencyCode"/>. ONE query, not one per
        /// product.
        ///
        /// Returns an EMPTY map when the wanted currency IS the
        /// workspace's own — there are no rows to find, because the home
        /// price lives on Product.ListPrice and the application refuses a
        /// ProductPrices row in the home currency. Resolve then answers
        /// from ListPrice, so the common single-currency workspace does
        /// no extra query at all.
        /// </summary>
        public static async Task<Dictionary<Guid, decimal>> LookupAsync(
            FlowDbContext db,
            Guid tenantId,
            string? homeCurrency,
            string? currencyCode,
            IEnumerable<Guid>? productIds = null,
            CancellationToken ct = default)
        {
            var wanted = CurrencyConfiguration.NormaliseCode(currencyCode);
            var home   = CurrencyConfiguration.NormaliseCode(homeCurrency);

            if (wanted is null) return new Dictionary<Guid, decimal>();

            // The home currency has no rows by construction.
            if (home is not null && wanted == home)
                return new Dictionary<Guid, decimal>();

            var q = db.ProductPrices
                .AsNoTracking()
                .Where(p => p.TenantId == tenantId && !p.IsDeleted && p.CurrencyCode == wanted);

            // Narrow to the products asked about when the caller knows
            // them — a quote with four lines should not read the whole
            // catalogue's prices. Omitted by the products LIST, which
            // wants all of them anyway.
            if (productIds is not null)
            {
                var ids = productIds.Where(i => i != Guid.Empty).Distinct().ToList();
                if (ids.Count == 0) return new Dictionary<Guid, decimal>();
                q = q.Where(p => ids.Contains(p.ProductId));
            }

            var rows = await q
                .Select(p => new { p.ProductId, p.ListPrice })
                .ToListAsync(ct);

            // ToDictionary would throw on a duplicate. The filtered
            // unique index makes that impossible — but an index can be
            // disabled, and a 500 on the products list is a worse way to
            // find out than the last row winning.
            var map = new Dictionary<Guid, decimal>();
            foreach (var r in rows) map[r.ProductId] = r.ListPrice;
            return map;
        }

        /// <summary>
        /// The price for one product, given the lookup above.
        ///
        /// <paramref name="homeListPrice"/> is Product.ListPrice — the
        /// price in the workspace's own currency, which the caller
        /// already has on the product it is rendering.
        /// </summary>
        public static ResolvedPrice Resolve(
            Guid productId,
            decimal homeListPrice,
            string? homeCurrency,
            string? wantedCurrency,
            IReadOnlyDictionary<Guid, decimal>? otherCurrencyPrices)
        {
            var home   = CurrencyConfiguration.NormaliseCode(homeCurrency);
            var wanted = CurrencyConfiguration.NormaliseCode(wantedCurrency);

            // Nothing asked for, or no workspace currency to compare
            // against: answer in the home currency, which is what every
            // screen did before 069 and is right for the single-currency
            // workspace that is most of them.
            if (wanted is null || home is null || wanted == home)
                return new ResolvedPrice(homeListPrice, wanted ?? home ?? string.Empty, true, true);

            if (otherCurrencyPrices is not null &&
                otherCurrencyPrices.TryGetValue(productId, out var priced))
                return new ResolvedPrice(priced, wanted, false, true);

            // NO FALLBACK. See the header — handing back homeListPrice
            // here is the bug.
            return new ResolvedPrice(0m, wanted, false, false);
        }

        // =============================================================
        // THE WRITE SIDE
        // =============================================================

        /// <summary>
        /// Replace a product's additional-currency prices with exactly
        /// what was supplied. Returns how many rows ended up live.
        ///
        /// ADDS AND UPDATES ROWS; DOES NOT SAVE. The caller's
        /// SaveChangesAsync commits the prices and the product together,
        /// so a product whose validation failed cannot leave new prices
        /// behind.
        ///
        /// REPLACE, not merge: the grid on the form is the complete
        /// statement of a product's other-currency prices, so a row the
        /// person deleted has to go. Existing rows are UPDATED in place
        /// rather than deleted and re-inserted, which keeps CreatedAtUtc
        /// and CreatedBy meaning what they say.
        ///
        /// <paramref name="supplied"/> null means "the caller said
        /// nothing about prices" and NOTHING is touched — an API client
        /// written before 069 updating a product's name must not wipe its
        /// currencies. An EMPTY list means "no other currencies", and
        /// does remove them. Same null-versus-empty distinction as
        /// LineTaxCodes.Resolve, and for the same reason.
        /// </summary>
        public static async Task<int> SaveAsync(
            FlowDbContext db,
            Guid tenantId,
            Guid productId,
            string? homeCurrency,
            IEnumerable<ProductPriceDto>? supplied,
            string actor,
            CancellationToken ct = default)
        {
            var existing = await db.ProductPrices
                .Where(p => p.TenantId == tenantId && p.ProductId == productId && !p.IsDeleted)
                .ToListAsync(ct);

            if (supplied is null) return existing.Count;

            var wanted = Validate(supplied, homeCurrency);
            var now    = DateTime.UtcNow;

            foreach (var (code, price) in wanted)
            {
                var row = existing.FirstOrDefault(e =>
                    string.Equals(e.CurrencyCode, code, StringComparison.OrdinalIgnoreCase));

                if (row is null)
                {
                    db.ProductPrices.Add(new ProductPrice
                    {
                        Id           = Guid.NewGuid(),
                        TenantId     = tenantId,
                        ProductId    = productId,
                        CurrencyCode = code,
                        ListPrice    = price,
                        CreatedAtUtc = now,
                        CreatedBy    = actor,
                        IsDeleted    = false
                    });
                }
                else if (row.ListPrice != price)
                {
                    row.ListPrice    = price;
                    row.UpdatedAtUtc = now;
                    row.UpdatedBy    = actor;
                }
            }

            // Anything live that the form no longer lists is removed.
            // SOFT, like everything else: the filtered unique index
            // ignores deleted rows, so the currency can be added back.
            foreach (var row in existing)
            {
                if (wanted.ContainsKey(row.CurrencyCode)) continue;

                row.IsDeleted    = true;
                row.UpdatedAtUtc = now;
                row.UpdatedBy    = actor;
            }

            return wanted.Count;
        }

        /// <summary>
        /// Normalise and check the supplied grid. Throws
        /// InvalidOperationException with a message written for the
        /// person — IApiService turns a 400 body of { "error": "..." }
        /// into exactly that on the page.
        ///
        /// Returns currency → price, upper-cased and de-duplicated.
        /// </summary>
        public static Dictionary<string, decimal> Validate(
            IEnumerable<ProductPriceDto> supplied, string? homeCurrency)
        {
            var home   = CurrencyConfiguration.NormaliseCode(homeCurrency);
            var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in supplied)
            {
                var code = CurrencyConfiguration.NormaliseCode(p.CurrencyCode);

                // A blank row in the grid. The form sends them; dropping
                // them quietly is right, because refusing the save over
                // an empty row somebody never filled in would be absurd.
                if (code is null) continue;

                if (!CurrencyConfiguration.IsKnown(code))
                    throw new InvalidOperationException(
                        $"\"{p.CurrencyCode}\" is not a currency Merkai can price in.");

                // THE ONE RULE WORTH A MESSAGE OF ITS OWN. Two places to
                // store the home price would disagree, and nothing would
                // say which one a quote had used.
                if (home is not null && code == home)
                    throw new InvalidOperationException(
                        $"{code} is your workspace currency — edit the main price above " +
                        "instead of adding it here.");

                if (p.ListPrice < 0)
                    throw new InvalidOperationException(
                        $"The {code} price cannot be negative.");

                if (result.ContainsKey(code))
                    throw new InvalidOperationException(
                        $"{code} is listed twice. Each currency can have one price.");

                result[code] = p.ListPrice;
            }

            return result;
        }

        /// <summary>
        /// 069. A product's additional-currency prices, for the read
        /// paths — the product page and the edit form's grid. In the
        /// order CurrencyConfiguration lists them, so two products never
        /// show their currencies in different orders.
        /// </summary>
        public static async Task<List<ProductPriceDto>> GetForProductAsync(
            FlowDbContext db, Guid tenantId, Guid productId, CancellationToken ct = default)
        {
            var rows = await db.ProductPrices
                .AsNoTracking()
                .Where(p => p.TenantId == tenantId && p.ProductId == productId && !p.IsDeleted)
                .Select(p => new ProductPriceDto
                {
                    CurrencyCode = p.CurrencyCode,
                    ListPrice    = p.ListPrice
                })
                .ToListAsync(ct);

            var order = CurrencyConfiguration.GetCurrencyCodes();

            return rows
                .OrderBy(r =>
                {
                    var i = order.FindIndex(c =>
                        string.Equals(c, r.CurrencyCode, StringComparison.OrdinalIgnoreCase));
                    // An unknown code sorts last rather than first, which
                    // is what IndexOf's -1 would otherwise do.
                    return i < 0 ? int.MaxValue : i;
                })
                .ThenBy(r => r.CurrencyCode, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
