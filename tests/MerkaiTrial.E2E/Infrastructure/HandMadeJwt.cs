// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/HandMadeJwt.cs
//
// NEW (105 — VUL-11). API tokens built by hand, piece by piece, so a test
// can make every kind of bad one: unsigned ("alg": "none"), signed with a
// different algorithm, a payload changed after signing, no expiry, an
// expiry just past, a start time in the future, the wrong issuer or
// audience, a session that was signed out.
//
// A token is header.payload.signature, each part base64url. The payload
// carries the same claims Admin.Web's ApiTokenService (and Api.Mint) put
// in, read by the API under their literal names (MapInboundClaims=false).
// =====================================================================

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MerkaiTrial.E2E.Infrastructure;

public static class HandMadeJwt
{
    /// <summary>The claims a genuine token for this person carries, valid for five minutes.</summary>
    public static Dictionary<string, object?> PayloadFor(ApiIdentity id, Guid? sessionId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new Dictionary<string, object?>
        {
            ["sub"] = id.UserId.ToString(),
            ["jti"] = Guid.NewGuid().ToString("N"),
            // As ApiTokenService writes them: the full claim-type names, which
            // JwtSecurityToken keeps as they are (no short "nameid"/"email").
            ["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier"] = id.UserId.ToString(),
            ["UserId"] = id.UserId.ToString(),
            ["TenantId"] = id.TenantId.ToString(),
            ["IsTenantAdmin"] = id.IsTenantAdmin ? "true" : "false",
            ["IsSuperAdmin"] = id.IsSuperAdmin ? "true" : "false",
            ["Plan"] = id.Plan,
            ["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"] = id.Email,
            ["permission"] = id.Permissions.ToArray(),
            ["iss"] = E2ESettings.JwtIssuer,
            ["aud"] = E2ESettings.JwtAudience,
            ["nbf"] = now.AddSeconds(-30).ToUnixTimeSeconds(),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(5).ToUnixTimeSeconds(),
        };
        if (id.SecurityStamp is not null) payload["SecurityStamp"] = id.SecurityStamp;
        if (sessionId is { } sid) payload["sid"] = sid.ToString();
        return payload;
    }

    /// <summary>Signs the payload. alg "HS256" / "HS512" use the key; "none" leaves the signature empty.</summary>
    public static string Make(IDictionary<string, object?> payload, string alg = "HS256", string? key = null)
    {
        var header = Part(new Dictionary<string, object?> { ["alg"] = alg, ["typ"] = "JWT" });
        var body = Part(payload);
        var signingInput = header + "." + body;

        if (alg == "none") return signingInput + ".";

        var keyBytes = Encoding.UTF8.GetBytes(key ?? Api.RequireKey());
        byte[] signature = alg switch
        {
            "HS256" => HMACSHA256.HashData(keyBytes, Encoding.ASCII.GetBytes(signingInput)),
            "HS512" => HMACSHA512.HashData(keyBytes, Encoding.ASCII.GetBytes(signingInput)),
            _ => throw new ArgumentOutOfRangeException(nameof(alg), alg, "HS256, HS512 or none."),
        };
        return signingInput + "." + Base64Url(signature);
    }

    /// <summary>The same token with its payload changed and its ORIGINAL signature kept.</summary>
    public static string Tamper(string token, Action<Dictionary<string, object?>> change)
    {
        var parts = token.Split('.');
        var json = Encoding.UTF8.GetString(FromBase64Url(parts[1]));
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!
            .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        change(payload);
        return parts[0] + "." + Part(payload) + "." + parts[2];
    }

    /// <summary>A GET to the API with this exact token.</summary>
    public static async Task<ApiResponse> GetAsync(string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Api.Url(path));
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        using var response = await Api.Http.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return new ApiResponse((int)response.StatusCode, Encoding.UTF8.GetString(bytes),
            response.Content.Headers.ContentType?.MediaType, bytes);
    }

    private static string Part(IDictionary<string, object?> value)
        => Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
