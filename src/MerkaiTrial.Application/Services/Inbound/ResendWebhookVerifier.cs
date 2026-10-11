// =====================================================================
// ResendWebhookVerifier.cs
// Location: MerkaiTrial.Application/Services/Inbound/ResendWebhookVerifier.cs
//
// NEW FILE (083). Proves a webhook call really came from Resend.
//
// WHY THIS MATTERS MORE THAN USUAL
//   The webhook endpoint is anonymous — it has to be, Resend has no login.
//   Without this check, anybody who found the URL could post "an email
//   from your biggest customer" straight onto a deal. With it, a call
//   without Resend's signature is refused before a single byte of it is
//   believed.
//
// THE SCHEME (Resend signs through Svix)
//   Headers:  svix-id, svix-timestamp, svix-signature
//   Signed:   "{svix-id}.{svix-timestamp}.{raw body}"
//   Key:      the webhook's signing secret, "whsec_<base64>" — the part
//             after the prefix, base64-decoded
//   MAC:      HMAC-SHA256, base64
//   Header:   space-separated "v1,<base64>" entries (several during a
//             secret rotation); any one matching is enough
//   Replay:   the timestamp must be within 5 minutes of now
//
//   The RAW body, exactly as received. Parsing and re-serialising the
//   JSON first changes whitespace and breaks the signature — which is why
//   the controller reads the stream itself instead of model binding.
// =====================================================================

using System.Security.Cryptography;
using System.Text;

namespace MerkaiTrial.Application.Services.Inbound;

public static class ResendWebhookVerifier
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Null when the call is genuine; otherwise a short reason for the log.
    /// The reason never contains the secret or the expected signature.
    /// </summary>
    public static string? Verify(
        string? secret,
        string? svixId,
        string? svixTimestamp,
        string? svixSignature,
        string body,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(secret)) return "no signing secret is configured";
        if (string.IsNullOrWhiteSpace(svixId) || string.IsNullOrWhiteSpace(svixTimestamp)
            || string.IsNullOrWhiteSpace(svixSignature))
            return "signature headers are missing";

        if (!long.TryParse(svixTimestamp, out var unix)) return "timestamp is not a number";

        DateTimeOffset sentAt;
        try { sentAt = DateTimeOffset.FromUnixTimeSeconds(unix); }
        catch (ArgumentOutOfRangeException) { return "timestamp is out of range"; }

        if ((now - sentAt).Duration() > Tolerance) return "timestamp is too old or in the future";

        byte[] key;
        try
        {
            var encoded = secret.Trim();
            if (encoded.StartsWith("whsec_", StringComparison.Ordinal)) encoded = encoded["whsec_".Length..];
            key = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return "the configured signing secret is not in the expected whsec_ format";
        }

        byte[] expected;
        using (var hmac = new HMACSHA256(key))
        {
            expected = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{svixId}.{svixTimestamp}.{body}"));
        }

        foreach (var part in svixSignature.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var comma = part.IndexOf(',');
            if (comma <= 0 || part[..comma] != "v1") continue;

            byte[] given;
            try { given = Convert.FromBase64String(part[(comma + 1)..]); }
            catch (FormatException) { continue; }

            // Constant time: a byte-by-byte compare that stops early leaks,
            // through timing, how much of a forged signature was right.
            if (given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected))
                return null;
        }

        return "signature does not match";
    }
}
