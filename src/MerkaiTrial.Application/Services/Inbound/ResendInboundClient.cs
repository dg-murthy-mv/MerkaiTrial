// =====================================================================
// ResendInboundClient.cs
// Location: MerkaiTrial.Application/Services/Inbound/ResendInboundClient.cs
//
// NEW FILE (083). Reads a RECEIVED email back from Resend.
//
// The webhook only says "an email arrived" — sender, recipients, subject
// and attachment names. The body, the headers (display name, In-Reply-To,
// auto-reply markers) and the files themselves are fetched here, with the
// SAME API key the sender already uses (Email__ApiKey). Nothing new to
// configure for this part.
//
//   GET /emails/receiving/{id}?html_format=data_uri
//       the email. data_uri puts inline images (signatures, pasted
//       screenshots) INTO the HTML, so the viewer can show them without
//       loading anything from the internet.
//   GET /emails/receiving/{id}/attachments
//       the files, each with a download_url that works for an hour.
//   GET {download_url}
//       the bytes. NOT sent the API key — it is a pre-signed link to
//       Resend's storage, and the key must never travel anywhere else.
//
// THE RESULT TYPE carries Retryable for the same reason EmailSendResult
// does: "Resend is down" and "that email does not exist" need opposite
// handling, and the worker reads only this flag to decide.
// =====================================================================

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MerkaiTrial.Application.Services.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MerkaiTrial.Application.Services.Inbound;

// ── The shapes Resend returns (snake_case) ──────────────────────────

public sealed class ResendReceivedEmail
{
    [JsonPropertyName("id")]          public string? Id { get; set; }
    [JsonPropertyName("from")]        public string? From { get; set; }
    [JsonPropertyName("to")]          public List<string>? To { get; set; }
    [JsonPropertyName("cc")]          public List<string>? Cc { get; set; }
    [JsonPropertyName("bcc")]         public List<string>? Bcc { get; set; }
    [JsonPropertyName("reply_to")]    public List<string>? ReplyTo { get; set; }
    [JsonPropertyName("received_for")] public List<string>? ReceivedFor { get; set; }
    [JsonPropertyName("subject")]     public string? Subject { get; set; }
    [JsonPropertyName("html")]        public string? Html { get; set; }
    [JsonPropertyName("text")]        public string? Text { get; set; }
    [JsonPropertyName("message_id")]  public string? MessageId { get; set; }
    [JsonPropertyName("created_at")]  public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>Header name (lower-case) → a string, or occasionally an array.</summary>
    [JsonPropertyName("headers")]     public Dictionary<string, JsonElement>? Headers { get; set; }

    [JsonPropertyName("authentication")] public ResendAuthentication? Authentication { get; set; }
    [JsonPropertyName("attachments")] public List<ResendAttachmentInfo>? Attachments { get; set; }

    /// <summary>A header's value as one string, whatever shape Resend sent it in.</summary>
    public string? Header(string name)
    {
        if (Headers is null) return null;

        var match = Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase));
        if (match.Key is null) return null;

        return match.Value.ValueKind switch
        {
            JsonValueKind.String => match.Value.GetString(),
            JsonValueKind.Array  => string.Join(" ", match.Value.EnumerateArray()
                                        .Where(e => e.ValueKind == JsonValueKind.String)
                                        .Select(e => e.GetString())),
            _                    => match.Value.ToString()
        };
    }
}

public sealed class ResendAuthentication
{
    [JsonPropertyName("spf")]   public string? Spf { get; set; }
    [JsonPropertyName("dkim")]  public string? Dkim { get; set; }
    [JsonPropertyName("dmarc")] public string? Dmarc { get; set; }
}

public sealed class ResendAttachmentInfo
{
    [JsonPropertyName("id")]                  public string? Id { get; set; }
    [JsonPropertyName("filename")]            public string? FileName { get; set; }
    [JsonPropertyName("content_type")]        public string? ContentType { get; set; }
    [JsonPropertyName("content_disposition")] public string? ContentDisposition { get; set; }
    [JsonPropertyName("content_id")]          public string? ContentId { get; set; }
    [JsonPropertyName("size")]                public long? Size { get; set; }
    [JsonPropertyName("download_url")]        public string? DownloadUrl { get; set; }
    [JsonPropertyName("expires_at")]          public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed record ResendCallResult<T>(bool Ok, T? Value, string? Error, bool Retryable)
{
    public static ResendCallResult<T> Success(T value) => new(true, value, null, false);
    public static ResendCallResult<T> Transient(string error) => new(false, default, error, true);
    public static ResendCallResult<T> Permanent(string error) => new(false, default, error, false);
}

// ── The contract ────────────────────────────────────────────────────

public interface IResendInboundClient
{
    bool IsEnabled { get; }

    Task<ResendCallResult<ResendReceivedEmail>> GetEmailAsync(string emailId, CancellationToken ct = default);

    Task<ResendCallResult<List<ResendAttachmentInfo>>> ListAttachmentsAsync(string emailId, CancellationToken ct = default);

    /// <summary>Downloads a pre-signed attachment link, refusing anything larger than maxBytes.</summary>
    Task<ResendCallResult<byte[]>> DownloadAsync(string downloadUrl, long maxBytes, CancellationToken ct = default);
}

// ── Resend ──────────────────────────────────────────────────────────

public sealed class ResendInboundClient : IResendInboundClient
{
    private readonly HttpClient _http;
    private readonly EmailOptions _options;
    private readonly ILogger<ResendInboundClient> _logger;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public ResendInboundClient(HttpClient http, IOptions<EmailOptions> options, ILogger<ResendInboundClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled => _options.IsConfigured;

    public Task<ResendCallResult<ResendReceivedEmail>> GetEmailAsync(string emailId, CancellationToken ct = default)
        => GetJsonAsync<ResendReceivedEmail>(
            $"emails/receiving/{Uri.EscapeDataString(emailId)}?html_format=data_uri", ct);

    public async Task<ResendCallResult<List<ResendAttachmentInfo>>> ListAttachmentsAsync(
        string emailId, CancellationToken ct = default)
    {
        var page = await GetJsonAsync<AttachmentList>(
            $"emails/receiving/{Uri.EscapeDataString(emailId)}/attachments", ct);

        return page.Ok
            ? ResendCallResult<List<ResendAttachmentInfo>>.Success(page.Value?.Data ?? new())
            : new ResendCallResult<List<ResendAttachmentInfo>>(false, null, page.Error, page.Retryable);
    }

    public async Task<ResendCallResult<byte[]>> DownloadAsync(
        string downloadUrl, long maxBytes, CancellationToken ct = default)
    {
        // A pre-signed https link from Resend, and nothing else. An http://
        // or relative value would mean the response was not what we think.
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return ResendCallResult<byte[]>.Permanent("The attachment link was not a secure web address.");

        try
        {
            // No Authorization header: see the file header.
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
                return Classify<byte[]>(response.StatusCode, "download");

            if (response.Content.Headers.ContentLength is long declared && declared > maxBytes)
                return ResendCallResult<byte[]>.Permanent("The file is larger than the limit.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;

            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                    return ResendCallResult<byte[]>.Permanent("The file is larger than the limit.");
                buffer.Write(chunk, 0, read);
            }

            return ResendCallResult<byte[]>.Success(buffer.ToArray());
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ResendCallResult<byte[]>.Transient("Timed out downloading the attachment.");
        }
        catch (HttpRequestException ex)
        {
            return ResendCallResult<byte[]>.Transient($"Network error downloading the attachment: {Short(ex.Message)}");
        }
    }

    private async Task<ResendCallResult<T>> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        if (!IsEnabled)
            return ResendCallResult<T>.Transient("Email is not configured (Email__ApiKey is not set).");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            using var response = await _http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Resend receiving call failed: HTTP {Status} {Body}",
                    (int)response.StatusCode, Short(body));
                return Classify<T>(response.StatusCode, "read");
            }

            var value = await response.Content.ReadFromJsonAsync<T>(Json, ct);
            return value is null
                ? ResendCallResult<T>.Permanent("Resend returned an empty answer.")
                : ResendCallResult<T>.Success(value);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ResendCallResult<T>.Transient($"Timed out after {_options.TimeoutSeconds}s waiting for Resend.");
        }
        catch (HttpRequestException ex)
        {
            return ResendCallResult<T>.Transient($"Network error: {Short(ex.Message)}");
        }
        catch (JsonException ex)
        {
            return ResendCallResult<T>.Permanent($"Resend's answer could not be read: {Short(ex.Message)}");
        }
    }

    /// <summary>429 and 5xx are worth another try; any other 4xx is not.</summary>
    private static ResendCallResult<T> Classify<T>(HttpStatusCode status, string what)
    {
        var code = (int)status;
        var message = $"Resend {what} failed (HTTP {code}).";

        return status == HttpStatusCode.TooManyRequests || code >= 500
            ? ResendCallResult<T>.Transient(message)
            : ResendCallResult<T>.Permanent(message);
    }

    private static string Short(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= 300 ? s : s[..300]);

    private sealed class AttachmentList
    {
        [JsonPropertyName("data")] public List<ResendAttachmentInfo>? Data { get; set; }
    }
}

// ── No key ──────────────────────────────────────────────────────────

/// <summary>
/// Registered when Email__ApiKey is not set. Every call is a RETRYABLE
/// failure, so emails the webhook already filed wait — and are fetched
/// the moment a key is configured — instead of being marked failed.
/// </summary>
public sealed class NullResendInboundClient : IResendInboundClient
{
    private const string Reason = "Email is not configured on this environment (Email__ApiKey is not set).";

    public bool IsEnabled => false;

    public Task<ResendCallResult<ResendReceivedEmail>> GetEmailAsync(string emailId, CancellationToken ct = default)
        => Task.FromResult(ResendCallResult<ResendReceivedEmail>.Transient(Reason));

    public Task<ResendCallResult<List<ResendAttachmentInfo>>> ListAttachmentsAsync(string emailId, CancellationToken ct = default)
        => Task.FromResult(ResendCallResult<List<ResendAttachmentInfo>>.Transient(Reason));

    public Task<ResendCallResult<byte[]>> DownloadAsync(string downloadUrl, long maxBytes, CancellationToken ct = default)
        => Task.FromResult(ResendCallResult<byte[]>.Transient(Reason));
}
