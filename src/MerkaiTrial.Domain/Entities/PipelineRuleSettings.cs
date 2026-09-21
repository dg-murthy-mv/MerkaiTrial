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

        public static PipelineRuleSettings For(Guid tenantId) => new()
        {
            TenantId = tenantId,
            ForwardOnly = ForwardOnly,
            ReopenRequiresReason = ReopenRequiresReason,
            ReopenRestrictedToManagers = ReopenRestrictedToManagers,
            BlockReopenWithIssuedInvoice = BlockReopenWithIssuedInvoice
        };
    }
}
