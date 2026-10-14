// =====================================================================
// EmailDeliveryEvents.cs
// Location: MerkaiTrial.Application/Services/Notifications/EmailDeliveryEvents.cs
//
// NEW FILE (085). What happened to an email AFTER Resend accepted it.
//
// Resend calls the SAME webhook as received mail (POST /api/inbound/resend,
// same signing secret) with events about mail we sent:
//
//   email.delivered         the recipient's server accepted it   → Delivered
//   email.delivery_delayed  still trying                         → Delayed
//   email.bounced           refused (bad address, full mailbox)  → Bounced
//   email.complained        the recipient marked it as spam      → Complained
//   email.failed            Resend could not send it at all      → Failed
//
// InboundEmailReceiver checks the signature, then hands these here.
// email.sent / opened / clicked are ignored on purpose (see SETUP.md for
// why opens are not tracked).
//
// FINDING THE EMAIL. The event names it only by Resend's id, which the
// worker stores as OutboundMessage.ProviderMessageId. There is no tenant on
// this call (Resend is anonymous to us), so the ONE lookup is
// IgnoreQueryFilters — FlowDbContext item 18 — and everything after it
// uses the TenantId of the row found.
//
// "NOT FOUND YET" IS NOT "NOT OURS". The worker saves the provider id a
// moment after Resend accepts the email, and "Delivered" can arrive within
// a second. So an unknown id on an event less than three minutes old is
// answered "try again later" (503 — Resend retries in a few seconds); an
// older one is genuinely not ours (sent before 085, or by another app on
// the same Resend account) and is answered 200 and dropped.
//
// STATES ONLY GET WORSE. Events can arrive out of order. A late
// "Delivered" never overwrites "Bounced"; a "Delayed" never overwrites
// anything. A bounce AFTER delivery does overwrite it — that is a real
// thing mail servers do.
//
// TELLING SOMEBODY. On the first move to Bounced, Complained or Failed,
// the person who pressed Send (SentByUserId) gets an EmailBounced
// notification; for an automatic email it goes to the deal's owner. Never
// for our own notification emails to users — a bounce on one of those
// would notify the person it could not reach. The notification is written
// on a context scoped to the email's workspace, exactly as
// InboundEmailWorker does it, so preferences and locks apply.
// =====================================================================

using System.Text.Json;
using MerkaiTrial.Application.Services.Inbound;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using MerkaiTrial.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MerkaiTrial.Application.Services.Notifications;

public interface IEmailDeliveryEvents
{
    /// <summary>True for the five delivery event types this class handles.</summary>
    bool Handles(string? eventType);

    /// <summary>Applies one signed event. <paramref name="root"/> is the whole event JSON.</summary>
    Task<InboundReceiveResult> ApplyAsync(string eventType, JsonElement root, CancellationToken ct = default);
}

public sealed class EmailDeliveryEvents : IEmailDeliveryEvents
{
    private static readonly TimeSpan RetryWindow = TimeSpan.FromMinutes(3);

    private readonly FlowDbContext _db;
    private readonly DbContextOptions<FlowDbContext> _dbOptions;
    private readonly IOptions<EmailOptions> _emailOptions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<EmailDeliveryEvents> _logger;

    public EmailDeliveryEvents(
        FlowDbContext db,
        DbContextOptions<FlowDbContext> dbOptions,
        IOptions<EmailOptions> emailOptions,
        ILoggerFactory loggerFactory,
        ILogger<EmailDeliveryEvents> logger)
    {
        _db = db;
        _dbOptions = dbOptions;
        _emailOptions = emailOptions;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public bool Handles(string? eventType) => eventType is
        "email.delivered" or "email.delivery_delayed" or "email.bounced" or "email.complained" or "email.failed";

    public async Task<InboundReceiveResult> ApplyAsync(string eventType, JsonElement root, CancellationToken ct = default)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return new(InboundReceiveOutcome.Malformed, "No data in the event.");

        var emailId = Str(data, "email_id");
        if (string.IsNullOrWhiteSpace(emailId) || emailId.Length > 200)
            return new(InboundReceiveOutcome.Malformed, "No email id in the event.");

        // ── The one anonymous lookup (FlowDbContext item 18) ──────────
        var message = await _db.OutboundMessages
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.ProviderMessageId == emailId, ct);

        if (message is null)
        {
            var eventAt = EventTime(root);
            if (eventAt is { } at && DateTime.UtcNow - at < RetryWindow)
            {
                _logger.LogInformation(
                    "Delivery event {Type} for {EmailId} arrived before the email was recorded — asking Resend to retry",
                    eventType, emailId);
                return new(InboundReceiveOutcome.RetryLater, "That email is not recorded yet — try again shortly.");
            }

            return new(InboundReceiveOutcome.Ignored, "Not an email this app sent.");
        }

        var previous = message.DeliveryStatus;
        var (next, detail) = Map(eventType, data);

        if (!ShouldReplace(previous, next))
            return new(InboundReceiveOutcome.Duplicate, $"Already {previous}.");

        message.DeliveryStatus = next;
        message.DeliveryStatusAtUtc = DateTime.UtcNow;
        message.DeliveryDetail = Cap(detail, 500);
        message.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Email {MessageId} to {To}: {Previous} → {Next}",
            message.Id, InboundAddressing.Mask(message.ToAddress), previous, next);

        if (IsProblem(next) && !IsProblem(previous) && message.RecipientUserId is null)
        {
            try
            {
                await NotifyAsync(message, ct);
            }
            catch (Exception ex)
            {
                // The status is saved; a missed bell is not worth a Resend retry.
                _logger.LogError(ex, "Could not raise the bounce notification for email {MessageId}", message.Id);
            }
        }

        return new(InboundReceiveOutcome.Accepted, $"Recorded {next}.");
    }

    // =================================================================
    // rules
    // =================================================================

    private static bool IsProblem(OutboundDeliveryStatus s)
        => s is OutboundDeliveryStatus.Bounced or OutboundDeliveryStatus.Complained or OutboundDeliveryStatus.Failed;

    /// <summary>States only get worse — see the header.</summary>
    private static bool ShouldReplace(OutboundDeliveryStatus current, OutboundDeliveryStatus next)
    {
        if (current == next) return false;

        return next switch
        {
            OutboundDeliveryStatus.Delayed   => current == OutboundDeliveryStatus.None,
            OutboundDeliveryStatus.Delivered => current is OutboundDeliveryStatus.None or OutboundDeliveryStatus.Delayed,
            _                                => true    // Bounced / Complained / Failed always win
        };
    }

    private static (OutboundDeliveryStatus Status, string? Detail) Map(string eventType, JsonElement data) => eventType switch
    {
        "email.delivered"        => (OutboundDeliveryStatus.Delivered, null),
        "email.delivery_delayed" => (OutboundDeliveryStatus.Delayed, "Delivery is delayed — the email provider is still trying."),
        "email.complained"       => (OutboundDeliveryStatus.Complained, "The recipient marked this email as spam."),
        "email.failed"           => (OutboundDeliveryStatus.Failed, FailedDetail(data)),
        _                        => (OutboundDeliveryStatus.Bounced, BounceDetail(data))
    };

    /// <summary>A sentence the rep can act on, then the server's own words.</summary>
    private static string BounceDetail(JsonElement data)
    {
        string? type = null, subType = null, raw = null;

        if (data.TryGetProperty("bounce", out var b) && b.ValueKind == JsonValueKind.Object)
        {
            type = Str(b, "type");
            subType = Str(b, "subType") ?? Str(b, "sub_type");
            raw = Str(b, "message");
        }

        var permanent = string.Equals(type, "Permanent", StringComparison.OrdinalIgnoreCase);

        var friendly = subType switch
        {
            "Suppressed"         => "This address bounced before, so the email provider did not try it again.",
            "OnAccountSuppressionList" => "This address is on the sending account's block list after an earlier bounce.",
            "NoEmail"            => "The address does not exist.",
            "MailboxFull"        => "The recipient's mailbox is full.",
            "MessageTooLarge"    => "The email was too large for the recipient's server.",
            "ContentRejected"    => "The recipient's server rejected the email's content.",
            "AttachmentRejected" => "The recipient's server rejected the attachment.",
            _ when permanent     => "The address does not exist or the recipient's server refused it.",
            _                    => "The recipient's server refused it for now."
        };

        return string.IsNullOrWhiteSpace(raw) ? friendly : $"{friendly} ({raw.Trim()})";
    }

    private static string FailedDetail(JsonElement data)
    {
        string? reason = null;
        if (data.TryGetProperty("failed", out var f) && f.ValueKind == JsonValueKind.Object)
            reason = Str(f, "reason");

        return string.IsNullOrWhiteSpace(reason)
            ? "The email provider could not send it."
            : $"The email provider could not send it ({reason.Trim()}).";
    }

    // =================================================================
    // the notification
    // =================================================================

    private async Task NotifyAsync(OutboundMessage message, CancellationToken ct)
    {
        // This email's workspace, for everything below — rule 4 of
        // InboundEmailWorker, for the same reason.
        await using var db = new FlowDbContext(_dbOptions, new FixedTenantProvider(message.TenantId));

        var recipients = new List<Guid>();
        if (message.SentByUserId is { } sender) recipients.Add(sender);

        var (label, owners) = await DocumentAsync(db, message, ct);
        if (recipients.Count == 0) recipients.AddRange(owners);

        if (recipients.Count == 0)
        {
            _logger.LogInformation("Email {MessageId} bounced but nobody is attached to it to tell", message.Id);
            return;
        }

        var what = message.DeliveryStatus switch
        {
            OutboundDeliveryStatus.Complained => "was marked as spam",
            OutboundDeliveryStatus.Failed     => "could not be sent",
            _                                 => "couldn't be delivered"
        };

        var title = string.IsNullOrWhiteSpace(label)
            ? $"Email to {message.ToAddress} {what}"
            : $"{label} to {message.ToAddress} {what}";

        var body = $"{message.DeliveryDetail} Check the address and send it again.";

        var dispatcher = new NotificationDispatcher(
            db, _loggerFactory.CreateLogger<NotificationDispatcher>(), _emailOptions);

        await dispatcher.AddForUsersAsync(new NotificationRequest(
            TenantId:    message.TenantId,
            EventType:   NotificationEventType.EmailBounced,
            Title:       Cap(title, 200)!,
            Body:        Cap(body, 500)!,
            EntityType:  AuditEntityType.OutboundMessage,
            EntityId:    message.Id,
            ActorUserId: null,
            ActorName:   "Email delivery"), recipients.Distinct().ToList(), ct);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>"Invoice INV-0003" / "Quote QUO-0007", and the owner of its deal.</summary>
    private static async Task<(string? Label, List<Guid> Owners)> DocumentAsync(
        FlowDbContext db, OutboundMessage m, CancellationToken ct)
    {
        string? label = null;
        string? owner = null;

        if (m.EntityType == "Invoice" && m.EntityId is { } invoiceId)
        {
            var row = await db.Invoices.AsNoTracking()
                .Where(i => i.Id == invoiceId && i.TenantId == m.TenantId)
                .Select(i => new
                {
                    i.Number,
                    Owner = i.Deal != null ? i.Deal.OwnerUserId
                          : i.Quote != null && i.Quote.Deal != null ? i.Quote.Deal.OwnerUserId : null
                })
                .FirstOrDefaultAsync(ct);
            label = row is null ? "Invoice" : $"Invoice {row.Number}";
            owner = row?.Owner;
        }
        else if (m.EntityType == "Quote" && m.EntityId is { } quoteId)
        {
            var row = await db.Quotes.AsNoTracking()
                .Where(q => q.Id == quoteId && q.TenantId == m.TenantId)
                .Select(q => new { q.Number, Owner = q.Deal != null ? q.Deal.OwnerUserId : null })
                .FirstOrDefaultAsync(ct);
            label = row is null ? "Quote" : $"Quote {row.Number}";
            owner = row?.Owner;
        }

        var owners = Guid.TryParse(owner, out var g) && g != Guid.Empty ? new List<Guid> { g } : new List<Guid>();
        return (label, owners);
    }

    // =================================================================
    // helpers
    // =================================================================

    private static DateTime? EventTime(JsonElement root)
    {
        if (root.TryGetProperty("created_at", out var c) && c.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(c.GetString(), out var at))
            return at.UtcDateTime;
        return null;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Cap(string? value, int max)
        => string.IsNullOrEmpty(value) ? value : (value.Length <= max ? value : value[..max]);

    /// <summary>The email's workspace, for the notification context.</summary>
    private sealed class FixedTenantProvider : ITenantProvider
    {
        public FixedTenantProvider(Guid tenantId) => TenantId = tenantId;
        public Guid TenantId { get; }
        public bool HasTenant => TenantId != Guid.Empty;
    }
}
