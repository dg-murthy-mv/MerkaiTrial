// =====================================================================
// QuoteCustomerEmail.cs
// Location: MerkaiTrial.Application/Services/Notifications/QuoteCustomerEmail.cs
//
// NEW FILE (061). The email that carries a quote to the CUSTOMER.
//
// WHY THIS IS NOT INotificationDispatcher
//
//   The dispatcher does one job well: tell OUR OWN USERS something, after
//   resolving their per-event preferences. Both of its methods are keyed
//   on user ids, and AddForUsersAsync validates every recipient against
//   dbo.Users. A customer is not a user — no row, no Guid, no preferences
//   — so there was no way to reach them, which is exactly why "Sent" has
//   never sent anything.
//
//   Bending the dispatcher to take a bare address would also mean
//   bypassing the preference resolution that is its whole point. A
//   customer must receive the quote they asked for whatever anybody's
//   notification settings say. Different recipient, different rules, its
//   own service.
//
// WHAT IT SHARES WITH THE DISPATCHER: THE ONE RULE
//
//   QueueAsync ADDS a row and returns. It NEVER calls SaveChanges. The
//   caller's own SaveChangesAsync commits it in the caller's transaction,
//   alongside the status change that caused it. That is the outbox
//   pattern OutboundMessage was built for, and it removes both halves of
//   the bug:
//
//     "status says Sent but the customer never got the email"
//         — the send happened outside the transaction and failed
//     "email went out, then the save rolled back"
//         — the send happened before the commit and cannot be un-sent
//
// WHY A SEPARATE TEMPLATE FROM NotificationEmailTemplate
//
//   That template's footer reads "You are receiving this because of your
//   notification settings in Merkai CRM. Change them under Settings →
//   Notifications." A customer has no account, no settings page and no
//   idea what Merkai is. Sending them that is confusing at best and looks
//   like a phishing attempt at worst.
//
//   The shell is deliberately the same — one table, inline styles,
//   web-safe fonts — because that is what survives Outlook's Word
//   renderer and Gmail stripping <style> blocks. Only the words differ.
//
// THE FROM LINE IS STILL OURS
//   Resend only accepts a From on a domain we have verified. The envelope
//   is Merkai's; Reply-To is the TENANT's, so a customer hitting reply
//   reaches the workspace that sent the quote. The tenant's name is in
//   the subject and all over the body, so nothing reads as coming from a
//   company the customer has never heard of.
// =====================================================================

using System.Net;
using System.Text;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MerkaiTrial.Application.Services.Notifications;

public interface IQuoteCustomerEmail
{
    /// <summary>
    /// Queues the quote email to the customer.
    ///
    /// ADDS A ROW; DOES NOT SAVE — the caller commits it.
    ///
    /// Returns NULL when the message was queued, or a short human-readable
    /// reason when it was not. A reason is not an exception: a quote whose
    /// contact has no email address is an ordinary situation that must not
    /// roll back the status change that triggered it. The explicit "email
    /// to customer" endpoint turns the reason into a 400 the rep can read;
    /// the automatic path on Sent just logs it.
    /// </summary>
    Task<string?> QueueAsync(
        Quote quote,
        Guid tenantId,
        string? sentBy,
        CancellationToken ct = default);
}

public sealed class QuoteCustomerEmail : IQuoteCustomerEmail
{
    private readonly FlowDbContext _db;
    private readonly EmailOptions _email;
    private readonly ILogger<QuoteCustomerEmail> _logger;

    public QuoteCustomerEmail(
        FlowDbContext db,
        IOptions<EmailOptions> email,
        ILogger<QuoteCustomerEmail> logger)
    {
        _db = db;
        _email = email.Value;
        _logger = logger;
    }

    public async Task<string?> QueueAsync(
        Quote quote,
        Guid tenantId,
        string? sentBy,
        CancellationToken ct = default)
    {
        if (quote is null) return "No quote.";

        if (tenantId == Guid.Empty)
        {
            // The same trap 036 had to repair in DealStageHistory: a row
            // with an empty TenantId is hidden by the global query filter
            // for ever, so it would queue, send, and be invisible in the
            // log afterwards.
            _logger.LogError(
                "Refusing to queue a quote email with an empty TenantId (quote {QuoteId})", quote.Id);
            return "Could not work out which workspace this quote belongs to.";
        }

        if (string.IsNullOrWhiteSpace(quote.PublicLinkToken))
        {
            // Nothing to link to. Sending a quote email with no way to open
            // the quote is worse than not sending one.
            return "This quote has no customer link yet. Send it to the customer first.";
        }

        // ── Who it goes to ────────────────────────────────────────────
        // IgnoreQueryFilters: this runs inside UpdateQuoteStatusHandler,
        // which itself reads the quote with IgnoreQueryFilters because the
        // customer's public link has no tenant claim. The TenantId
        // predicates below are therefore doing the real work, not
        // belt-and-braces.
        var deal = await _db.Deals
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(d => d.Id == quote.DealId && d.TenantId == tenantId)
            .Include(d => d.Contact)
            .Include(d => d.Company)
            .FirstOrDefaultAsync(ct);

        var contact = deal?.Contact;

        if (contact is null)
            return "This quote's deal has no contact, so there is nobody to email.";

        if (string.IsNullOrWhiteSpace(contact.Email))
        {
            // Contact.Email is nullable and plenty of contacts are phone-only.
            // Named in the message, because "add an email address" is useless
            // if the rep does not know which of three contacts it means.
            return $"{contact.DisplayName} has no email address on file. "
                 + "Add one to the contact, then send the quote again.";
        }

        // ── Who it is from ────────────────────────────────────────────
        // Tenants is not tenant-filtered (it IS the tenant), so this is an
        // ordinary lookup by id.
        var tenant = await _db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.Name, t.ReplyToEmail, t.FromEmail, t.Phone })
            .FirstOrDefaultAsync(ct);

        var tenantName = string.IsNullOrWhiteSpace(tenant?.Name) ? "Your supplier" : tenant!.Name;

        // Reply-To is the TENANT's, never ours. A customer replying to a
        // quote must reach the people who sent it.
        var replyTo = !string.IsNullOrWhiteSpace(tenant?.ReplyToEmail)
            ? tenant!.ReplyToEmail
            : tenant?.FromEmail;

        // ── The link ──────────────────────────────────────────────────
        var url = ResolvePublicUrl(quote);

        if (string.IsNullOrWhiteSpace(url))
        {
            // PaymentLinkUrl was stored relative (UpdateQuoteStatusHandler
            // falls back to "" when no BaseUrl was passed) AND
            // Email:AppBaseUrl is not configured. A relative link in an
            // email goes nowhere.
            _logger.LogError(
                "Quote {QuoteId} has no absolute public URL and Email:AppBaseUrl is not set — " +
                "cannot queue the customer email.", quote.Id);

            return "The customer link has no web address. Set Email__AppBaseUrl on the API.";
        }

        var rendered = Render(
            tenantName: tenantName,
            tenantPhone: tenant?.Phone,
            replyTo: replyTo,
            contactName: contact.DisplayName,
            companyName: deal?.Company?.Name,
            quote: quote,
            url: url);

        _db.OutboundMessages.Add(new OutboundMessage
        {
            Id        = Guid.NewGuid(),           // also the idempotency key
            TenantId  = tenantId,
            Channel   = OutboundChannel.Email,
            Status    = OutboundStatus.Pending,
            EventType = NotificationEventType.QuoteSent,

            // NULL — the whole reason this service exists. The customer is
            // not a user of the system. OutboundMessage.RecipientUserId has
            // always been nullable for exactly this case.
            RecipientUserId = null,

            // Resolved NOW, not at send time: if the contact's address
            // changes between sending and the worker running, this message
            // still goes where it was addressed.
            ToAddress      = Truncate(contact.Email, 320)!,
            ToName         = Truncate(contact.DisplayName, 200),
            ReplyToAddress = Truncate(replyTo, 320),

            Subject  = Truncate(rendered.Subject, 300)!,
            BodyHtml = rendered.Html,
            BodyText = rendered.Text,

            EntityType = "Quote",
            EntityId   = quote.Id,

            AttemptCount     = 0,
            NextAttemptAtUtc = null,              // null = due now
            CreatedAtUtc     = DateTime.UtcNow,
            UpdatedBy        = Truncate(sentBy, 200)
        });

        // NO SaveChangesAsync. See the header.
        _logger.LogInformation(
            "Queued quote email for {QuoteNumber} to {Recipient}",
            quote.Number, MaskAddress(contact.Email));

        return null;
    }

    /// <summary>
    /// The absolute URL the customer opens.
    ///
    /// PaymentLinkUrl holds the public quote URL — the column is named for
    /// payments but UpdateQuoteStatusHandler has stored "/q/{token}" in it
    /// since 017. It is absolute when a BaseUrl was passed (the Detail
    /// page passes Request.Scheme + Host) and RELATIVE when it was not,
    /// which happens on any path that does not go through a browser. The
    /// configured AppBaseUrl is the fallback.
    /// </summary>
    private string? ResolvePublicUrl(Quote quote)
    {
        var stored = quote.PaymentLinkUrl?.Trim();

        if (!string.IsNullOrWhiteSpace(stored) &&
            (stored.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             stored.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return stored;
        }

        var baseUrl = _email.AppBaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;

        return $"{baseUrl.TrimEnd('/')}/q/{quote.PublicLinkToken}";
    }

    // ═════════════════════════════════════════════════════════════════
    // THE TEMPLATE
    //
    // Every interpolated value is HTML-encoded. A company called
    // "Bolt & Nut Co" must not break the markup, and a name containing a
    // "<" is worse than broken. There is exactly one place below that
    // emits raw HTML and it is a constant.
    // ═════════════════════════════════════════════════════════════════

    private static RenderedEmail Render(
        string tenantName,
        string? tenantPhone,
        string? replyTo,
        string contactName,
        string? companyName,
        Quote quote,
        string url)
    {
        var subject = Trim($"Quote {quote.Number} from {tenantName}", 300);

        // ── HTML ──────────────────────────────────────────────────────
        var sb = new StringBuilder();

        sb.Append(
@"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""></head>
<body style=""margin:0;padding:0;background:#f1f5f9;"">
<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#f1f5f9;padding:24px 12px;"">
<tr><td align=""center"">
<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""max-width:560px;background:#ffffff;border-radius:10px;border:1px solid #e2e8f0;"">
<tr><td style=""padding:26px 28px 8px 28px;font-family:Arial,Helvetica,sans-serif;"">");

        // Who it is FROM, first and in the tenant's name. On the public
        // page this is the thing the customer cannot find.
        sb.Append(
            $@"<p style=""margin:0 0 4px;font-size:12px;color:#94a3b8;letter-spacing:.04em;text-transform:uppercase;"">{Enc(tenantName)}</p>");

        sb.Append(
            $@"<h1 style=""margin:0 0 14px;font-size:19px;line-height:1.35;color:#0f172a;font-weight:600;"">Quote {Enc(quote.Number)}</h1>");

        sb.Append(
            $@"<p style=""margin:0 0 14px;font-size:14px;line-height:1.55;color:#475569;"">Hello {Enc(FirstWord(contactName))},</p>");

        var forLine = string.IsNullOrWhiteSpace(companyName)
            ? string.Empty
            : $" for {Enc(companyName)}";

        sb.Append(
            $@"<p style=""margin:0 0 16px;font-size:14px;line-height:1.55;color:#475569;"">{Enc(tenantName)} has sent you a quote{forLine}. You can open it below to see the full breakdown, and accept or decline it there.</p>");

        // The figures, as a small table. Deliberately NOT the full line
        // items: the page has those, and a long email is a worse version
        // of the page. Totals only, so the customer can sanity-check the
        // number before clicking.
        sb.Append(
@"<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""margin:0 0 18px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;"">");

        sb.Append(Row("Total", $"{Enc(quote.Currency)} {quote.GrandTotal:N2}", bold: true));

        if (quote.ExpiresAtUtc != default && quote.ExpiresAtUtc.Year > 1900)
            sb.Append(Row("Valid until", Enc(quote.ExpiresAtUtc.ToString("dd MMM yyyy")), bold: false));

        sb.Append("</table>");

        // Table-wrapped anchor: Outlook ignores padding on an <a> and the
        // button collapses to bare underlined text.
        sb.Append(
            $@"<table role=""presentation"" cellpadding=""0"" cellspacing=""0"" style=""margin:4px 0 14px;"">
<tr><td style=""background:#4f46e5;border-radius:6px;"">
<a href=""{Enc(url)}"" style=""display:inline-block;padding:12px 24px;font-family:Arial,Helvetica,sans-serif;font-size:15px;color:#ffffff;text-decoration:none;font-weight:600;"">View &amp; respond to quote</a>
</td></tr></table>");

        // The raw link as well. Some clients strip buttons, some people do
        // not trust them, and a quote is a document about money.
        sb.Append(
            $@"<p style=""margin:0 0 4px;font-size:12px;line-height:1.5;color:#94a3b8;"">Or paste this into your browser:</p>
<p style=""margin:0 0 4px;font-size:12px;line-height:1.5;color:#4f46e5;word-break:break-all;"">{Enc(url)}</p>");

        sb.Append(
@"</td></tr>
<tr><td style=""padding:10px 28px 24px 28px;font-family:Arial,Helvetica,sans-serif;border-top:1px solid #f1f5f9;"">");

        // The footer names the SENDER and how to reach them. The
        // notification template's "change your settings" footer would be
        // meaningless to someone with no account.
        sb.Append(
            $@"<p style=""margin:14px 0 0;font-size:12px;line-height:1.6;color:#94a3b8;"">Sent by {Enc(tenantName)}");

        if (!string.IsNullOrWhiteSpace(tenantPhone))
            sb.Append($@" &middot; {Enc(tenantPhone)}");

        sb.Append("<br>");

        sb.Append(!string.IsNullOrWhiteSpace(replyTo)
            ? "Questions about this quote? Just reply to this email."
            : "If you were not expecting this, you can ignore it.");

        sb.Append("</p>");

        sb.Append(
@"</td></tr>
</table>
</td></tr></table>
</body></html>");

        // ── Plain text ────────────────────────────────────────────────
        // Not optional: an email with no text part scores worse with spam
        // filters and is unreadable in a text-only client.
        var text = new StringBuilder();
        text.AppendLine(tenantName).AppendLine();
        text.AppendLine($"Quote {quote.Number}").AppendLine();
        text.AppendLine($"Hello {FirstWord(contactName)},").AppendLine();

        text.AppendLine(string.IsNullOrWhiteSpace(companyName)
            ? $"{tenantName} has sent you a quote. Open it to see the full breakdown, and accept or decline it there."
            : $"{tenantName} has sent you a quote for {companyName}. Open it to see the full breakdown, and accept or decline it there.");

        text.AppendLine();
        text.AppendLine($"Total: {quote.Currency} {quote.GrandTotal:N2}");

        if (quote.ExpiresAtUtc != default && quote.ExpiresAtUtc.Year > 1900)
            text.AppendLine($"Valid until: {quote.ExpiresAtUtc:dd MMM yyyy}");

        text.AppendLine().AppendLine(url).AppendLine();
        text.AppendLine("--");
        text.Append($"Sent by {tenantName}");
        if (!string.IsNullOrWhiteSpace(tenantPhone)) text.Append($" · {tenantPhone}");
        text.AppendLine();
        text.AppendLine(!string.IsNullOrWhiteSpace(replyTo)
            ? "Questions about this quote? Just reply to this email."
            : "If you were not expecting this, you can ignore it.");

        return new RenderedEmail(subject, sb.ToString(), text.ToString());
    }

    private static string Row(string label, string valueAlreadyEncoded, bool bold)
    {
        var weight = bold ? "700" : "400";
        var size = bold ? "16px" : "13px";
        var colour = bold ? "#0f172a" : "#475569";

        return
            $@"<tr>
<td style=""padding:10px 16px;font-family:Arial,Helvetica,sans-serif;font-size:13px;color:#64748b;"">{Enc(label)}</td>
<td align=""right"" style=""padding:10px 16px;font-family:Arial,Helvetica,sans-serif;font-size:{size};font-weight:{weight};color:{colour};"">{valueAlreadyEncoded}</td>
</tr>";
    }

    /// <summary>
    /// "Hello Worapong," rather than "Hello Worapong Thongsuk," — a first
    /// name reads as a person writing, a full name reads as a mail merge.
    /// Falls back to the whole string when there is only one word.
    /// </summary>
    private static string FirstWord(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "there";
        var trimmed = name.Trim();
        var space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }

    /// <summary>
    /// "wo****@example.com". The log should say enough to find the row and
    /// not enough to be a contact list — the same rule as the phone
    /// numbers in WhatsApp sending and the quote tokens in 059.
    /// </summary>
    private static string MaskAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "(none)";

        var at = address.IndexOf('@');
        if (at <= 0) return "****";

        var local = address[..at];
        var domain = address[at..];
        var keep = Math.Min(2, local.Length);

        return local[..keep] + "****" + domain;
    }

    private static string Enc(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : WebUtility.HtmlEncode(value);

    private static string Trim(string value, int max)
        => value.Length <= max ? value : value[..max];

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
