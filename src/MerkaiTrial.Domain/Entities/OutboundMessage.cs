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
// 085 — WHAT HAPPENED AFTER IT LEFT
//   Sent only ever meant "Resend accepted it". The customer's mail server
//   could still refuse it a minute later. DeliveryStatus records what
//   Resend's webhook tells us afterwards — Delivered, Bounced, marked as
//   spam — so the quote and invoice pages can say "Delivered" or
//   "Bounced: the address doesn't exist" instead of a hopeful "Sent".
//
//   Also new: CcAddresses (invoice emails can copy the customer's accounts
//   team), SentByUserId (who to tell when it bounces), and attachments
//   (the invoice PDF) in their own table, OutboundMessageAttachments.
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

        /// <summary>
        /// Not implemented, and further away than it looks. India's TRAI DLT
        /// rules mean each TENANT registers as a principal entity with their
        /// own header and their own operator-approved templates — we cannot
        /// send on their behalf under ours. Left here as a reserved number,
        /// not as a to-do.
        /// </summary>
        Sms = 1,

        /// <summary>
        /// 044. WhatsApp, through Meta's Cloud API.
        ///
        /// HOW A WHATSAPP ROW USES THE EMAIL-SHAPED FIELDS BELOW. The queue
        /// was built for email and its columns say so. Rather than adding
        /// four nullable columns only one channel would ever fill, a
        /// WhatsApp row reuses them by a fixed convention:
        ///
        ///   ToAddress   the recipient's E.164 number, "+919876543210"
        ///   ToName      their display name, same as email
        ///   Subject     the Meta TEMPLATE NAME, e.g. "lead_assigned_v1"
        ///   BodyText    the template's variables as a JSON array, in
        ///               order: ["Priya","Acme Ltd","₹450,000"]
        ///   BodyHtml    the rendered message, for the log only — nobody
        ///               sends it, but an admin looking at the log needs to
        ///               see what the person actually received
        ///
        /// The convention lives here, beside the enum, so the sender and
        /// the log page cannot drift apart about what Subject means on a
        /// WhatsApp row.
        /// </summary>
        WhatsApp = 2
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

    /// <summary>
    /// 085. What the recipient's side did with a message Resend accepted.
    /// Stored as an int — append, never renumber.
    /// </summary>
    public enum OutboundDeliveryStatus
    {
        /// <summary>Nothing heard yet (or the message predates 085).</summary>
        None = 0,

        /// <summary>The recipient's mail server accepted it.</summary>
        Delivered = 1,

        /// <summary>Delivery is being retried by the provider — not a failure yet.</summary>
        Delayed = 2,

        /// <summary>Refused by the recipient's server — wrong address, full mailbox, blocked.</summary>
        Bounced = 3,

        /// <summary>The recipient marked it as spam.</summary>
        Complained = 4,

        /// <summary>The provider could not send it at all after accepting it.</summary>
        Failed = 5
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
        public string ToAddress { get => _toAddress; set => _toAddress = EmailHeaderText.OneLine(value) ?? string.Empty; }
        private string _toAddress = string.Empty;

        // 106 — VUL-24: every field that ends up in an email HEADER (To,
        // Reply-To, Cc, Subject) is kept to one line. A name or subject
        // carrying a line break ("Acme\r\nBcc: everyone@…") could otherwise
        // add a header of its own wherever the message is turned into a
        // real email. The body is HTML and is encoded where it is built.
        public string? ToName { get => _toName; set => _toName = EmailHeaderText.OneLine(value); }
        private string? _toName;

        /// <summary>Where replies go — normally the tenant's ReplyToEmail.</summary>
        public string? ReplyToAddress { get => _replyTo; set => _replyTo = EmailHeaderText.OneLine(value); }
        private string? _replyTo;

        /// <summary>
        /// 085. Copies, comma-separated ("accounts@acme.com, cfo@acme.com").
        /// Null for almost everything; invoice emails may carry up to five.
        /// </summary>
        public string? CcAddresses { get => _cc; set => _cc = EmailHeaderText.OneLine(value); }
        private string? _cc;

        /// <summary>
        /// 085. The person who pressed Send, when a person did. They are the
        /// one told if it bounces. Null for automatic emails (notifications,
        /// a quote moved to Sent by a status change) — a bounce then goes to
        /// the owner of the deal it is about.
        /// </summary>
        public Guid? SentByUserId { get; set; }

        // ── What it says ──────────────────────────────────────────────
        public string Subject { get => _subject; set => _subject = EmailHeaderText.OneLine(value) ?? string.Empty; }
        private string _subject = string.Empty;

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

        // ── After it left (085) ───────────────────────────────────────

        /// <summary>What the recipient's side did, from Resend's webhook.</summary>
        public OutboundDeliveryStatus DeliveryStatus { get; set; } = OutboundDeliveryStatus.None;

        /// <summary>When DeliveryStatus last changed.</summary>
        public DateTime? DeliveryStatusAtUtc { get; set; }

        /// <summary>
        /// 085. The reason, for a bounce or failure, in the words the
        /// provider gave ("Permanent: the address does not exist"). Shown
        /// on the record so the rep knows what to fix.
        /// </summary>
        public string? DeliveryDetail { get; set; }

        // ── Audit ─────────────────────────────────────────────────────
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? SentAtUtc { get; set; }

        /// <summary>Who retried or cancelled it by hand, if anyone.</summary>
        public string? UpdatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }

        /// <summary>085. Files sent with it — the invoice PDF.</summary>
        public ICollection<OutboundMessageAttachment> Attachments { get; set; } = new List<OutboundMessageAttachment>();

        // ── Computed ──────────────────────────────────────────────────

        [NotMapped]
        public bool IsFinished =>
            Status is OutboundStatus.Sent or OutboundStatus.Cancelled;

        /// <summary>Needs a person: out of attempts, or cancelled by mistake.</summary>
        [NotMapped]
        public bool NeedsAttention => Status == OutboundStatus.Dead;
    }

    /// <summary>
    /// 085. A file sent with an outbound message — today, the invoice PDF.
    ///
    /// The BYTES are stored here, not a path on disk. The PDF is made at
    /// queue time (that is when the tenant context exists to render it),
    /// sent seconds later, and removed with its message by the worker's
    /// retention (cascade delete). A file on disk would outlive its row
    /// the first time a retention pass deleted the message.
    ///
    /// Capped by the sender (5 MB per file); an invoice PDF is ~50–200 KB.
    /// </summary>
    public class OutboundMessageAttachment
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid OutboundMessageId { get; set; }
        public Guid TenantId { get; set; }

        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = "application/pdf";
        public long SizeBytes { get; set; }
        public byte[] Content { get; set; } = Array.Empty<byte>();

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public OutboundMessage? Message { get; set; }
    }
}

namespace MerkaiTrial.Domain.Entities
{
    /// <summary>
    /// 106 — VUL-24. Text that goes into an email header: one line, always.
    /// Every control character (line breaks, tabs, NUL) and the Unicode line
    /// and paragraph separators become a space; runs of spaces become one.
    /// </summary>
    public static class EmailHeaderText
    {
        public static string? OneLine(string? value)
        {
            if (value is null) return null;
            var sb = new System.Text.StringBuilder(value.Length);
            var lastWasSpace = false;
            foreach (var c in value)
            {
                var space = char.IsControl(c) || c is '\u2028' or '\u2029' or '\u0085' || char.IsWhiteSpace(c);
                if (space)
                {
                    if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                    lastWasSpace = true;
                }
                else
                {
                    sb.Append(c);
                    lastWasSpace = false;
                }
            }
            return sb.ToString().TrimEnd();
        }
    }
}
