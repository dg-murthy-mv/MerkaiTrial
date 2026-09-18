using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Domain.Entities
{
    public static class AuditAction
    {
        // ── Access and identity (existing) ────────────────────────────
        public const string ViewAsStart = "ViewAsStart";
        public const string ViewAsEnd = "ViewAsEnd";
        public const string SignIn = "SignIn";
        public const string SignInFailed = "SignInFailed";
        public const string PasswordChanged = "PasswordChanged";
        public const string InviteIssued = "InviteIssued";
        public const string ResetIssued = "ResetIssued";
        public const string PermissionChanged = "PermissionChanged";

        // ── Tenant lifecycle (existing) ───────────────────────────────
        public const string TrialProvisioned = "TrialProvisioned";
        public const string TrialExtended = "TrialExtended";
        public const string TrialSuspended = "TrialSuspended";
        public const string TenantPlanChanged = "TenantPlanChanged";
        public const string TrialConverted = "TrialConverted";

        public const string ContactDeleted = "ContactDeleted";
        public const string CompanyDeleted = "CompanyDeleted";

        public const string ProductPriceChanged = "ProductPriceChgd";
        public const string ProductDeleted = "ProductDeleted";
        public const string ActivityCreated = "ActivityCreated";
        public const string ActivityUpdated = "ActivityUpdated";
        // =============================================================
        // CRM RECORDS — new
        //
        // WHAT IS AUDITED, AND WHAT IS NOT
        //
        //   Audited: creation, deletion, ownership changes, and every
        //   STATE TRANSITION — lead status, deal stage, quote status,
        //   invoice status. Plus anything touching money.
        //
        //   Not audited: reads (every page load would write a row), and
        //   routine activity or note CREATION — the timeline already
        //   shows those. Their DELETION is audited, because that is
        //   precisely what the timeline can no longer show you.
        //
        //   Field-level edits are audited as one row with the changed
        //   fields in Data, not one row per field.
        //
        // Values must stay <= 32 chars — AuditLogConfiguration caps the
        // Action column. The longest below is 18.
        // =============================================================

        // ── Leads ─────────────────────────────────────────────────────
        public const string LeadCreated = "LeadCreated";
        public const string LeadUpdated = "LeadUpdated";
        public const string LeadDeleted = "LeadDeleted";
        /// <summary>Data: { from, to }. This IS the lead's status history.</summary>
        public const string LeadStatusChanged = "LeadStatusChanged";
        public const string LeadOwnerChanged = "LeadOwnerChanged";
        public const string LeadConverted = "LeadConverted";
        public const string LeadsImported = "LeadsImported";

        // ── Deals ─────────────────────────────────────────────────────
        public const string DealCreated = "DealCreated";
        public const string DealUpdated = "DealUpdated";
        public const string DealDeleted = "DealDeleted";
        /// <summary>Data: { from, to, reason }. Mirrors DealStageHistory.</summary>
        public const string DealStageChanged = "DealStageChanged";
        public const string DealOwnerChanged = "DealOwnerChanged";
        /// <summary>An automatic stage move failed. Someone must fix it by hand.</summary>
        public const string DealStageFailed = "DealStageFailed";

        // ── Quotes ────────────────────────────────────────────────────
        public const string QuoteCreated = "QuoteCreated";
        public const string QuoteUpdated = "QuoteUpdated";
        public const string QuoteDeleted = "QuoteDeleted";
        public const string QuoteStatusChanged = "QuoteStatusChanged";
        /// <summary>Accepted by the customer through the public link.</summary>
        public const string QuoteAcceptedPublic = "QuoteAcceptedPublic";
        public const string QuoteRejectedPublic = "QuoteRejectedPublic";

        // ── Invoices ──────────────────────────────────────────────────
        public const string InvoiceCreated = "InvoiceCreated";
        public const string InvoiceUpdated = "InvoiceUpdated";
        public const string InvoiceDeleted = "InvoiceDeleted";
        public const string InvoiceStatusChanged = "InvoiceStatusChgd";
        public const string InvoiceCancelled = "InvoiceCancelled";

        // ── Payments — money, so audited without exception ────────────
        public const string PaymentRecorded = "PaymentRecorded";
        public const string PaymentDeleted = "PaymentDeleted";

        // ── Activities ────────────────────────────────────────────────
        /// <summary>Only deletion. Creation is visible in the timeline.</summary>
        public const string ActivityDeleted = "ActivityDeleted";

        // ── Refused actions ───────────────────────────────────────────
        /// <summary>
        /// A write was refused by a business rule, not by permissions.
        /// Worth keeping: "why can't I mark this paid" is answerable only
        /// if the refusal was recorded.
        /// </summary>
        public const string ActionRefused = "ActionRefused";
    }

    public class AuditLog
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Which tenant's data this concerns. NULL for system-level
        /// events (a failed login before any tenant is known, a plan edit).</summary>
        public Guid? TenantId { get; set; }

        public string EntityType { get; set; } = string.Empty;
        public Guid? EntityId { get; set; }
        public string Action { get; set; } = string.Empty;

        /// <summary>Identity the action was performed AS — during ViewAs this
        /// is the impersonated user.</summary>
        public string By { get; set; } = "system";

        /// <summary>Identity that actually performed it. During ViewAs this is
        /// the real super admin, and the difference between this and By is the
        /// entire value of the record.</summary>
        public Guid? ActorUserId { get; set; }

        public string? IpAddress { get; set; }

        /// <summary>JSON detail. Never put credentials, tokens or raw
        /// personal data here — an audit log is widely readable by design.</summary>
        public string? Data { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }

        // Present on the table, deliberately unused — see migration 002.
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }
    }

    /// <summary>
    /// EntityType values used in AuditLog. Strings rather than an enum so
    /// old rows with retired types still read back.
    /// </summary>
    public static class AuditEntityType
    {
        public const string Tenant   = "Tenant";
        public const string User     = "User";
        public const string Role     = "Role";
        public const string Lead     = "Lead";
        public const string Deal     = "Deal";
        public const string Quote    = "Quote";
        public const string Invoice  = "Invoice";
        public const string Payment  = "Payment";
        public const string Activity = "Activity";
        public const string Contact = "Contact";
        public const string Company = "Company";
        public const string Product = "Product";
    }
}
