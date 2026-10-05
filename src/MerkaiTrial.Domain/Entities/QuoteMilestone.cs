// =====================================================================
// QuoteMilestone.cs
// Location: MerkaiTrial.Domain/Entities/QuoteMilestone.cs
//
// NEW IN 071 — THE BILLING SCHEDULE ON A QUOTE
//
//   "30% on signing, 40% on delivery, 30% on completion."
//
//   Zero or more rows per quote. ZERO IS THE NORMAL STATE and means
//   "invoice the whole thing once", which is exactly what the product
//   did before this round — so no existing quote changes behaviour and
//   there is nothing to opt out of.
//
// A MILESTONE IS A PERCENTAGE **OR** A FIXED AMOUNT, NEVER BOTH
//   Percent      = 30.0000  → 30% of the quote's grand total
//   FixedAmount  = 50000.00 → ₹50,000 of it, in the quote's currency
//
//   Exactly one of the two is set. The database says so as well
//   (CK_QuoteMilestones_OneBasis in 071_QuoteMilestones.sql) because a
//   row with both has two answers to "how much is this stage" and a row
//   with neither invoices nothing at all.
//
//   Mixing the two WITHIN one schedule is normal and expected:
//   "₹50,000 on signing, the rest on completion" is two rows, one of
//   each. QuoteMilestones.Allocate is what turns the mixture into
//   amounts.
//
// PERCENT IS A PERCENT, NOT A FRACTION
//   30.0000 means 30%. This matches Product.TaxRate (which stores 18.00
//   for 18%) and is the OPPOSITE of QuoteItem.TaxRate / InvoiceLine
//   .TaxRate (which store 0.18). Three conventions in one database is
//   one too many — but this column is the one a human types into on a
//   form, so it matches the form, and every conversion in the codebase
//   happens exactly once, where the DTO is built.
//
// WHY THERE IS NO "Amount" COLUMN
//   Because a schedule is a DEFINITION, and the amount is derived from
//   the quote total, which can still change while the quote is a draft.
//   Storing the amount would mean a 40% milestone silently keeping an
//   old figure after a line was added. The amount is computed by
//   QuoteMilestones.Allocate every time it is needed, and SNAPSHOTTED
//   onto the invoice when one is raised (Invoice.MilestonePercent and
//   the invoice's own totals). Definition live, document frozen — the
//   same rule round 070 wrote down for bundle contents.
// =====================================================================

namespace MerkaiTrial.Domain.Entities
{
    public class QuoteMilestone
    {
        public Guid Id { get; set; }

        /// <summary>
        /// NOT NULL. A billing schedule is never shared across
        /// workspaces, so this entity is in FlowDbContext's STRICT query
        /// filter group (TenantId == CurrentTenantId) beside ProductPrice
        /// and ProductBundleItem — not the shared-or-tenant group that
        /// Role, CompanyVertical, TaxRate and ProductCategory use.
        /// </summary>
        public Guid TenantId { get; set; }

        public Guid QuoteId { get; set; }

        /// <summary>
        /// The order the stages happen in, hand-set. "Signing, delivery,
        /// completion" is the order the work happens in; alphabetical
        /// would scramble it. 0-based as stored, 1-based as displayed.
        /// </summary>
        public int SortOrder { get; set; }

        /// <summary>What the customer reads. "Advance", "On delivery", "Final payment".</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// A PERCENT (30.0000 = 30%), 0 &lt; Percent &lt;= 100.
        /// NULL when this milestone is a fixed amount instead.
        ///
        /// ⚠ THE COLUMN IS CALLED <c>SharePercent</c>, not Percent.
        /// PERCENT is a reserved T-SQL keyword — the one in "SELECT TOP
        /// 10 PERCENT" — so a column of that name would have to be
        /// bracketed in every hand-written query from now on. EF quotes
        /// identifiers anyway, which is exactly why the problem would
        /// have stayed invisible until somebody typed a SELECT in SSMS.
        ///
        /// The join between the two names is a single
        /// <c>.HasColumnName("SharePercent")</c> in
        /// QuoteMilestoneConfiguration. Without it the app starts fine
        /// and the first query fails with "Invalid column name
        /// 'Percent'".
        /// </summary>
        public decimal? Percent { get; set; }

        /// <summary>
        /// A money amount in the QUOTE's currency.
        /// NULL when this milestone is a percentage instead.
        /// </summary>
        public decimal? FixedAmount { get; set; }

        /// <summary>
        /// Free text — "On signing", "On delivery of the staging site",
        /// "30 days after handover". Printed under the milestone name on
        /// the quote PDF. Never parsed, never used in arithmetic.
        /// </summary>
        public string? DueCondition { get; set; }

        /// <summary>
        /// Optional hard date. Used as the SUGGESTED issue date when an
        /// invoice is raised for this milestone, and nothing else — no
        /// job raises invoices on a schedule, and one that did would
        /// need a conversation about who signs it off first.
        /// </summary>
        public DateTime? DueDateUtc { get; set; }

        // Audit fields
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }

        // Navigation
        public Quote? Quote { get; set; }

        /// <summary>
        /// Every invoice ever raised against this milestone — including
        /// the void ones, which is the point: a voided invoice frees the
        /// milestone to be billed again, and the history has to show
        /// that it happened.
        /// </summary>
        public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
    }
}
