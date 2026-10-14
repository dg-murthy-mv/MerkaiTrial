// =====================================================================
// AccountEmails.cs
// Location: MerkaiTrial.Application/Services/Notifications/AccountEmails.cs
//
// NEW FILE (086). The emails that get a person INTO their account:
//
//   invite            "You've been added to {workspace} — set your password"
//   password reset    "Reset your Merkai password"            (2 hours)
//   password changed  "Your Merkai password was changed"      (no link)
//
// ONE PLACE ISSUES THE LINKS. Before this round four pages each built a
// SetPassword URL by hand (Admin/Users/Create, Settings/Users/Create and
// Index, ForgotPassword — which built it and threw it away). Now every
// one of them asks this service, so the lifetime, the URL shape and the
// "older reset links stop working" rule are written once.
//
// HOW THE EMAIL TRAVELS
//   An OutboundMessage row with EventType = AccountSecurity, drained by the
//   WebApi's OutboundMessageWorker like every other email. Admin.Web never
//   holds the Resend key — it only writes the row. RecipientUserId is the
//   person, so the email log and /Emails/Sent never show the body to
//   anyone (the 038 rule for emails to users), and the worker wipes the
//   body once it has gone (086) because it holds a working link.
//
// THIS SERVICE SAVES. Unlike the quote / invoice / notification queues,
// nothing else rides in the caller's transaction: IUserTokenService saves
// the token itself, and an account email is the whole operation. Saving
// here means a page cannot forget to.
//
// THE RAW TOKEN is returned to the caller (for the "copy link" boxes) and
// written into the email body — and nowhere else. It is never logged.
// =====================================================================

using System.Net;
using System.Text;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Services.Notifications;

/// <summary>How long each kind of link works.</summary>
public static class AccountLinkLifetimes
{
    /// <summary>A new person may not open their email for a day or two.</summary>
    public static readonly TimeSpan Invite = TimeSpan.FromHours(72);

    /// <summary>
    /// Someone who asked for a reset is waiting for it right now. Two hours
    /// is plenty for that and keeps a forgotten link in an old inbox from
    /// staying a key to the account for days.
    /// </summary>
    public static readonly TimeSpan PasswordReset = TimeSpan.FromHours(2);

    public static string Describe(TimeSpan lifetime)
        => lifetime.TotalHours >= 24 && lifetime.TotalHours % 24 == 0
            ? $"{lifetime.TotalHours / 24:0} days"
            : $"{lifetime.TotalHours:0} hours";
}

/// <summary>What happened when a link was issued.</summary>
public sealed record AccountLinkResult(
    string Url,
    bool EmailQueued,
    string? EmailSkippedReason,
    string ToAddress,
    string PersonName,
    TimeSpan Lifetime);

public interface IAccountEmails
{
    /// <summary>
    /// A new password-reset link. Every earlier unused reset link for this
    /// person stops working. Emails it when <paramref name="sendEmail"/> is
    /// true; always returns the link (for the super admin's copy box).
    /// Throws InvalidOperationException for a deactivated or deleted user.
    /// </summary>
    Task<AccountLinkResult> IssuePasswordResetAsync(
        Guid userId, string baseUrl, string issuedBy, bool sendEmail, CancellationToken ct = default);

    /// <summary>
    /// A new invite link. Earlier invites are NOT revoked — someone may have
    /// forwarded the first one to the colleague already.
    /// </summary>
    Task<AccountLinkResult> IssueInviteAsync(
        Guid userId, string baseUrl, string issuedBy, bool sendEmail, CancellationToken ct = default);

    /// <summary>"Your password was changed" — no link to sign in, only a way to report it.</summary>
    Task QueuePasswordChangedAsync(Guid userId, string baseUrl, CancellationToken ct = default);

    /// <summary>Reset links issued for this person within the window — the rate limit.</summary>
    Task<int> CountRecentResetsAsync(Guid userId, TimeSpan window, CancellationToken ct = default);
}

public sealed class AccountEmails : IAccountEmails
{
    private readonly FlowDbContext _db;
    private readonly IUserTokenService _tokens;
    private readonly ILogger<AccountEmails> _logger;

    public AccountEmails(FlowDbContext db, IUserTokenService tokens, ILogger<AccountEmails> logger)
    {
        _db = db;
        _tokens = tokens;
        _logger = logger;
    }

    // =================================================================
    // RESET
    // =================================================================

    public async Task<AccountLinkResult> IssuePasswordResetAsync(
        Guid userId, string baseUrl, string issuedBy, bool sendEmail, CancellationToken ct = default)
    {
        var person = await LoadAsync(userId, ct);

        if (!person.IsActive)
            throw new InvalidOperationException(
                "This person is deactivated, so they can't sign in. Reactivate them first.");

        var root = CleanBase(baseUrl);

        // Older links stop working the moment a new one exists — the one in
        // the newest email is the only one that should open the account.
        await _tokens.RevokeAllAsync(userId, TokenPurpose.PasswordReset, ct);

        var raw = await _tokens.IssueAsync(
            userId, person.TenantId, TokenPurpose.PasswordReset,
            AccountLinkLifetimes.PasswordReset, Cap(issuedBy, 200), ct);

        var url = SetPasswordUrl(root, raw, TokenPurpose.PasswordReset);

        var (queued, skipped) = sendEmail
            ? await QueueAsync(person, ResetEmail(person, url), ct)
            : (false, "Not emailed — link only.");

        _logger.LogInformation(
            "Password reset link issued for user {UserId} by {IssuedBy} (emailed: {Emailed})",
            userId, issuedBy, queued);

        return new AccountLinkResult(url, queued, skipped, person.Email, person.Name, AccountLinkLifetimes.PasswordReset);
    }

    // =================================================================
    // INVITE
    // =================================================================

    public async Task<AccountLinkResult> IssueInviteAsync(
        Guid userId, string baseUrl, string issuedBy, bool sendEmail, CancellationToken ct = default)
    {
        var person = await LoadAsync(userId, ct);

        if (!person.IsActive)
            throw new InvalidOperationException(
                "This person is deactivated, so an invite would not let them in. Reactivate them first.");

        var root = CleanBase(baseUrl);

        var raw = await _tokens.IssueAsync(
            userId, person.TenantId, TokenPurpose.Invite,
            AccountLinkLifetimes.Invite, Cap(issuedBy, 200), ct);

        var url = SetPasswordUrl(root, raw, TokenPurpose.Invite);

        var (queued, skipped) = sendEmail
            ? await QueueAsync(person, InviteEmail(person, url, issuedBy), ct)
            : (false, "Not emailed — link only.");

        _logger.LogInformation(
            "Invite link issued for user {UserId} by {IssuedBy} (emailed: {Emailed})",
            userId, issuedBy, queued);

        return new AccountLinkResult(url, queued, skipped, person.Email, person.Name, AccountLinkLifetimes.Invite);
    }

    // =================================================================
    // PASSWORD CHANGED
    // =================================================================

    public async Task QueuePasswordChangedAsync(Guid userId, string baseUrl, CancellationToken ct = default)
    {
        var person = await LoadAsync(userId, ct);
        var forgotUrl = $"{CleanBase(baseUrl)}/Account/ForgotPassword";

        var (queued, _) = await QueueAsync(person, ChangedEmail(person, forgotUrl), ct);

        _logger.LogInformation("Password-changed email for user {UserId} (queued: {Queued})", userId, queued);
    }

    // =================================================================
    // RATE LIMIT
    // =================================================================

    public async Task<int> CountRecentResetsAsync(Guid userId, TimeSpan window, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow - window;

        // IgnoreQueryFilters — the caller may be anonymous (ForgotPassword).
        return await _db.Set<UserToken>()
            .IgnoreQueryFilters()
            .CountAsync(t => t.UserId == userId
                          && t.Purpose == TokenPurpose.PasswordReset
                          && t.CreatedAtUtc >= since, ct);
    }

    // =================================================================
    // plumbing
    // =================================================================

    private sealed record Person(
        Guid Id, Guid TenantId, string Email, string Name, string FirstName,
        bool IsActive, string WorkspaceName);

    private async Task<Person> LoadAsync(Guid userId, CancellationToken ct)
    {
        // Users is not tenant-filtered (sign-in reads it with no tenant),
        // and this may run anonymously — IgnoreQueryFilters says so plainly.
        var u = await _db.Users.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(x => x.Id == userId && !x.IsDeleted)
            .Select(x => new { x.Id, x.TenantId, x.Email, x.FirstName, x.LastName, x.IsActive })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("That person could not be found.");

        var workspace = await _db.Tenants.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(t => t.Id == u.TenantId)
            .Select(t => t.Name)
            .FirstOrDefaultAsync(ct) ?? "Merkai";

        var name = $"{u.FirstName} {u.LastName}".Trim();

        return new Person(u.Id, u.TenantId, u.Email ?? string.Empty,
            string.IsNullOrWhiteSpace(name) ? (u.Email ?? string.Empty) : name,
            u.FirstName ?? string.Empty, u.IsActive, workspace);
    }

    private async Task<(bool Queued, string? Skipped)> QueueAsync(Person person, RenderedEmail email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(person.Email))
            return (false, "This person has no email address.");

        _db.OutboundMessages.Add(new OutboundMessage
        {
            Id              = Guid.NewGuid(),          // also the idempotency key
            TenantId        = person.TenantId,
            Channel         = OutboundChannel.Email,
            Status          = OutboundStatus.Pending,
            EventType       = NotificationEventType.AccountSecurity,
            RecipientUserId = person.Id,               // keeps the body out of every log screen

            ToAddress       = Cap(person.Email.Trim(), 320)!,
            ToName          = Cap(person.Name, 200),

            Subject         = Cap(email.Subject, 300)!,
            BodyHtml        = email.Html,
            BodyText        = email.Text,

            EntityType      = "User",
            EntityId        = person.Id,

            AttemptCount     = 0,
            NextAttemptAtUtc = null,
            CreatedAtUtc     = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);
        return (true, null);
    }

    private static string CleanBase(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !(baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                 || baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The sign-in link has no web address to point at.");

        return baseUrl.Trim().TrimEnd('/');
    }

    private static string SetPasswordUrl(string root, string raw, string purpose)
        => $"{root}/Account/SetPassword?token={Uri.EscapeDataString(raw)}&purpose={purpose}";

    // =================================================================
    // the three emails — every value encoded, one layout
    // =================================================================

    private static RenderedEmail ResetEmail(Person p, string url)
    {
        var hours = AccountLinkLifetimes.Describe(AccountLinkLifetimes.PasswordReset);

        return Build(
            subject:  "Reset your Merkai password",
            eyebrow:  p.WorkspaceName,
            heading:  "Reset your password",
            greeting: p.FirstName,
            paras: new[]
            {
                "Someone — hopefully you — asked to reset the password for your Merkai account.",
                $"The button below works once and expires in {hours}."
            },
            buttonText: "Choose a new password",
            buttonUrl:  url,
            after: new[]
            {
                "Didn't ask for this? Ignore this email. Your password stays as it is, and the link stops working by itself."
            });
    }

    private static RenderedEmail InviteEmail(Person p, string url, string invitedBy)
    {
        var days = AccountLinkLifetimes.Describe(AccountLinkLifetimes.Invite);
        var who = string.IsNullOrWhiteSpace(invitedBy) || invitedBy.Contains('@') ? "Your administrator" : invitedBy;

        return Build(
            subject:  $"You've been added to {p.WorkspaceName} on Merkai",
            eyebrow:  p.WorkspaceName,
            heading:  $"Welcome to {p.WorkspaceName}",
            greeting: p.FirstName,
            paras: new[]
            {
                $"{who} has added you to the {p.WorkspaceName} workspace on Merkai CRM.",
                $"Choose a password to finish setting up your account. The button works once and expires in {days}.",
                $"You'll sign in with this email address: {p.Email}"
            },
            buttonText: "Set your password",
            buttonUrl:  url,
            after: new[]
            {
                "Weren't expecting this? You can ignore this email — nothing happens until a password is set."
            });
    }

    private static RenderedEmail ChangedEmail(Person p, string forgotUrl)
        => Build(
            subject:  "Your Merkai password was changed",
            eyebrow:  p.WorkspaceName,
            heading:  "Your password was changed",
            greeting: p.FirstName,
            paras: new[]
            {
                $"The password for your Merkai account ({p.Email}) was just changed, and any other devices signed in to it were signed out.",
                "If that was you, there's nothing more to do."
            },
            buttonText: "This wasn't me — reset my password",
            buttonUrl:  forgotUrl,
            after: new[]
            {
                "If you didn't change it, reset your password straight away and tell your workspace administrator."
            });

    private static RenderedEmail Build(
        string subject, string eyebrow, string heading, string? greeting,
        IEnumerable<string> paras, string buttonText, string buttonUrl, IEnumerable<string> after)
    {
        var html = new StringBuilder();
        html.Append(
@"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""></head>
<body style=""margin:0;padding:0;background:#f1f5f9;"">
<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#f1f5f9;padding:24px 12px;"">
<tr><td align=""center"">
<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""max-width:560px;background:#ffffff;border-radius:10px;border:1px solid #e2e8f0;"">
<tr><td style=""padding:24px 28px 8px 28px;font-family:Arial,Helvetica,sans-serif;"">");

        html.Append($@"<p style=""margin:0 0 4px;font-size:12px;color:#94a3b8;letter-spacing:.04em;text-transform:uppercase;"">{Enc(eyebrow)}</p>");
        html.Append($@"<h1 style=""margin:0 0 14px;font-size:19px;line-height:1.35;color:#0f172a;font-weight:600;"">{Enc(heading)}</h1>");

        if (!string.IsNullOrWhiteSpace(greeting))
            html.Append($@"<p style=""margin:0 0 12px;font-size:14px;line-height:1.55;color:#334155;"">Hi {Enc(greeting)},</p>");

        foreach (var p in paras)
            html.Append($@"<p style=""margin:0 0 12px;font-size:14px;line-height:1.55;color:#334155;"">{Enc(p)}</p>");

        // Table-wrapped anchor: Outlook ignores padding on a bare <a>.
        html.Append($@"<table role=""presentation"" cellpadding=""0"" cellspacing=""0"" style=""margin:10px 0 16px;"">
<tr><td style=""background:#4f46e5;border-radius:6px;"">
<a href=""{Enc(buttonUrl)}"" style=""display:inline-block;padding:12px 22px;font-family:Arial,Helvetica,sans-serif;font-size:14px;color:#ffffff;text-decoration:none;font-weight:600;"">{Enc(buttonText)}</a>
</td></tr></table>");

        html.Append($@"<p style=""margin:0 0 14px;font-size:12px;line-height:1.5;color:#64748b;word-break:break-all;"">If the button doesn't work, copy this address into your browser:<br>{Enc(buttonUrl)}</p>");

        foreach (var a in after)
            html.Append($@"<p style=""margin:0 0 12px;font-size:13px;line-height:1.5;color:#64748b;"">{Enc(a)}</p>");

        html.Append(
@"</td></tr>
<tr><td style=""padding:8px 28px 22px 28px;font-family:Arial,Helvetica,sans-serif;border-top:1px solid #f1f5f9;"">
<p style=""margin:14px 0 0;font-size:12px;line-height:1.5;color:#94a3b8;"">
This is an account email from Merkai CRM about signing in. It is sent whatever your notification settings are.
</p>
</td></tr>
</table>
</td></tr></table>
</body></html>");

        var text = new StringBuilder();
        text.AppendLine(heading).AppendLine();
        if (!string.IsNullOrWhiteSpace(greeting)) text.AppendLine($"Hi {greeting},").AppendLine();
        foreach (var p in paras) text.AppendLine(p).AppendLine();
        text.AppendLine($"{buttonText}:").AppendLine(buttonUrl).AppendLine();
        foreach (var a in after) text.AppendLine(a).AppendLine();
        text.AppendLine("--").AppendLine("An account email from Merkai CRM about signing in.");

        return new RenderedEmail(subject, html.ToString(), text.ToString());
    }

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string? Cap(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var t = value.Trim();
        return t.Length <= max ? t : t[..max];
    }
}
