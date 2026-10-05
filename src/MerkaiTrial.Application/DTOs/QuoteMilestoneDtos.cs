// =====================================================================
// QuoteMilestoneDtos.cs
// Location: MerkaiTrial.Application/DTOs/QuoteMilestoneDtos.cs
//
// NEW FILE (071) — the shapes the billing schedule travels in.
//
// THREE SHAPES, AND THE DIFFERENCE BETWEEN THEM MATTERS
//
//   QuoteMilestoneDto      — one row as a human TYPED it. Percent OR
//                            FixedAmount, never both. This is what the
//                            form posts and what the API accepts.
//
//   MilestoneRowDto        — one row as a human READS it: the same
//                            fields PLUS the computed amount, its
//                            position, and what has been invoiced
//                            against it. Never posted; only returned.
//
//   BillingScheduleDto     — the whole schedule plus the agreed /
//                            invoiced / outstanding rollup. This is the
//                            read every screen in the round does.
//
// WHY THE AMOUNT IS COMPUTED AND NEVER STORED
//   Because the quote total can still change while the quote is a
//   draft, and a 40% stage holding a stale figure would be a silent
//   wrong number on a document. QuoteMilestones.Allocate computes it
//   every time, from the quote total that exists now. It is snapshotted
//   exactly once — onto the invoice, when one is raised.
// =====================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace MerkaiTrial.Application.DTOs
{
    /// <summary>
    /// One stage, as typed. Exactly one of Percent / FixedAmount is set;
    /// QuoteMilestones.Validate is what refuses the alternatives rather
    /// than letting the database CHECK produce an unreadable error.
    /// </summary>
    public class QuoteMilestoneDto
    {
        /// <summary>Guid.Empty for a stage being added for the first time.</summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Position in the schedule. The SAVE path re-numbers these from
        /// the order the rows arrive in (10, 20, 30…), so the browser
        /// does not have to keep them tidy — it only has to keep them in
        /// order.
        /// </summary>
        public int SortOrder { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary>A PERCENT: 30 means 30%. Not a fraction. See QuoteMilestone.cs.</summary>
        public decimal? Percent { get; set; }

        /// <summary>A money amount in the quote's currency.</summary>
        public decimal? FixedAmount { get; set; }

        public string? DueCondition { get; set; }
        public DateTime? DueDateUtc { get; set; }
    }

    /// <summary>
    /// One stage, as read. Everything in QuoteMilestoneDto plus what the
    /// screens need in order to show the stage and decide what can be
    /// done to it.
    /// </summary>
    public class MilestoneRowDto : QuoteMilestoneDto
    {
        /// <summary>1-based — the 2 in "milestone 2 of 3".</summary>
        public int Sequence { get; set; }

        /// <summary>The 3 in "milestone 2 of 3".</summary>
        public int Count { get; set; }

        /// <summary>
        /// What this stage will actually invoice, in the quote's
        /// currency. COMPUTED. For the final stage this is "everything
        /// not already allocated", so it can differ by a paisa or two
        /// from a straight percentage — which is the rounding rule
        /// working, and why the screen shows this number rather than the
        /// percentage the person typed.
        /// </summary>
        public decimal Amount { get; set; }

        /// <summary>
        /// The share actually applied, as a percent, after the rounding
        /// rule. Equals Percent for every stage except the last.
        /// </summary>
        public decimal EffectivePercent { get; set; }

        /// <summary>True for the stage that absorbs the remainder.</summary>
        public bool IsFinal { get; set; }

        // ── What has been invoiced against this stage ─────────────────
        //
        // "Live" means not deleted and not void. A VOIDED invoice does
        // not hold the stage: voiding is how a mistake on an issued
        // invoice gets corrected, and the stage has to be billable again
        // afterwards. The void one stays in the history on the quote.

        public Guid? InvoiceId { get; set; }
        public string? InvoiceNumber { get; set; }
        public string? InvoiceStatus { get; set; }

        /// <summary>True when a live invoice exists for this stage.</summary>
        public bool IsInvoiced => InvoiceId.HasValue;

        /// <summary>Captured payments against this stage's live invoice.</summary>
        public decimal PaidAmount { get; set; }

        /// <summary>
        /// True when an invoice can be raised for this stage right now.
        /// False with a reason in BlockedReason — the quote is not
        /// accepted, or this stage is already invoiced.
        /// </summary>
        public bool CanBill { get; set; }
        public string? BlockedReason { get; set; }
    }

    /// <summary>
    /// The whole schedule, with the one rollup a business actually asks
    /// for: agreed, invoiced, outstanding, still to bill.
    /// </summary>
    public class BillingScheduleDto
    {
        public Guid QuoteId { get; set; }
        public string QuoteNumber { get; set; } = string.Empty;
        public string QuoteStatus { get; set; } = string.Empty;
        public string Currency { get; set; } = "USD";

        /// <summary>The quote's grand total — what every share is a share of.</summary>
        public decimal QuoteTotal { get; set; }

        /// <summary>
        /// Empty when the quote has no schedule, which is the normal
        /// state and means "invoice the whole thing once". Every screen
        /// branches on HasSchedule rather than on Rows.Count so the
        /// intent reads.
        /// </summary>
        public List<MilestoneRowDto> Rows { get; set; } = new();

        public bool HasSchedule => Rows.Count > 0;

        // ── Rollup ───────────────────────────────────────────────────

        /// <summary>Total of every LIVE invoice raised against this quote.</summary>
        public decimal InvoicedTotal { get; set; }

        /// <summary>Captured payments across those invoices.</summary>
        public decimal PaidTotal { get; set; }

        /// <summary>Invoiced but not yet paid.</summary>
        public decimal OutstandingTotal => InvoicedTotal - PaidTotal;

        /// <summary>
        /// Agreed but not yet invoiced. Never negative: an over-invoiced
        /// quote reads as 0 here and the screen says so separately,
        /// because a negative "still to bill" is a number nobody can act
        /// on.
        /// </summary>
        public decimal NotYetInvoicedTotal =>
            QuoteTotal - InvoicedTotal > 0 ? QuoteTotal - InvoicedTotal : 0m;

        /// <summary>
        /// True when more has been invoiced than the quote agreed. Should
        /// not happen — the unique index and the stage guard both prevent
        /// it — but if it ever does, the quote screen has to say so
        /// rather than quietly showing 0 left to bill.
        /// </summary>
        public bool IsOverInvoiced => InvoicedTotal > QuoteTotal;

        /// <summary>
        /// Sum of what the stages SAY they are worth — each stage's own
        /// percentage of the quote, or its fixed amount — before the
        /// last-stage remainder rule is applied. Set by the read handler.
        ///
        /// ⚠ THIS IS NOT Rows.Sum(r => r.Amount). That sum ALWAYS equals
        /// the quote total, by construction: the final stage absorbs
        /// whatever is left, and earlier stages are clamped so they
        /// cannot over-allocate. A property that can only ever report
        /// "correct" would be decoration.
        ///
        /// The STATED sum can disagree, and the case is real: set a
        /// schedule with a ₹60,000 fixed first stage on a ₹1,00,000
        /// quote, then edit the quote down to ₹50,000. Nothing is
        /// invalid, nothing throws, and the panel would quietly show
        /// ₹50,000 against the first stage and ₹0 against the last.
        /// MatchesQuoteTotal is what makes that visible.
        /// </summary>
        public decimal StatedTotal { get; set; }

        /// <summary>
        /// False when the schedule no longer adds up to the quote —
        /// almost always because the quote was edited after the schedule
        /// was set. Same tolerance the save path uses, so the screen and
        /// the validator agree on what counts as adding up.
        /// </summary>
        public bool MatchesQuoteTotal =>
            Rows.Count == 0 ||
            Math.Abs(QuoteTotal - StatedTotal) <= 0.01m * Rows.Count;

        /// <summary>
        /// The number of a live invoice raised for the WHOLE quote —
        /// i.e. one with no stage against it. Normally null.
        ///
        /// ⚠ WHEN THIS IS SET, NO SCHEDULE CAN BE SAVED AT ALL. A quote
        /// invoiced in full and then split into stages would have every
        /// stage billable on top of the invoice already sent. The editor
        /// says so and the save path refuses it; see the note in
        /// QuoteMilestones.Validate.
        /// </summary>
        public string? WholeQuoteInvoiceNumber { get; set; }

        /// <summary>True when a live invoice covers the whole quote.</summary>
        public bool IsInvoicedInFull => !string.IsNullOrWhiteSpace(WholeQuoteInvoiceNumber);

        /// <summary>
        /// True when the schedule's money is fixed: any live invoice at
        /// all, whether against a stage or against the whole quote.
        /// Drives the explanation on the schedule editor.
        /// </summary>
        public bool AmountsLocked => IsInvoicedInFull || Rows.Any(r => r.IsInvoiced);

        /// <summary>
        /// The invoice number holding the schedule, for the sentence the
        /// editor shows: "Stage 1 is on INV-0042 — void it first."
        /// </summary>
        public string? LockedByInvoiceNumber =>
            WholeQuoteInvoiceNumber ?? Rows.FirstOrDefault(r => r.IsInvoiced)?.InvoiceNumber;

        /// <summary>
        /// True when the quote is in a state that allows invoicing at
        /// all. The 018 rule, unchanged: accepted quotes only.
        /// </summary>
        public bool QuoteIsBillable { get; set; }

        /// <summary>Why not, when QuoteIsBillable is false.</summary>
        public string? NotBillableReason { get; set; }
    }

    /// <summary>
    /// The save shape. THE NULL-VS-EMPTY RULE APPLIES, and this is the
    /// fourth time it has mattered (067 TaxCode, 069 Prices, 070
    /// BundleItems):
    ///
    ///   Milestones == null   → "nothing was said about the schedule";
    ///                          leave whatever is there alone.
    ///   Milestones == []     → "there is no schedule"; remove it, and
    ///                          the quote goes back to being invoiced
    ///                          once for the whole amount.
    ///
    /// The form ALWAYS sends a list, so on that path the two cases are
    /// never confused. null exists for API callers that are updating
    /// something else about the quote entirely.
    /// </summary>
    public class SaveBillingScheduleDto
    {
        public Guid TenantId { get; set; }
        public Guid QuoteId { get; set; }
        public List<QuoteMilestoneDto>? Milestones { get; set; }

        /// <summary>
        /// Who did it, for CreatedBy / UpdatedBy. Blank falls back to
        /// the signed-in user, the same way every other handler does it.
        /// </summary>
        public string? ActorName { get; set; }
    }
}
