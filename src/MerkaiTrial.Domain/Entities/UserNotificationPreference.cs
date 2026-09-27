// =====================================================================
// UserNotificationPreference.cs
// Location: MerkaiTrial.Domain/Entities/UserNotificationPreference.cs
//
// NEW FILE (038).
//
// ONE ROW PER USER PER EVENT TYPE. Two booleans on it: in-app, email.
//
// ABSENT MEANS "USE THE DEFAULT"
//   A user with no rows at all is the normal case, not an unconfigured
//   one. NotificationDefaults below decides what they get, and only the
//   settings page writes rows — and only for the event types the person
//   actually changed.
//
//   The alternative, seeding eight rows per user at provisioning, means
//   that adding a ninth event type in six months silently gives every
//   existing user nothing for it, because their row set predates it. This
//   way a new event type picks up its default for everyone immediately.
//
// WHY EMAIL DEFAULTS ARE MOSTLY OFF
//   The fastest way to make people ignore your product's email is to send
//   them one for everything. Only the two approval events default to
//   email, because those BLOCK someone else's work — a quote sitting
//   unapproved is a deal not moving, and the approver may not open the CRM
//   for hours.
//
//   Everything else is in-app only by default. A user who wants more can
//   turn it on; nobody has to turn anything off to make the product usable,
//   which is the test a notification default should pass.
// =====================================================================

namespace MerkaiTrial.Domain.Entities
{
    public class UserNotificationPreference
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid TenantId { get; set; }
        public Guid UserId { get; set; }

        public NotificationEventType EventType { get; set; }

        /// <summary>The bell. Defaults to on for everything.</summary>
        public bool InApp { get; set; } = true;

        /// <summary>Email. Defaults to off except the approval events.</summary>
        public bool Email { get; set; }

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public string? UpdatedBy { get; set; }

        // Navigation
        public User? User { get; set; }
    }

    /// <summary>
    /// What a user with no saved row gets. Kept beside the entity so the
    /// defaults and the property initialisers cannot disagree — the same
    /// arrangement as PipelineRuleDefaults.
    /// </summary>
    public static class NotificationDefaults
    {
        /// <summary>The bell is on for everything. It costs the user nothing.</summary>
        public static bool InAppFor(NotificationEventType _) => true;

        /// <summary>
        /// Email only where NOT being told promptly blocks someone.
        ///
        /// QuoteApprovalRequested — a quote is sitting unapproved, which is a
        ///   deal not moving, and the approver may not open the CRM today.
        /// QuoteApprovalDecided — the person who asked is waiting to send.
        ///
        /// Everything else waits for the bell.
        /// </summary>
        public static bool EmailFor(NotificationEventType eventType) => eventType switch
        {
            NotificationEventType.QuoteApprovalRequested => true,
            NotificationEventType.QuoteApprovalDecided   => true,
            _                                             => false
        };

        /// <summary>
        /// Every event type, in the order the settings page shows them:
        /// grouped by area, most interesting first.
        /// </summary>
        public static readonly NotificationEventType[] AllInDisplayOrder =
        {
            NotificationEventType.QuoteApprovalRequested,
            NotificationEventType.QuoteApprovalDecided,
            NotificationEventType.QuoteSent,
            NotificationEventType.QuoteAccepted,
            NotificationEventType.QuoteRejected,
            NotificationEventType.DealStageChanged,
            NotificationEventType.DealAssigned,
            NotificationEventType.LeadAssigned
        };

        /// <summary>The label on the settings page. Written for a salesperson.</summary>
        public static string Label(NotificationEventType eventType) => eventType switch
        {
            NotificationEventType.QuoteSent              => "A quote is sent to a customer",
            NotificationEventType.QuoteAccepted          => "A customer accepts a quote",
            NotificationEventType.QuoteRejected          => "A customer rejects a quote",
            NotificationEventType.QuoteApprovalRequested => "A quote needs my approval",
            NotificationEventType.QuoteApprovalDecided   => "My approval request is decided",
            NotificationEventType.DealStageChanged       => "One of my deals changes stage",
            NotificationEventType.LeadAssigned           => "A lead is assigned to me",
            NotificationEventType.DealAssigned           => "A deal is assigned to me",
            _                                             => eventType.ToString()
        };

        /// <summary>The group heading on the settings page.</summary>
        public static string Group(NotificationEventType eventType) => eventType switch
        {
            NotificationEventType.QuoteApprovalRequested => "Approvals",
            NotificationEventType.QuoteApprovalDecided   => "Approvals",
            NotificationEventType.QuoteSent              => "Quotes",
            NotificationEventType.QuoteAccepted          => "Quotes",
            NotificationEventType.QuoteRejected          => "Quotes",
            _                                             => "Deals and leads"
        };
    }
}
