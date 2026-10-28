// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/Webhooks.cs
//
// NEW (105 — VUL-19). Signing webhook events the way Resend (Svix) does,
// with THIS RUN's secret.
//
// run-e2e.ps1 gives the WebApi Email__InboundWebhookSecret made from the
// run's signing key: "whsec_" + base64(SHA-256("e2e-webhook:" + key)). The
// tests have the key (E2E_JWT_KEY), so they make the same secret here. It
// exists only for the run, and nothing can be SENT with it — it only lets
// the tests post events the server will believe.
//
// A signature is "v1," + base64(HMAC-SHA256(secret bytes,
//                                            "{svix-id}.{svix-timestamp}.{body}")).
// =====================================================================

using System.Security.Cryptography;
using System.Text;

namespace MerkaiTrial.E2E.Infrastructure;

public static class Webhooks
{
    public const string ResendPath = "/api/inbound/resend";

    /// <summary>This run's webhook secret, exactly as run-e2e.ps1 made it.</summary>
    public static string Secret =>
        "whsec_" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("e2e-webhook:" + Api.RequireKey())));

    public static string Now() => Now(0);

    /// <summary>A Svix timestamp this many seconds from now (negative: in the past).</summary>
    public static string Now(int offsetSeconds) =>
        DateTimeOffset.UtcNow.AddSeconds(offsetSeconds).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string Sign(string secret, string id, string timestamp, string body)
    {
        var key = Convert.FromBase64String(secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret["whsec_".Length..] : secret);
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}"));
        return "v1," + Convert.ToBase64String(mac);
    }

    /// <summary>A signed request, ready to post (and to post again, unchanged).</summary>
    public sealed record SignedEvent(string Id, string Timestamp, string Signature, string Body);

    public static SignedEvent Signed(string body, string? id = null, string? timestamp = null, string? secret = null)
    {
        id ??= "msg_e2e_" + Guid.NewGuid().ToString("N");
        timestamp ??= Now();
        return new SignedEvent(id, timestamp, Sign(secret ?? Secret, id, timestamp, body), body);
    }

    public static Task<ApiResponse> PostAsync(SignedEvent e) => PostAsync(e.Body, e.Id, e.Timestamp, e.Signature);

    /// <summary>Posts to the Resend webhook with whichever of the three headers are given.</summary>
    public static async Task<ApiResponse> PostAsync(string body, string? id, string? timestamp, string? signature)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Api.Url(ResendPath))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (id is not null) request.Headers.TryAddWithoutValidation("svix-id", id);
        if (timestamp is not null) request.Headers.TryAddWithoutValidation("svix-timestamp", timestamp);
        if (signature is not null) request.Headers.TryAddWithoutValidation("svix-signature", signature);

        using var response = await Api.Http.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return new ApiResponse((int)response.StatusCode, Encoding.UTF8.GetString(bytes),
            response.Content.Headers.ContentType?.MediaType, bytes);
    }

    /// <summary>A delivery event about one email we sent, as Resend words it.</summary>
    public static string DeliveryEvent(string type, string providerEmailId) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            type,
            created_at = DateTime.UtcNow.ToString("O"),
            data = new
            {
                email_id = providerEmailId,
                bounce = type == "email.bounced" ? new { type = "Permanent", subType = "NoEmail", message = "e2e" } : null,
            },
        });
}
