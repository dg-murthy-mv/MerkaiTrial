// =====================================================================
// InvoiceCustomerEmail.cs
// Location: MerkaiTrial.Application/Services/Notifications/InvoiceCustomerEmail.cs
//
// NEW FILE (085). The email that carries an invoice to the CUSTOMER:
// the PDF attached, plus a link to view it online (/i/{token}).
//
// SAME RULES AS QuoteCustomerEmail (read its header — this is its twin):
//
//   • QueueAsync ADDS rows and returns. It NEVER saves. The caller's
//     SaveChangesAsync commits the outbox row, the attachment and the
//     reply token together, or none of them.
//   • From is Merkai's verified address; Reply-To is the WORKSPACE's — and,
//     when receiving is on, a tracked reply+<token> address first, so the
//     customer's answer lands on the deal (083).
//   • Every interpolated value is HTML-encoded. The one raw block is a
//     constant.
//
// WHAT IS DIFFERENT FROM A QUOTE EMAIL
//   • The PDF goes with it. An accounts department files the PDF; it does
//     not click links. The bytes are made by the caller (it has the
//     signed-in workspace the renderer needs) and stored on the
//     attachment row, so the worker sends exactly what was made.
//   • The address is chosen by the PERSON sending, not looked up: the
//     invoice dialog pre-fills the deal's contact, and the rep may change
//     it (to accounts@…) and add up to five copies. Validated by the caller.
//   • An optional personal note from the rep, shown above the figures.
//   • The figures are the AMOUNT DUE, not just the total — on a part-paid
//     invoice "Total ₹1,18,000" and "Due ₹68,000" are different sentences.
// =====================================================================

using System.Globalization;
using System.Net;
using System.Text;
using MerkaiTrial.Application.Services.Inbound;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MerkaiTrial.Application.Services.Notifications;

/// <summary>Everything one invoice email needs. Validated by the caller.</summary>
public sealed record InvoiceEmailRequest(
    Invoice Invoice,
    Guid TenantId,
    string ToAddress,
    string? ToName,
    IReadOnlyList<string> CcAddresses,
    string? PersonalMessage,
    string PublicUrl,
    byte[] Pdf,
    string PdfFileName,
    Guid? SentByUserId,
    string? SentByName,
    string? CompanyName);

public interface IInvoiceCustomerEmail
{
    /// <summary>
    /// Queues the invoice email (outbox row + PDF attachment + reply token).
    /// ADDS ROWS; DOES NOT SAVE. Returns the new message's id.
    /// </summary>
    Task<Guid> QueueAsync(InvoiceEmailRequest request, CancellationToken ct = default);
}

public sealed class InvoiceCustomerEmail : IInvoiceCustomerEmail
{
    /// <summary>Per file. An invoice PDF is ~50–200 KB; this only stops an accident.</summary>
    public const int MaxAttachmentBytes = 5 * 1024 * 1024;

    private readonly FlowDbContext _db;
    private readonly EmailOptions _email;
    private readonly ILogger<InvoiceCustomerEmail> _logger;

    public InvoiceCustomerEmail(FlowDbContext db, IOptions<EmailOptions> email, ILogger<InvoiceCustomerEmail> logger)
    {
        _db = db;
        _email = email.Value;
        _logger = logger;
    }

    public async Task<Guid> QueueAsync(InvoiceEmailRequest r, CancellationToken ct = default)
    {
        if (r.TenantId == Guid.Empty)
            throw new InvalidOperationException("Could not work out which workspace this invoice belongs to.");

        if (r.Pdf is null || r.Pdf.Length == 0)
            throw new InvalidOperationException("The invoice PDF could not be made, so nothing was sent.");

        if (r.Pdf.Length > MaxAttachmentBytes)
            throw new InvalidOperationException("The invoice PDF is too large to email (over 5 MB).");

        // Tenants is not tenant-filtered (it IS the tenant).
        var tenant = await _db.Tenants.AsNoTracking()
            .Where(t => t.Id == r.TenantId)
            .Select(t => new { t.Name, t.ReplyToEmail, t.FromEmail, t.Phone })
            .FirstOrDefaultAsync(ct);

        var tenantName = string.IsNullOrWhiteSpace(tenant?.Name) ? "Your supplier" : tenant!.Name;
        var mailbox = !string.IsNullOrWhiteSpace(tenant?.ReplyToEmail) ? tenant!.ReplyToEmail : tenant?.FromEmail;

        // 083 — the tracked reply address first, then the workspace mailbox.
        var messageId = Guid.NewGuid();
        string? tracked = null;

        if (_email.NormalisedInboundDomain is { } inboundDomain)
        {
            var token = InboundAddressing.AddReplyToken(_db, r.TenantId, InboundLinkTypes.Invoice, r.Invoice.Id, messageId);
            tracked = InboundAddressing.ReplyAddress(token, inboundDomain);
        }

        var replyTo = InboundAddressing.CombineReplyTo(tracked, mailbox);

        var rendered = Render(tenantName, tenant?.Phone, replyTo, r);

        var cc = r.CcAddresses.Count == 0 ? null : string.Join(", ", r.CcAddresses);

        var message = new OutboundMessage
        {
            Id              = messageId,
            TenantId        = r.TenantId,
            Channel         = OutboundChannel.Email,
            Status          = OutboundStatus.Pending,
            EventType       = NotificationEventType.InvoiceSent,
            RecipientUserId = null,                    // a customer, not a user

            ToAddress       = Truncate(r.ToAddress, 320)!,
            ToName          = Truncate(r.ToName, 200),
            ReplyToAddress  = replyTo,
            CcAddresses     = Truncate(cc, 1700),
            SentByUserId    = r.SentByUserId,

            Subject         = Truncate(rendered.Subject, 300)!,
            BodyHtml        = rendered.Html,
            BodyText        = rendered.Text,

            EntityType      = "Invoice",
            EntityId        = r.Invoice.Id,

            AttemptCount     = 0,
            NextAttemptAtUtc = null,
            CreatedAtUtc     = DateTime.UtcNow,
            UpdatedBy        = Truncate(r.SentByName, 200)
        };

        message.Attachments.Add(new OutboundMessageAttachment
        {
            OutboundMessageId = messageId,
            TenantId          = r.TenantId,
            FileName          = Truncate(r.PdfFileName, 255) ?? "invoice.pdf",
            ContentType       = "application/pdf",
            SizeBytes         = r.Pdf.Length,
            Content           = r.Pdf,
            CreatedAtUtc      = DateTime.UtcNow
        });

        _db.OutboundMessages.Add(message);

        // NO SaveChangesAsync — see the header.
        _logger.LogInformation(
            "Queued invoice email for {Number} to {Recipient} (+{Cc} cc)",
            r.Invoice.Number, InboundAddressing.Mask(r.ToAddress), r.CcAddresses.Count);

        return messageId;
    }

    // ═════════════════════════════════════════════════════════════════
    // THE TEMPLATE — same shell as the quote email (tables, inline styles)
    // ═════════════════════════════════════════════════════════════════

    private static RenderedEmail Render(string tenantName, string? tenantPhone, string? replyTo, InvoiceEmailRequest r)
    {
        var inv = r.Invoice;
        var culture = CultureFor(inv.Currency);
        string Money(decimal v) => $"{inv.Currency} {v.ToString("N2", culture)}";

        var total = inv.Subtotal - inv.DiscountTotal + inv.TaxTotal;
        var due = inv.Balance;
        var isPaid = due <= 0;
        var greeting = FirstWord(r.ToName);

        var subject = Trim(isPaid
            ? $"Invoice {inv.Number} from {tenantName} (paid)"
            : $"Invoice {inv.Number} from {tenantName}", 300);

        var sb = new StringBuilder();
        sb.Append(
@"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""></head>
<body style=""margin:0;padding:0;background:#f1f5f9;"">
<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#f1f5f9;padding:24px 12px;"">
<tr><td align=""center"">
<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""max-width:560px;background:#ffffff;border-radius:10px;border:1px solid #e2e8f0;"">
<tr><td style=""padding:26px 28px 8px 28px;font-family:Arial,Helvetica,sans-serif;"">");

        sb.Append($@"<p style=""margin:0 0 4px;font-size:12px;color:#94a3b8;letter-spacing:.04em;text-transform:uppercase;"">{Enc(tenantName)}</p>");
        sb.Append($@"<h1 style=""margin:0 0 14px;font-size:19px;line-height:1.35;color:#0f172a;font-weight:600;"">Invoice {Enc(inv.Number)}</h1>");
        sb.Append($@"<p style=""margin:0 0 14px;font-size:14px;line-height:1.55;color:#475569;"">Hello {Enc(greeting)},</p>");

        if (!string.IsNullOrWhiteSpace(r.PersonalMessage))
        {
            // The rep's own words: encoded, then line breaks kept.
            var note = Enc(r.PersonalMessage.Trim()).Replace("\r\n", "\n").Replace("\n", "<br>");
            sb.Append($@"<p style=""margin:0 0 16px;font-size:14px;line-height:1.6;color:#334155;"">{note}</p>");
        }
        else
        {
            var forLine = string.IsNullOrWhiteSpace(r.CompanyName) ? string.Empty : $" for {Enc(r.CompanyName)}";
            sb.Append(isPaid
                ? $@"<p style=""margin:0 0 16px;font-size:14px;line-height:1.55;color:#475569;"">Here is your copy of invoice {Enc(inv.Number)}{forLine}. It has been paid in full — thank you.</p>"
                : $@"<p style=""margin:0 0 16px;font-size:14px;line-height:1.55;color:#475569;"">Please find invoice {Enc(inv.Number)}{forLine} attached as a PDF. You can also view it online using the button below.</p>");
        }

        sb.Append(@"<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""margin:0 0 18px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;"">");
        sb.Append(Row("Invoice", Enc(inv.Number), bold: false));
        sb.Append(Row("Issued", Enc(inv.IssueDateUtc.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)), bold: false));
        if (inv.DueDateUtc.HasValue)
            sb.Append(Row("Due", Enc(inv.DueDateUtc.Value.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)), bold: false));
        sb.Append(Row("Total", Enc(Money(total)), bold: isPaid || due == total));
        if (!isPaid && due != total)
            sb.Append(Row("Amount due", Enc(Money(due)), bold: true));
        if (isPaid)
            sb.Append(Row("Status", "Paid in full", bold: false));
        sb.Append("</table>");

        sb.Append($@"<table role=""presentation"" cellpadding=""0"" cellspacing=""0"" style=""margin:4px 0 14px;"">
<tr><td style=""background:#4f46e5;border-radius:6px;"">
<a href=""{Enc(r.PublicUrl)}"" style=""display:inline-block;padding:12px 24px;font-family:Arial,Helvetica,sans-serif;font-size:15px;color:#ffffff;text-decoration:none;font-weight:600;"">View invoice online</a>
</td></tr></table>");

        sb.Append($@"<p style=""margin:0 0 4px;font-size:12px;line-height:1.5;color:#94a3b8;"">The PDF is attached ({Enc(r.PdfFileName)}). Or open this link in your browser:</p>
<p style=""margin:0 0 4px;font-size:12px;line-height:1.5;color:#4f46e5;word-break:break-all;"">{Enc(r.PublicUrl)}</p>");

        sb.Append(@"</td></tr>
<tr><td style=""padding:10px 28px 24px 28px;font-family:Arial,Helvetica,sans-serif;border-top:1px solid #f1f5f9;"">");
        sb.Append($@"<p style=""margin:14px 0 0;font-size:12px;line-height:1.6;color:#94a3b8;"">Sent by {Enc(tenantName)}");
        if (!string.IsNullOrWhiteSpace(r.SentByName)) sb.Append($" ({Enc(r.SentByName)})");
        if (!string.IsNullOrWhiteSpace(tenantPhone)) sb.Append($" &middot; {Enc(tenantPhone)}");
        sb.Append("<br>");
        sb.Append(!string.IsNullOrWhiteSpace(replyTo)
            ? "Questions about this invoice? Just reply to this email."
            : "If you were not expecting this, you can ignore it.");
        sb.Append(@"</p>
</td></tr>
</table>
</td></tr></table>
</body></html>");

        // ── Plain text ────────────────────────────────────────────────
        var text = new StringBuilder();
        text.AppendLine(tenantName).AppendLine();
        text.AppendLine($"Invoice {inv.Number}").AppendLine();
        text.AppendLine($"Hello {greeting},").AppendLine();
        if (!string.IsNullOrWhiteSpace(r.PersonalMessage))
            text.AppendLine(r.PersonalMessage.Trim()).AppendLine();
        else
            text.AppendLine(isPaid
                ? $"Here is your copy of invoice {inv.Number}. It has been paid in full — thank you."
                : $"Please find invoice {inv.Number} attached as a PDF. You can also view it online at the link below.").AppendLine();

        text.AppendLine($"Issued: {inv.IssueDateUtc.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}");
        if (inv.DueDateUtc.HasValue)
            text.AppendLine($"Due: {inv.DueDateUtc.Value.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}");
        text.AppendLine($"Total: {Money(total)}");
        if (!isPaid && due != total) text.AppendLine($"Amount due: {Money(due)}");
        if (isPaid) text.AppendLine("Status: Paid in full");
        text.AppendLine().AppendLine(r.PublicUrl).AppendLine();
        text.AppendLine($"The PDF is attached ({r.PdfFileName}).").AppendLine();
        text.AppendLine("--");
        text.Append($"Sent by {tenantName}");
        if (!string.IsNullOrWhiteSpace(tenantPhone)) text.Append($" · {tenantPhone}");
        text.AppendLine();
        text.AppendLine(!string.IsNullOrWhiteSpace(replyTo)
            ? "Questions about this invoice? Just reply to this email."
            : "If you were not expecting this, you can ignore it.");

        return new RenderedEmail(subject, sb.ToString(), text.ToString());
    }

    /// <summary>The invoice's own number format — lakh grouping on an INR invoice, as on its PDF.</summary>
    private static CultureInfo CultureFor(string? currency) => (currency ?? string.Empty).ToUpperInvariant() switch
    {
        "INR" => new CultureInfo("en-IN"),
        "PHP" => new CultureInfo("en-PH"),
        "THB" => new CultureInfo("en-US"),
        "AED" => new CultureInfo("en-US"),
        _     => CultureInfo.InvariantCulture
    };

    private static string Row(string label, string valueAlreadyEncoded, bool bold)
    {
        var weight = bold ? "700" : "400";
        var size = bold ? "16px" : "13px";
        var colour = bold ? "#0f172a" : "#475569";
        return $@"<tr>
<td style=""padding:9px 16px;font-family:Arial,Helvetica,sans-serif;font-size:13px;color:#64748b;"">{Enc(label)}</td>
<td align=""right"" style=""padding:9px 16px;font-family:Arial,Helvetica,sans-serif;font-size:{size};font-weight:{weight};color:{colour};"">{valueAlreadyEncoded}</td>
</tr>";
    }

    private static string FirstWord(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "there";
        var trimmed = name.Trim();
        if (trimmed.Contains('@')) return "there";      // an address typed by hand, not a name
        var space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }

    private static string Enc(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : WebUtility.HtmlEncode(value);

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
