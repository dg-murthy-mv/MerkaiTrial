// =====================================================================
// InboundEmailReceiver.cs
// Location: MerkaiTrial.Application/Services/Inbound/InboundEmailReceiver.cs
//
// NEW FILE (083). What the webhook does, and ONLY that:
//
//   1. check Resend's signature                     (else 401)
//   2. read who the email was sent to
//   3. work out which workspace that address belongs to
//   4. write one InboundEmail row, ProcessingState = Pending
//   5. answer
//
// Downloading the body and attachments, matching, notifying — all of it
// is InboundEmailWorker's job, after this has answered. Resend expects a
// quick answer and retries a slow one; a retry is harmless (the unique
// ProviderEmailId turns it into "Duplicate"), but doing the slow work here
// would turn every big attachment into a retry.
//
// NO TENANT IN CONTEXT. The call is anonymous, so the global query filter
// sees Guid.Empty. Every read below is IgnoreQueryFilters() with the
// workspace either being looked up (by alias / token) or already known —
// the permitted pattern described in FlowDbContext item 17.
//
// 085 — DELIVERY EVENTS COME HERE TOO. Resend sends "what happened to
// the email you sent" (delivered, bounced, spam, …) to the same webhook
// with the same signature. After the signature check those are handed to
// IEmailDeliveryEvents; only email.received carries on below. Delivery
// tracking needs only the signing secret — a workspace without a receiving
// domain still gets Delivered / Bounced.
//
// AN EMAIL FOR NOBODY IS NOT AN ERROR. Mail to an address that matches no
// workspace (an old regenerated alias, a typo, spam to random names) is
// answered 200 and dropped with a masked log line. Answering with an
// error would only make Resend retry it.
//
// 105 — EACH SIGNED EVENT IS ACTED ON ONCE (VUL-19). After the signature
// check, the event's id (svix-id) is taken from WebhookReplayGuard. A copy
// of an event already handled — the same signed request posted again
// inside the five minutes its timestamp is accepted — is answered
// "duplicate" and changes nothing. The id is given back when handling did
// not finish (try again later, not configured, an error), because
// Resend's own retry carries the same id and must get through.
// =====================================================================

using System.Text.Json;
using MerkaiTrial.Application.Services.Notifications;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MerkaiTrial.Application.Services.Inbound;

public enum InboundReceiveOutcome
{
    /// <summary>Filed for the worker.</summary>
    Accepted,

    /// <summary>Already filed — Resend retried. Fine.</summary>
    Duplicate,

    /// <summary>Genuine, but not for any workspace, or not an email event. Fine.</summary>
    Ignored,

    /// <summary>The signature did not check out. 401.</summary>
    Unauthorised,

    /// <summary>Receiving is not set up on this server. 503.</summary>
    NotConfigured,

    /// <summary>Signed, but not JSON we can read. 400.</summary>
    Malformed,

    /// <summary>085. Genuine, but arrived before we could match it. 503 — Resend retries.</summary>
    RetryLater
}

public sealed record InboundReceiveResult(InboundReceiveOutcome Outcome, string Detail);

public sealed record InboundWebhookHeaders(string? SvixId, string? SvixTimestamp, string? SvixSignature);

public interface IInboundEmailReceiver
{
    Task<InboundReceiveResult> ReceiveAsync(string rawBody, InboundWebhookHeaders headers, CancellationToken ct = default);
}

public sealed class InboundEmailReceiver : IInboundEmailReceiver
{
    private const int MetadataMaxChars = 20_000;

    private readonly FlowDbContext _db;
    private readonly EmailOptions _options;
    private readonly IEmailDeliveryEvents _delivery;     // 085
    private readonly WebhookReplayGuard _replays;        // 105
    private readonly ILogger<InboundEmailReceiver> _logger;

    public InboundEmailReceiver(
        FlowDbContext db,
        IOptions<EmailOptions> options,
        IEmailDeliveryEvents delivery,
        WebhookReplayGuard replays,
        ILogger<InboundEmailReceiver> logger)
    {
        _db = db;
        _replays = replays;
        _options = options.Value;
        _delivery = delivery;
        _logger = logger;
    }

    public async Task<InboundReceiveResult> ReceiveAsync(
        string rawBody, InboundWebhookHeaders headers, CancellationToken ct = default)
    {
        var domain = _options.NormalisedInboundDomain;

        // 085: the secret is what every event needs; the domain only
        // received mail (checked below, once we know which kind this is).
        if (string.IsNullOrWhiteSpace(_options.InboundWebhookSecret))
        {
            _logger.LogWarning(
                "Resend webhook called but Email:InboundWebhookSecret is not set. The call was refused.");
            return new(InboundReceiveOutcome.NotConfigured, "The webhook is not configured on this server.");
        }

        // ── 1. Signature — before anything in the body is believed ────
        var refusal = ResendWebhookVerifier.Verify(
            _options.InboundWebhookSecret, headers.SvixId, headers.SvixTimestamp, headers.SvixSignature,
            rawBody, DateTimeOffset.UtcNow);

        if (refusal is not null)
        {
            _logger.LogWarning("Inbound webhook refused: {Reason}", refusal);
            return new(InboundReceiveOutcome.Unauthorised, "Signature check failed.");
        }

        // ── 105: once per event ───────────────────────────────────────
        // The verifier has already refused a missing svix-id.
        var eventId = headers.SvixId!.Trim();
        if (!_replays.TryTake(eventId, DateTime.UtcNow))
        {
            _logger.LogWarning("Inbound webhook event {EventId} was already handled — the copy changed nothing", Short(eventId));
            return new(InboundReceiveOutcome.Duplicate, "Already received.");
        }

        InboundReceiveResult result;
        try
        {
            result = await HandleSignedAsync(rawBody, domain, ct);
        }
        catch
        {
            _replays.GiveBack(eventId);
            throw;
        }

        if (result.Outcome is InboundReceiveOutcome.RetryLater or InboundReceiveOutcome.NotConfigured)
            _replays.GiveBack(eventId);

        return result;
    }

    private static string Short(string id) => id.Length <= 40 ? id : id[..40];

    /// <summary>Everything after the signature and the once-only check.</summary>
    private async Task<InboundReceiveResult> HandleSignedAsync(string rawBody, string? domain, CancellationToken ct)
    {
        // ── 2. Read it ────────────────────────────────────────────────
        JsonDocument doc;
        try { doc = JsonDocument.Parse(rawBody); }
        catch (JsonException)
        {
            return new(InboundReceiveOutcome.Malformed, "The body is not JSON.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var type = Str(root, "type");

            // 085 — delivered / bounced / spam / failed for mail we SENT.
            if (_delivery.Handles(type))
                return await _delivery.ApplyAsync(type!, root, ct);

            if (!string.Equals(type, "email.received", StringComparison.Ordinal))
                return new(InboundReceiveOutcome.Ignored, $"Event '{type}' is not handled here.");

            if (domain is null)
            {
                _logger.LogWarning(
                    "A received email arrived but Email:InboundDomain is not set. The call was refused.");
                return new(InboundReceiveOutcome.NotConfigured, "Receiving is not configured on this server.");
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return new(InboundReceiveOutcome.Malformed, "No data in the event.");

            var emailId = Str(data, "email_id");
            if (string.IsNullOrWhiteSpace(emailId) || emailId.Length > 100)
                return new(InboundReceiveOutcome.Malformed, "No email id in the event.");

            var from = InboundAddressing.ParseMailbox(Str(data, "from"));
            if (from is null)
                return new(InboundReceiveOutcome.Ignored, "No readable sender.");

            // A loop guard: our own sending address. Nothing we send should
            // ever come back here, and if it did it must not be re-filed
            // and re-notified for ever.
            if (string.Equals(from.Value.Address, InboundAddressing.BareAddress(_options.FromAddress),
                              StringComparison.OrdinalIgnoreCase))
                return new(InboundReceiveOutcome.Ignored, "From our own sending address.");

            var to          = Strs(data, "to");
            var cc          = Strs(data, "cc");
            var bcc         = Strs(data, "bcc");
            var receivedFor = Strs(data, "received_for");

            // ── 3. Which workspace ────────────────────────────────────
            // received_for first: for a BCC copy it is the ONLY place our
            // address appears — a BCC is, by definition, not in the headers.
            var ours = InboundAddressing.Addresses(receivedFor, to, cc, bcc)
                .Select(a => InboundAddressing.LocalPartOnDomain(a, domain))
                .Where(l => l is not null)
                .Select(l => l!)
                .ToList();

            if (ours.Count == 0)
            {
                _logger.LogInformation(
                    "Inbound email {EmailId} from {From} was not addressed to {Domain} — ignored",
                    emailId, InboundAddressing.Mask(from.Value.Address), domain);
                return new(InboundReceiveOutcome.Ignored, "Not addressed to the receiving domain.");
            }

            var tenantId = await ResolveTenantAsync(ours, ct);
            if (tenantId is null)
            {
                _logger.LogInformation(
                    "Inbound email {EmailId} from {From} matched no workspace address — ignored",
                    emailId, InboundAddressing.Mask(from.Value.Address));
                return new(InboundReceiveOutcome.Ignored, "No workspace uses that address.");
            }

            // ── 4. File it ────────────────────────────────────────────
            var exists = await _db.InboundEmails.IgnoreQueryFilters()
                .AnyAsync(e => e.ProviderEmailId == emailId, ct);
            if (exists)
                return new(InboundReceiveOutcome.Duplicate, "Already received.");

            var receivedAt = DateTime.UtcNow;
            if (data.TryGetProperty("created_at", out var created)
                && created.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(created.GetString(), out var createdAt))
                receivedAt = createdAt.UtcDateTime;

            var metadata = data.GetRawText();

            var row = new InboundEmail
            {
                Id                 = Guid.NewGuid(),
                TenantId           = tenantId.Value,
                ProviderEmailId    = emailId,
                MessageId          = InboundAddressing.Truncate(Str(data, "message_id"), 500),
                FromAddress        = InboundAddressing.Truncate(from.Value.Address, 320)!,
                FromName           = InboundAddressing.Truncate(from.Value.DisplayName, 200),
                ToAddresses        = InboundAddressing.Truncate(string.Join(", ", to), 2000),
                CcAddresses        = InboundAddressing.Truncate(string.Join(", ", cc), 2000),
                Subject            = InboundAddressing.Truncate(Str(data, "subject"), 500) ?? "(no subject)",
                Status             = InboundEmailStatus.Unmatched,
                MatchMethod        = InboundMatchMethod.None,
                ProcessingState    = InboundProcessingState.Pending,
                ReceivedAtUtc      = receivedAt,
                CreatedAtUtc       = DateTime.UtcNow,
                MetadataJson       = metadata.Length <= MetadataMaxChars ? metadata : null
            };

            _db.InboundEmails.Add(row);

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Two deliveries of the same email raced each other; the
                // unique index let exactly one in. That is the design.
                // Anything else is a real failure and goes back to Resend
                // as a 500, so Resend tries again later.
                if (await IsDuplicateAsync(emailId, ct))
                    return new(InboundReceiveOutcome.Duplicate, "Already received.");
                throw;
            }

            _logger.LogInformation(
                "Inbound email {EmailId} from {From} filed for tenant {TenantId} — waiting for the worker",
                emailId, InboundAddressing.Mask(from.Value.Address), tenantId);

            return new(InboundReceiveOutcome.Accepted, "Received.");
        }
    }

    /// <summary>
    /// A reply token wins over an alias — it names the exact record. Both
    /// are looked up with IgnoreQueryFilters; there is no tenant yet, that
    /// is precisely what is being found out.
    /// </summary>
    private async Task<Guid?> ResolveTenantAsync(List<string> localParts, CancellationToken ct)
    {
        var tokens = localParts.Select(InboundAddressing.TokenFromLocalPart)
                               .Where(t => t is not null).Select(t => t!).Distinct().ToList();

        if (tokens.Count > 0)
        {
            var byToken = await _db.EmailReplyTokens.IgnoreQueryFilters().AsNoTracking()
                .Where(t => tokens.Contains(t.Token))
                .Select(t => (Guid?)t.TenantId)
                .FirstOrDefaultAsync(ct);

            if (byToken.HasValue) return byToken;
        }

        var aliases = localParts.Select(InboundAddressing.AliasFromLocalPart)
                                .Where(a => a is not null).Select(a => a!).Distinct().ToList();

        if (aliases.Count == 0) return null;

        // Tenants has no query filter (it IS the tenant). A suspended or
        // deleted workspace receives nothing.
        return await _db.Tenants.AsNoTracking()
            .Where(t => t.InboundAlias != null && aliases.Contains(t.InboundAlias)
                     && !t.IsDeleted && t.IsActive && !t.IsSuspended)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<bool> IsDuplicateAsync(string emailId, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        return await _db.InboundEmails.IgnoreQueryFilters().AnyAsync(e => e.ProviderEmailId == emailId, ct);
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>A string array, or a single string, or nothing — Resend has used all three.</summary>
    private static List<string> Strs(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return new();

        return v.ValueKind switch
        {
            JsonValueKind.Array  => v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                                     .Select(x => x.GetString()!).Where(s => s.Length > 0).ToList(),
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? new() : new() { v.GetString()! },
            _                    => new()
        };
    }
}
