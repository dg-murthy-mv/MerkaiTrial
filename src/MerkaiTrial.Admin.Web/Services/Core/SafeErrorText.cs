// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Services/Core/SafeErrorText.cs
//
// NEW FILE (105 — VUL-12). What an error may say to the person on screen.
//
// A few pages showed ex.Message for ANY exception — "Failed to create lead:
// {ex.Message}" — and for an API call that failed, ex.Message is the
// ApiClient's "{operation} {status}: {the whole response body}". For a
// refusal that is the API's own sentence, which is the point. For a server
// error, a timeout or a bug it could be SQL Server's text, a type name, a
// stack trace or the raw JSON of an error.
//
// For(ex, fallback) keeps the useful case and drops the rest:
//   • a refusal the person can act on — the API answered 4xx with an
//     "error" (or "title") sentence, or the app threw one of its own
//     InvalidOperation / UnauthorizedAccess / KeyNotFound messages —
//     is shown, as long as it does not look technical;
//   • anything else is the fallback ("…couldn't be saved just now"), and
//     the full exception is only in the log, where it always was.
// =====================================================================

using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MerkaiTrial.Admin.Web.Services.Core;

public static class SafeErrorText
{
    // "{op} {StatusCode}: {body}" (ApiClient) or "{context} failed: {code} {reason}…" (EnsureSuccess).
    private static readonly Regex ApiFailure = new(
        @"\b(?<status>BadRequest|NotFound|Conflict|Forbidden|UnprocessableEntity|PreconditionFailed|400|403|404|409|412|422)\b[^:]*:\s*(?<body>.*)$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    // Matched ignoring case.
    private static readonly string[] Technical =
    {
        "Exception", "   at ", "Microsoft.", "System.", "SqlClient", "constraint", "FOREIGN KEY",
        "PRIMARY KEY", "Cannot insert", "String or binary data", "Server=", "Password=", "User Id=",
        "stack trace", "dbo.", "Invalid column", "Invalid object name", "deadlock", "timeout expired",
        "{\"", "traceId", "(Parameter '", "NullReference", "Object reference not set",
    };

    // SQL words, matched as written (upper case) so "can't be deleted" is not one.
    private static readonly string[] SqlWords = { "SELECT ", "INSERT INTO", "UPDATE ", "DELETE FROM", " FROM ", " WHERE " };

    /// <summary>The person's message for <paramref name="ex"/>, or <paramref name="fallback"/>.</summary>
    public static string For(Exception ex, string fallback)
    {
        string? text = ex switch
        {
            HttpRequestException http => FromApi(http.Message),
            InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException => ex.Message,
            // A plain ArgumentException is a validation sentence; its
            // Null/OutOfRange kinds name a parameter and are not shown.
            ArgumentNullException or ArgumentOutOfRangeException => null,
            ArgumentException => ex.Message,
            _ => null,
        };

        return string.IsNullOrWhiteSpace(text) || LooksTechnical(text) ? fallback : text.Trim();
    }

    /// <summary>True when text reads like a program's error rather than a sentence for a person.</summary>
    public static bool LooksTechnical(string text)
        => text.Length > 400
           || Technical.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase))
           || SqlWords.Any(t => text.Contains(t, StringComparison.Ordinal));

    /// <summary>The API's own sentence from a 4xx answer, or null.</summary>
    private static string? FromApi(string message)
    {
        Match m;
        try { m = ApiFailure.Match(message); }
        catch (RegexMatchTimeoutException) { return null; }
        if (!m.Success) return null;

        var body = m.Groups["body"].Value.Trim();
        if (body.Length == 0) return null;

        if (body.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                foreach (var name in new[] { "error", "message", "title" })
                    if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString();
                return null;
            }
            catch (JsonException) { return null; }
        }

        // A plain-text body: a sentence, if it is short.
        return body.Length <= 300 ? body : null;
    }
}
