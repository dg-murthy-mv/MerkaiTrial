// =====================================================================
// OutboundMessage.cs
// Location: MerkaiTrial.Domain/Entities/OutboundMessage.cs
//
// NEW FILE (038).
//
// THE OUTBOX
//   One row per email (later: SMS) waiting to be sent. Written in the SAME
//   TRANSACTION as the thing that caused it, and drained afterwards by a
//   background worker.
//
//   That is the whole point, and it is worth being precise about what it
//   buys, because it is two different bugs at once:
//
//     "deal saved but the customer never got the quote email"
//         — the send happened outside the transaction and failed
//     "email sent, then the save rolled back"
//         — the send happened before the commit and could not be un-sent
//
//   Writing a ROW inside the transaction removes both. The row exists if
//   and only if the business change did, and the actual call to Resend
//   happens later where it can fail, retry and be seen.
//
// WHY THIS IS NOT HOW IN-APP NOTIFICATIONS WORK
//   An in-app notification is an INSERT into our own database — no server
//   to time out, nothing to retry. It is written inline (037). Email calls
//   somebody else's API over the internet, which can hang for thirty
//   seconds and fail for a dozen reasons. Different problem, different
//   mechanism.
//
// IDEMPOTENCY
//   Id doubles as the Resend idempotency key. If we time out waiting for a
//   response and retry, Resend recognises the key and does not send twice.
//   Without that, every network blip becomes a duplicate email to a
//   customer.
// =====================================================================

using System.ComponentModel.DataAnnotations.Schema;

namespace MerkaiTrial.Domain.Entities
{
    /// <summary>
    /// Stored as an int — append, never renumber.
    /// </summary>
    public enum OutboundChannel
    {
        Email = 0,

        /// <summary>Not implemented yet. The queue and the worker are
        /// channel-agnostic; only the sender is missing.</summary>
        Sms = 1
    }

    public enum OutboundStatus
    {
        /// <summary>Waiting for the worker.</summary>
        Pending = 0,

        /// <summary>Claimed by a worker and in flight. See LockedUntilUtc.</summary>
        Sending = 1,

        /// <summary>Accepted by the provider. ProviderMessageId holds their id.</summary>
        Sent = 2,

        /// <summary>Failed and will be retried. NextAttemptAtUtc says when.</summary>
        Failed = 3,

        /// <summary>Out of attempts. Needs a person to look at it and retry.</summary>
        Dead = 4,

        /// <summary>Cancelled by an admin before it went out.</summary>
        Cancelled = 5
    }

    public class OutboundMessage
    {
        /// <summary>
        /// Also the provider idempotency key — see the header. Generated on
        /// creation and never reused, including across retries, which is
        /// exactly what makes a retry safe.
        /// </summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid TenantId { get; set; }

        public OutboundChannel Channel { get; set; } = OutboundChannel.Email;
        public OutboundStatus Status { get; set; } = OutboundStatus.Pending;

        /// <summary>Which event produced it. For the log's filter.</summary>
        public NotificationEventType EventType { get; set; }

        // ── Who it goes to ────────────────────────────────────────────
        /// <summary>
        /// The user this is for, when there is one. Null for a message to a
        /// customer, who is not a user of the system.
        /// </summary>
        public Guid? RecipientUserId { get; set; }

        /// <summary>
        /// The address, resolved at QUEUE time rather than at send time.
        /// Deliberate: if someone's email changes between the deal closing
        /// and the worker running, the message should still go where it was
        /// addressed. It is also the only copy once a user is deleted.
        /// </summary>
        public string ToAddress { get; set; } = string.Empty;

        public string? ToName { get; set; }

        /// <summary>Where replies go — normally the tenant's ReplyToEmail.</summary>
        public string? ReplyToAddress { get; set; }

        // ── What it says ──────────────────────────────────────────────
        public string Subject { get; set; } = string.Empty;

        /// <summary>
        /// Rendered at QUEUE time, not at send time. A message is a record of
        /// what was said, and rendering it later would quietly change the
        /// wording if a template or the underlying record moved on.
        /// </summary>
        public string BodyHtml { get; set; } = string.Empty;

        /// <summary>
        /// The plain-text alternative. Not optional in practice: an email
        /// with no text part scores worse with spam filters and is unreadable
        /// in a text-only client.
        /// </summary>
        public string BodyText { get; set; } = string.Empty;

        // ── What it is about ──────────────────────────────────────────
        public string? EntityType { get; set; }
        public Guid? EntityId { get; set; }

        // ── Delivery state ────────────────────────────────────────────
        public int AttemptCount { get; set; }

        /// <summary>When the worker should next try. Null means "now".</summary>
        public DateTime? NextAttemptAtUtc { get; set; }

        /// <summary>
        /// Set when a worker claims the row, so a second instance skips it.
        /// A crash mid-send leaves this in the past, and the row becomes
        /// claimable again — which is why the provider idempotency key
        /// matters.
        /// </summary>
        public DateTime? LockedUntilUtc { get; set; }

        /// <summary>Resend's id for the accepted message. For support questions.</summary>
        public string? ProviderMessageId { get; set; }

        /// <summary>Why the last attempt failed. Shown in the log.</summary>
        public string? LastError { get; set; }

        // ── Audit ─────────────────────────────────────────────────────
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? SentAtUtc { get; set; }

        /// <summary>Who retried or cancelled it by hand, if anyone.</summary>
        public string? UpdatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }

        // ── Computed ──────────────────────────────────────────────────

        [NotMapped]
        public bool IsFinished =>
            Status is OutboundStatus.Sent or OutboundStatus.Cancelled;

        /// <summary>Needs a person: out of attempts, or cancelled by mistake.</summary>
        [NotMapped]
        public bool NeedsAttention => Status == OutboundStatus.Dead;
    }
}
