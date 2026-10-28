// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/CorsTests.cs
//
// VUL-17 (round 105) — other websites can't read the app's answers.
//
//   Other_websites_cannot_read_protected_answers
//
// A browser lets a page on one website read another website's answers only
// when that other website says so (CORS: Access-Control-Allow-Origin).
// Neither server should ever say so to anyone:
//
//   • the API, asked with a valid token from Origin https://evil.example,
//     from Origin "null" (a sandboxed page or a file), and from an origin
//     that only LOOKS like ours — no Access-Control-Allow-Origin, no
//     Allow-Credentials;
//   • the API's "may I?" question (OPTIONS preflight, asking to send an
//     Authorization header) gets no yes;
//   • the website, signed in, asked from another origin — the same;
//   • and for real, in a browser: a page that is not this site tries to
//     read the signed-in person's dashboard (with their cookie) and the
//     API — the browser blocks both, and the page learns nothing.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class CorsTests : E2ETest
{
    [Test]
    public async Task Other_websites_cannot_read_protected_answers()
    {
        var a = World.A;
        var apiA = await Api.AsAsync(a.Admin);
        var siteHost = new Uri(E2ESettings.BaseUrl).Host;
        var problems = new List<string>();

        var origins = new[]
        {
            "https://evil.example",
            "null",
            $"https://{siteHost}.evil.example",
            $"https://evil{siteHost}",
            "http://" + new Uri(E2ESettings.BaseUrl).Authority,     // ours, but plain http
        };

        await Step("The API, asked with a valid token from other origins: no permission to read", async () =>
        {
            foreach (var origin in origins)
            {
                var r = await apiA.GetAsync("/api/leads?pageNumber=1&pageSize=1", new Dictionary<string, string> { ["Origin"] = origin });
                Assert.That(r.Status, Is.EqualTo(200), $"The control call itself failed: {r}");
                Judge($"The API (GET, Origin {origin})", r.Header("Access-Control-Allow-Origin"), r.Header("Access-Control-Allow-Credentials"), problems);
            }
        });

        await Step("The API's preflight (OPTIONS, asking to send Authorization): no yes", async () =>
        {
            foreach (var origin in origins)
            {
                var r = await Api.Anonymous.SendAsync(HttpMethod.Options, "/api/leads", headers: new Dictionary<string, string>
                {
                    ["Origin"] = origin,
                    ["Access-Control-Request-Method"] = "DELETE",
                    ["Access-Control-Request-Headers"] = "authorization,content-type",
                });
                Judge($"The API (preflight, Origin {origin})", r.Header("Access-Control-Allow-Origin"), r.Header("Access-Control-Allow-Credentials"), problems);
                if (r.Header("Access-Control-Allow-Methods") is { Length: > 0 } methods)
                    problems.Add($"The API's preflight from {origin} lists allowed methods: {methods}.");
                if (r.Status >= 500) problems.Add($"The API's preflight from {origin} gave a server error: {r}");
            }
        });

        await Step("The website, signed in, asked from other origins: no permission to read", async () =>
        {
            using var web = await WebSession.SignedInAsync(a.Admin);
            foreach (var origin in origins)
            {
                var r = await web.GetAsync("/Dashboard", new Dictionary<string, string> { ["Origin"] = origin });
                Judge($"The website (GET /Dashboard, Origin {origin})", r.Header("Access-Control-Allow-Origin"), r.Header("Access-Control-Allow-Credentials"), problems);

                var pre = await web.SendAsync(HttpMethod.Options, "/Leads", headers: new Dictionary<string, string>
                {
                    ["Origin"] = origin,
                    ["Access-Control-Request-Method"] = "POST",
                    ["Access-Control-Request-Headers"] = "content-type",
                });
                Judge($"The website (preflight, Origin {origin})", pre.Header("Access-Control-Allow-Origin"), pre.Header("Access-Control-Allow-Credentials"), problems);
            }
        });

        await Step("In a browser: a page that isn't this site can't read the dashboard or the API", async () =>
        {
            var page = await SignedInAsync(a.Admin);
            var marker = a.Admin.Email;      // on every signed-in page (the person menu)

            // Control: the site itself reads its own dashboard.
            var own = await page.EvaluateAsync<string>(
                "async () => { const r = await fetch('/Dashboard', { credentials: 'include' }); return await r.text(); }");
            Assert.That(own.Contains(marker, StringComparison.OrdinalIgnoreCase) || own.Length > 500, Is.True,
                "The site couldn't read its own dashboard, so the check below would prove nothing.");

            // Another origin: a blank page with no site of its own, in the same browser (same cookies).
            var evil = await page.Context.NewPageAsync();
            await evil.GotoAsync("about:blank");
            await evil.SetContentAsync("<html><body>elsewhere</body></html>");

            var script =
                "async ([site, api]) => {" +
                "  const out = [];" +
                "  for (const [name, url, opts] of [" +
                "      ['dashboard', site + '/Dashboard', { credentials: 'include' }]," +
                "      ['api', api + '/api/leads?pageNumber=1&pageSize=1', { credentials: 'include', headers: { 'Authorization': 'Bearer x' } }]," +
                "      ['api-plain', api + '/api/leads?pageNumber=1&pageSize=1', { credentials: 'include' }]]) {" +
                "    try { const r = await fetch(url, opts); const t = await r.text(); out.push(name + ':read:' + r.status + ':' + t.length + ':' + t.slice(0, 4000)); }" +
                "    catch (e) { out.push(name + ':blocked'); }" +
                "  }" +
                "  return out;" +
                "}";
            var results = await evil.EvaluateAsync<string[]>(script, new[] { E2ESettings.BaseUrl, E2ESettings.ApiBaseUrl });
            await evil.CloseAsync();

            foreach (var line in results)
            {
                if (line.EndsWith(":blocked", StringComparison.Ordinal)) continue;
                // An opaque answer reads as status 0 and no text — also nothing learned.
                var parts = line.Split(':', 5);
                var status = parts.Length > 2 ? parts[2] : "?";
                var text = parts.Length > 4 ? parts[4] : "";
                if (status == "0" && text.Length == 0) continue;
                if (text.Contains(marker, StringComparison.OrdinalIgnoreCase) || text.Contains("\"items\"", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"A page that isn't this site READ the {parts[0]} answer (HTTP {status}).");
                else
                    problems.Add($"A page that isn't this site got an answer it could read from the {parts[0]} (HTTP {status}), even if it held nothing private.");
            }
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    private static void Judge(string who, string? allowOrigin, string? allowCredentials, List<string> problems)
    {
        if (!string.IsNullOrEmpty(allowOrigin))
            problems.Add($"{who} answered Access-Control-Allow-Origin: {allowOrigin} — that origin may read the answer.");
        if (string.Equals(allowCredentials, "true", StringComparison.OrdinalIgnoreCase))
            problems.Add($"{who} answered Access-Control-Allow-Credentials: true.");
    }
}
