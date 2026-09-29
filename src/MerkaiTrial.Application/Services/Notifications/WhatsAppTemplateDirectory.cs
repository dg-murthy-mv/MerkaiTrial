// =====================================================================
// WhatsAppTemplateDirectory.cs
// Location: MerkaiTrial.Application/Services/Notifications/WhatsAppTemplateDirectory.cs
//
// NEW FILE (046). Asks Meta what it actually thinks of our templates.
//
// WHY
//   Until now the template screen showed what YOU ticked. Meta's opinion
//   — approved, still in review, rejected, paused for quality — lived in
//   a browser tab, and the first sign of a problem was a queued message
//   dying hours later with a numeric code.
//
//   GET https://graph.facebook.com/v21.0/{WABA_ID}/message_templates
//   Authorization: Bearer {AccessToken}
//
//   {
//     "data": [
//       { "name": "lead_assigned_v1", "language": "en",
//         "status": "APPROVED", "category": "UTILITY", "id": "1234" },
//       { "name": "quote_sent_v1", "language": "en",
//         "status": "REJECTED", "category": "MARKETING",
//         "rejected_reason": "INVALID_FORMAT" }
//     ],
//     "paging": { "next": "..." }
//   }
//
// THIS IS READ-ONLY AND ADVISORY. It never changes a template and never
// blocks a send: Meta's answer at send time is the only authoritative
// one, and a directory that happens to be unreachable must not stop a
// notification going out. When this call fails the screen says it could
// not reach Meta and shows your own settings, which is exactly what it
// showed before this file existed.
// =====================================================================

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MerkaiTrial.Application.Services.Notifications;

/// <summary>One template as Meta holds it.</summary>
public sealed record MetaTemplate(
    string Name,
    string Language,
    string Status,
    string? Category,
    string? RejectedReason)
{
    public bool IsApproved => string.Equals(Status, "APPROVED", StringComparison.OrdinalIgnoreCase);

    /// <summary>A colour for the badge, in Bootstrap's vocabulary.</summary>
    public string StatusClass => Status?.ToUpperInvariant() switch
    {
        "APPROVED" => "bg-success-subtle text-success-emphasis",
        "PENDING" or "IN_APPEAL" or "PENDING_DELETION"
                   => "bg-warning-subtle text-warning-emphasis",
        "REJECTED" or "DISABLED" or "PAUSED"
                   => "bg-danger-subtle text-danger-emphasis",
        _          => "bg-secondary-subtle text-secondary-emphasis"
    };

    /// <summary>What it means, for someone who has not memorised Meta's vocabulary.</summary>
    public string StatusHint => Status?.ToUpperInvariant() switch
    {
        "APPROVED" => "Approved — this template can send.",
        "PENDING"  => "In review. Meta usually answers within a day.",
        "REJECTED" => string.IsNullOrWhiteSpace(RejectedReason)
                        ? "Rejected. Edit the wording in WhatsApp Manager and resubmit."
                        : $"Rejected: {RejectedReason}. Edit the wording and resubmit.",
        "PAUSED"   => "Paused by Meta for quality. It will resume, or be disabled if it keeps happening.",
        "DISABLED" => "Disabled by Meta. This one will not send again.",
        _          => Status ?? "Unknown"
    };
}

/// <summary>
/// What the status endpoint answers. Reachable = false means we could not
/// ask Meta, NOT that something is wrong with the templates — the screen
/// says so rather than showing eight scary unknowns.
/// </summary>
public sealed record WhatsAppDirectory(
    bool Reachable,
    string? Error,
    IReadOnlyList<MetaTemplate> Templates)
{
    public static WhatsAppDirectory Unreachable(string error)
        => new(false, error, Array.Empty<MetaTemplate>());

    public MetaTemplate? Find(string? name, string? language)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        return Templates.FirstOrDefault(t =>
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(language)
                || string.Equals(t.Language, language, StringComparison.OrdinalIgnoreCase)));
    }
}

public interface IWhatsAppTemplateDirectory
{
    Task<WhatsAppDirectory> GetAsync(CancellationToken ct = default);
}

public sealed class MetaWhatsAppTemplateDirectory : IWhatsAppTemplateDirectory
{
    private readonly HttpClient _http;
    private readonly WhatsAppOptions _options;
    private readonly ILogger<MetaWhatsAppTemplateDirectory> _logger;

    public MetaWhatsAppTemplateDirectory(
        HttpClient http,
        IOptions<WhatsAppOptions> options,
        ILogger<MetaWhatsAppTemplateDirectory> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<WhatsAppDirectory> GetAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.AccessToken))
            return WhatsAppDirectory.Unreachable("No WhatsApp access token is configured.");

        if (string.IsNullOrWhiteSpace(_options.BusinessAccountId))
            return WhatsAppDirectory.Unreachable(
                "WhatsApp__BusinessAccountId is not set, so Meta cannot be asked about templates. " +
                "It is the \"WhatsApp Business account ID\" beside the Phone number ID on API Setup.");

        // 100 is above the eight we have and below Meta's page size limit,
        // so paging never comes up. If this product ever has more than a
        // hundred templates, follow paging.next — but a hundred templates
        // is a different problem than a missing page.
        var path = $"{_options.ApiVersion.Trim('/')}/{_options.BusinessAccountId}/message_templates" +
                   "?fields=name,language,status,category,rejected_reason&limit=100";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);

            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.AccessToken);

            using var response = await _http.SendAsync(request, ct);

            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var detail = ReadErrorMessage(body) ?? $"Meta returned {(int)response.StatusCode}.";

                _logger.LogWarning("Could not read the template directory: {Detail}", detail);

                return WhatsAppDirectory.Unreachable(detail);
            }

            return new WhatsAppDirectory(true, null, Parse(body));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return WhatsAppDirectory.Unreachable("The request to Meta timed out.");
        }
        catch (HttpRequestException ex)
        {
            return WhatsAppDirectory.Unreachable($"Could not reach Meta: {ex.Message}");
        }
    }

    private static List<MetaTemplate> Parse(string body)
    {
        var list = new List<MetaTemplate>();

        try
        {
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in data.EnumerateArray())
            {
                var name = Read(item, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                list.Add(new MetaTemplate(
                    Name: name!,
                    Language: Read(item, "language") ?? "en",
                    Status: Read(item, "status") ?? "UNKNOWN",
                    Category: Read(item, "category"),

                    // Meta returns "NONE" rather than omitting it when a
                    // template was not rejected. Showing "Rejected: NONE"
                    // would be worse than showing nothing.
                    RejectedReason: Read(item, "rejected_reason") is { } reason
                                    && !string.Equals(reason, "NONE", StringComparison.OrdinalIgnoreCase)
                        ? reason
                        : null));
            }
        }
        catch (JsonException)
        {
            // A body we cannot parse is the same as no directory. The caller
            // shows its own settings and says Meta could not be read.
        }

        return list;
    }

    private static string? Read(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);

            return doc.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message)
                    ? message.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Registered when there is no token. Answers "not reachable" without a
/// network call, so the screen renders the same way it does when Meta is
/// down — one code path for the caller rather than two.
/// </summary>
public sealed class NullWhatsAppTemplateDirectory : IWhatsAppTemplateDirectory
{
    public Task<WhatsAppDirectory> GetAsync(CancellationToken ct = default)
        => Task.FromResult(WhatsAppDirectory.Unreachable(
            "WhatsApp is not configured on this environment."));
}
