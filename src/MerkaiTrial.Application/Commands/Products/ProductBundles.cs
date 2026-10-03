// =====================================================================
// ProductBundles.cs
// Location: MerkaiTrial.Application/Commands/Products/ProductBundles.cs
//
// NEW FILE (070). The rules a bundle has to obey, the one query that
// reads every bundle's contents, and the sentence that goes onto a
// quote line.
//
// Shaped like ProductPricing (069) and LineUnits / LineTaxCodes before
// it: a LookupAsync that reads the whole catalogue in one query, a
// Validate that throws messages written for a person, and a SaveAsync
// that adds rows without saving. Four copies of a rule is four chances
// to get it wrong differently.
//
// ─────────────────────────────────────────────────────────────────────
// THE FOUR RULES, AND WHY EACH ONE EXISTS
//
//   1. A bundle cannot contain ITSELF.
//      The database has a CHECK for this one — it is the only cycle a
//      CHECK can see, because it is within a single row.
//
//   2. A bundle cannot contain ANOTHER BUNDLE.
//      Refused here, since a CHECK cannot read another row's Type.
//      One level keeps three things finite: the breakdown printed on a
//      quote line, the rollup arithmetic (one query, not a recursion),
//      and the answer to "what is actually in this". A nested bundle
//      would also make the single-line quote design collapse — the
//      description would have to nest, and a customer reading an
//      indented tree on a quote is a customer reading a bill of
//      materials.
//
//   3. A component must be a product in the SAME workspace.
//      The query filter already prevents reading another workspace's
//      product, so this is really a check that the id exists at all —
//      a stale form or a hand-made request otherwise stores a dangling
//      reference that renders as a blank line in the breakdown.
//
//   4. A bundle must contain SOMETHING.
//      An empty bundle is a product with a price and no explanation,
//      which is strictly worse than an ordinary product — the customer
//      sees "Starter package" and no contents at all.
//
// WHAT IS DELIBERATELY NOT A RULE: a component may be INACTIVE. A
// business stops selling a part separately and keeps it inside the
// package all the time. The form flags it so nobody is surprised; it
// does not refuse it.
// ─────────────────────────────────────────────────────────────────────
// =====================================================================

using System.Text;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.Products
{
    public static class ProductBundles
    {
        /// <summary>
        /// The longest breakdown this will put on a quote line, in
        /// characters.
        ///
        /// QuoteItem.Description is NVARCHAR(500) and the quote line
        /// editor's description input carries maxlength="500". A bundle
        /// of fifteen components with long names would overrun both —
        /// SQL Server would truncate silently and the editor would
        /// refuse the paste. Cut here instead, with an honest ellipsis.
        /// </summary>
        public const int BreakdownMaxLength = 460;

        // =============================================================
        // READ
        // =============================================================

        /// <summary>
        /// bundleProductId → its components, in display order, for every
        /// bundle asked about. ONE query, not one per bundle.
        ///
        /// Returns an empty map when there are no bundle ids, so a
        /// catalogue with no bundles in it — which is every catalogue
        /// until somebody makes one — runs no query at all.
        /// </summary>
        public static async Task<Dictionary<Guid, List<ProductBundleItemDto>>> LookupAsync(
            FlowDbContext db,
            Guid tenantId,
            IEnumerable<Guid>? bundleProductIds = null,
            CancellationToken ct = default)
        {
            var q = db.ProductBundleItems
                .AsNoTracking()
                .Where(i => i.TenantId == tenantId && !i.IsDeleted);

            if (bundleProductIds is not null)
            {
                var ids = bundleProductIds.Where(i => i != Guid.Empty).Distinct().ToList();
                if (ids.Count == 0) return new Dictionary<Guid, List<ProductBundleItemDto>>();
                q = q.Where(i => ids.Contains(i.BundleProductId));
            }

            // The component's own name, unit and price come across with
            // it. A LIVE read, not a snapshot — a bundle is a definition,
            // so renaming "Training day" should change what the catalogue
            // says the package contains. See ProductBundleItem.cs.
            var rows = await q
                .OrderBy(i => i.SortOrder)
                .Select(i => new
                {
                    i.BundleProductId,
                    i.ComponentProductId,
                    i.Quantity,
                    i.SortOrder,
                    Name          = i.ComponentProduct != null ? i.ComponentProduct.Name : null,
                    Sku           = i.ComponentProduct != null ? i.ComponentProduct.Sku : null,
                    UnitOfMeasure = i.ComponentProduct != null ? i.ComponentProduct.UnitOfMeasure : null,
                    ListPrice     = i.ComponentProduct != null ? i.ComponentProduct.ListPrice : 0m,
                    IsActive      = i.ComponentProduct == null || i.ComponentProduct.IsActive,
                    Kind          = i.ComponentProduct != null ? i.ComponentProduct.Type : null
                })
                .ToListAsync(ct);

            var map = new Dictionary<Guid, List<ProductBundleItemDto>>();

            foreach (var r in rows)
            {
                if (!map.TryGetValue(r.BundleProductId, out var list))
                {
                    list = new List<ProductBundleItemDto>();
                    map[r.BundleProductId] = list;
                }

                list.Add(new ProductBundleItemDto
                {
                    ComponentProductId = r.ComponentProductId,
                    ComponentName      = r.Name ?? "(removed product)",
                    ComponentSku       = r.Sku,
                    UnitOfMeasure      = r.UnitOfMeasure ?? UnitsOfMeasure.Unit,
                    Quantity           = r.Quantity,
                    SortOrder          = r.SortOrder,
                    ComponentListPrice = r.ListPrice,
                    ComponentIsActive  = r.IsActive
                });
            }

            return map;
        }

        /// <summary>One bundle's contents.</summary>
        public static async Task<List<ProductBundleItemDto>> GetForBundleAsync(
            FlowDbContext db, Guid tenantId, Guid bundleProductId, CancellationToken ct = default)
        {
            var map = await LookupAsync(db, tenantId, new[] { bundleProductId }, ct);
            return map.TryGetValue(bundleProductId, out var list) ? list : new List<ProductBundleItemDto>();
        }

        /// <summary>
        /// 070. How many bundles each of these products is PART OF, for
        /// the "used in 3 bundles" warning on the product page. One
        /// grouped query.
        /// </summary>
        public static async Task<Dictionary<Guid, int>> UsedInBundleCountsAsync(
            FlowDbContext db, Guid tenantId, IEnumerable<Guid> productIds, CancellationToken ct = default)
        {
            var ids = productIds.Where(i => i != Guid.Empty).Distinct().ToList();
            if (ids.Count == 0) return new Dictionary<Guid, int>();

            return await db.ProductBundleItems
                .AsNoTracking()
                .Where(i => i.TenantId == tenantId && !i.IsDeleted && ids.Contains(i.ComponentProductId))
                .GroupBy(i => i.ComponentProductId)
                .Select(g => new { ProductId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.ProductId, g => g.Count, ct);
        }

        // =============================================================
        // THE SENTENCE THAT GOES ON A QUOTE LINE
        // =============================================================

        /// <summary>
        /// "Includes: 1 × Onboarding setup; 3 × Training day; 1 × Support
        /// (12 months)".
        ///
        /// SNAPSHOTTED into QuoteItem.Description the moment a bundle is
        /// added to a quote — which is why it is a plain string and not a
        /// relationship. A quote already sent must not change when
        /// somebody edits the bundle, and the components are a LIVE
        /// reference everywhere else. See ProductBundleItem.cs:
        /// definition live, document frozen.
        ///
        /// NO PRICES IN IT, deliberately. A bundle is sold at one price
        /// that is less than the sum of its parts; printing the parts'
        /// prices invites the customer to add them up, find a different
        /// number, and ask which one is real. Quantities and names answer
        /// "what am I getting"; the line total answers "what does it
        /// cost".
        ///
        /// The quantity goes through UnitsOfMeasure.Describe, so it reads
        /// "3 days" rather than "3.0000" — the same formatting the quote
        /// line's own quantity has used since 052.
        /// </summary>
        public static string BuildBreakdown(IEnumerable<ProductBundleItemDto>? items)
        {
            var list = items?
                .OrderBy(i => i.SortOrder)
                .ToList() ?? new List<ProductBundleItemDto>();

            if (list.Count == 0) return string.Empty;

            // Room kept back for a "; and 12 more" tail, so the cut
            // happens between components rather than inside one. 24 is
            // generous for any plausible count — a bundle of a thousand
            // parts is not a bundle.
            const int TailReserve = 24;

            var sb = new StringBuilder("Includes: ");
            var shown = 0;

            foreach (var item in list)
            {
                var qty  = UnitsOfMeasure.Describe(item.Quantity, item.UnitOfMeasure);
                var part = $"{qty} × {item.ComponentName}";
                var sep  = shown == 0 ? string.Empty : "; ";

                var wouldBe = sb.Length + sep.Length + part.Length;

                // `shown > 0` so at least one component always makes it.
                // A single component with a 500-character name is
                // pathological, and one truncated entry beats the words
                // "Includes:" followed by nothing.
                if (shown > 0 && wouldBe + TailReserve > BreakdownMaxLength)
                {
                    sb.Append("; and ").Append(list.Count - shown).Append(" more");
                    return sb.ToString();
                }

                sb.Append(sep).Append(part);
                shown++;
            }

            var text = sb.ToString();

            // The pathological single-component case, and nothing else.
            return text.Length <= BreakdownMaxLength
                ? text
                : text[..(BreakdownMaxLength - 1)] + "\u2026";
        }

        // =============================================================
        // THE WRITE SIDE
        // =============================================================

        /// <summary>
        /// Replace a bundle's components with exactly what was supplied.
        ///
        /// ADDS AND UPDATES ROWS; DOES NOT SAVE. The caller's
        /// SaveChangesAsync commits the product and its contents
        /// together, so a bundle whose validation failed cannot leave
        /// components behind.
        ///
        /// <paramref name="supplied"/> NULL means "the caller said
        /// nothing about contents" and nothing is touched — the same
        /// null-versus-empty rule as ProductPricing.SaveAsync and
        /// LineTaxCodes.Resolve, for the same reason: an API client
        /// written before this round renaming a bundle must not empty it.
        ///
        /// Called ONLY for a product whose kind is Bundle. For any other
        /// kind the caller passes an empty list, which clears whatever
        /// was there — see the note in ProductsCommandHandler about
        /// changing a bundle back into an ordinary product.
        /// </summary>
        public static async Task<int> SaveAsync(
            FlowDbContext db,
            Guid tenantId,
            Guid bundleProductId,
            IEnumerable<ProductBundleItemDto>? supplied,
            string actor,
            CancellationToken ct = default)
        {
            var existing = await db.ProductBundleItems
                .Where(i => i.TenantId == tenantId && i.BundleProductId == bundleProductId && !i.IsDeleted)
                .ToListAsync(ct);

            if (supplied is null) return existing.Count;

            var wanted = supplied
                .Where(i => i.ComponentProductId != Guid.Empty)
                .ToList();

            var now = DateTime.UtcNow;
            var order = 0;

            foreach (var item in wanted)
            {
                order += 10;

                var row = existing.FirstOrDefault(e => e.ComponentProductId == item.ComponentProductId);

                if (row is null)
                {
                    db.ProductBundleItems.Add(new ProductBundleItem
                    {
                        Id                 = Guid.NewGuid(),
                        TenantId           = tenantId,
                        BundleProductId    = bundleProductId,
                        ComponentProductId = item.ComponentProductId,
                        Quantity           = item.Quantity,
                        SortOrder          = order,
                        CreatedAtUtc       = now,
                        CreatedBy          = actor,
                        IsDeleted          = false
                    });
                }
                else if (row.Quantity != item.Quantity || row.SortOrder != order)
                {
                    // Updated in place rather than deleted and
                    // re-inserted, so CreatedAtUtc and CreatedBy keep
                    // meaning what they say.
                    row.Quantity     = item.Quantity;
                    row.SortOrder    = order;
                    row.UpdatedAtUtc = now;
                    row.UpdatedBy    = actor;
                }
            }

            // Anything live the form no longer lists is removed. SOFT,
            // so the filtered unique index lets the component be added
            // back later.
            var keep = wanted.Select(i => i.ComponentProductId).ToHashSet();
            foreach (var row in existing)
            {
                if (keep.Contains(row.ComponentProductId)) continue;

                row.IsDeleted    = true;
                row.UpdatedAtUtc = now;
                row.UpdatedBy    = actor;
            }

            return wanted.Count;
        }

        /// <summary>
        /// Check the supplied contents against the four rules in the
        /// header. Throws InvalidOperationException with a message
        /// written for the person — IApiService turns a 400 body of
        /// { "error": "..." } into exactly that on the page.
        ///
        /// Returns the components in order, de-duplicated and normalised.
        /// </summary>
        public static async Task<List<ProductBundleItemDto>> ValidateAsync(
            FlowDbContext db,
            Guid tenantId,
            Guid bundleProductId,
            IEnumerable<ProductBundleItemDto> supplied,
            CancellationToken ct = default)
        {
            var rows = supplied
                .Where(i => i.ComponentProductId != Guid.Empty)
                .ToList();

            // RULE 4. An empty bundle is a price with no explanation.
            if (rows.Count == 0)
                throw new InvalidOperationException(
                    "A bundle has to contain something. Add at least one product to it, " +
                    "or change its kind to Product or Service.");

            // RULE 1. Also enforced by CK_ProductBundleItems_NotSelf, but
            // a message beats a constraint violation.
            if (rows.Any(i => i.ComponentProductId == bundleProductId))
                throw new InvalidOperationException("A bundle cannot contain itself.");

            var seen = new HashSet<Guid>();
            foreach (var r in rows)
            {
                if (!seen.Add(r.ComponentProductId))
                    throw new InvalidOperationException(
                        "The same product is listed twice. Use one row with a bigger quantity instead.");

                if (r.Quantity <= 0)
                    throw new InvalidOperationException(
                        "Every component needs a quantity above zero. Remove the row instead of setting it to nothing.");

                // Matches QuoteLineChecks.QuantityDecimals and the
                // DECIMAL(18,4) column. Anything finer is rounded by SQL
                // Server without a word — the 062 bug, in a new place.
                if (decimal.Round(r.Quantity, 4) != r.Quantity)
                    throw new InvalidOperationException(
                        "A component quantity can have at most 4 decimal places.");
            }

            // RULES 2 and 3, in ONE query. Reads what these ids actually
            // are rather than trusting the form, because the form is
            // where a stale page or a hand-made request gets in.
            var ids = seen.ToList();
            var components = await db.Products
                .AsNoTracking()
                .Where(p => p.TenantId == tenantId && !p.IsDeleted && ids.Contains(p.Id))
                .Select(p => new { p.Id, p.Name, p.Type })
                .ToListAsync(ct);

            // RULE 3.
            var missing = ids.Where(i => components.All(c => c.Id != i)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    "One of the products in this bundle no longer exists. " +
                    "Reopen the page to refresh the list.");

            // RULE 2.
            var nested = components.Where(c => ProductKinds.IsBundle(c.Type)).ToList();
            if (nested.Count > 0)
                throw new InvalidOperationException(
                    $"\"{nested[0].Name}\" is itself a bundle, and a bundle cannot contain another bundle. " +
                    "Add its contents directly instead.");

            // Renumbered from the order they arrived in — the form's row
            // order is the intended print order.
            var ordered = new List<ProductBundleItemDto>();
            var order = 0;
            foreach (var r in rows)
            {
                order += 10;
                r.SortOrder = order;
                ordered.Add(r);
            }

            return ordered;
        }

        // =============================================================
        // THE ROLLUP HINT
        // =============================================================

        /// <summary>
        /// What the components come to, added up at their own list
        /// prices, in the currency those prices are in.
        ///
        /// A HINT, NEVER A STORED PRICE. The bundle's price is
        /// Product.ListPrice. If this number were the price, editing one
        /// component would silently reprice the bundle and every quote
        /// drafted from it afterwards — which is the reasoning in
        /// ProductBundleItem.cs and the single most important thing about
        /// this feature to get right.
        ///
        /// What it is FOR: "components total ₹52,000 — this bundle is
        /// ₹7,000 (13%) below" is the number a business actually decides
        /// with, and nothing else in the product shows it.
        ///
        /// ComponentListPrice is the HOME-currency price (Product.
        /// ListPrice), so this total is a home-currency figure. The form
        /// shows it beside the home price field only, never beside a
        /// 069 other-currency row — adding up USD prices for some
        /// components and rupee prices for the rest would produce a
        /// number that means nothing.
        /// </summary>
        public static decimal ComponentsTotal(IEnumerable<ProductBundleItemDto>? items)
            => items?.Sum(i => i.ComponentListPrice * i.Quantity) ?? 0m;

        /// <summary>
        /// How far below the components' total the bundle's own price
        /// sits, as a percentage. NULL when there is nothing to compare —
        /// no components, or a components total of zero.
        ///
        /// Negative when the bundle costs MORE than its parts, which is
        /// unusual but legal (a package with service wrapped around it)
        /// and the form says so rather than hiding it.
        /// </summary>
        public static decimal? DiscountPercent(decimal bundlePrice, decimal componentsTotal)
        {
            if (componentsTotal <= 0m) return null;
            return decimal.Round((componentsTotal - bundlePrice) / componentsTotal * 100m, 1);
        }
    }
}
