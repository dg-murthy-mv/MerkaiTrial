// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/ApiAuthorizationTests.cs
//
// NEW (100) — test 77, Critical · API security.
//
//   An_unauthorized_user_cannot_call_protected_API_endpoints
//
// The browser tests only ever reach the API through Admin.Web's pages,
// which check permissions themselves. This goes straight to the WebApi,
// the way someone with a copied token or a curious integration would, and
// calls EVERY endpoint the API has — GET, POST, PUT, PATCH and DELETE.
//
// WHERE THE LIST COMES FROM
//   The Development-only Swagger document (/swagger/v1/swagger.json). Since
//   round 100 each operation in it says who may call it:
//     x-merkai-anonymous  true for [AllowAnonymous]
//     x-merkai-policies   every "Module.Action" policy on it
//   So an endpoint added next month is tested the day it exists.
//
// THE CALLERS
//   • a signed-out visitor     → every endpoint that is not anonymous must
//                                answer 401.
//   • a Viewer, a Sales Rep    → every endpoint whose policy their role does
//     (workspace A)              NOT grant must answer 403 (read from their
//                                roles in the database, exactly as their
//                                sign-in would). Endpoints they ARE allowed
//                                are not called at all — this test never
//                                changes anything it is allowed to change.
//   • forged and expired tokens → 401, even on the plainest endpoint, and
//                                even when the forged token claims to be a
//                                workspace admin.
//   • workspace B's admin       → workspace A's lead, by id: read, edit,
//                                status, assign, notes, delete — all 404,
//                                and the lead unchanged.
//   • workspace A's admin naming workspace B in an X-Tenant-Id header (the
//     header the two controllers removed in 100 trusted) → still only A's
//                                data.
//
// Ids in the addresses are random (they belong to nobody): a refusal has
// to come from WHO is asking, before any record is looked up.
//
// 100b (after the first run): B's admin listing the notes of A's lead
// may get 200 with an EMPTY list — that is B's own (empty) notes, not a
// leak. A's lead now carries a note only A knows, and any answer that
// contains it, or the lead's name, fails the test. Writes and reads of the
// lead itself must still be refused.
//
// Every wrong answer is collected and reported together.
// =====================================================================

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class ApiAuthorizationTests : E2ETest
{
    private sealed record Operation(string Method, string Template, string Path, bool Anonymous, IReadOnlyList<string> Policies,
        bool MultipartOnly = false)
    {
        /// <summary>
        /// The body to send: none, or — for an upload that accepts only
        /// multipart/form-data — a tiny multipart form, because such an
        /// endpoint answers a JSON body with 415 BEFORE authorization runs,
        /// and the test would be measuring the wrong thing.
        /// </summary>
        public HttpContent? Content()
        {
            if (!MultipartOnly) return null;
            var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("name\nx\n")), "file", "e2e.csv");
            return form;
        }
    }

    [Test]
    public async Task An_unauthorized_user_cannot_call_protected_API_endpoints()
    {
        var admin = await Api.AsAsync(World.A.Admin);
        await Step("The API accepts this run's tokens", () => Api.EnsureTokensWorkAsync(admin));

        var operations = await Step("Read every endpoint from the API's own description", ReadOperationsAsync);
        TestContext.Progress.WriteLine(
            $"  {operations.Count} operations, {operations.Count(o => o.Anonymous)} of them public, " +
            $"{operations.Count(o => o.Policies.Count == 0 && !o.Anonymous)} signed-in only.");

        var problems = new List<string>();

        // ── 1. Nobody signed in ───────────────────────────────────────
        await Step("A signed-out visitor gets 401 from every protected endpoint", async () =>
        {
            foreach (var op in operations.Where(o => !o.Anonymous))
            {
                var r = await Api.Anonymous.SendAsync(new HttpMethod(op.Method), op.Path, content: op.Content());
                if (r.Status != 401)
                    problems.Add($"Signed out: {op.Method} {op.Template} → {r.Status} (expected 401). {ApiResponse.Short(r.Body, 100)}");
            }
        });

        // ── 2. A Viewer and a Sales Rep ───────────────────────────────
        foreach (var key in new[] { "viewer", "rep" })
        {
            var caller = await Api.AsAsync(World.A[key]);
            var id = caller.Identity!;
            var denied = operations.Where(o => !o.Anonymous && o.Policies.Count > 0 && o.Policies.Any(p => !id.Has(p))).ToList();

            await Step($"The {key} gets 403 from the {denied.Count} endpoints their role does not allow", async () =>
            {
                Assert.That(id.IsTenantAdmin, Is.False, $"The seeded {key} is a workspace admin, so this proves nothing.");
                foreach (var op in denied)
                {
                    var r = await caller.SendAsync(new HttpMethod(op.Method), op.Path, content: op.Content());
                    if (r.Status != 403)
                        problems.Add($"{key}: {op.Method} {op.Template} → {r.Status} (expected 403; needs " +
                                      $"{string.Join(" + ", op.Policies.Where(p => !id.Has(p)))}). {ApiResponse.Short(r.Body, 100)}");
                }
            });
        }

        // ── 3. Tokens that are not this server's, or not valid now ────
        await Step("Forged, expired and unsigned tokens get 401", async () =>
        {
            var viewerId = await Db.ApiIdentityAsync(World.A["viewer"].Email);
            var asAdmin = viewerId with { IsTenantAdmin = true, Permissions = new[] { "Leads.Delete", "Settings.Update" } };

            var forged = Api.Mint(asAdmin, key: "not-the-servers-key-" + new string('x', 48));
            var expired = Api.Mint(viewerId, expiresUtc: DateTime.UtcNow.AddMinutes(-10));
            var unsigned = UnsignedToken(asAdmin);

            foreach (var (label, token) in new[] { ("forged (claims workspace admin)", forged), ("expired", expired), ("unsigned (alg none)", unsigned) })
            {
                var caller = new ApiCaller(label, () => token);
                foreach (var (method, path) in new[]
                         {
                             ("GET", "/api/leads?pageNumber=1&pageSize=1"),
                             ("DELETE", $"/api/leads/{Guid.NewGuid()}"),
                             ("PUT", "/api/quote-approvals/enabled"),
                         })
                {
                    var r = await caller.SendAsync(new HttpMethod(method), path, method == "PUT" ? new { isEnabled = false } : null);
                    if (r.Status != 401)
                        problems.Add($"{label} token: {method} {path} → {r.Status} (expected 401).");
                }
            }
        });

        // ── 4. Another workspace's admin ─────────────────────────────
        await Step("Workspace B's admin is refused workspace A's lead, and it is left alone", async () =>
        {
            var a = await SignedInAsync(World.A.Admin);
            var name = World.Unique("Api Guard Lead");
            var leadId = await Flows.CreateLeadAsync(a, name, World.EmailFor("A", "apiguard" + Random.Shared.Next(1000, 9999)), 5000m);
            var tenantA = await Db.TenantIdAsync(World.A.Name);

            // A note only A can know about — a list B is given back must not contain it.
            var secretNote = World.Unique("Api guard note");
            var noted = await admin.PostAsync($"/api/leads/{leadId}/notes", new { leadId, tenantId = tenantA, note = secretNote });
            Assert.That(noted.IsSuccess, Is.True, $"Adding workspace A's own note failed: {noted}");

            var b = await Api.AsAsync(World.B.Admin);
            var before = await Db.RowJsonAsync("Leads", leadId);
            var notesBefore = await Db.CountWhereAsync("LeadNotes", "LeadId", leadId);

            var attempts = new (string Method, string Path, object? Body)[]
            {
                ("GET", $"/api/leads/{leadId}", null),
                ("GET", $"/api/leads/{leadId}/notes?tenantId={tenantA}", null),
                ("PUT", $"/api/leads/{leadId}", new { leadId, tenantId = tenantA, fullName = "Changed by B", currency = "INR" }),
                ("PUT", $"/api/leads/{leadId}/status?status=Contacted", null),
                ("PATCH", $"/api/leads/{leadId}/assign", new { leadId, tenantId = tenantA, ownerUserId = "" }),
                ("POST", $"/api/leads/{leadId}/notes", new { leadId, tenantId = tenantA, note = "Note by B" }),
                ("DELETE", $"/api/leads/{leadId}", null),
            };

            foreach (var (method, path, body) in attempts)
            {
                var r = await b.SendAsync(new HttpMethod(method), path, body);
                var leaked = r.Body.Contains(name, StringComparison.OrdinalIgnoreCase)
                             || r.Body.Contains(secretNote, StringComparison.OrdinalIgnoreCase);
                if (leaked)
                    problems.Add($"Workspace B's admin: {method} {path} → {r.Status}, and the answer carries workspace A's data.");
                // 100b: a LIST may answer 200 with nothing in it (B's notes on
                // a lead B doesn't have: none). A write, or a read of the lead
                // itself, must be refused.
                else if (r.IsSuccess && !(method == "GET" && path.Contains("/notes")))
                    problems.Add($"Workspace B's admin: {method} {path} → {r.Status}. It must be refused.");
            }

            if (await Db.RowJsonAsync("Leads", leadId) != before)
                problems.Add("Workspace B's admin CHANGED workspace A's lead.");
            if (await Db.CountWhereAsync("LeadNotes", "LeadId", leadId) != notesBefore)
                problems.Add("Workspace B's admin added a note to workspace A's lead.");
        });

        // ── 5. Naming another workspace in a header ──────────────────
        await Step("An X-Tenant-Id header naming workspace B changes nothing", async () =>
        {
            var b = await SignedInAsync(World.B.Admin);
            var secret = World.Unique("Header Secret");
            await Flows.CreateLeadAsync(b, secret, World.EmailFor("B", "hdr" + Random.Shared.Next(1000, 9999)), 7000m, World.B);
            var tenantB = await Db.TenantIdAsync(World.B.Name);

            var headers = new Dictionary<string, string> { ["X-Tenant-Id"] = tenantB.ToString() };
            var r = await admin.GetAsync($"/api/leads?pageNumber=1&pageSize=50&searchTerm={Uri.EscapeDataString(secret)}", headers);
            if (r.Body.Contains(secret, StringComparison.OrdinalIgnoreCase))
                problems.Add("Workspace A's admin read workspace B's lead by sending X-Tenant-Id: B.");
        });

        Assert.That(problems, Is.Empty,
            $"{problems.Count} endpoint answer(s) were wrong:\n  • " + string.Join("\n  • ", problems));
    }

    // =================================================================

    /// <summary>
    /// Every operation in the API's Swagger document, with its path made
    /// callable: guid parameters get a fresh random id, the rest a
    /// harmless word. Fails if the document lacks the round-100 extensions
    /// (the WebApi was not rebuilt) — otherwise every endpoint would look
    /// "signed-in only" and the test would quietly prove nothing.
    /// </summary>
    private static async Task<List<Operation>> ReadOperationsAsync()
    {
        var r = await Api.Anonymous.GetAsync("/swagger/v1/swagger.json");
        if (!r.IsSuccess)
            throw new AssertionException(
                $"The API's Swagger document could not be read ({r.Status}). It is served in Development only — " +
                "run-e2e.ps1 starts the WebApi in Development.");

        using var doc = JsonDocument.Parse(r.Body);
        var ops = new List<Operation>();
        var sawExtension = false;

        foreach (var path in doc.RootElement.GetProperty("paths").EnumerateObject())
        {
            // Parameters declared once for the whole path apply to every verb.
            var pathParams = path.Value.TryGetProperty("parameters", out var pp) ? pp : default;

            foreach (var verb in path.Value.EnumerateObject())
            {
                var method = verb.Name.ToUpperInvariant();
                if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE")) continue;

                var op = verb.Value;
                var anonymous = op.TryGetProperty("x-merkai-anonymous", out var an) && an.ValueKind == JsonValueKind.True;
                var policies = new List<string>();
                if (op.TryGetProperty("x-merkai-policies", out var pol) && pol.ValueKind == JsonValueKind.Array)
                {
                    sawExtension = true;
                    policies.AddRange(pol.EnumerateArray().Select(p => p.GetString()).Where(p => !string.IsNullOrWhiteSpace(p))!);
                }
                if (op.TryGetProperty("x-merkai-anonymous", out _)) sawExtension = true;

                var guidParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var source in new[] { pathParams, op.TryGetProperty("parameters", out var ps) ? ps : default })
                {
                    if (source.ValueKind != JsonValueKind.Array) continue;
                    foreach (var prm in source.EnumerateArray())
                    {
                        if (!prm.TryGetProperty("in", out var where) || where.GetString() != "path") continue;
                        var schema = prm.TryGetProperty("schema", out var s) ? s : default;
                        if (schema.ValueKind == JsonValueKind.Object &&
                            schema.TryGetProperty("format", out var f) && f.GetString() == "uuid")
                            guidParams.Add(prm.GetProperty("name").GetString() ?? "");
                    }
                }

                var filled = Regex.Replace(path.Name, @"\{([^}:]+)(:[^}]*)?\}", m =>
                {
                    var name = m.Groups[1].Value;
                    if (guidParams.Contains(name)) return Guid.NewGuid().ToString();
                    return name.ToLowerInvariant() switch
                    {
                        "entitytype" => "Lead",
                        "code" or "countrycode" => "IN",
                        "provider" => "Stripe",
                        _ when name.EndsWith("id", StringComparison.OrdinalIgnoreCase) => Guid.NewGuid().ToString(),
                        _ => "e2e" + Guid.NewGuid().ToString("N")[..8],
                    };
                });

                // Request body types: multipart only (an upload) → send a form.
                var multipartOnly = false;
                if (op.TryGetProperty("requestBody", out var rb) && rb.TryGetProperty("content", out var ct) &&
                    ct.ValueKind == JsonValueKind.Object)
                {
                    var types = ct.EnumerateObject().Select(t => t.Name).ToList();
                    multipartOnly = types.Count > 0 && types.All(t => t.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase));
                }

                ops.Add(new Operation(method, path.Name, filled, anonymous, policies, multipartOnly));
            }
        }

        if (!sawExtension)
            throw new AssertionException(
                "The Swagger document has no x-merkai-anonymous / x-merkai-policies on its operations, so it cannot say " +
                "who may call what. The WebApi running is older than round 100 — rebuild it (PermissionOperationFilter).");
        if (ops.Count < 50)
            throw new AssertionException($"The Swagger document lists only {ops.Count} operations — expected well over 100.");

        return ops;
    }

    /// <summary>A token with "alg": "none" and no signature — accepted by nothing that checks.</summary>
    private static string UnsignedToken(ApiIdentity id)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["sub"] = id.UserId.ToString(),
            ["UserId"] = id.UserId.ToString(),
            ["TenantId"] = id.TenantId.ToString(),
            ["IsTenantAdmin"] = "true",
            ["iss"] = E2ESettings.JwtIssuer,
            ["aud"] = E2ESettings.JwtAudience,
            ["nbf"] = now - 30,
            ["exp"] = now + 300,
        });
        return B64("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "." + B64(payload) + ".";
    }
}
