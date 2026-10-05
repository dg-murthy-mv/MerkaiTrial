// =====================================================================
// Invoice.cs
// Location: MerkaiTrial.Domain/Entities/Invoice.cs
//
// CHANGES (018 — invoice workflow)
//   ✅ IssuedAtUtc / IssuedBy — when the invoice was issued (left Draft).
//   ✅ VoidedAtUtc / VoidedBy / VoidReason — an issued invoice is never
//      deleted or edited; it is VOIDED, with a reason. (Stored status stays
//      InvoiceStatus.Cancelled; the screens call it "Void".)
//   ✅ IsDraftNumber — a draft carries a placeholder "DRAFT-XXXXXXXX"; the
//      real INV-nnnn is given out only when it is issued, so deleting a
//      draft never leaves a gap in the tax invoice sequence.
//   ✅ TotalPaid counts CAPTURED payments only (reversed ones never).
//   ✅ IsOverdue ignores drafts (a draft isn't owed yet).
//   Columns are added by 018_InvoiceWorkflow.sql.
//
// CHANGES (052 — Phase B of the catalogue), on InvoiceLine only:
//   ✅ Quantity is DECIMAL(18,4), not int. 12.5 m², 3.5 days, 0.75 kg.
//   ✅ UnitOfMeasure snapshotted onto the line, so the number means
//      something on its own instead of leaving the unit in the description.
//   QuoteItem.cs takes the same two changes in the same round, on purpose:
//   an invoice raised from a quote copies the quote's lines, so a quote
//   that can say 12.5 against an invoice that rounds it to 12 would be
//   worse than neither having it.
//   Columns are altered / added by Sql/052_LineQuantityAndUnits.sql.
//
// CHANGES (055), on InvoiceLine only:
//   ✅ DiscountPercent — see LineDiscounts.cs. Added by
//      Sql/055_LineDiscountPercent.sql.
//
// CHANGES (067), on InvoiceLine only:
//   ✅ TaxCode — the HSN / SAC code or local equivalent, snapshotted onto
//      the line. Products.TaxCode has existed since 051 and had nowhere
//      to go: a GST invoice prints the code against every line, and the
//      line had no column for it. Copied from the quote item when the
//      invoice is raised from a quote, so an invoice says exactly what
//      the accepted quote said. QuoteItem.cs takes the same change in the
//      same round, for the same reason the 052 note gives.
//      Added by Sql/067_LineTaxCode.sql.
//
// CHANGES (071 — partial invoicing / milestone billing), on Invoice:
//   ✅ MilestoneId — which stage of the quote's billing schedule this
//      invoice is. NULL means "the whole quote", which is what every
//      invoice raised before this round is, and what every invoice
//      against a quote with no schedule still is.
//   ✅ MilestoneName / MilestoneSequence / MilestoneCount /
//      MilestonePercent — SNAPSHOTS, so "Milestone 2 of 3 — On delivery
//      (40%)" keeps saying that after somebody renames the stage or
//      re-cuts the schedule. Definition live, document frozen: the same
//      rule 067 applied to TaxCode and 070 applied to bundle contents.
//   ✅ The one-live-invoice-per-QUOTE rule becomes one live invoice per
//      MILESTONE — enforced for the first time by the database, in
//      UX_Invoices_Quote_Milestone_Live.
//      Columns are added by Sql/071_QuoteMilestones.sql.
// =====================================================================

using MerkaiTrial.Domain.Enums;

namespace MerkaiTrial.Domain.Entities
{
    public class Invoice
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid? QuoteId { get; set; }
        public Guid? DealId { get; set; }

        public string Number { get; set; } = string.Empty;  // INV-0001 once issued; DRAFT-XXXXXXXX before
        public DateTime IssueDateUtc { get; set; }
        public DateTime? DueDateUtc { get; set; }
        public string Currency { get; set; } = "USD";
        public InvoiceStatus Status { get; set; }

        // Breakdown amounts (matching Quotes structure)
        public decimal Subtotal { get; set; }       // GROSS — before line discounts
        public decimal DiscountTotal { get; set; }
        public decimal TaxTotal { get; set; }
        public decimal Total { get; set; }          // Subtotal − DiscountTotal + TaxTotal
        public decimal Balance { get; set; }        // Total − captured payments

        // Legacy fields (kept for compatibility with existing schema)
        public decimal Amount { get; set; }  // Same as Total

        // Payment gateway fields (existing)
        public string? PromptPayQrImageUrl { get; set; }
        public string? GatewayRef { get; set; }
        public string? Provider { get; set; }
        public string? ProviderRef { get; set; }
        public string? CheckoutUrl { get; set; }

        // Additional fields
        public string? PdfUrl { get; set; }
        public string? Notes { get; set; }

        // ── Workflow (018) ────────────────────────────────────────────
        public DateTime? IssuedAtUtc { get; set; }
        public string? IssuedBy { get; set; }
        public DateTime? VoidedAtUtc { get; set; }
        public string? VoidedBy { get; set; }
        public string? VoidReason { get; set; }

        // ── Milestone billing (071) ───────────────────────────────────
        //
        // ALL FIVE ARE NULL ON AN ORDINARY INVOICE, and "ordinary" means
        // both of the two cases that existed before this round: a manual
        // invoice, and an invoice for the whole of a quote that has no
        // billing schedule. Nothing has to be migrated and nothing has
        // to be opted out of — a workspace that never opens the schedule
        // screen will never see a non-NULL value here.

        /// <summary>
        /// The live link to the stage this invoice bills. Kept (rather
        /// than relying on the snapshots alone) for two jobs the
        /// snapshots cannot do: the "which stages are still to bill"
        /// rollup on the quote, and the schedule screen's refusal to
        /// change a stage that already has a live invoice.
        /// </summary>
        public Guid? MilestoneId { get; set; }

        /// <summary>
        /// SNAPSHOT of QuoteMilestone.Name at the moment the invoice was
        /// raised. An invoice already with the customer, and already in
        /// somebody's GST return, must keep saying "On delivery" after
        /// the schedule is renamed to "On handover".
        /// </summary>
        public string? MilestoneName { get; set; }

        /// <summary>SNAPSHOT. 1-based — the 2 in "milestone 2 of 3".</summary>
        public int? MilestoneSequence { get; set; }

        /// <summary>SNAPSHOT. The 3 in "milestone 2 of 3".</summary>
        public int? MilestoneCount { get; set; }

        /// <summary>
        /// SNAPSHOT of the share actually applied, as a PERCENT
        /// (40.0000 = 40%), whether the milestone was defined as a
        /// percentage or as a fixed amount. decimal(9,4), so a third of
        /// a quote stays 33.3333 instead of being rounded to 33.33 on
        /// its way in — the 062 lesson, in a new column.
        ///
        /// For the LAST milestone this is the share that was actually
        /// allocated, which is "everything not yet billed" and can
        /// therefore differ in the fourth decimal place from what the
        /// schedule screen showed. That is the rounding rule working,
        /// not a defect: the invoices sum to exactly the quote total.
        /// </summary>
        public decimal? MilestonePercent { get; set; }

        // Audit fields
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }

        // Navigation properties
        public Quote? Quote { get; set; }
        public Deal? Deal { get; set; }
        public QuoteMilestone? Milestone { get; set; }                 // 071
        public ICollection<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();
        public ICollection<Payment> Payments { get; set; } = new List<Payment>();

        // Calculated properties (not mapped — no setter)
        // Captured payments only — the same rule the handlers, queries and
        // reports use. Reversed / pending / failed never count.
        public decimal TotalPaid => Payments?
            .Where(p => !p.IsDeleted && p.Status == PaymentStatusNames.Captured)
            .Sum(p => p.Amount) ?? 0;

        public bool IsFullyPaid => Balance <= 0;

        public bool IsDraftNumber => Number?.StartsWith(InvoiceNumbering.DraftPrefix, StringComparison.Ordinal) == true;

        public bool IsOverdue =>
            DueDateUtc.HasValue && DueDateUtc.Value < DateTime.UtcNow && !IsFullyPaid &&
            Status != InvoiceStatus.Cancelled && Status != InvoiceStatus.Draft;

        /// <summary>
        /// 071. True when this invoice bills one stage of a schedule
        /// rather than a whole quote. Reads MilestoneId, not the
        /// snapshots — a row with a name but no id would be a bug, and
        /// this is the property every screen branches on.
        /// </summary>
        public bool IsMilestoneInvoice => MilestoneId.HasValue;

        /// <summary>
        /// 071. "Milestone 2 of 3 — On delivery (40%)", or null when
        /// this is not a milestone invoice. One place, so the three
        /// screens and the PDF cannot word it three different ways.
        /// </summary>
        public string? MilestoneLabel
        {
            get
            {
                if (!IsMilestoneInvoice) return null;

                var position = MilestoneSequence.HasValue && MilestoneCount.HasValue
                    ? $"Milestone {MilestoneSequence} of {MilestoneCount}"
                    : "Milestone";

                var name = string.IsNullOrWhiteSpace(MilestoneName) ? null : MilestoneName!.Trim();
                var share = MilestonePercent.HasValue ? $"{MilestonePercent.Value:0.##}%" : null;

                if (name != null && share != null) return $"{position} — {name} ({share})";
                if (name != null) return $"{position} — {name}";
                if (share != null) return $"{position} ({share})";
                return position;
            }
        }
    }

    public class InvoiceLine
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid InvoiceId { get; set; }
        public Guid? ProductId { get; set; }

        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public decimal UnitPrice { get; set; }

        /// <summary>
        /// 052: DECIMAL(18,4), was int. Same column, widened — no data moves.
        /// </summary>
        public decimal Quantity { get; set; }

        /// <summary>
        /// 052: a code from UnitsOfMeasure ("unit", "hour", "sqm"…). Copied
        /// from the quote item when the invoice is raised from a quote, and
        /// taken from the product (or typed) on a manual invoice. Snapshotted,
        /// never read live through ProductId: an issued tax invoice must not
        /// change because somebody edited the catalogue afterwards.
        /// </summary>
        public string UnitOfMeasure { get; set; } = "unit";

        public decimal LineDiscount { get; set; }

        /// <summary>
        /// 055: set when the discount was agreed as a PERCENTAGE, NULL when
        /// typed as an amount. Copied from the quote line when the invoice is
        /// raised from a quote, so the invoice says "-10%" exactly where the
        /// quote the customer accepted did.
        /// </summary>
        public decimal? DiscountPercent { get; set; }

        public decimal TaxRate { get; set; }  // 0.18 for 18%

        /// <summary>
        /// 067: NVARCHAR(20), nullable. The tax classification this line
        /// was INVOICED under — HSN / SAC in India, the local equivalent
        /// elsewhere, NULL where none applies.
        ///
        /// Copied from the quote item when the invoice is raised from a
        /// quote, and resolved from the product (or typed) on a manual
        /// invoice. Snapshotted, never read live through ProductId: an
        /// issued tax invoice is a filed document and must keep saying
        /// what was filed, whatever the catalogue says later.
        ///
        /// NULL and "" mean the same thing and are stored as NULL.
        /// </summary>
        public string? TaxCode { get; set; }

        // Legacy field (kept for compatibility)
        public decimal Amount { get; set; }  // Total line amount (calculated)

        // Audit fields
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }

        // Navigation properties
        public Invoice? Invoice { get; set; }
        public Product? Product { get; set; }

        // Calculated properties (same as QuoteItem)
        public decimal LineTotal => (UnitPrice * Quantity) - LineDiscount;
        public decimal LineTax => LineTotal * TaxRate;
        public decimal LineGrandTotal => LineTotal + LineTax;
    }

    /// <summary>Invoice numbers — the placeholder a draft carries, and the real sequence.</summary>
    public static class InvoiceNumbering
    {
        public const string DraftPrefix = "DRAFT-";
        public const string IssuedPrefix = "INV-";

        /// <summary>"DRAFT-3F9A1C2B" — unique enough per tenant; the unique index is the backstop.</summary>
        public static string NewDraftNumber()
            => DraftPrefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    }
}
