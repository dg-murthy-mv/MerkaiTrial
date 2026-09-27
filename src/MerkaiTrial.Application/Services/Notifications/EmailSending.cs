// =====================================================================
// EmailSending.cs
// Location: MerkaiTrial.Application/Services/Notifications/EmailSending.cs
//
// NEW FILE (038). EmailOptions, IEmailSender and the Resend implementation.
//
// WHY AN INTERFACE FOR ONE PROVIDER
//   Not speculative generality. Two concrete uses today:
//     • NullEmailSender below, so Development does not need an API key and
//       nobody accidentally emails a real customer from a laptop.
//     • Swapping Resend for SES or a per-tenant SMTP later is one class.
//
// THE FROM ADDRESS IS ALWAYS YOURS
//   Resend will only accept a From on a domain you have verified, which is
//   madeevision.com. Sending as sathorn.co.th would be rejected outright,
//   and even if it were not, it would land in spam until every tenant
//   published SPF and DKIM records for your sending domain.
//
//   So: From is Merkai's, Reply-To is the TENANT's. A customer hitting
//   reply reaches the tenant; the envelope stays deliverable. When a
//   customer eventually wants their own domain in the From line, that is a
//   per-tenant sending identity with its own setup screen — a feature, not
//   a config flag.
//
// IDEMPOTENCY
//   Every send carries an Idempotency-Key header set to the
//   OutboundMessage's Id. If we time out waiting for Resend and the worker
//   retries, Resend recognises the key and does NOT send a second copy.
//   Without it, one network blip means a customer gets the same quote
//   email twice.
//
// RATE LIMIT
//   Resend's default is 2 requests per second. The worker paces itself;
//   this class also treats HTTP 429 as retryable rather than fatal, so a
//   burst backs off instead of dying.
// =====================================================================

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MerkaiTrial.Application.Services.Notifications;

// ── Configuration ─────────────────────────────────────────────────────

/// <summary>
/// Bound from configuration section "Email".
///
/// THE API KEY DOES NOT GO IN appsettings.json. Same rule as Jwt__SigningKey
/// in Program.cs: App Service settings or Key Vault, as Email__ApiKey.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Resend API key. Empty disables sending — see IsConfigured.</summary>
    public string ApiKey { get; set; } = String.Empty;

    /// <summary>Must be on a domain verified in Resend.</summary>
    public string FromAddress { get; set; } = "noreply@madeevision.com";

    /// <summary>The display name customers see.</summary>
    public string FromName { get; set; } = "Merkai CRM";

    /// <summary>
    /// When set, EVERY email goes here instead of its real recipient, with
    /// the intended address preserved in the subject. This is what makes it
    /// safe to run the worker against a copy of production data.
    /// </summary>
    public string? RedirectAllTo { get; set; }

    /// <summary>Base URL for links in emails, e.g. https://app.merkai.com</summary>
    public string AppBaseUrl { get; set; } = string.Empty;

    public string ApiBaseUrl { get; set; } = "https://api.resend.com";

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// No key means no sending. The worker then leaves messages Pending
    /// rather than marking them failed, so turning the key on later sends
    /// the backlog instead of losing it.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

// ── The contract ──────────────────────────────────────────────────────

public sealed record EmailMessage(
    Guid IdempotencyKey,
    string ToAddress,
    string? ToName,
    string Subject,
    string BodyHtml,
    string BodyText,
    string? ReplyToAddress);

/// <summary>
/// Outcome of one attempt. Retryable is the important field: it separates
/// "the network hiccuped" from "that address does not exist", and the
/// worker treats those completely differently.
/// </summary>
public sealed record EmailSendResult(
    bool Success,
    string? ProviderMessageId,
    string? Error,
    bool Retryable)
{
    public static EmailSendResult Ok(string? id) => new(true, id, null, false);

    /// <summary>Try again later: timeout, 429, 5xx.</summary>
    public static EmailSendResult TransientFailure(string error) => new(false, null, error, true);

    /// <summary>Do not try again: bad address, rejected content, bad key.</summary>
    public static EmailSendResult PermanentFailure(string error) => new(false, null, error, false);
}

public interface IEmailSender
{
    bool IsEnabled { get; }
    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default);
}

// ── Resend ────────────────────────────────────────────────────────────

public sealed class ResendEmailSender : IEmailSender
{
    private readonly HttpClient _http;
    private readonly EmailOptions _options;
    private readonly ILogger<ResendEmailSender> _logger;

    public ResendEmailSender(
        HttpClient http,
        IOptions<EmailOptions> options,
        ILogger<ResendEmailSender> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled => _options.IsConfigured;

    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return EmailSendResult.TransientFailure("Email is not configured (Email__ApiKey is not set).");

        // Safety valve for running against a copy of production data.
        var to = string.IsNullOrWhiteSpace(_options.RedirectAllTo)
            ? message.ToAddress
            : _options.RedirectAllTo!;

        var subject = string.IsNullOrWhiteSpace(_options.RedirectAllTo)
            ? message.Subject
            : $"[to: {message.ToAddress}] {message.Subject}";

        var payload = new ResendRequest
        {
            From = string.IsNullOrWhiteSpace(_options.FromName)
                ? _options.FromAddress
                : $"{_options.FromName} <{_options.FromAddress}>",
            To = new[] { to },
            Subject = subject,
            Html = message.BodyHtml,
            Text = message.BodyText,
            ReplyTo = string.IsNullOrWhiteSpace(message.ReplyToAddress)
                ? null
                : new[] { message.ReplyToAddress! }
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/emails")
            {
                Content = JsonContent.Create(payload)
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            // The one header that makes a retry safe. See the file header.
            request.Headers.TryAddWithoutValidation(
                "Idempotency-Key", message.IdempotencyKey.ToString("D"));

            using var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                string? id = null;
                try
                {
                    id = System.Text.Json.JsonDocument.Parse(body)
                        .RootElement.TryGetProperty("id", out var idProp)
                            ? idProp.GetString()
                            : null;
                }
                catch
                {
                    // Accepted but the body was not what we expected. The
                    // message HAS been sent — losing the provider id is a
                    // support inconvenience, not a reason to send it again.
                }

                return EmailSendResult.Ok(id);
            }

            var error = $"HTTP {(int)response.StatusCode}: {Truncate(body, 1000)}";

            // The distinction that matters. 429 and 5xx are worth retrying;
            // 4xx means the request itself is wrong and retrying it for six
            // hours just delays someone noticing.
            var retryable = response.StatusCode == HttpStatusCode.TooManyRequests
                         || (int)response.StatusCode >= 500;

            if (retryable)
            {
                _logger.LogWarning(
                    "Resend rejected message {Id} temporarily: {Error}",
                    message.IdempotencyKey, error);
                return EmailSendResult.TransientFailure(error);
            }

            _logger.LogError(
                "Resend refused message {Id}: {Error}", message.IdempotencyKey, error);
            return EmailSendResult.PermanentFailure(error);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own timeout, NOT a shutdown. Retryable — and the
            // idempotency key is what makes that safe, because the message
            // may well have been accepted before we gave up waiting.
            return EmailSendResult.TransientFailure(
                $"Timed out after {_options.TimeoutSeconds}s waiting for Resend.");
        }
        catch (HttpRequestException ex)
        {
            return EmailSendResult.TransientFailure($"Network error: {Truncate(ex.Message, 500)}");
        }
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s[..max];
    }

    // snake_case, as Resend's API expects.
    private sealed class ResendRequest
    {
        [JsonPropertyName("from")]     public string From { get; set; } = string.Empty;
        [JsonPropertyName("to")]       public string[] To { get; set; } = Array.Empty<string>();
        [JsonPropertyName("subject")]  public string Subject { get; set; } = string.Empty;
        [JsonPropertyName("html")]     public string? Html { get; set; }
        [JsonPropertyName("text")]     public string? Text { get; set; }

        [JsonPropertyName("reply_to")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? ReplyTo { get; set; }
    }
}

// ── Development / no-key fallback ─────────────────────────────────────

/// <summary>
/// Logs instead of sending. Registered when no API key is configured, so a
/// developer running locally never emails a real customer by accident and
/// never needs a key to work on anything else.
///
/// It reports failure, not success, and a RETRYABLE one — so the message
/// stays queued. Point a real key at the same database later and the
/// backlog goes out rather than having been silently swallowed.
/// </summary>
public sealed class NullEmailSender : IEmailSender
{
    private readonly ILogger<NullEmailSender> _logger;

    public NullEmailSender(ILogger<NullEmailSender> logger) => _logger = logger;

    public bool IsEnabled => false;

    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "EMAIL NOT SENT (no provider configured) — to {To}, subject \"{Subject}\"",
            message.ToAddress, message.Subject);

        return Task.FromResult(EmailSendResult.TransientFailure(
            "Email is not configured on this environment — the message is still queued."));
    }
}
