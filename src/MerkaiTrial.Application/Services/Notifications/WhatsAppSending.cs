// =====================================================================
// WhatsAppSending.cs
// Location: MerkaiTrial.Application/Services/Notifications/WhatsAppSending.cs
//
// NEW FILE (045). The WhatsApp half of what EmailSending.cs does for
// Resend, deliberately the same shape: a message record, a result that
// separates retryable from permanent, an interface, a real sender and a
// null one.
//
// ── WHAT CLOUD API ACCEPTS ───────────────────────────────────────────
//
// A business cannot send free text to someone who has not messaged it in
// the last 24 hours, which is every message we send. So every message is
// a TEMPLATE: a name Meta approved, a language, and the variables that
// fill its {{headline}} {{detail}} slots.
//
//   POST https://graph.facebook.com/v21.0/{PhoneNumberId}/messages
//   Authorization: Bearer {AccessToken}
//
//   {
//     "messaging_product": "whatsapp",
//     "to": "919876543210",
//     "type": "template",
//     "template": {
//       "name": "lead_assigned_v1",
//       "language": { "code": "en" },
//       "components": [{
//         "type": "body",
//         "parameters": [
//           { "type": "text", "parameter_name": "headline",
//             "text": "Priya assigned you a lead" },
//           { "type": "text", "parameter_name": "detail",
//             "text": "Acme Ltd — THB 95,000.00" }
//         ]
//       }]
//     }
//   }
//
// 048: those "parameter_name" fields are new, and not optional. Meta's
// template editor stopped accepting {{1}} / {{2}} placeholders, so the
// approved body now reads
//
//     Merkai update: {{headline}} — {{detail}}. Open your workspace for more.
//
// and a named template must be filled by name. The old positional array
// against a named template fails with 132000 — "the template expects a
// different number of variables" — which is a misleading thing to read
// when the count is right and only the shape is wrong.
//
// ── RETRYABLE OR NOT ─────────────────────────────────────────────────
//
// This is the field the worker acts on, and Meta makes it harder than
// Resend does: a 400 can mean "try later" (rate limited on this number)
// or "never" (the template does not exist). The mapping is by error code
// and is documented at MapError below — a wrong answer here either
// hammers Meta with something that will never work, or throws away a
// message that would have gone on the next attempt.
// =====================================================================

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MerkaiTrial.Application.Services.Notifications;

// ── The contract ──────────────────────────────────────────────────────

/// <summary>
/// One WhatsApp message, already resolved: who, which approved template,
/// and the variables in order.
/// </summary>
public sealed record WhatsAppMessage(
    Guid IdempotencyKey,
    string ToE164,
    string TemplateName,
    string LanguageCode,
    IReadOnlyList<string> Variables);

/// <summary>
/// Outcome of one attempt. Same three-way shape as EmailSendResult, and
/// for the same reason — the worker's retry logic reads Retryable and
/// nothing else.
/// </summary>
public sealed record WhatsAppSendResult(
    bool Success,
    string? ProviderMessageId,
    string? Error,
    bool Retryable)
{
    public static WhatsAppSendResult Ok(string? id) => new(true, id, null, false);
    public static WhatsAppSendResult TransientFailure(string error) => new(false, null, error, true);
    public static WhatsAppSendResult PermanentFailure(string error) => new(false, null, error, false);
}

public interface IWhatsAppSender
{
    bool IsEnabled { get; }
    Task<WhatsAppSendResult> SendAsync(WhatsAppMessage message, CancellationToken ct = default);
}

// ── Meta Cloud API ────────────────────────────────────────────────────

public sealed class CloudApiWhatsAppSender : IWhatsAppSender
{
    private readonly HttpClient _http;
    private readonly WhatsAppOptions _options;
    private readonly ILogger<CloudApiWhatsAppSender> _logger;

    public CloudApiWhatsAppSender(
        HttpClient http,
        IOptions<WhatsAppOptions> options,
        ILogger<CloudApiWhatsAppSender> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled => _options.IsConfigured;

    public async Task<WhatsAppSendResult> SendAsync(
        WhatsAppMessage message, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return WhatsAppSendResult.TransientFailure(
                "WhatsApp is not configured (WhatsApp__AccessToken / WhatsApp__PhoneNumberId).");

        if (string.IsNullOrWhiteSpace(message.ToE164))
            return WhatsAppSendResult.PermanentFailure("No recipient number.");

        if (string.IsNullOrWhiteSpace(message.TemplateName))
            return WhatsAppSendResult.PermanentFailure("No template name on the queued message.");

        // Meta takes the number WITHOUT the leading plus. It tolerates the
        // plus in most cases and not in all, and the failure when it does
        // not is "invalid recipient", which sends you looking at the number
        // itself rather than at the punctuation.
        var to = message.ToE164.TrimStart('+').Trim();

        var payload = new
        {
            messaging_product = "whatsapp",
            to,
            type = "template",
            template = new
            {
                name = message.TemplateName,
                language = new { code = message.LanguageCode },
                // 048: NAMED parameters, not positional.
                //
                // Meta's template editor no longer accepts {{1}} / {{2}} —
                // it requires named placeholders ({{headline}}, {{detail}})
                // and refuses a body that starts or ends with one. A template
                // approved that way must be filled by NAME: sending the old
                // positional array against it fails with 132000, "the
                // template expects a different number of variables", which is
                // a misleading message for what is really a shape mismatch.
                //
                // The names come from WhatsAppVariables.Names, which is the
                // same place the values come from, so the two cannot drift.
                components = message.Variables.Count == 0
                    ? Array.Empty<object>()
                    : new object[]
                    {
                        new
                        {
                            type = "body",
                            parameters = message.Variables
                                .Select((v, i) => new
                                {
                                    type = "text",
                                    parameter_name = WhatsAppVariables.NameAt(i),
                                    text = Clip(v)
                                })
                                .ToArray()
                        }
                    }
            }
        };

        var path = $"{_options.ApiVersion.Trim('/')}/{_options.PhoneNumberId}/messages";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(payload)
            };

            // Per-request rather than on the HttpClient: the token can be
            // rotated in configuration without recycling the pooled client.
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.AccessToken);

            using var response = await _http.SendAsync(request, ct);

            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                var id = ReadMessageId(body);

                _logger.LogInformation(
                    "WhatsApp sent to {To} using {Template} — {MessageId}",
                    Mask(to), message.TemplateName, id ?? "(no id)");

                return WhatsAppSendResult.Ok(id);
            }

            return MapError(response.StatusCode, body, message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // The HttpClient's own timeout, not our shutdown.
            return WhatsAppSendResult.TransientFailure("The request to WhatsApp timed out.");
        }
        catch (HttpRequestException ex)
        {
            return WhatsAppSendResult.TransientFailure($"Could not reach WhatsApp: {ex.Message}");
        }
    }

    /// <summary>
    /// { "messages": [ { "id": "wamid.HBgM..." } ] }
    ///
    /// The id is worth storing: it is what Meta's own logs are keyed on,
    /// and it is the only way to ask them about one specific message.
    /// </summary>
    private static string? ReadMessageId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("messages", out var messages)
                && messages.ValueKind == JsonValueKind.Array
                && messages.GetArrayLength() > 0
                && messages[0].TryGetProperty("id", out var id))
            {
                return id.GetString();
            }
        }
        catch (JsonException) { /* fall through */ }

        return null;
    }

    /// <summary>
    /// Meta's failures, sorted into "try again" and "never".
    ///
    /// The codes that matter, and why each lands where it does:
    ///
    ///   4, 80007, 130429  rate limited — the classic retryable case
    ///   131048            spam rate limit: this number is sending too
    ///                     much. Retryable, but if it persists the answer
    ///                     is fewer messages, not more attempts
    ///   131056            pair rate limit (us ↔ this recipient)
    ///   133016            number temporarily unavailable at Meta's end
    ///
    ///   132000            wrong number of variables for the template
    ///   132001            no such template, or not approved in this
    ///                     language
    ///   132005            template paused by Meta for quality
    ///   132007            template's content was rejected
    ///   131026            not deliverable — the recipient does not have
    ///                     WhatsApp, or the number is wrong
    ///   131047            outside the 24-hour window without a template
    ///
    /// Everything in the second group would fail identically on every
    /// retry, so retrying costs attempts and hides the real problem. They
    /// go Dead immediately, with the reason on the row where the send log
    /// shows it.
    ///
    /// ── 047: 190 AND 102 MOVED TO RETRYABLE ──────────────────────────
    ///
    ///   190, 102          the access token is invalid or expired
    ///
    /// I had these as permanent, and that was wrong.
    ///
    /// Permanent should mean "this MESSAGE will never work". A lapsed
    /// token says nothing about the message — the message is perfectly
    /// valid and will send the moment someone renews the credential. What
    /// actually happened was that a 24-hour test token expired overnight
    /// and every notification raised after that was binned on its first
    /// attempt, silently, with nothing left to send once the token was
    /// replaced.
    ///
    /// Retryable is the honest classification: a human fixes the token,
    /// and the backlog drains by itself. It does not retry forever — the
    /// worker's backoff runs 1m, 5m, 15m, 1h, 6h and then gives up, so a
    /// token nobody renews still ends as Dead, just after several hours
    /// of grace instead of none.
    ///
    /// This costs one thing worth naming: while a token is broken, every
    /// queued message retries on that schedule rather than failing fast.
    /// That is a handful of pointless calls an hour, against losing a
    /// day of notifications. Cheap trade.
    /// </summary>
    private WhatsAppSendResult MapError(HttpStatusCode status, string body, WhatsAppMessage message)
    {
        var (code, detail) = ReadError(body);

        var text = detail ?? $"WhatsApp returned {(int)status}.";

        // Permanent first: a 400 is only permanent for specific codes, and
        // treating all 400s as permanent would throw away rate-limited
        // messages that were about to succeed.
        // 047: 190 and 102 are no longer here — see the note above. A dead
        // token is a configuration fault, not a bad message.
        var permanent = code is 132000 or 132001 or 132005 or 132007
                             or 131026 or 131047;

        if (permanent)
        {
            _logger.LogWarning(
                "WhatsApp refused {Template} to {To} permanently ({Code}): {Detail}",
                message.TemplateName, Mask(message.ToE164), code, text);

            return WhatsAppSendResult.PermanentFailure(Describe(code, text));
        }

        var retryable = status is HttpStatusCode.TooManyRequests
                     or HttpStatusCode.RequestTimeout
                     || (int)status >= 500
                     || code is 4 or 80007 or 130429 or 131048 or 131056 or 133016
                     || code is 190 or 102;   // 047: token problems are fixable

        if (retryable)
        {
            // A token failure is logged louder than a rate limit. Both are
            // retryable, but only one of them needs a person: nothing drains
            // this backlog until someone sets a new credential, and a line
            // buried at Information would not say so.
            if (code is 190 or 102)
            {
                _logger.LogError(
                    "WhatsApp rejected the access token ({Code}) sending {Template} to {To}. " +
                    "Queued messages will keep retrying — set a valid WhatsApp:AccessToken " +
                    "and they will go out. Detail: {Detail}",
                    code, message.TemplateName, Mask(message.ToE164), text);
            }

            return WhatsAppSendResult.TransientFailure(Describe(code, text));
        }

        // An unmapped 4xx. Treated as PERMANENT on purpose: an unknown
        // client error that we retry sixteen times over six hours teaches
        // us nothing and burns quota. The message goes Dead with Meta's own
        // words on it, which is what someone reading the log needs.
        _logger.LogWarning(
            "WhatsApp returned an unmapped {Status} ({Code}) for {Template}: {Detail}",
            (int)status, code, message.TemplateName, text);

        return WhatsAppSendResult.PermanentFailure(Describe(code, text));
    }

    /// <summary>Meta's message, plus a sentence about what to do about it.</summary>
    private static string Describe(int? code, string text) => code switch
    {
        132001 => $"{text} — the template name or language is not approved on this account.",
        132000 => $"{text} — the template expects a different number of variables.",
        132005 => $"{text} — Meta has paused this template for quality.",
        131026 => $"{text} — that number cannot receive WhatsApp messages.",
        190 or 102 => $"{text} — the access token is invalid or has expired.",
        _ => text
    };

    /// <summary>
    /// { "error": { "message": "...", "code": 132001, "error_data": { "details": "..." } } }
    /// error_data.details is usually the useful half.
    /// </summary>
    private static (int? Code, string? Detail) ReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("error", out var error))
                return (null, null);

            int? code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var value)
                ? value
                : null;

            var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;

            string? details = null;
            if (error.TryGetProperty("error_data", out var data)
                && data.TryGetProperty("details", out var d))
            {
                details = d.GetString();
            }

            var text = string.IsNullOrWhiteSpace(details)
                ? message
                : $"{message} ({details})";

            return (code, text);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Meta caps a body variable at 1024 characters and rejects the whole
    /// message if one is longer. Clipping beats a rejected send of a
    /// notification whose first line already said the important part.
    /// </summary>
    private static string Clip(string? value)
    {
        var text = (value ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();

        // A variable may not be empty either — Meta rejects "" outright.
        if (text.Length == 0) return "—";

        return text.Length <= 1000 ? text : text[..997] + "...";
    }

    /// <summary>
    /// Phone numbers in logs are personal data. Enough to identify which
    /// row this was, not enough to be a contact list.
    /// </summary>
    private static string Mask(string? number)
    {
        if (string.IsNullOrWhiteSpace(number)) return "(none)";

        var digits = number.TrimStart('+');

        return digits.Length <= 4 ? "****" : $"****{digits[^4..]}";
    }
}

// ── No token configured ───────────────────────────────────────────────

/// <summary>
/// Registered when WhatsApp is not configured. It logs and reports a
/// RETRYABLE failure, so a queued message stays queued — configure a
/// token later and the backlog goes out rather than having been silently
/// swallowed. Same reasoning as NullEmailSender.
/// </summary>
public sealed class NullWhatsAppSender : IWhatsAppSender
{
    private readonly ILogger<NullWhatsAppSender> _logger;

    public NullWhatsAppSender(ILogger<NullWhatsAppSender> logger) => _logger = logger;

    public bool IsEnabled => false;

    public Task<WhatsAppSendResult> SendAsync(WhatsAppMessage message, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "WHATSAPP NOT SENT (not configured) — template {Template}", message.TemplateName);

        return Task.FromResult(WhatsAppSendResult.TransientFailure(
            "WhatsApp is not configured on this environment — the message is still queued."));
    }
}

// ── How a queued row becomes a message ────────────────────────────────

/// <summary>
/// 045. The convention documented on OutboundChannel.WhatsApp, in code,
/// in ONE place. The dispatcher writes a row this way and the worker
/// reads it back this way; if the two ever disagree, messages go out with
/// the template name as their body.
///
///   Subject   the Meta template name
///   BodyText  the variables, as a JSON array of strings
///   BodyHtml  the rendered text, for the log only
/// </summary>
public static class WhatsAppPayload
{
    public static string PackVariables(IEnumerable<string> variables)
        => JsonSerializer.Serialize(variables.ToArray());

    public static IReadOnlyList<string> UnpackVariables(string? bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText)) return Array.Empty<string>();

        try
        {
            return JsonSerializer.Deserialize<string[]>(bodyText) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            // A row written before this convention, or by hand. Sending it
            // with no variables would produce a message reading "{{1}}", so
            // the worker treats an empty list as a permanent failure.
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// What the person will actually see, for the send log. Meta renders
    /// the real thing from its own copy of the template; this is our best
    /// reconstruction from the approved body preview.
    /// </summary>
    public static string Render(string? bodyPreview, IReadOnlyList<string> variables)
    {
        if (string.IsNullOrWhiteSpace(bodyPreview))
            return string.Join(" — ", variables);

        var text = bodyPreview;

        for (var i = 0; i < variables.Count; i++)
            text = text.Replace("{{" + (i + 1) + "}}", variables[i], StringComparison.Ordinal);

        return text;
    }
}

/// <summary>
/// 045. The variables every one of our templates takes, in order.
///
/// TWO, DELIBERATELY: the notification's title and its body.
///
/// Round 044's migration seeded three-variable templates with per-event
/// shapes — "quote number", "customer", "amount". That was my mistake: a
/// NotificationRequest carries a Title and a Body, both already written
/// for a person to read, and nothing structured underneath. Filling three
/// per-event slots would mean every producer in the app passing
/// structured fields it does not have today.
///
/// Migration 045 corrects the seeded rows to this shape. It only touches
/// rows still marked as seeded by 044 and still inactive, so anything you
/// have already edited or approved is left alone.
///
/// If a per-event shape is ever wanted — "QUO-0012" in its own slot so
/// the template can bold it — that is a change to NotificationRequest
/// first, and to every producer second. Worth doing one day; not worth
/// blocking the channel on.
/// </summary>
public static class WhatsAppVariables
{
    public const int Count = 2;

    /// <summary>
    /// 048. The placeholder NAMES, in the same order as the values below.
    ///
    /// Meta's template editor stopped accepting {{1}} / {{2}} and now wants
    /// named placeholders. So an approved body looks like:
    ///
    ///     Merkai update: {{headline}} — {{detail}}. Open your workspace for more.
    ///
    /// and the send must name each value rather than rely on its position.
    ///
    /// These two strings must match the placeholder names in the approved
    /// template EXACTLY — Meta matches on the name, not the order, and a
    /// typo here fails every send on that template with a code that talks
    /// about counts rather than names.
    ///
    /// Note the trailing sentence in the example. Meta refuses a body that
    /// starts or ends with a variable, so "Merkai: {{headline}} — {{detail}}"
    /// is rejected at creation time however correct it looks.
    /// </summary>
    public static readonly IReadOnlyList<string> Names = new[] { "headline", "detail" };

    /// <summary>
    /// The name for a slot. Out-of-range falls back to "param{n}" rather
    /// than throwing: a template with an unexpected number of variables is
    /// a configuration problem that belongs in Meta's error response, where
    /// someone can read it, not in an exception that kills the worker's
    /// batch.
    /// </summary>
    public static string NameAt(int index)
        => index >= 0 && index < Names.Count ? Names[index] : $"param{index + 1}";

    public static IReadOnlyList<string> For(string title, string? body)
        => new[] { title, string.IsNullOrWhiteSpace(body) ? "—" : body };
}
