// =====================================================================
// InboundEmailProcessor.cs
// Location: MerkaiTrial.Application/Services/Inbound/InboundEmailProcessor.cs
//
// NEW FILE (083). Finishes ONE email the webhook filed:
//
//   1. download the body and headers from Resend
//   2. decide what kind of email it is — a customer writing to us, a rep's
//      own email they BCC'd, or a customer email a rep forwarded
//   3. decide which record it belongs to, strongest evidence first:
//        reply token   — it answered one of OUR emails (a quote)
//        thread        — it answered an email we already filed
//        address       — the customer's address is a contact or a lead
//   4. keep its attachments, and copy them onto the lead / deal
//   5. tell the record's owner
//
// RUN BY InboundEmailWorker with a FlowDbContext SCOPED TO THE EMAIL'S
// WORKSPACE (see the worker). That is not decoration: the notification
// dispatcher reads each person's preferences and the workspace's locked
// defaults through the normal tenant filter, and in a background service
// with no workspace in context it would find none and quietly ignore
// them. Every query here ALSO names the TenantId explicitly.
//
// WHAT IS NEVER DONE AUTOMATICALLY
//   • Filing by address when the sender's domain FAILED its checks
//     (DMARC). Anyone can type a From line; a spoofed "email from your
//     customer" must not appear on their deal. It waits in the Inbox,
//     labelled, for a person to decide.
//   • Notifying about an auto-reply (out of office, bounce) or about a
//     rep's own BCC'd email. Both are filed; neither is news.
//
// RETRIES. A failure Resend calls temporary (timeout, 429, 5xx) is tried
// again: 1, 5, 15, 60, 180 minutes. After that — or straight away for a
// permanent failure — the email is filed on what the webhook already told
// us (sender, subject) and marked Failed, so a reply is never lost just
// because its body could not be downloaded.
// =====================================================================

using System.Text.Json;
using System.Text.RegularExpressions;
using MerkaiTrial.Application.Services.Notifications;
using MerkaiTrial.Application.Services.Storage;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Services.Inbound;

public sealed class InboundEmailProcessor
{
    public const int MaxAttempts = 6;

    private const int MaxHtmlChars = 1_000_000;
    private const int MaxTextChars = 200_000;
    private const int MaxFiles = 20;
    private const long MaxTotalBytes = 25L * 1024 * 1024;
    private const long MaxFileBytes = 10L * 1024 * 1024;

    private readonly FlowDbContext _db;
    private readonly IResendInboundClient _resend;
    private readonly IFileStorageService _storage;
    private readonly INotificationDispatcher _dispatcher;
    private readonly EmailOptions _options;
    private readonly ILogger _logger;

    public InboundEmailProcessor(
        FlowDbContext db,
        IResendInboundClient resend,
        IFileStorageService storage,
        INotificationDispatcher dispatcher,
        EmailOptions options,
        ILogger logger)
    {
        _db = db;
        _resend = resend;
        _storage = storage;
        _dispatcher = dispatcher;
        _options = options;
        _logger = logger;
    }

    public async Task ProcessAsync(Guid emailId, Guid tenantId, CancellationToken ct)
    {
        var email = await _db.InboundEmails
            .Include(e => e.Attachments)
            .FirstOrDefaultAsync(e => e.Id == emailId && e.TenantId == tenantId, ct);

        if (email is null || email.IsDeleted || email.ProcessingState == InboundProcessingState.Done)
            return;

        email.ProcessingAttempts++;

        // ── 1. Content ────────────────────────────────────────────────
        ResendReceivedEmail? content = null;
        var contentFailed = false;

        var fetched = await _resend.GetEmailAsync(email.ProviderEmailId, ct);

        if (fetched.Ok)
        {
            content = fetched.Value!;
            ApplyContent(email, content);
            email.ProcessingError = null;
        }
        else if (fetched.Retryable && email.ProcessingAttempts < MaxAttempts)
        {
            email.ProcessingState = InboundProcessingState.Retry;
            email.NextAttemptAtUtc = DateTime.UtcNow.Add(Backoff(email.ProcessingAttempts));
            email.LockedUntilUtc = null;
            email.ProcessingError = InboundAddressing.Truncate(fetched.Error, 1000);
            await _db.SaveChangesAsync(CancellationToken.None);

            _logger.LogWarning("Inbound email {Id}: content not fetched (attempt {Attempt}), retrying at {Next}: {Error}",
                email.Id, email.ProcessingAttempts, email.NextAttemptAtUtc, fetched.Error);
            return;
        }
        else
        {
            contentFailed = true;
            email.ProcessingError = InboundAddressing.Truncate(
                $"The message text could not be downloaded from Resend: {fetched.Error}", 1000);
        }

        // ── 2 + 3. What it is, and where it goes ──────────────────────
        var newlyMatched = false;
        if (email.MatchMethod == InboundMatchMethod.None && email.Status != InboundEmailStatus.Dismissed)
            newlyMatched = await ClassifyAndMatchAsync(email, content, ct);

        // ── 4. Attachments ────────────────────────────────────────────
        if (!contentFailed && email.Attachments.Count == 0 && (content?.Attachments?.Count ?? 0) > 0)
            await SaveAttachmentsAsync(email, ct);

        if (email.HasLinks && email.Attachments.Count > 0)
            await InboundEmailFiling.CopyAttachmentsToRecordsAsync(
                _db, _storage, email, email.Attachments.ToList(), _logger, ct);

        // ── 5. Tell the owner ─────────────────────────────────────────
        if (newlyMatched && email.Direction != InboundEmailDirection.SentByUser && !email.IsAutoReply)
            await NotifyAsync(email, ct);

        email.ProcessingState = contentFailed ? InboundProcessingState.Failed : InboundProcessingState.Done;
        email.ProcessedAtUtc = DateTime.UtcNow;
        email.LockedUntilUtc = null;
        email.NextAttemptAtUtc = null;

        await _db.SaveChangesAsync(CancellationToken.None);

        _logger.LogInformation(
            "Inbound email {Id} processed: {Direction}, {Status} by {Method}, {Files} file(s)",
            email.Id, email.Direction, email.Status, email.MatchMethod, email.Attachments.Count);
    }

    // =================================================================
    // CONTENT
    // =================================================================

    private static void ApplyContent(InboundEmail email, ResendReceivedEmail c)
    {
        // The display name lives only in the raw From header.
        var fromHeader = InboundAddressing.ParseMailbox(c.Header("from"));
        if (fromHeader?.DisplayName is { } name) email.FromName = InboundAddressing.Truncate(name, 200);

        if (c.To is { Count: > 0 }) email.ToAddresses = InboundAddressing.Truncate(string.Join(", ", c.To), 2000);
        if (c.Cc is { Count: > 0 }) email.CcAddresses = InboundAddressing.Truncate(string.Join(", ", c.Cc), 2000);
        if (!string.IsNullOrWhiteSpace(c.Subject)) email.Subject = InboundAddressing.Truncate(c.Subject, 500)!;

        email.MessageId = InboundAddressing.Truncate(c.MessageId ?? c.Header("message-id"), 500) ?? email.MessageId;

        // In-Reply-To plus References, so a reply deep in a thread still
        // finds the email that started it.
        var thread = string.Join(" ", new[] { c.Header("in-reply-to"), c.Header("references") }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        email.InReplyTo = InboundAddressing.Truncate(thread, 500);

        email.BodyHtml = Cap(c.Html, MaxHtmlChars);

        var text = !string.IsNullOrWhiteSpace(c.Text) ? c.Text : InboundAddressing.HtmlToText(c.Html);
        email.BodyText = Cap(text, MaxTextChars);
        email.Snippet = InboundAddressing.Snippet(email.BodyText);

        email.IsAutoReply = LooksAutomatic(c, email.FromAddress, email.Subject);

        var auth = c.Authentication;
        if (auth is not null)
        {
            string Low(string? s) => (s ?? "none").Trim().ToLowerInvariant();
            var spf = Low(auth.Spf); var dkim = Low(auth.Dkim); var dmarc = Low(auth.Dmarc);

            email.AuthSummary = InboundAddressing.Truncate($"spf={spf} dkim={dkim} dmarc={dmarc}", 100);
            email.SenderVerified =
                dmarc == "fail" || (spf == "fail" && dkim == "fail") ? false
              : dmarc == "pass" || dkim == "pass" ? true
              : null;
        }
    }

    private static bool LooksAutomatic(ResendReceivedEmail c, string from, string subject)
    {
        var autoSubmitted = c.Header("auto-submitted");
        if (!string.IsNullOrWhiteSpace(autoSubmitted) &&
            !autoSubmitted.Trim().Equals("no", StringComparison.OrdinalIgnoreCase))
            return true;

        if (c.Header("x-autoreply") is not null || c.Header("x-autorespond") is not null) return true;

        var precedence = c.Header("precedence")?.Trim().ToLowerInvariant();
        if (precedence is "bulk" or "junk" or "auto_reply" or "list") return true;

        if (InboundAddressing.IsAutomatedSender(from)) return true;

        return Regex.IsMatch(subject ?? string.Empty,
            @"^\s*(automatic reply|auto(matic)?[- ]?reply|out of (the )?office|undeliver(able|ed)|delivery status notification|mail delivery failed)",
            RegexOptions.IgnoreCase);
    }

    // =================================================================
    // CLASSIFY + MATCH
    // =================================================================

    /// <summary>True when the email ended up filed on at least one record.</summary>
    private async Task<bool> ClassifyAndMatchAsync(InboundEmail email, ResendReceivedEmail? content, CancellationToken ct)
    {
        var domain = _options.NormalisedInboundDomain;
        var tenantId = email.TenantId;

        // Every address the email went to, from the webhook AND the fetch.
        var recipients = InboundAddressing.Addresses(
            MetadataList(email.MetadataJson, "received_for"),
            MetadataList(email.MetadataJson, "to"),
            MetadataList(email.MetadataJson, "cc"),
            content?.To, content?.Cc, content?.ReceivedFor,
            Split(email.ToAddresses), Split(email.CcAddresses));

        // Our people: a sender who is one of them means BCC or forward.
        var users = await _db.Users.AsNoTracking()
            .Where(u => u.TenantId == tenantId && u.IsActive && !u.IsDeleted && u.Email != null)
            .Select(u => new { u.Id, u.Email })
            .ToListAsync(ct);

        var userEmails = new HashSet<string>(
            users.Select(u => u.Email!.Trim().ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);

        bool IsOurs(string address) =>
            userEmails.Contains(address)
            || (domain is not null && address.EndsWith("@" + domain, StringComparison.OrdinalIgnoreCase));

        var sender = users.FirstOrDefault(u =>
            string.Equals(u.Email!.Trim(), email.FromAddress, StringComparison.OrdinalIgnoreCase));

        // ── a. Reply token — the strongest evidence, so it goes first ─────────────────────────────────────────────────
        var tokens = recipients
            .Select(r => InboundAddressing.TokenFromLocalPart(InboundAddressing.LocalPartOnDomain(r, domain)))
            .Where(t => t is not null).Select(t => t!).Distinct().ToList();

        if (tokens.Count > 0)
        {
            var token = await _db.EmailReplyTokens.AsNoTracking()
                .Where(t => t.TenantId == tenantId && tokens.Contains(t.Token))
                .OrderByDescending(t => t.CreatedAtUtc)
                .FirstOrDefaultAsync(ct);

            if (token is not null && await InboundEmailFiling.ApplyTargetAsync(_db, email, token.EntityType, token.EntityId, ct))
            {
                // A reply to one of OUR emails is the customer writing back,
                // whoever's address it comes from — even a colleague testing
                // with their own mailbox. Never treated as a BCC.
                email.Direction = InboundEmailDirection.Received;
                email.CounterpartAddress = InboundAddressing.Truncate(email.FromAddress, 320);
                email.ForwardedByUserId = null;
                email.MatchMethod = InboundMatchMethod.ReplyToken;
                if (email.SenderVerified == false)
                    email.MatchNote = "Filed from the reply address, but the sender's domain failed its checks — read it with care.";
                return true;
            }
        }

        // ── Direction ─────────────────────────────────────────────────
        List<string> candidates;

        if (sender is null)
        {
            email.Direction = InboundEmailDirection.Received;
            email.CounterpartAddress = email.FromAddress;
            candidates = new() { email.FromAddress };
        }
        else
        {
            email.ForwardedByUserId = sender.Id;

            var exclude = new HashSet<string>(userEmails, StringComparer.OrdinalIgnoreCase);
            foreach (var r in recipients.Where(IsOurs)) exclude.Add(r);
            exclude.Add(email.FromAddress);

            var original = InboundAddressing.LooksForwarded(email.Subject)
                ? InboundAddressing.ForwardedSender(email.BodyText, exclude)
                : null;

            if (original is not null)
            {
                email.Direction = InboundEmailDirection.Forwarded;
                email.CounterpartAddress = InboundAddressing.Truncate(original.Value.Address, 320);
                candidates = new() { original.Value.Address };
            }
            else if (InboundAddressing.LooksForwarded(email.Subject))
            {
                // Forwarded, but nothing in it we can read as the sender.
                email.Direction = InboundEmailDirection.Forwarded;
                email.MatchNote = "Forwarded, but the original sender could not be read from it.";
                candidates = new();
            }
            else
            {
                email.Direction = InboundEmailDirection.SentByUser;
                candidates = recipients.Where(r => !IsOurs(r)).ToList();
                email.CounterpartAddress = InboundAddressing.Truncate(candidates.FirstOrDefault(), 320);
            }
        }

        // ── b. Thread ─────────────────────────────────────────────────
        var threadIds = MessageIds(email.InReplyTo);
        if (threadIds.Count > 0)
        {
            var parent = await _db.InboundEmails.AsNoTracking()
                .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Id != email.Id
                         && e.MessageId != null && threadIds.Contains(e.MessageId)
                         && (e.LeadId != null || e.DealId != null || e.ContactId != null))
                .OrderByDescending(e => e.ReceivedAtUtc)
                .Select(e => new { e.LeadId, e.DealId, e.ContactId, e.CompanyId })
                .FirstOrDefaultAsync(ct);

            if (parent is not null)
            {
                email.LeadId = parent.LeadId;
                email.DealId = parent.DealId;
                email.ContactId = parent.ContactId;
                email.CompanyId = parent.CompanyId;
                email.Status = InboundEmailStatus.Matched;
                email.MatchMethod = InboundMatchMethod.Thread;
                return true;
            }
        }

        // ── c. Address ────────────────────────────────────────────────
        if (email.SenderVerified == false && email.Direction == InboundEmailDirection.Received)
        {
            email.MatchNote = "The sender's domain failed its authenticity checks, so this was not filed automatically. Check it is genuine before linking it.";
            return false;
        }

        foreach (var address in candidates.Where(a => !string.IsNullOrWhiteSpace(a)).Take(10))
        {
            if (await MatchByAddressAsync(email, address, ct))
            {
                email.MatchMethod = InboundMatchMethod.Address;
                if (email.Direction == InboundEmailDirection.SentByUser)
                    email.CounterpartAddress = InboundAddressing.Truncate(address, 320);
                return true;
            }
        }

        if (string.IsNullOrEmpty(email.MatchNote) && candidates.Count > 0)
            email.MatchNote = "No contact or lead has this address yet.";

        email.Status = InboundEmailStatus.Unmatched;
        return false;
    }

    private async Task<bool> MatchByAddressAsync(InboundEmail email, string address, CancellationToken ct)
    {
        var tenantId = email.TenantId;
        var lower = address.Trim().ToLowerInvariant();

        var contacts = await _db.Contacts.AsNoTracking()
            .Where(c => c.TenantId == tenantId && !c.IsDeleted && c.Email != null && c.Email.ToLower() == lower)
            .OrderByDescending(c => c.IsPrimary)
            .ThenByDescending(c => c.UpdatedAtUtc ?? c.CreatedAtUtc)
            .Select(c => new { c.Id, c.CompanyId })
            .Take(5)
            .ToListAsync(ct);

        var leads = await _db.Leads.AsNoTracking()
            .Where(l => l.TenantId == tenantId && !l.IsDeleted && l.Email != null && l.Email.ToLower() == lower)
            .OrderBy(l => l.IsConverted)
            .ThenByDescending(l => l.UpdatedAtUtc ?? l.CreatedAtUtc)
            .Select(l => new { l.Id })
            .Take(5)
            .ToListAsync(ct);

        if (contacts.Count == 0 && leads.Count == 0) return false;

        if (leads.Count > 0)
        {
            // Lead first: it fills in the converted contact and deal too.
            await InboundEmailFiling.ApplyTargetAsync(_db, email, InboundLinkTypes.Lead, leads[0].Id, ct);
        }

        if (contacts.Count > 0 && email.ContactId is null)
        {
            email.ContactId = contacts[0].Id;
            email.CompanyId ??= contacts[0].CompanyId;
            email.Status = InboundEmailStatus.Matched;
        }

        if (contacts.Count > 1 || leads.Count > 1)
        {
            var parts = new List<string>();
            if (contacts.Count > 1) parts.Add($"{contacts.Count} contacts");
            if (leads.Count > 1) parts.Add($"{leads.Count} leads");
            email.MatchNote = $"{string.Join(" and ", parts)} share this address — filed on the most recent. Move it if that is wrong.";
        }

        return email.HasLinks;
    }

    // =================================================================
    // ATTACHMENTS
    // =================================================================

    private async Task SaveAttachmentsAsync(InboundEmail email, CancellationToken ct)
    {
        var listed = await _resend.ListAttachmentsAsync(email.ProviderEmailId, ct);
        if (!listed.Ok)
        {
            email.AttachmentNote = InboundAddressing.Truncate(
                $"The attachments could not be downloaded: {listed.Error}", 1000);
            return;
        }

        var skipped = new List<string>();
        long total = 0;

        foreach (var file in listed.Value!)
        {
            var name = string.IsNullOrWhiteSpace(file.FileName) ? "attachment" : file.FileName.Trim();

            // Inline pictures (signature logos, pasted screenshots) are
            // already inside the HTML (html_format=data_uri). Keeping them
            // as files too would bury the real attachments in icons.
            if (string.Equals(file.ContentDisposition, "inline", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(file.ContentId))
                continue;

            if (email.Attachments.Count >= MaxFiles)
            {
                skipped.Add($"{name} — more than {MaxFiles} files");
                continue;
            }

            var size = file.Size ?? 0;
            var refusal = _storage.WhyNotAllowed(file.ContentType, size > 0 ? size : 1, name);
            if (refusal is not null)
            {
                skipped.Add($"{name} — {refusal}");
                continue;
            }

            if (total + size > MaxTotalBytes)
            {
                skipped.Add($"{name} — over the 25 MB limit for one email");
                continue;
            }

            if (string.IsNullOrWhiteSpace(file.DownloadUrl))
            {
                skipped.Add($"{name} — no download link");
                continue;
            }

            var bytes = await _resend.DownloadAsync(file.DownloadUrl, MaxFileBytes, ct);
            if (!bytes.Ok)
            {
                skipped.Add($"{name} — could not be downloaded");
                continue;
            }

            try
            {
                var saved = await _storage.SaveBytesAsync(
                    email.TenantId, InboundEmailEntityType.InboundEmail, name, file.ContentType, bytes.Value!, ct);

                total += saved.FileSize;

                email.Attachments.Add(new InboundEmailAttachment
                {
                    Id             = Guid.NewGuid(),
                    TenantId       = email.TenantId,
                    InboundEmailId = email.Id,
                    FileName       = InboundAddressing.Truncate(saved.FileName, 255) ?? "attachment",
                    FileUrl        = saved.FileUrl,
                    FileSize       = saved.FileSize,
                    MimeType       = InboundAddressing.Truncate(saved.MimeType, 200),
                    CreatedAtUtc   = DateTime.UtcNow
                });
            }
            catch (InvalidOperationException ex)
            {
                skipped.Add($"{name} — {ex.Message}");
            }
        }

        email.AttachmentCount = email.Attachments.Count;
        email.AttachmentNote = skipped.Count == 0
            ? null
            : InboundAddressing.Truncate("Not kept: " + string.Join("; ", skipped), 1000);
    }

    // =================================================================
    // NOTIFY
    // =================================================================

    private async Task NotifyAsync(InboundEmail email, CancellationToken ct)
    {
        var owners = await InboundEmailFiling.OwnersAsync(_db, email, ct);
        if (owners.Count == 0) return;

        string? actorName = null;
        if (email.ForwardedByUserId.HasValue)
        {
            actorName = await _db.Users.AsNoTracking()
                .Where(u => u.Id == email.ForwardedByUserId && u.TenantId == email.TenantId)
                .Select(u => (u.FirstName + " " + u.LastName).Trim())
                .FirstOrDefaultAsync(ct);
        }

        var who = email.Direction == InboundEmailDirection.Forwarded
            ? (email.CounterpartAddress ?? "a customer")
            : (email.FromName ?? email.FromAddress);

        var title = email.Direction switch
        {
            InboundEmailDirection.Forwarded                     => $"Email from {who} (forwarded)",
            _ when email.MatchMethod == InboundMatchMethod.ReplyToken => $"Reply from {who}",
            _                                                   => $"Email from {who}"
        };

        var body = string.IsNullOrWhiteSpace(email.Snippet)
            ? email.Subject
            : $"{email.Subject} — {email.Snippet}";

        await _dispatcher.AddForUsersAsync(new NotificationRequest(
            TenantId:    email.TenantId,
            EventType:   NotificationEventType.CustomerEmailReceived,
            Title:       title,
            Body:        body,
            EntityType:  InboundEmailEntityType.InboundEmail,
            EntityId:    email.Id,
            ActorUserId: email.ForwardedByUserId,
            ActorName:   string.IsNullOrWhiteSpace(actorName) ? "the customer" : actorName), owners, ct);
    }

    // =================================================================
    // helpers
    // =================================================================

    private static TimeSpan Backoff(int attempt) => attempt switch
    {
        1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        4 => TimeSpan.FromHours(1),
        _ => TimeSpan.FromHours(3)
    };

    private static string? Cap(string? value, int max)
        => string.IsNullOrEmpty(value) ? null : (value.Length <= max ? value : value[..max]);

    private static IEnumerable<string> Split(string? joined)
        => string.IsNullOrWhiteSpace(joined)
            ? Enumerable.Empty<string>()
            : joined.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static List<string> MessageIds(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? new()
            : Regex.Matches(value, @"<[^<>\s]+>").Select(m => m.Value).Distinct().Take(20).ToList();

    private static IEnumerable<string> MetadataList(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) return Enumerable.Empty<string>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(name, out var v)) return Enumerable.Empty<string>();

            return v.ValueKind switch
            {
                JsonValueKind.Array  => v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                                         .Select(x => x.GetString()!).ToList(),
                JsonValueKind.String => new[] { v.GetString()! },
                _                    => Enumerable.Empty<string>()
            };
        }
        catch (JsonException)
        {
            return Enumerable.Empty<string>();
        }
    }
}
