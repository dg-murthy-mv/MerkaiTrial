// =====================================================================
// Notification.cs
// Location: MerkaiTrial.Domain/Entities/Notification.cs
//
// NEW FILE (037).
//
// WHAT THIS IS
//   One in-app notification for one person. Written SYNCHRONOUSLY, in the
//   same transaction as the event that caused it, because "sending" an
//   in-app notification is an INSERT into our own database — there is no
//   gateway to time out and nothing to retry.
//
//   That is the whole difference from email and SMS. Those call somebody
//   else's server, so they go through a queue and a worker (038). Putting
//   in-app notifications through the same queue would buy nothing and cost
//   the one property that matters here: a deal cannot move without the
//   notification, and a notification cannot exist for a move that rolled
//   back.
//
// NO URL COLUMN — ON PURPOSE
//   EntityType + EntityId are stored; the link is built by the page. A URL
//   in the database is a route decision frozen at write time, and it breaks
//   silently the first time a page moves. Notifications live for months.
//
// READ vs DISMISSED
//   Two separate timestamps, because they answer different questions.
//   ReadAtUtc = "I have seen this" (clears the bell count).
//   DismissedAtUtc = "take it off my list" (hides it, keeps the record).
//   A single IsRead flag would force "clear all" to be either a lie or a
//   delete.
// =====================================================================

using System.ComponentModel.DataAnnotations.Schema;

namespace MerkaiTrial.Domain.Entities
{
    /// <summary>
    /// What happened. Stored as an int, so ADD new members at the END and
    /// never renumber an existing one — the numbers are in the database and
    /// in every user's notification history.
    /// </summary>
    public enum NotificationEventType
    {
        /// <summary>A quote was sent to the customer.</summary>
        QuoteSent = 0,

        /// <summary>The customer accepted a quote.</summary>
        QuoteAccepted = 1,

        /// <summary>The customer rejected a quote.</summary>
        QuoteRejected = 2,

        /// <summary>Someone asked for a quote to be approved.</summary>
        QuoteApprovalRequested = 3,

        /// <summary>An approval request was approved, or changes were asked for.</summary>
        QuoteApprovalDecided = 4,

        /// <summary>A deal moved to a different stage.</summary>
        DealStageChanged = 5,

        /// <summary>A lead was assigned to someone.</summary>
        LeadAssigned = 6,

        /// <summary>A deal was assigned to someone.</summary>
        DealAssigned = 7
    }

    public class Notification
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid TenantId { get; set; }

        /// <summary>
        /// Who this is for. One row per recipient — notifying three people
        /// writes three rows, so each of them can read and dismiss it
        /// independently.
        /// </summary>
        public Guid RecipientUserId { get; set; }

        public NotificationEventType EventType { get; set; }

        /// <summary>
        /// The line shown in the bell. Written at dispatch time and never
        /// recomputed: "Quote QUO-0042 was accepted" must still say that
        /// after the quote is revised, because it is a record of what
        /// happened, not a view of current state.
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>One line of detail. Optional.</summary>
        public string? Body { get; set; }

        // ── What it points at ─────────────────────────────────────────
        /// <summary>"Quote", "Deal", "Lead". The page maps this to a route.</summary>
        public string? EntityType { get; set; }

        public Guid? EntityId { get; set; }

        // ── Who caused it ─────────────────────────────────────────────
        /// <summary>
        /// The person whose action produced this. Used to make sure nobody is
        /// told about their own click — being notified of what you just did is
        /// what makes people mute notifications. Null for a customer action on
        /// a public quote link, where there is no signed-in user.
        /// </summary>
        public Guid? ActorUserId { get; set; }

        /// <summary>
        /// Their name at the time, or "the customer". Denormalised on purpose:
        /// a notification must still read correctly after the actor's account
        /// is deactivated or renamed.
        /// </summary>
        public string? ActorName { get; set; }

        // ── State ─────────────────────────────────────────────────────
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Null = unread. Clearing this is what drops the bell count.</summary>
        public DateTime? ReadAtUtc { get; set; }

        /// <summary>Null = still on the list. Set = hidden, but kept.</summary>
        public DateTime? DismissedAtUtc { get; set; }

        // ── Navigation ────────────────────────────────────────────────
        public User? Recipient { get; set; }

        // ── Computed ──────────────────────────────────────────────────
        // [NotMapped] explicitly: EF does not map a get-only expression-bodied
        // property, but saying so stops anyone "fixing" it into a column.

        [NotMapped]
        public bool IsRead => ReadAtUtc.HasValue;

        [NotMapped]
        public bool IsDismissed => DismissedAtUtc.HasValue;
    }
}
