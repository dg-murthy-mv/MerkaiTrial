// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/Api.cs
//
// NEW (100). Calling the WebApi DIRECTLY, as a given person — the way an
// attacker with a stolen session, a curious viewer with the browser's
// developer tools, or a buggy integration would. The browser tests only
// ever go through Admin.Web's pages, which check permissions themselves;
// these go straight past them to the API, which must hold the line on
// its own.
//
// THE TOKEN
//   Made exactly the way Admin.Web's ApiTokenService makes one: the same
//   claims (UserId, TenantId, IsTenantAdmin, IsSuperAdmin, Plan, email and
//   one "permission" per role permission — read from the test database
//   the same way SignInService reads them at sign-in), the same issuer and
//   audience, five minutes' life, HMAC-SHA256.
//
//   Signed with the key run-e2e.ps1 gave BOTH test servers for this run
//   only (E2ESettings.JwtSigningKey). It is random, made fresh each run,
//   and exists nowhere else: it is not your real Jwt:SigningKey, and a
//   token made with it is worthless against any other server.
//
// 100b/c: idle connections are recycled after 15s. Every request is sent
// exactly once — see SendAsync.
//
// Every call returns an ApiResponse (status, body, content type) rather
// than throwing, because most calls here are EXPECTED to be refused.
// =====================================================================

using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Tokens;

namespace MerkaiTrial.E2E.Infrastructure;

/// <summary>Who a token speaks for, as the database has it today.</summary>
/// <remarks>
/// 102: <see cref="SecurityStamp"/> is the person's stamp when the identity
/// was read — like a session cookie, a snapshot. The API refuses a token
/// whose stamp is no longer the person's, so a caller made BEFORE someone's
/// roles changed stops working afterwards; make a new caller with
/// Api.AsAsync after such a change (ManualInvoiceApprovalTests does).
/// </remarks>
public sealed record ApiIdentity(
    Guid UserId, Guid TenantId, string Email, bool IsTenantAdmin, bool IsSuperAdmin, string Plan,
    IReadOnlyList<string> Permissions, string? SecurityStamp = null)
{
    /// <summary>
    /// Would the API's PermissionHandler let this person through
    /// <paramref name="policy"/>? A workspace admin passes every
    /// "module.action" policy; everyone else needs the permission, compared
    /// without regard to case (as the handler does). A policy that is not
    /// "module.action" (none today) is treated as super-admin only.
    /// </summary>
    public bool Has(string policy)
    {
        if (!policy.Contains('.')) return IsSuperAdmin;
        if (IsTenantAdmin) return true;
        return Permissions.Any(p => string.Equals(p, policy, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>One answer from the API. Never thrown — tests read it.</summary>
public sealed record ApiResponse(int Status, string Body, string? ContentType, byte[] Bytes)
{
    public bool IsSuccess => Status is >= 200 and < 300;

    public JsonElement Json()
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(Body) ? "null" : Body);
        return doc.RootElement.Clone();
    }

    /// <summary>The API's own sentence: { "error": "…" }, or the body itself cut short.</summary>
    public string Error
    {
        get
        {
            try
            {
                var j = Json();
                if (j.ValueKind == JsonValueKind.Object)
                {
                    if (j.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) return e.GetString() ?? "";
                    if (j.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) return t.GetString() ?? "";
                }
            }
            catch (JsonException) { }
            return Short(Body);
        }
    }

    public override string ToString() => $"{Status} {Short(Body)}";

    public static string Short(string? s, int max = 160)
    {
        if (string.IsNullOrEmpty(s)) return "(empty)";
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= max ? s : s[..max] + "…";
    }
}

/// <summary>Calls the API as one person (or nobody).</summary>
public sealed class ApiCaller
{
    private readonly Func<string?> _token;

    public string Who { get; }

    /// <summary>Null for the signed-out caller and for hand-made tokens.</summary>
    public ApiIdentity? Identity { get; }

    public ApiCaller(string who, Func<string?> token, ApiIdentity? identity = null)
    {
        Who = who;
        _token = token;
        Identity = identity;
    }

    public Task<ApiResponse> GetAsync(string path, IDictionary<string, string>? headers = null) =>
        SendAsync(HttpMethod.Get, path, null, headers);

    public Task<ApiResponse> PostAsync(string path, object? json = null, IDictionary<string, string>? headers = null) =>
        SendAsync(HttpMethod.Post, path, json, headers);

    public Task<ApiResponse> PutAsync(string path, object? json = null, IDictionary<string, string>? headers = null) =>
        SendAsync(HttpMethod.Put, path, json, headers);

    public Task<ApiResponse> PatchAsync(string path, object? json = null, IDictionary<string, string>? headers = null) =>
        SendAsync(HttpMethod.Patch, path, json, headers);

    public Task<ApiResponse> DeleteAsync(string path, IDictionary<string, string>? headers = null) =>
        SendAsync(HttpMethod.Delete, path, null, headers);

    /// <summary>
    /// Any verb. <paramref name="json"/> is sent as JSON when given; a
    /// write with no body sends "{}" so the request is well formed and
    /// any refusal is about WHO is asking, not about a missing body.
    /// </summary>
    public async Task<ApiResponse> SendAsync(HttpMethod method, string path, object? json = null,
        IDictionary<string, string>? headers = null, HttpContent? content = null)
    {
        // 100c: no retry. update1 re-sent a request whose connection closed
        // while sending; the real cause turned out to be the API cutting
        // the connection AFTER handling the request (fixed in the API), so
        // a re-send could repeat something that had happened. Every
        // request is now sent exactly once.
        return await SendOnceAsync(method, path, json, headers, content);
    }

    private async Task<ApiResponse> SendOnceAsync(HttpMethod method, string path, object? json,
        IDictionary<string, string>? headers, HttpContent? content)
    {
        using var request = new HttpRequestMessage(method, Api.Url(path));

        if (_token() is { Length: > 0 } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (headers != null)
            foreach (var (k, v) in headers) request.Headers.TryAddWithoutValidation(k, v);

        if (content != null)
            request.Content = content;
        else if (json != null)
            request.Content = new StringContent(JsonSerializer.Serialize(json, Api.JsonOptions), Encoding.UTF8, "application/json");
        else if (method != HttpMethod.Get && method != HttpMethod.Delete && method != HttpMethod.Head)
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await Api.Http.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var type = response.Content.Headers.ContentType?.MediaType;
        var text = IsText(type) ? Encoding.UTF8.GetString(bytes) : $"({bytes.Length} bytes of {type ?? "unknown"})";
        return new ApiResponse((int)response.StatusCode, text, type, bytes);
    }

    /// <summary>A multipart upload with one file, the way the pages send attachments.</summary>
    public Task<ApiResponse> UploadAsync(string path, string fileName, byte[] bytes, string contentType = "text/plain",
        string field = "file")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, field, fileName);
        return SendAsync(HttpMethod.Post, path, content: form);
    }

    private static bool IsText(string? type) =>
        type is null
        || type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || type.Contains("json", StringComparison.OrdinalIgnoreCase)
        || type.Contains("xml", StringComparison.OrdinalIgnoreCase)
        || type.Contains("html", StringComparison.OrdinalIgnoreCase);
}

public static class Api
{
    /// <summary>One client for the run. The test servers use the ASP.NET development certificate.</summary>
    /// <remarks>
    /// 100b. Idle connections are dropped after 15 seconds, well before
    /// the server (Kestrel) drops them at 2 minutes — so the test never
    /// picks up a connection the server is in the middle of closing. A
    /// tests-wide pool of connections made by the "all at once" tests used
    /// to sit idle for minutes and then fail the next request with "Error
    /// while copying content to a stream".
    /// </remarks>
    internal static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        AllowAutoRedirect = false,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    {
        Timeout = TimeSpan.FromSeconds(90),
    };

    /// <summary>The API's own JSON shape: camelCase in, any case out, enums as strings.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Url(string path) =>
        path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? path
            : E2ESettings.ApiBaseUrl + (path.StartsWith('/') ? path : "/" + path);

    /// <summary>Nobody signed in: no Authorization header at all.</summary>
    public static ApiCaller Anonymous { get; } = new("a signed-out visitor", () => null);

    /// <summary>The run's signing key, or a failure that says how to get one.</summary>
    public static string RequireKey() =>
        E2ESettings.JwtSigningKey
        ?? throw new AssertionException(
            "This test calls the WebApi directly and needs the signing key run-e2e.ps1 gives the test " +
            "servers. Run it through run-e2e.ps1 (or start the servers with run-e2e.ps1 -ServersOnly). " +
            "If a WebApi was ALREADY running when run-e2e.ps1 started, it was not given this run's key — " +
            "stop it and run again.");

    /// <summary>A caller that is <paramref name="user"/>, with a fresh token per request.</summary>
    public static async Task<ApiCaller> AsAsync(TestUser user)
    {
        var key = RequireKey();
        var identity = await GlobalSetup.Db.ApiIdentityAsync(user.Email);
        return new ApiCaller($"{user.Key} ({user.Email})", () => Mint(identity, key), identity);
    }

    /// <summary>
    /// A token like ApiTokenService's. <paramref name="key"/> other than the
    /// run's makes a FORGED token; a past <paramref name="expiresUtc"/>
    /// makes an EXPIRED one.
    /// </summary>
    public static string Mint(ApiIdentity id, string? key = null, DateTime? expiresUtc = null)
    {
        key ??= RequireKey();
        var userId = id.UserId.ToString();

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(ClaimTypes.NameIdentifier, userId),
            new("UserId", userId),
            new("TenantId", id.TenantId.ToString()),
            new("IsTenantAdmin", id.IsTenantAdmin ? "true" : "false"),
            new("IsSuperAdmin", id.IsSuperAdmin ? "true" : "false"),
            new("Plan", id.Plan),
            new(ClaimTypes.Email, id.Email),
        };
        // 102 — ApiTokenService puts the session's stamp in every token, and
        // the API checks it against the database on every call.
        if (id.SecurityStamp is not null) claims.Add(new Claim("SecurityStamp", id.SecurityStamp));
        foreach (var p in id.Permissions) claims.Add(new Claim("permission", p));

        var expires = expiresUtc ?? DateTime.UtcNow.AddMinutes(5);
        var notBefore = expires < DateTime.UtcNow ? expires.AddMinutes(-10) : DateTime.UtcNow.AddSeconds(-30);

        var token = new JwtSecurityToken(
            issuer: E2ESettings.JwtIssuer,
            audience: E2ESettings.JwtAudience,
            claims: claims,
            notBefore: notBefore,
            expires: expires,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Before anything else: does the API accept this run's tokens? A
    /// workspace admin listing leads must get 200. Fails with the likely
    /// reason when it doesn't, so a key mix-up never reads as a security
    /// failure.
    /// </summary>
    public static async Task EnsureTokensWorkAsync(ApiCaller admin)
    {
        var r = await admin.GetAsync("/api/leads?pageNumber=1&pageSize=1");
        if (r.Status == 200) return;

        throw new AssertionException(
            $"The WebApi did not accept this run's token for {admin.Who}: {r}. " +
            (r.Status == 401
                ? "The WebApi was probably started outside run-e2e.ps1 (or before it), so it has a different " +
                  "signing key. Stop it and run run-e2e.ps1 again."
                : "Check e2e-logs/webapi.out.log for the reason."));
    }

    /// <summary>
    /// Fires every request at the same moment and waits for all of them —
    /// "two people press the button together". The tasks are created
    /// first and released through one gate, so they really do overlap.
    /// </summary>
    public static async Task<ApiResponse[]> AllAtOnceAsync(params Func<Task<ApiResponse>>[] calls)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = calls.Select(async call =>
        {
            await gate.Task;
            return await call();
        }).ToArray();

        gate.SetResult();
        return await Task.WhenAll(tasks);
    }
}
