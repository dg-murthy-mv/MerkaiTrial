// =====================================================================
// InboundEmail.cs
// Location: MerkaiTrial.Domain/Entities/InboundEmail.cs
//
// NEW FILE (083). Email that came IN — a customer's reply, a copy a rep
// BCC'd, or a message a rep forwarded.
//
// THREE ENTITIES, ONE FILE
//   InboundEmail            the message itself
//   InboundEmailAttachment  files that came with it
//   EmailReplyToken         the code in reply+<token>@<domain>
//
// HOW A ROW IS BORN AND FINISHED
//   1. Resend calls the webhook. The webhook verifies the signature, works
//      out WHICH WORKSPACE the email is for (from the address it was sent
//      to), and writes this row with ProcessingState = Pending. That is
//      all — it answers Resend within milliseconds.
//   2. InboundEmailWorker picks the row up, downloads the body and the
//      attachments from Resend, decides which lead / deal / contact it
//      belongs to, copies the attachments onto that record, and tells the
//      owner. ProcessingState = Done.
//
//   Splitting it this way is the outbox pattern in reverse. Resend gives
//   up on a webhook that is slow to answer and retries it; doing the
//   downloads inside the webhook would turn one slow attachment into a
//   duplicate delivery. The unique index on ProviderEmailId makes those
//   retries harmless anyway.
//
// NO FOREIGN KEYS TO LEADS / DEALS / CONTACTS — ON PURPOSE
//   Those records are soft-deleted, and an email is a record of what a
//   customer said. Deleting a lead must not delete the conversation, and
//   a hard FK would either block the delete or cascade it.
// =====================================================================

using System.ComponentModel.DataAnnotations.Schema;

namespace MerkaiTrial.Domain.Entities
{
    /// <summary>Stored as an int — append, never renumber.</summary>
    public enum InboundEmailStatus
    {
        /// <summary>Nobody it belongs to was found. Waits in the Inbox.</summary>
        Unmatched = 0,

        /// <summary>Linked to at least one lead, deal or contact.</summary>
        Matched = 1,

        /// <summary>Someone decided it is not a customer email. Hidden, kept.</summary>
        Dismissed = 2
    }

    /// <summary>Stored as an int — append, never renumber.</summary>
    public enum InboundEmailDirection
    {
        /// <summary>From a customer to us — a reply, or a new message.</summary>
        Received = 0,

        /// <summary>
        /// A rep's own email to a customer, logged because they BCC'd the
        /// workspace address. The sender is one of our users.
        /// </summary>
        SentByUser = 1,

        /// <summary>
        /// A customer's email a rep forwarded to the workspace address. The
        /// original sender is read out of the forwarded text.
        /// </summary>
        Forwarded = 2
    }

    /// <summary>How the record was found. Stored as an int — append only.</summary>
    public enum InboundMatchMethod
    {
        None = 0,

        /// <summary>The tracked reply address on one of our emails. The strongest match.</summary>
        ReplyToken = 1,

        /// <summary>Its In-Reply-To pointed at an email we already filed.</summary>
        Thread = 2,

        /// <summary>The customer's address matched a contact or lead.</summary>
        Address = 3,

        /// <summary>Someone linked it by hand.</summary>
        Manual = 4
    }

    /// <summary>Where the worker is with it. Stored as an int — append only.</summary>
    public enum InboundProcessingState
    {
        /// <summary>Written by the webhook, waiting for the worker.</summary>
        Pending = 0,

        /// <summary>Claimed by a worker. LockedUntilUtc says until when.</summary>
        Processing = 1,

        /// <summary>Body fetched, matched, attachments saved.</summary>
        Done = 2,

        /// <summary>Failed for a reason worth retrying. NextAttemptAtUtc says when.</summary>
        Retry = 3,

        /// <summary>
        /// Out of attempts. Still matched on what the webhook told us —
        /// sender, subject — so it is not lost; only the body is missing.
        /// </summary>
        Failed = 4
    }

    public class InboundEmail
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid TenantId { get; set; }

        // ── Identity ──────────────────────────────────────────────────
        /// <summary>Resend's email_id. Unique: a retried webhook is ignored.</summary>
        public string ProviderEmailId { get; set; } = string.Empty;

        /// <summary>The RFC Message-ID. Lets a later reply find this email.</summary>
        public string? MessageId { get; set; }
        public string? InReplyTo { get; set; }

        public InboundEmailDirection Direction { get; set; } = InboundEmailDirection.Received;
        public InboundEmailStatus Status { get; set; } = InboundEmailStatus.Unmatched;
        public InboundMatchMethod MatchMethod { get; set; } = InboundMatchMethod.None;

        // ── Envelope ──────────────────────────────────────────────────
        public string FromAddress { get; set; } = string.Empty;
        public string? FromName { get; set; }

        /// <summary>Comma-separated, as received. Display only.</summary>
        public string? ToAddresses { get; set; }
        public string? CcAddresses { get; set; }

        /// <summary>
        /// The CUSTOMER's address — the sender for a received email, the
        /// recipient for one a rep BCC'd, the original sender for a forward.
        /// What matching was done against, and what "Create a lead" fills in.
        /// </summary>
        public string? CounterpartAddress { get; set; }

        // ── Content ───────────────────────────────────────────────────
        public string Subject { get; set; } = string.Empty;

        /// <summary>The first couple of lines, for lists and notifications.</summary>
        public string? Snippet { get; set; }

        public string? BodyText { get; set; }

        /// <summary>
        /// UNTRUSTED HTML from outside. Never written into a page as markup:
        /// the viewer shows it in a sandboxed iframe with scripts and remote
        /// content blocked.
        /// </summary>
        public string? BodyHtml { get; set; }

        /// <summary>Out-of-office, delivery failure and similar. Filed, never notified.</summary>
        public bool IsAutoReply { get; set; }

        /// <summary>
        /// True when the sender's domain vouched for it (DMARC/DKIM/SPF pass),
        /// false when it failed, null when Resend did not say. A failed check
        /// is never auto-matched by address — anyone can type a From line.
        /// </summary>
        public bool? SenderVerified { get; set; }
        public string? AuthSummary { get; set; }

        // ── What it belongs to ────────────────────────────────────────
        public Guid? LeadId { get; set; }
        public Guid? DealId { get; set; }
        public Guid? ContactId { get; set; }
        public Guid? CompanyId { get; set; }
        public Guid? QuoteId { get; set; }

        /// <summary>The user who BCC'd or forwarded it, when it came from one.</summary>
        public Guid? ForwardedByUserId { get; set; }

        /// <summary>A sentence about the match, e.g. "2 contacts share this address".</summary>
        public string? MatchNote { get; set; }

        // ── Attachments ───────────────────────────────────────────────
        public int AttachmentCount { get; set; }

        /// <summary>Which files were NOT kept and why ("setup.exe — type not allowed").</summary>
        public string? AttachmentNote { get; set; }

        /// <summary>What the webhook sent, so a failed download still has something to show.</summary>
        public string? MetadataJson { get; set; }

        // ── Processing ────────────────────────────────────────────────
        public InboundProcessingState ProcessingState { get; set; } = InboundProcessingState.Pending;
        public int ProcessingAttempts { get; set; }
        public DateTime? NextAttemptAtUtc { get; set; }
        public DateTime? LockedUntilUtc { get; set; }
        public string? ProcessingError { get; set; }

        // ── Audit ─────────────────────────────────────────────────────
        public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? ProcessedAtUtc { get; set; }
        public string? LinkedBy { get; set; }
        public DateTime? LinkedAtUtc { get; set; }

        public bool IsDeleted { get; set; }
        public DateTime? DeletedAtUtc { get; set; }
        public string? DeletedBy { get; set; }

        // ── Navigation ────────────────────────────────────────────────
        public ICollection<InboundEmailAttachment> Attachments { get; set; } = new List<InboundEmailAttachment>();

        // ── Computed ──────────────────────────────────────────────────
        [NotMapped]
        public bool HasLinks => LeadId.HasValue || DealId.HasValue || ContactId.HasValue || CompanyId.HasValue;
    }

    public class InboundEmailAttachment
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid TenantId { get; set; }
        public Guid InboundEmailId { get; set; }

        public string FileName { get; set; } = string.Empty;

        /// <summary>From IFileStorageService. Never build a URL by hand.</summary>
        public string FileUrl { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public string? MimeType { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public InboundEmail? Email { get; set; }
    }

    /// <summary>
    /// The code in reply+&lt;token&gt;@&lt;receiving domain&gt;. Random and
    /// unguessable, so there is no secret to configure and nothing to
    /// rotate; kept for good, so a reply months later still finds its quote.
    /// </summary>
    public class EmailReplyToken
    {
        public string Token { get; set; } = string.Empty;
        public Guid TenantId { get; set; }

        /// <summary>"Quote" today; "Deal", "Lead", "Invoice" as other emails gain tracking.</summary>
        public string EntityType { get; set; } = string.Empty;
        public Guid EntityId { get; set; }

        public Guid? OutboundMessageId { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>The EntityType a notification about an email carries.</summary>
    public static class InboundEmailEntityType
    {
        public const string InboundEmail = "InboundEmail";
    }
}
