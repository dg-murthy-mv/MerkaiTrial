// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/SecurityHeaderTests.cs
//
// VUL-22 (round 106) — what the browser is told about every answer.
//
//   Every_answer_carries_the_browser_security_headers
//
// THE WEBSITE — signed-in pages, the sign-in page, a static file, a
// customer link and an error answer all carry:
//   • X-Frame-Options: DENY and CSP frame-ancestors 'none' (no framing);
//   • X-Content-Type-Options: nosniff;
//   • Referrer-Policy — no-referrer on customer links (/q/, /i/),
//     strict-origin-when-cross-origin elsewhere;
//   • Permissions-Policy with camera, microphone and location off;
//   • a Content-Security-Policy (agreed in 106): scripts only from this
//     site and the named CDNs, no plugins, forms only to this site.
// THE API — every answer: nosniff, no framing, no referrer, and a policy
// that lets an API answer load nothing.
//
// AND FOR REAL, in a browser:
//   • a page on another origin that frames the dashboard is refused;
//   • the app's own pages run under the policy with NO violations — the
//     policy doesn't break the app.
// (HSTS is set outside Development only, so the test servers don't send
// it; that is unchanged.)
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class SecurityHeaderTests : E2ETest
{
    private static readonly string[] AppPages =
    {
        "/Dashboard", "/Leads", "/Leads/Create", "/Pipeline", "/Contacts", "/Companies", "/Products",
        "/Quotes", "/Invoices", "/Tasks", "/Inbox", "/Reports", "/Notifications",
        "/Settings/Users", "/Settings/Pipeline", "/Settings/CustomFields", "/Settings/LeadStatuses",
    };

    [Test]
    public async Task Every_answer_carries_the_browser_security_headers()
    {
        var a = World.A;
        var apiA = await Api.AsAsync(a.Admin);
        var problems = new List<string>();

        await Step("The website: pages, sign-in, a static file, a customer link and an error all carry the headers", async () =>
        {
            using var anon = new WebSession();
            using var web = await WebSession.SignedInAsync(a.Admin);

            var answers = new List<(string What, WebSession.Answer Answer, bool CustomerLink)>
            {
                ("the dashboard", await web.GetAsync("/Dashboard"), false),
                ("the leads list", await web.GetAsync("/Leads"), false),
                ("the sign-in page", await anon.GetAsync("/Account/Login"), false),
                ("a static file", await anon.GetAsync("/js/country-currency.js"), false),
                ("a customer quote link", await anon.GetAsync("/q/" + new string('Z', 40)), true),
                ("a customer invoice link", await anon.GetAsync("/i/" + new string('Z', 40)), true),
                ("a page that doesn't exist", await web.GetAsync("/No/Such/Page"), false),
            };

            foreach (var (what, r, customerLink) in answers)
            {
                Expect(problems, what, r.Header("X-Frame-Options"), "DENY");
                Expect(problems, what, r.Header("X-Content-Type-Options"), "nosniff");
                Expect(problems, what, r.Header("Referrer-Policy"), customerLink ? "no-referrer" : "strict-origin-when-cross-origin");

                var perms = r.Header("Permissions-Policy") ?? "";
                foreach (var feature in new[] { "camera=()", "microphone=()", "geolocation=()" })
                    if (!perms.Contains(feature, StringComparison.Ordinal))
                        problems.Add($"{Cap(what)}: Permissions-Policy doesn't switch off {feature.TrimEnd('(', ')', '=')} ('{perms}').");

                var csp = r.Header("Content-Security-Policy") ?? "";
                if (csp.Length == 0) { problems.Add($"{Cap(what)} has no Content-Security-Policy."); continue; }
                foreach (var part in new[] { "frame-ancestors 'none'", "object-src 'none'", "base-uri 'self'", "form-action 'self'" })
                    if (!csp.Contains(part, StringComparison.Ordinal))
                        problems.Add($"{Cap(what)}: the content policy lacks \"{part}\".");
                var scripts = Directive(csp, "script-src") ?? Directive(csp, "default-src") ?? "";
                if (scripts.Split(' ').Any(s => s is "*" or "https:" or "http:" or "data:"))
                    problems.Add($"{Cap(what)}: scripts may come from anywhere ('{scripts}').");
            }
        });

        await Step("The API: every answer — data, a refusal, not found, health — carries its headers", async () =>
        {
            var answers = new List<(string What, ApiResponse Answer)>
            {
                ("a list", await apiA.GetAsync("/api/leads?pageNumber=1&pageSize=1")),
                ("a refusal (no token)", await Api.Anonymous.GetAsync("/api/leads?pageNumber=1&pageSize=1")),
                ("not found", await apiA.GetAsync($"/api/leads/{Guid.NewGuid()}")),
                ("the health check", await Api.Anonymous.GetAsync("/health")),
            };

            foreach (var (what, r) in answers)
            {
                var who = "The API's " + what;
                Expect(problems, who, r.Header("X-Content-Type-Options"), "nosniff");
                Expect(problems, who, r.Header("X-Frame-Options"), "DENY");
                Expect(problems, who, r.Header("Referrer-Policy"), "no-referrer");
                var csp = r.Header("Content-Security-Policy") ?? "";
                if (!csp.Contains("default-src 'none'", StringComparison.Ordinal) || !csp.Contains("frame-ancestors 'none'", StringComparison.Ordinal))
                    problems.Add($"{who}: the content policy is '{csp}' (expected default-src 'none'; frame-ancestors 'none').");
            }
        });

        await Step("In a browser: a page on another origin that frames the dashboard is refused", async () =>
        {
            var page = await SignedInAsync(a.Admin);
            var evil = await page.Context.NewPageAsync();
            var console = new List<string>();
            evil.Console += (_, msg) => { lock (console) console.Add(msg.Text); };

            await evil.GotoAsync("about:blank");
            await evil.SetContentAsync($"<html><body><iframe id=\"f\" src=\"{E2ESettings.BaseUrl}/Dashboard\" width=\"800\" height=\"600\"></iframe></body></html>");
            await evil.WaitForTimeoutAsync(4000);

            string[] seen;
            lock (console) seen = console.ToArray();
            var refused = seen.Any(t => t.Contains("frame-ancestors", StringComparison.OrdinalIgnoreCase)
                                     || t.Contains("X-Frame-Options", StringComparison.OrdinalIgnoreCase)
                                     || t.Contains("Refused to display", StringComparison.OrdinalIgnoreCase)
                                     || t.Contains("Refused to frame", StringComparison.OrdinalIgnoreCase));
            if (!refused)
                problems.Add("A page on another origin framed the dashboard and the browser did not refuse it. Console: " +
                             (seen.Length == 0 ? "(nothing)" : string.Join(" | ", seen.Take(5))));
            await evil.CloseAsync();
        });

        await Step($"In a browser: {AppPages.Length} of the app's pages run under the policy with no violations", async () =>
        {
            var page = await SignedInAsync(a.Admin);
            var violations = new List<string>();
            page.Console += (_, msg) =>
            {
                var t = msg.Text;
                if (t.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase)
                    || t.Contains("Refused to load", StringComparison.OrdinalIgnoreCase)
                    || t.Contains("Refused to execute", StringComparison.OrdinalIgnoreCase)
                    || t.Contains("Refused to apply", StringComparison.OrdinalIgnoreCase)
                    || t.Contains("Refused to connect", StringComparison.OrdinalIgnoreCase))
                    lock (violations) violations.Add($"{Ui.PathOf(page.Url)}: {t}");
            };

            foreach (var path in AppPages)
            {
                try
                {
                    await page.GotoAsync(path);
                    await Ui.WaitForDocumentAsync(page);
                    await page.WaitForTimeoutAsync(400);
                }
                catch (Exception ex) when (ex is not AssertionException)
                {
                    TestContext.Progress.WriteLine($"    {path}: {ex.Message.Split('\n')[0]}");
                }
            }

            lock (violations)
                foreach (var v in violations.Distinct().Take(15))
                    problems.Add("Blocked by the content policy — the policy would break this page: " + v);
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    private static void Expect(List<string> problems, string what, string? actual, string expected)
    {
        if (!string.Equals(actual?.Trim(), expected, StringComparison.OrdinalIgnoreCase))
            problems.Add($"{Cap(what)}: expected '{expected}', got '{actual ?? "(none)"}'.");
    }

    private static string? Directive(string csp, string name)
        => csp.Split(';').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith(name + " ", StringComparison.Ordinal));

    private static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
