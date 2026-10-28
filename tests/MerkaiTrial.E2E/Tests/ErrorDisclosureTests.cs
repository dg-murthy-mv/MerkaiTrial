// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/ErrorDisclosureTests.cs
//
// VUL-12 (round 105) — errors say nothing about the inside of the app.
//
//   Errors_show_no_stack_traces_sql_or_secrets
//
// run-e2e.ps1 starts both servers with Diagnostics:ShowErrorDetails=false
// (errors answered the way production answers them) and
// Diagnostics:FaultProbe=true, which maps GET /__e2e/fault: it throws an
// exception whose message holds a SQL statement, a connection string and a
// password. Then:
//
//   • the API's answer to that crash is a plain sentence and a reference
//     number — none of the SQL, the connection string, the password, a
//     stack trace or a .NET type name;
//   • the website's answer is its error page, likewise;
//   • the crash IS in the server's log (so the reference is useful);
//   • broken requests — a body that isn't JSON, a number sent as words, an
//     unknown id, an id that isn't an id, a refused token — get short
//     answers with no type names, no stack and no serializer text.
// =====================================================================

using System.Text;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class ErrorDisclosureTests : E2ETest
{
    private const string ProbePath = "/__e2e/fault";

    // Pieces of the probe's message, and the marks of a .NET error page.
    private static readonly string[] Secret =
    {
        "Pr0be-S3cret-105", "Password=", "probe-sql.internal", "MerkaiProbe", "SELECT PasswordHash", "dbo.Users",
    };

    // In an API error answer (JSON), any of these is the inside showing.
    private static readonly string[] Internals =
    {
        "   at ", "Exception", "MerkaiTrial.", "System.", "Microsoft.", "StackTrace", "stack trace",
        "SqlClient", "Invalid column", "Invalid object name", ".cs:line",
    };

    // In a web PAGE only the unmistakable ones: a page legitimately loads
    // "MerkaiTrial.Admin.Web.styles.css" and scripts that say "Exception".
    private static readonly string[] PageInternals =
    {
        "   at Merkai", "   at System.", "   at Microsoft.", ".cs:line", "SqlClient", "Invalid column",
        "Invalid object name", "InvalidOperationException", "NullReferenceException", "DbUpdateException",
        "SqlException", "Stack trace", "StackTrace",
    };

    [Test]
    public async Task Errors_show_no_stack_traces_sql_or_secrets()
    {
        var a = World.A;
        var tenantA = await Db.TenantIdAsync(a.Name);
        var apiA = await Api.AsAsync(a.Admin);
        var problems = new List<string>();

        await Step("The API's answer to a crash: a sentence and a reference, nothing from inside", async () =>
        {
            var r = await Api.Anonymous.GetAsync(ProbePath);
            if (r.Status == 404)
                Assert.Fail("GET /__e2e/fault is not there on the WebApi. Start the servers through run-e2e.ps1 " +
                            "(it turns on Diagnostics:FaultProbe for the test servers only).");

            if (r.Status != 500) problems.Add($"The API answered its crash with {r} (expected 500).");
            Check("The API's crash answer", r.Body, problems);

            if (!r.Body.Contains("traceId", StringComparison.OrdinalIgnoreCase))
                problems.Add("The API's crash answer carries no reference (traceId) for anyone to quote.");
        });

        await Step("The website's answer to a crash: its error page, nothing from inside", async () =>
        {
            using var web = new WebSession();
            var r = await web.GetAsync(ProbePath);
            if (r.Status == 404)
                Assert.Fail("GET /__e2e/fault is not there on the website. Start the servers through run-e2e.ps1.");

            if (r.Status < 500) problems.Add($"The website answered its crash with HTTP {r.Status} (expected 500 with the error page).");
            CheckPage("The website's error page", r.Body, problems);
        });

        await Step("…and the crash is in the servers' logs, where the reference leads", () =>
        {
            var logs = ServerLogs.ReadAll();
            if (!logs["webapi.out.log"].Contains("E2E fault probe", StringComparison.Ordinal)
                && !logs.GetValueOrDefault("webapi.err.log", "").Contains("E2E fault probe", StringComparison.Ordinal))
                problems.Add("The API's crash is not in webapi.out.log, so the reference number would lead nowhere.");
            return Task.CompletedTask;
        });

        await Step("Broken requests get short answers: no type names, no stack, no serializer text", async () =>
        {
            var cases = new List<(string What, Func<Task<ApiResponse>> Call)>
            {
                ("a body that is not JSON", () => apiA.SendAsync(HttpMethod.Post, "/api/leads",
                    content: new StringContent("{\"fullName\": \"Broken", Encoding.UTF8, "application/json"))),
                ("a number sent as words", () => apiA.PostAsync("/api/leads", new
                    { tenantId = tenantA, fullName = "Err Words", estimatedValue = "a lot", currency = "INR" })),
                ("a date that is not a date", () => apiA.PostAsync("/api/leads", new
                    { tenantId = tenantA, fullName = "Err Date", nextFollowUpUtc = "next Tuesday-ish", currency = "INR" })),
                ("an unknown lead id", () => apiA.GetAsync($"/api/leads/{Guid.NewGuid()}")),
                ("an id that is not an id", () => apiA.GetAsync("/api/leads/not-a-guid'--")),
                ("an unknown deal id, edited", () => apiA.PutAsync($"/api/deals/{Guid.NewGuid()}", new { title = "x" })),
                ("a refused token", () => HandMadeJwt.GetAsync("/api/leads?pageNumber=1&pageSize=1", "eyJhbGciOiJub25lIn0.e30.")),
                ("a list asked for an impossible page", () => apiA.GetAsync("/api/leads?pageNumber=2147483647&pageSize=2147483647")),
            };

            foreach (var (what, call) in cases)
            {
                var r = await call();
                if (r.Status >= 500) problems.Add($"The API answered {what} with a server error: {r}");
                if (r.IsSuccess) CheckSecretsOnly($"The API's answer to {what}", r.Body, problems);   // a list: people's text
                else Check($"The API's answer to {what}", r.Body, problems);
            }
        });

        await Step("The website's own refusals: an unknown record and a broken address", async () =>
        {
            using var web = await WebSession.SignedInAsync(a.Admin);
            foreach (var path in new[] { $"/Leads/Detail/{Guid.NewGuid()}", "/Leads/Detail/not-a-guid'--", $"/Pipeline/Detail/{Guid.NewGuid()}" })
            {
                var r = await web.GetAsync(path);
                if (r.Status >= 500) problems.Add($"The website answered {path} with HTTP {r.Status}.");
                CheckPage($"The website's answer to {path}", r.Body, problems);
            }
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    private static void Check(string what, string body, List<string> problems)
        => CheckAgainst(what, body, Internals, problems);

    private static void CheckPage(string what, string body, List<string> problems)
        => CheckAgainst(what, body, PageInternals, problems);

    private static void CheckSecretsOnly(string what, string body, List<string> problems)
        => CheckAgainst(what, body, Array.Empty<string>(), problems);

    private static void CheckAgainst(string what, string body, string[] internals, List<string> problems)
    {
        foreach (var s in Secret)
            if (body.Contains(s, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{what} shows '{s}' — part of the error's inside story.");

        foreach (var s in internals)
            if (body.Contains(s, StringComparison.Ordinal))
            {
                problems.Add($"{what} shows '{s.Trim()}': {Around(body, s)}");
                break;
            }
    }

    private static string Around(string body, string s)
    {
        var i = body.IndexOf(s, StringComparison.Ordinal);
        var start = Math.Max(0, i - 60);
        return "…" + body.Substring(start, Math.Min(160, body.Length - start)).Replace('\n', ' ') + "…";
    }
}
