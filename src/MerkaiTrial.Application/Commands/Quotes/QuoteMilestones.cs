// =====================================================================
// QuoteMilestones.cs
// Location: MerkaiTrial.Application/Commands/Quotes/QuoteMilestones.cs
//
// NEW FILE (071). THE ARITHMETIC OF MILESTONE BILLING, IN ONE PLACE.
//
// Everything that decides how much a stage is worth lives here: the
// schedule screen, the invoice-creation handler, the quote PDF and the
// rollup on the quote page all call into this file. Two copies of this
// arithmetic in two places would eventually disagree, and the way a
// business would find out is a customer's invoices not adding up to the
// quote they signed.
//
// ─────────────────────────────────────────────────────────────────────
// 1. THE ROUNDING RULE, WRITTEN DOWN ONCE
//
//   Three stages of 33.3333% do not add up to a round number. So the
//   LAST stage is never computed from its own percentage: it is
//   computed as EVERYTHING NOT ALREADY ALLOCATED — and not at the
//   document level, which would be approximate, but LINE BY LINE.
//
//   For a quote line with a net of N and stages f1, f2, f3:
//
//       stage 1 net = Round(N × f1, 2)
//       stage 2 net = Round(N × f2, 2)
//       stage 3 net = N − stage1 − stage2          ← the remainder
//
//   Σ stage nets = N, exactly, for every line. And because an invoice's
//   tax is computed from the net at the line's own rate with no further
//   rounding, Σ stage taxes = N × rate = the quote's line tax, exactly
//   as well. So the invoices sum to the quote's Subtotal, DiscountTotal,
//   TaxTotal and Total — all four, to the paisa, with no rounding line
//   and no tolerance.
//
//   THIS IS WHY ALLOCATION IS PER LINE AND NOT PER DOCUMENT. Scaling
//   the grand total and working backwards cannot be made exact when the
//   lines carry different tax rates, which after 067 they routinely do.
//
// 2. WHY THE MONEY FREEZES ONCE ANYTHING IS INVOICED
//
//   The remainder on the last stage is computed from the CURRENT
//   schedule. Change stage 1 from 30% to 40% after stage 3 has been
//   invoiced, and stage 3's invoice — already with the customer, already
//   in a GST return — no longer agrees with the schedule it came from.
//
//   So: once ANY stage has a live invoice, no stage's share can change,
//   and stages cannot be added or removed. Names, due conditions and
//   dates stay editable, because none of them touch the arithmetic. To
//   change the money, void the invoice first — which is already how
//   this product corrects an issued invoice everywhere else.
//
// 3. A STAGE IS A PERCENT **OR** A FIXED AMOUNT
//
//   Both are converted to a fraction of the quote's grand total before
//   anything else happens, because the line allocation needs one kind of
//   number. A fixed amount of ₹50,000 on a ₹2,00,000 quote is the
//   fraction 0.25 — and the invoice comes to exactly ₹50,000, because
//   scaling every line by one fraction scales the total by that
//   fraction: total(f) = Σ net_l × f × (1 + rate_l) = f × quoteTotal.
//   Mixed tax rates do not break it; that identity is linear in f.
//
// 4. WHAT THIS FILE DELIBERATELY DOES NOT DO
//
//   • It does not read invoices to work out the remainder. The schedule
//     alone determines every stage's share, which is what makes the
//     result reproducible and the rule in section 2 necessary.
//   • It does not raise invoices. CreateInvoiceFromQuoteHandler does
//     that, and calls AllocateLines for the line amounts.
//   • It does not format money. The Application layer has no business
//     knowing the tenant's culture; the pages and the PDFs own that.
// ─────────────────────────────────────────────────────────────────────
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MerkaiTrial.Application.Commands.Quotes
{
    /// <summary>
    /// One stage's resolved share of a quote. The output of Allocate,
    /// and the input to AllocateLines.
    /// </summary>
    public sealed record MilestoneShare(
        Guid MilestoneId,
        int Sequence,                 // 1-based
        int Count,
        string Name,
        decimal Fraction,             // 0..1 — Amount / quote total
        decimal Percent,              // the same thing as a percent, for display and the snapshot
        decimal Amount,               // the stage's grand total, in the quote's currency
        bool IsFinal);

    /// <summary>
    /// One quote line, scaled to one stage. Deliberately NOT a
    /// CreateInvoiceLineDto: this type knows nothing about invoices, and
    /// the handler is what turns it into one.
    /// </summary>
    public sealed record MilestoneLineShare(
        Guid QuoteItemId,
        decimal Gross,                // UnitPrice × Quantity, scaled
        decimal Discount);            // LineDiscount, scaled

    public static class QuoteMilestones
    {
        /// <summary>A schedule longer than this is a project plan, not a payment schedule.</summary>
        public const int MaxStages = 24;

        public const int NameMaxLength = 120;
        public const int DueConditionMaxLength = 200;

        /// <summary>
        /// How far the stated shares may miss the quote total before the
        /// schedule is refused: one minor unit per stage. Generous enough
        /// to absorb honest rounding in a percentage, tight enough that
        /// "99.99%" of a ₹1,00,000 quote — a ₹10 gap — is still rejected
        /// with a message naming the gap.
        /// </summary>
        public static decimal Tolerance(int stageCount) => 0.01m * Math.Max(stageCount, 1);

        // =================================================================
        // READ
        // =================================================================

        /// <summary>
        /// The schedule for one quote, in order. An EMPTY LIST is the
        /// normal answer and means "no schedule" — never null, so no
        /// caller has to null-check before a foreach.
        /// </summary>
        public static async Task<List<QuoteMilestone>> GetForQuoteAsync(
            FlowDbContext db,
            Guid tenantId,
            Guid quoteId,
            CancellationToken ct = default)
            => await db.QuoteMilestones
                .AsNoTracking()
                .Where(m => m.TenantId == tenantId && m.QuoteId == quoteId && !m.IsDeleted)
                .OrderBy(m => m.SortOrder)
                .ToListAsync(ct);

        // =================================================================
        // ALLOCATE — the heart of the round
        // =================================================================

        /// <summary>
        /// Turn a schedule into shares of a quote total.
        ///
        /// The last stage absorbs the remainder (see the header), so its
        /// Amount is the quote total minus everything already allocated,
        /// NOT its own percentage. Its Percent comes back recomputed from
        /// the amount actually allocated, which is what gets snapshotted
        /// onto the invoice — so the invoice says what it billed rather
        /// than what the form said.
        ///
        /// An empty schedule returns an empty list. The caller treats
        /// that as "one invoice for the whole quote", which is the
        /// pre-071 behaviour and the default.
        /// </summary>
        public static List<MilestoneShare> Allocate(
            IReadOnlyList<QuoteMilestone> milestones,
            decimal quoteTotal)
        {
            var result = new List<MilestoneShare>();
            if (milestones == null || milestones.Count == 0) return result;

            var ordered = milestones.OrderBy(m => m.SortOrder).ToList();
            var count = ordered.Count;
            var allocated = 0m;

            for (var i = 0; i < count; i++)
            {
                var m = ordered[i];
                var isFinal = i == count - 1;

                decimal amount;
                if (isFinal)
                {
                    // THE REMAINDER. Never this stage's own percentage.
                    // Clamped at zero: a schedule whose earlier stages
                    // already over-allocate is refused by Validate, but
                    // a negative amount here would mean a credit note
                    // raised by accident, so it cannot be allowed
                    // through even on an invalid schedule.
                    amount = quoteTotal - allocated;
                    if (amount < 0m) amount = 0m;
                }
                else
                {
                    amount = StatedAmount(m, quoteTotal);

                    // Never allocate more than is left, for the same
                    // reason: it would push the final stage negative.
                    var remaining = quoteTotal - allocated;
                    if (amount > remaining) amount = remaining < 0m ? 0m : remaining;
                }

                allocated += amount;

                var fraction = quoteTotal == 0m ? 0m : amount / quoteTotal;

                result.Add(new MilestoneShare(
                    MilestoneId: m.Id,
                    Sequence: i + 1,
                    Count: count,
                    Name: m.Name,
                    Fraction: fraction,
                    Percent: decimal.Round(fraction * 100m, 4, MidpointRounding.AwayFromZero),
                    Amount: amount,
                    IsFinal: isFinal));
            }

            return result;
        }

        /// <summary>
        /// What a stage SAYS it is worth, before the rounding rule: its
        /// percentage of the quote total, or its fixed amount. Rounded to
        /// two places because it is a money amount in every currency this
        /// product supports.
        /// </summary>
        public static decimal StatedAmount(QuoteMilestone m, decimal quoteTotal)
        {
            if (m.Percent.HasValue)
                return decimal.Round(quoteTotal * m.Percent.Value / 100m, 2, MidpointRounding.AwayFromZero);

            return decimal.Round(m.FixedAmount ?? 0m, 2, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Scale every quote line to ONE stage, line by line, with the
        /// final stage taking the per-line remainder.
        ///
        /// <paramref name="stageIndex"/> is 0-based and indexes into the
        /// SAME ordered schedule that produced <paramref name="shares"/>.
        ///
        /// Returns one entry per live quote item, in the quote's own
        /// order. Zero-value entries are kept rather than dropped: a line
        /// worth nothing in this stage is still part of what the customer
        /// agreed, and silently removing it would make the invoice and
        /// the quote list different things.
        /// </summary>
        public static List<MilestoneLineShare> AllocateLines(
            IReadOnlyList<QuoteItem> items,
            IReadOnlyList<MilestoneShare> shares,
            int stageIndex)
        {
            var result = new List<MilestoneLineShare>();
            if (items == null || items.Count == 0) return result;

            if (shares == null || shares.Count == 0 || stageIndex < 0 || stageIndex >= shares.Count)
            {
                // No schedule, or an index that cannot be honoured —
                // the whole line. Callers should not reach here with a
                // bad index; returning the full amounts is the safe
                // answer because it matches pre-071 behaviour.
                foreach (var item in items)
                    result.Add(new MilestoneLineShare(item.Id, item.UnitPrice * item.Quantity, item.LineDiscount));

                return result;
            }

            var isFinal = stageIndex == shares.Count - 1;

            foreach (var item in items)
            {
                var grossFull = item.UnitPrice * item.Quantity;
                var discountFull = item.LineDiscount;

                decimal gross, discount;

                if (isFinal)
                {
                    // Everything the earlier stages did not take. The
                    // earlier stages' figures are recomputed here with
                    // exactly the same expression used for them below,
                    // so the two agree by construction rather than by
                    // being kept in step by hand.
                    var grossTaken = 0m;
                    var discountTaken = 0m;

                    for (var i = 0; i < shares.Count - 1; i++)
                    {
                        grossTaken += decimal.Round(grossFull * shares[i].Fraction, 2, MidpointRounding.AwayFromZero);
                        discountTaken += decimal.Round(discountFull * shares[i].Fraction, 2, MidpointRounding.AwayFromZero);
                    }

                    gross = grossFull - grossTaken;
                    discount = discountFull - discountTaken;

                    // Rounding can only ever push these a fraction of a
                    // minor unit either way, but a negative gross would
                    // be a credit line on a tax invoice, so clamp.
                    if (gross < 0m) gross = 0m;
                    if (discount < 0m) discount = 0m;
                    if (discount > gross) discount = gross;
                }
                else
                {
                    var fraction = shares[stageIndex].Fraction;
                    gross = decimal.Round(grossFull * fraction, 2, MidpointRounding.AwayFromZero);
                    discount = decimal.Round(discountFull * fraction, 2, MidpointRounding.AwayFromZero);

                    if (discount > gross) discount = gross;
                }

                result.Add(new MilestoneLineShare(item.Id, gross, discount));
            }

            return result;
        }

        // =================================================================
        // VALIDATE
        // =================================================================

        /// <summary>
        /// Check a schedule a human just typed. Throws
        /// InvalidOperationException with a message written FOR THAT
        /// PERSON — IApiService turns a 400 body of { "error": "..." }
        /// into exactly that sentence on the page.
        ///
        /// Returns the rows normalised and renumbered (10, 20, 30…), so
        /// the caller saves what came back rather than what came in.
        ///
        /// <paramref name="liveInvoiceNumber"/> and
        /// <paramref name="liveInvoiceIsWholeQuote"/> come from the
        /// caller, which has already looked for LIVE invoices on this
        /// quote — live meaning not deleted and not void.
        ///
        /// ⚠ THE WHOLE-QUOTE CASE IS THE ONE THAT BITES, and it is why
        /// this signature is not simply a bool:
        ///
        ///   A quote with no schedule gets invoiced in full. Somebody
        ///   then adds a three-stage schedule. If "is anything invoiced"
        ///   only looked at invoices carrying a MilestoneId, that
        ///   whole-quote invoice would be invisible here, the schedule
        ///   would save, and all three stages would then be billable —
        ///   invoicing the customer twice over, with nothing on any
        ///   screen to suggest it had happened.
        ///
        ///   So a live whole-quote invoice refuses a schedule outright:
        ///   void it first. A live STAGE invoice freezes the shares
        ///   instead, which is section 2 of the header.
        /// </summary>
        public static List<QuoteMilestoneDto> Validate(
            IEnumerable<QuoteMilestoneDto>? supplied,
            decimal quoteTotal,
            IReadOnlyList<QuoteMilestone> existing,
            string? liveInvoiceNumber,
            bool liveInvoiceIsWholeQuote)
        {
            var rows = (supplied ?? Enumerable.Empty<QuoteMilestoneDto>())
                .Where(r => r != null)
                .ToList();

            var amountsLocked = liveInvoiceNumber != null;
            var lockedByInvoiceNumber = liveInvoiceNumber;

            // An empty schedule is legal and means "remove it". Nothing
            // below applies.
            if (rows.Count == 0)
            {
                // Nothing there to remove — a no-op, not a refusal. This
                // is the shape a quote invoiced in full arrives in, and
                // erroring on it would mean a whole-quote-invoiced quote
                // could not even save "no schedule".
                if (existing.Count == 0)
                    return new List<QuoteMilestoneDto>();

                if (amountsLocked)
                    throw new InvalidOperationException(
                        $"This schedule cannot be removed — {Describe(lockedByInvoiceNumber)} has already been raised against it. " +
                        "Void that invoice first.");

                return new List<QuoteMilestoneDto>();
            }

            // ⚠ See the header. A live invoice for the WHOLE quote means
            // the quote is already fully billed; splitting it into stages
            // afterwards would make every stage billable on top of it.
            if (liveInvoiceIsWholeQuote)
                throw new InvalidOperationException(
                    $"This quote has already been invoiced in full ({lockedByInvoiceNumber ?? "an invoice exists"}). " +
                    "To bill it in stages instead, void that invoice first.");

            if (rows.Count > MaxStages)
                throw new InvalidOperationException(
                    $"A payment schedule can have at most {MaxStages} stages. This one has {rows.Count}.");

            if (quoteTotal <= 0m)
                throw new InvalidOperationException(
                    "This quote has no value yet, so it cannot be split into payment stages. Add some items first.");

            // ── Row-level rules ──────────────────────────────────────
            var order = 0;
            foreach (var row in rows)
            {
                order += 10;
                row.SortOrder = order;

                row.Name = (row.Name ?? string.Empty).Trim();
                row.DueCondition = string.IsNullOrWhiteSpace(row.DueCondition) ? null : row.DueCondition!.Trim();

                var position = order / 10;

                if (row.Name.Length == 0)
                    throw new InvalidOperationException($"Stage {position} needs a name — \"Advance\", \"On delivery\", \"Final payment\".");

                if (row.Name.Length > NameMaxLength)
                    throw new InvalidOperationException(
                        $"Stage {position}'s name is too long — {NameMaxLength} characters at most.");

                if (row.DueCondition != null && row.DueCondition.Length > DueConditionMaxLength)
                    throw new InvalidOperationException(
                        $"Stage {position}'s due condition is too long — {DueConditionMaxLength} characters at most.");

                // Exactly one basis. The database says the same thing
                // (CK_QuoteMilestones_OneBasis), but a CHECK violation
                // reaches the user as a wall of SQL.
                var hasPercent = row.Percent.HasValue && row.Percent.Value != 0m;
                var hasAmount = row.FixedAmount.HasValue && row.FixedAmount.Value != 0m;

                if (hasPercent && hasAmount)
                    throw new InvalidOperationException(
                        $"Stage {position} has both a percentage and a fixed amount. Pick one — a stage can be a share of the quote or a figure, not both.");

                if (!hasPercent && !hasAmount)
                    throw new InvalidOperationException(
                        $"Stage {position} needs either a percentage or a fixed amount.");

                if (hasPercent)
                {
                    if (row.Percent!.Value < 0m)
                        throw new InvalidOperationException($"Stage {position}'s percentage cannot be negative.");

                    if (row.Percent.Value > 100m)
                        throw new InvalidOperationException(
                            $"Stage {position} is {row.Percent.Value:0.##}% of the quote. A single stage cannot be more than 100%.");

                    row.FixedAmount = null;
                }
                else
                {
                    if (row.FixedAmount!.Value < 0m)
                        throw new InvalidOperationException($"Stage {position}'s amount cannot be negative.");

                    if (row.FixedAmount.Value > quoteTotal)
                        throw new InvalidOperationException(
                            $"Stage {position} is more than the whole quote ({row.FixedAmount.Value:0.00} of {quoteTotal:0.00}).");

                    row.Percent = null;
                    row.FixedAmount = decimal.Round(row.FixedAmount.Value, 2, MidpointRounding.AwayFromZero);
                }
            }

            // ── Does the schedule add up? ────────────────────────────
            //
            // Against the STATED amounts, not the allocated ones — the
            // allocated ones add up by construction, because the last
            // stage absorbs whatever is missing. Checking the stated
            // figures is what catches "30 / 30 / 30" where somebody
            // meant 40 in the middle, which would otherwise be silently
            // corrected on the final invoice.
            var stated = rows.Sum(r => StatedAmountOf(r, quoteTotal));
            var gap = quoteTotal - stated;
            var tolerance = Tolerance(rows.Count);

            if (Math.Abs(gap) > tolerance)
            {
                var gapPercent = quoteTotal == 0m ? 0m : decimal.Round(gap / quoteTotal * 100m, 2, MidpointRounding.AwayFromZero);

                throw new InvalidOperationException(gap > 0m
                    ? $"The stages come to {stated:0.00} of {quoteTotal:0.00} — {gap:0.00} ({gapPercent:0.##}%) is unaccounted for. Add it to a stage, or use the Remainder button on the last one."
                    : $"The stages come to {stated:0.00}, which is {Math.Abs(gap):0.00} ({Math.Abs(gapPercent):0.##}%) more than the quote's {quoteTotal:0.00}. Reduce a stage.");
            }

            // ── The lock (section 2 of the header) ───────────────────
            if (amountsLocked)
            {
                var before = existing.OrderBy(m => m.SortOrder).ToList();

                if (before.Count != rows.Count)
                    throw new InvalidOperationException(
                        $"Stages cannot be added or removed — {Describe(lockedByInvoiceNumber)} has already been raised against this schedule. " +
                        "Void that invoice first, or change only the names and dates.");

                for (var i = 0; i < rows.Count; i++)
                {
                    var now = rows[i];
                    var was = before[i];

                    if (now.Id != was.Id)
                        throw new InvalidOperationException(
                            $"The stages have been reordered, and {Describe(lockedByInvoiceNumber)} has already been raised against this schedule. " +
                            "Void that invoice first.");

                    var samePercent = now.Percent == was.Percent;
                    var sameAmount = now.FixedAmount == was.FixedAmount;

                    if (!samePercent || !sameAmount)
                        throw new InvalidOperationException(
                            $"Stage {i + 1}'s amount cannot change — {Describe(lockedByInvoiceNumber)} has already been raised against this schedule, " +
                            "and re-cutting the shares would leave it disagreeing with the quote. Void that invoice first. " +
                            "Names, due conditions and dates can still be changed.");
                }
            }

            return rows;
        }

        /// <summary>StatedAmount, for the DTO shape rather than the entity.</summary>
        public static decimal StatedAmountOf(QuoteMilestoneDto r, decimal quoteTotal)
        {
            if (r.Percent.HasValue && r.Percent.Value != 0m)
                return decimal.Round(quoteTotal * r.Percent.Value / 100m, 2, MidpointRounding.AwayFromZero);

            return decimal.Round(r.FixedAmount ?? 0m, 2, MidpointRounding.AwayFromZero);
        }

        private static string Describe(string? invoiceNumber)
            => string.IsNullOrWhiteSpace(invoiceNumber) ? "an invoice" : $"invoice {invoiceNumber}";

        // =================================================================
        // SAVE
        // =================================================================

        /// <summary>
        /// Write a validated schedule. Rows are matched by Id and updated
        /// in place so CreatedAtUtc and CreatedBy keep meaning what they
        /// say; anything live the form no longer lists is SOFT-deleted, so
        /// the filtered unique index lets a position be re-used.
        ///
        /// THE NULL-VS-EMPTY RULE: a null list leaves the schedule alone,
        /// an empty list removes it. Same rule as 067's TaxCode, 069's
        /// prices and 070's bundle contents.
        ///
        /// Does NOT call SaveChangesAsync — the caller owns the unit of
        /// work, the same way ProductBundles.SaveAsync does.
        /// </summary>
        public static async Task<int> SaveAsync(
            FlowDbContext db,
            Guid tenantId,
            Guid quoteId,
            IEnumerable<QuoteMilestoneDto>? supplied,
            string actor,
            CancellationToken ct = default)
        {
            var existing = await db.QuoteMilestones
                .Where(m => m.TenantId == tenantId && m.QuoteId == quoteId && !m.IsDeleted)
                .ToListAsync(ct);

            if (supplied is null) return existing.Count;

            var wanted = supplied.ToList();
            var now = DateTime.UtcNow;

            foreach (var row in wanted)
            {
                var entity = row.Id == Guid.Empty
                    ? null
                    : existing.FirstOrDefault(e => e.Id == row.Id);

                if (entity is null)
                {
                    var created = new QuoteMilestone
                    {
                        Id = row.Id == Guid.Empty ? Guid.NewGuid() : row.Id,
                        TenantId = tenantId,
                        QuoteId = quoteId,
                        SortOrder = row.SortOrder,
                        Name = row.Name,
                        Percent = row.Percent,
                        FixedAmount = row.FixedAmount,
                        DueCondition = row.DueCondition,
                        DueDateUtc = row.DueDateUtc,
                        CreatedAtUtc = now,
                        CreatedBy = actor,
                        IsDeleted = false
                    };

                    db.QuoteMilestones.Add(created);

                    // So the Id travels back out to the page, which uses
                    // it to decide which rows are new on the next post.
                    row.Id = created.Id;
                }
                else
                {
                    entity.SortOrder = row.SortOrder;
                    entity.Name = row.Name;
                    entity.Percent = row.Percent;
                    entity.FixedAmount = row.FixedAmount;
                    entity.DueCondition = row.DueCondition;
                    entity.DueDateUtc = row.DueDateUtc;
                    entity.UpdatedAtUtc = now;
                    entity.UpdatedBy = actor;
                }
            }

            var keep = wanted.Select(r => r.Id).ToHashSet();
            foreach (var entity in existing)
            {
                if (keep.Contains(entity.Id)) continue;

                entity.IsDeleted = true;
                entity.UpdatedAtUtc = now;
                entity.UpdatedBy = actor;
            }

            return wanted.Count;
        }

        // =================================================================
        // THE DEFAULT SCHEDULE
        // =================================================================

        /// <summary>
        /// The two-stage schedule the editor offers as a starting point,
        /// because "half up front, half on delivery" is the single most
        /// common arrangement in all four of this product's markets.
        ///
        /// There is deliberately NO "one stage, 100%" default anywhere in
        /// this file. A quote with no rows already behaves exactly like
        /// one — it is invoiced once for the whole amount — so a function
        /// that produced that schedule would write a row to the database
        /// in order to reproduce the behaviour of having no row.
        ///
        /// NOT written on its own: nothing creates a schedule until
        /// somebody saves one.
        /// </summary>
        public static List<QuoteMilestoneDto> SuggestedSchedule() => new()
        {
            new QuoteMilestoneDto { Id = Guid.Empty, SortOrder = 10, Name = "Advance",       Percent = 50m, DueCondition = "On signing" },
            new QuoteMilestoneDto { Id = Guid.Empty, SortOrder = 20, Name = "Final payment", Percent = 50m, DueCondition = "On completion" }
        };

        // =================================================================
        // BILLABILITY
        // =================================================================

        /// <summary>
        /// Can this quote be invoiced at all? The 018 rule, unchanged and
        /// in one place so the three screens and the handler cannot word
        /// it three different ways.
        /// </summary>
        public static (bool Billable, string? Reason) QuoteBillability(QuoteStatus status)
            => status == QuoteStatus.Accepted
                ? (true, null)
                : (false, status switch
                {
                    QuoteStatus.Draft => "This quote is still a draft. Send it and get it accepted first.",
                    QuoteStatus.Sent => "The customer has not accepted this quote yet.",
                    QuoteStatus.Viewed => "The customer has seen this quote but not accepted it yet.",
                    QuoteStatus.Rejected => "This quote was rejected, so it cannot be invoiced.",
                    QuoteStatus.Expired => "This quote has expired. Revise it and get the new version accepted.",
                    QuoteStatus.Revised => "This quote was revised. Invoice the version the customer accepted.",
                    QuoteStatus.PendingApproval => "This quote is waiting for internal approval.",
                    QuoteStatus.Approved => "This quote is approved internally but the customer has not accepted it yet.",
                    _ => "Only accepted quotes can be invoiced."
                });
    }
}
