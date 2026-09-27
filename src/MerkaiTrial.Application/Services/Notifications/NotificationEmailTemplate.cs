// =====================================================================
// NotificationEmailTemplate.cs
// Location: MerkaiTrial.Application/Services/Notifications/NotificationEmailTemplate.cs
//
// NEW FILE (038).
//
// Turns a NotificationRequest into a subject, an HTML body and a plain
// text body. Rendered at QUEUE time and stored on the OutboundMessage —
// see that entity's header for why.
//
// EVERY INTERPOLATED VALUE IS HTML-ENCODED
//   A notification title carries deal titles, quote numbers and people's
//   names. "Bolt & Nut Co" becomes "Bolt &amp; Nut Co" or the email is
//   malformed, and a name containing a < is worse than malformed. There is
//   exactly one place in this file where raw HTML is emitted and it is a
//   constant.
//
// WHY THE HTML IS PLAIN AND INLINE
//   Email clients are not browsers. Outlook renders with Word's engine;
//   Gmail strips <style> blocks; dark mode inverts things unpredictably.
//   A single-column table with inline styles and web-safe fonts is what
//   survives, and it is what every transactional email you have ever
//   received actually is underneath.
//
// A PLAIN TEXT PART IS NOT OPTIONAL
//   An email with no text alternative scores worse with spam filters and
//   is unreadable in a text-only client. It costs six lines.
// =====================================================================

using System.Net;
using System.Text;
using MerkaiTrial.Domain.Entities;

namespace MerkaiTrial.Application.Services.Notifications;

public sealed record RenderedEmail(string Subject, string Html, string Text);

public static class NotificationEmailTemplate
{
    /// <summary>
    /// Builds the email for one notification.
    /// </summary>
    /// <param name="appBaseUrl">
    /// From EmailOptions. When blank, the email simply carries no button —
    /// a link to "/Quotes/Detail/..." with no host is worse than no link.
    /// </param>
    public static RenderedEmail Render(
        NotificationRequest request,
        string tenantName,
        string? appBaseUrl)
    {
        var subject = Subject(request, tenantName);
        var url = BuildUrl(appBaseUrl, request.EntityType, request.EntityId);

        return new RenderedEmail(
            subject,
            Html(request, tenantName, url),
            Text(request, tenantName, url));
    }

    // ── Subject ───────────────────────────────────────────────────────

    private static string Subject(NotificationRequest request, string tenantName)
    {
        // The workspace name in brackets, because someone who works with two
        // Merkai workspaces needs to know which one this is about before
        // they open it. Not the event name — the title already says that.
        var prefix = string.IsNullOrWhiteSpace(tenantName) ? "" : $"[{tenantName}] ";
        return Trim(prefix + request.Title, 300);
    }

    // ── HTML ──────────────────────────────────────────────────────────

    private static string Html(NotificationRequest request, string tenantName, string? url)
    {
        // Encoded here, once, so nothing below can forget.
        var title = Enc(request.Title);
        var body = Enc(request.Body);
        var actor = Enc(request.ActorName);
        var workspace = Enc(tenantName);
        var cta = CallToAction(request.EntityType);

        var sb = new StringBuilder();

        sb.Append(
@"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""></head>
<body style=""margin:0;padding:0;background:#f1f5f9;"">
<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#f1f5f9;padding:24px 12px;"">
<tr><td align=""center"">
<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""max-width:560px;background:#ffffff;border-radius:10px;border:1px solid #e2e8f0;"">
<tr><td style=""padding:24px 28px 8px 28px;font-family:Arial,Helvetica,sans-serif;"">");

        sb.Append(
            $@"<p style=""margin:0 0 4px;font-size:12px;color:#94a3b8;letter-spacing:.04em;text-transform:uppercase;"">{workspace}</p>");

        sb.Append(
            $@"<h1 style=""margin:0 0 12px;font-size:18px;line-height:1.35;color:#0f172a;font-weight:600;"">{title}</h1>");

        if (!string.IsNullOrWhiteSpace(body))
        {
            sb.Append(
                $@"<p style=""margin:0 0 14px;font-size:14px;line-height:1.5;color:#475569;"">{body}</p>");
        }

        if (!string.IsNullOrWhiteSpace(actor))
        {
            sb.Append(
                $@"<p style=""margin:0 0 18px;font-size:13px;color:#64748b;"">by {actor}</p>");
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            // A table-wrapped anchor, because Outlook ignores padding on an
            // <a> and the button collapses to bare text.
            sb.Append(
                $@"<table role=""presentation"" cellpadding=""0"" cellspacing=""0"" style=""margin:6px 0 10px;"">
<tr><td style=""background:#4f46e5;border-radius:6px;"">
<a href=""{Enc(url)}"" style=""display:inline-block;padding:10px 20px;font-family:Arial,Helvetica,sans-serif;font-size:14px;color:#ffffff;text-decoration:none;font-weight:600;"">{cta}</a>
</td></tr></table>");
        }

        sb.Append(
@"</td></tr>
<tr><td style=""padding:8px 28px 22px 28px;font-family:Arial,Helvetica,sans-serif;border-top:1px solid #f1f5f9;"">
<p style=""margin:14px 0 0;font-size:12px;line-height:1.5;color:#94a3b8;"">
You are receiving this because of your notification settings in Merkai CRM.
Change them under Settings &rarr; Notifications.
</p>
</td></tr>
</table>
</td></tr></table>
</body></html>");

        return sb.ToString();
    }

    // ── Plain text ────────────────────────────────────────────────────

    private static string Text(NotificationRequest request, string tenantName, string? url)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(tenantName))
            sb.AppendLine($"[{tenantName}]").AppendLine();

        sb.AppendLine(request.Title);

        if (!string.IsNullOrWhiteSpace(request.Body))
            sb.AppendLine().AppendLine(request.Body);

        if (!string.IsNullOrWhiteSpace(request.ActorName))
            sb.AppendLine().AppendLine($"by {request.ActorName}");

        if (!string.IsNullOrWhiteSpace(url))
            sb.AppendLine().AppendLine(url);

        sb.AppendLine()
          .AppendLine("--")
          .AppendLine("You are receiving this because of your notification settings in Merkai CRM.")
          .AppendLine("Change them under Settings > Notifications.");

        return sb.ToString();
    }

    // ── helpers ───────────────────────────────────────────────────────

    private static string CallToAction(string? entityType) => entityType switch
    {
        "Quote" => "Open the quote",
        "Deal"  => "Open the deal",
        "Lead"  => "Open the lead",
        _       => "Open Merkai"
    };

    /// <summary>
    /// The same route map as NotificationDto.Url, with a host in front.
    /// Returns null when there is no base URL configured — a relative link
    /// in an email goes nowhere, so no button is better than a broken one.
    /// </summary>
    private static string? BuildUrl(string? appBaseUrl, string? entityType, Guid? entityId)
    {
        if (string.IsNullOrWhiteSpace(appBaseUrl)) return null;

        var path = (entityType, entityId) switch
        {
            ("Quote", { } id) => $"/Quotes/Detail/{id}",
            ("Deal",  { } id) => $"/Pipeline/Detail/{id}",
            ("Lead",  { } id) => $"/Leads/Detail/{id}",
            _                 => "/"
        };

        return appBaseUrl.TrimEnd('/') + path;
    }

    private static string Enc(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : WebUtility.HtmlEncode(value);

    private static string Trim(string value, int max)
        => value.Length <= max ? value : value[..max];
}
