// =====================================================================
// PipelineRuleSettings.cs
// Location: MerkaiTrial.Domain/Entities/PipelineRuleSettings.cs
//
// NEW FILE (019).
//
// The transition rules that belong to the PIPELINE rather than to any
// one stage. Per-stage rules — "a deal needs an accepted quote before it
// can be Won" — live on PipelineStage, because they describe that stage.
// These four describe how the pipeline behaves as a whole, so they have
// nowhere else to go.
//
// WHY NOT TenantSettings
//   TenantSettings is the PLAN: how many users, how many deals, how much
//   storage. It is set by us when a tenant is provisioned, and a tenant
//   admin cannot touch it. These are COMMERCIAL POLICY, set by the client
//   about their own sales process. Mixing the two would mean a settings
//   page that is half theirs and half ours, and the first time we add a
//   plan field we would have to work out which half it belongs to.
//
//   The same reasoning put QuoteApprovalSettings in its own table, and
//   this follows that pattern exactly — including the "no row means the
//   defaults" rule below, which is why nothing is seeded.
// =====================================================================

namespace MerkaiTrial.Domain.Entities
{
    public class PipelineRuleSettings
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid TenantId { get; set; }

        /// <summary>
        /// Open stages may only be entered in ascending SortOrder — a deal
        /// cannot go back from Negotiation to Proposal.
        ///
        /// OFF by default, and deliberately so. Reps genuinely do move a
        /// deal back when a client goes quiet, and a CRM that refuses just
        /// teaches them to leave the stage wrong. A tenant who wants the
        /// discipline can switch it on; most should not.
        /// </summary>
        public bool ForwardOnly { get; set; }

        /// <summary>
        /// Reopening a Won or Lost deal needs a written reason, which is
        /// kept on the stage history row. ON by default: reopening a closed
        /// deal moves money between periods, and "why" is the first thing
        /// anyone asks three months later.
        /// </summary>
        public bool ReopenRequiresReason { get; set; } = true;

        /// <summary>
        /// Only a manager of the deal owner's team, or a workspace admin,
        /// may reopen a closed deal. ON by default — the same people who
        /// approve an over-limit quote.
        /// </summary>
        public bool ReopenRestrictedToManagers { get; set; } = true;

        /// <summary>
        /// A Won deal with an issued, un-voided invoice cannot be reopened
        /// at all. ON by default: the invoice is a tax document that has
        /// gone to the customer, and reopening the deal behind it would
        /// leave the books saying one thing and the pipeline another. Void
        /// the invoice first — that is a decision with its own reason and
        /// its own audit trail.
        /// </summary>
        public bool BlockReopenWithIssuedInvoice { get; set; } = true;

        // ── Quote-driven automatic moves (036) ────────────────────────
        //
        // These three replace the hardcoded stage names that used to sit in
        // QuotesCommandHandler: "Proposal" with probability 40 when a quote
        // was raised, "Negotiation" with 80 when one was accepted, and
        // "ClosedLost" with 0 when one was rejected.
        //
        // Every one of those was a literal, so a workspace whose pipeline is
        // Prospect → Demo → Commercials → Closed had its deals written into
        // a stage KEY THAT DOES NOT EXIST in their PipelineStages: no kanban
        // column, no probability, and IsTerminal false because the stage
        // could not be found at all. The deal simply disappeared off the
        // board when someone raised a quote on it.
        //
        // NULL means DO NOT MOVE THE DEAL. That is the default, and it is
        // the only safe answer when a workspace has not said what it wants —
        // a CRM that silently moves a deal to a stage nobody chose is worse
        // than one that leaves it alone. Migration 036 fills these in for
        // existing tenants wherever the old literal key really is one of
        // their stages, so nothing that worked before stops working.
        //
        // The probability is NOT configured here. It comes from the target
        // stage's own Probability, through StageTransitionGuard.ApplyToDeal,
        // which is the whole point: 40 and 80 were guesses about somebody
        // else's pipeline.

        /// <summary>
        /// Stage a deal moves to when a quote is raised on it. Null = no move.
        /// The move never demotes: it is skipped when the deal is already at
        /// or past this stage, and always skipped when the deal is closed.
        /// </summary>
        public string? QuoteSentStageKey { get; set; }

        /// <summary>
        /// Stage a deal moves to when a quote is accepted. Null = no move.
        /// Usually a late OPEN stage — "Negotiation", "Contracting" — but a
        /// workspace that treats acceptance as the sale can point it straight
        /// at a Won stage, and the close date and final value are then filled
        /// in for them.
        /// </summary>
        public string? QuoteAcceptedStageKey { get; set; }

        /// <summary>
        /// Stage a deal moves to when the customer rejects a quote. Null = no
        /// move, which is the right default for anyone who sends several
        /// quotes per deal — one rejected option is not a lost deal.
        /// </summary>
        public string? QuoteRejectedStageKey { get; set; }

        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }

        // Navigation
        public Tenant? Tenant { get; set; }
    }

    /// <summary>
    /// What a tenant with no saved row gets. Kept beside the entity so the
    /// entity's property initialisers and these can never disagree.
    /// </summary>
    public static class PipelineRuleDefaults
    {
        public const bool ForwardOnly = false;
        public const bool ReopenRequiresReason = true;
        public const bool ReopenRestrictedToManagers = true;
        public const bool BlockReopenWithIssuedInvoice = true;

        /// <summary>
        /// 036: null, deliberately. A tenant with no saved row gets NO
        /// automatic stage moves from quotes. Guessing a stage key for a
        /// pipeline we have never seen is exactly the bug this replaced.
        /// </summary>
        public const string? QuoteSentStageKey = null;
        public const string? QuoteAcceptedStageKey = null;
        public const string? QuoteRejectedStageKey = null;

        public static PipelineRuleSettings For(Guid tenantId) => new()
        {
            TenantId = tenantId,
            ForwardOnly = ForwardOnly,
            ReopenRequiresReason = ReopenRequiresReason,
            ReopenRestrictedToManagers = ReopenRestrictedToManagers,
            BlockReopenWithIssuedInvoice = BlockReopenWithIssuedInvoice,
            QuoteSentStageKey = QuoteSentStageKey,
            QuoteAcceptedStageKey = QuoteAcceptedStageKey,
            QuoteRejectedStageKey = QuoteRejectedStageKey
        };
    }
}
