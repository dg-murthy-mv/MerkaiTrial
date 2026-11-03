// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/ReportAccessTests.cs
//
// NEW (113). Who may open the reports — pages AND the API.
//
// Workspace A's people, with the starting roles as 113_ReportPermissions.sql
// sets them:
//   Vani  (Viewer)        no reports at all
//   Ravi  (Sales Rep)     Reports, not Finance Reports — the sales and lead
//                         reports (on his own deals), never the money ones
//   Mira  (Sales Manager) Reports and Finance Reports — everything
//
// For each: what the sidebar and the Reports page offer, what happens when
// the address is TYPED (the hole this round closes: before 113 every report
// opened for anyone signed in), and the same reports through the API.
//
// The API's full sweep (every endpoint, every role) is ApiAuthorizationTests;
// this is the readable version for the reports alone.
//
// Every wrong answer is collected and reported together.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Reports")]
public class ReportAccessTests : E2ETest
{
    private const string Finance = "Revenue & Finance";

    /// <summary>One sales report and one lead report, neither of them Advanced (so the plan doesn't matter).</summary>
    private static readonly string[] SalesPages = { "/Reports/Pipeline", "/Reports/LeadFunnel" };

    /// <summary>Every Revenue &amp; Finance report.</summary>
    private static readonly string[] FinancePages =
        { "/Reports/Revenue", "/Reports/Outstanding", "/Reports/QuotePerformance", "/Reports/InvoiceCollections" };

    private static readonly string[] SalesApi = { "pipeline-summary", "lead-funnel" };
    private static readonly string[] FinanceApi =
        { "revenue-by-period", "outstanding-invoices", "quote-performance", "invoice-collections" };

    [Test]
    public async Task Reports_open_only_for_the_people_allowed_to_see_them()
    {
        var a = World.A;
        var viewer = a.People["viewer"];
        var rep = a.People["rep"];
        var manager = a.People["manager"];
        var period = $"from={DateTime.UtcNow.AddMonths(-3):yyyy-MM-dd}&to={DateTime.UtcNow:yyyy-MM-dd}";
        var problems = new List<string>();

        // ── the roles first: without the SQL script nothing below can pass ──
        await Step("The Viewer role has no Reports (113_ReportPermissions.sql has been run on this database)", async () =>
        {
            var api = await Api.AsAsync(viewer);
            api.WaitWhenPaced = true;
            var r = await api.GetAsync($"/api/reports/pipeline-summary?{period}");
            Assert.That(r.Status, Is.EqualTo(403),
                "Vani (Viewer) can still call the Pipeline report. Run Sql\\113_ReportPermissions.sql on MerkaiTrial_E2E " +
                "(@Commit = 1), then run the tests again — new workspaces copy their roles from dbo.RoleTemplates. " +
                $"Answer: {r}");
        });

        // ── Viewer ───────────────────────────────────────────────────
        await Step("Vani (Viewer): no Reports in the menu; every report address refused; the API says 403", async () =>
        {
            var page = await SignedInAsync(viewer);
            await page.GotoAsync("/Dashboard");
            if (await page.Locator("a.lf-nav-item[href='/Reports']").CountAsync() > 0)
                problems.Add("viewer: the sidebar shows Reports");

            foreach (var path in new[] { "/Reports" }.Concat(SalesPages).Concat(FinancePages))
                await ExpectRefusedAsync(page, path, "viewer", problems);

            var api = await Api.AsAsync(viewer);
            api.WaitWhenPaced = true;
            foreach (var ep in SalesApi.Concat(FinanceApi))
                await ExpectStatusAsync(api, $"/api/reports/{ep}?{period}", 403, "viewer", problems);
        });

        // ── Sales Rep ────────────────────────────────────────────────
        await Step("Ravi (Sales Rep): sales and lead reports open; no Revenue & Finance in the menu or on the page; typed addresses refused; API 403 for those", async () =>
        {
            var page = await SignedInAsync(rep);
            await page.GotoAsync("/Reports");
            if (!Ui.PathOf(page.Url).StartsWith("/Reports", StringComparison.OrdinalIgnoreCase))
                problems.Add($"rep: /Reports did not open (landed on {Ui.PathOf(page.Url)})");
            else
            {
                if (await page.Locator("section[data-category='Sales Performance']").CountAsync() != 1)
                    problems.Add("rep: the Reports page has no Sales Performance section");
                if (await page.Locator($"section[data-category='{Finance}']").CountAsync() != 0)
                    problems.Add($"rep: the Reports page shows the {Finance} section");
                foreach (var path in FinancePages)
                    if (await page.Locator($".lf-subnav a[href='{path}']").CountAsync() != 0)
                        problems.Add($"rep: the sidebar links to {path}");
            }

            foreach (var path in SalesPages)
                await ExpectOpensAsync(page, path, "rep", problems);
            foreach (var path in FinancePages)
                await ExpectRefusedAsync(page, path, "rep", problems);

            var api = await Api.AsAsync(rep);
            api.WaitWhenPaced = true;
            foreach (var ep in SalesApi)
                await ExpectStatusAsync(api, $"/api/reports/{ep}?{period}", 200, "rep", problems);
            foreach (var ep in FinanceApi)
                await ExpectStatusAsync(api, $"/api/reports/{ep}?{period}", 403, "rep", problems);
        });

        // ── Sales Manager ────────────────────────────────────────────
        await Step("Mira (Sales Manager): every report opens, Revenue & Finance included", async () =>
        {
            var page = await SignedInAsync(manager);
            await page.GotoAsync("/Reports");
            if (await page.Locator($"section[data-category='{Finance}']").CountAsync() != 1)
                problems.Add($"manager: the Reports page has no {Finance} section");

            foreach (var path in SalesPages.Concat(FinancePages))
                await ExpectOpensAsync(page, path, "manager", problems);

            var api = await Api.AsAsync(manager);
            api.WaitWhenPaced = true;
            foreach (var ep in SalesApi.Concat(FinanceApi))
                await ExpectStatusAsync(api, $"/api/reports/{ep}?{period}", 200, "manager", problems);
        });

        if (problems.Count > 0)
            Assert.Fail($"{problems.Count} report access problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    // ── helpers ───────────────────────────────────────────────────────

    private static async Task ExpectRefusedAsync(IPage page, string path, string who, List<string> problems)
    {
        await page.GotoAsync(path);
        var landed = Ui.PathOf(page.Url);
        if (!landed.StartsWith("/AccessDenied", StringComparison.OrdinalIgnoreCase))
            problems.Add($"{who}: typing {path} opened {landed} (expected Access Denied)");
    }

    private static async Task ExpectOpensAsync(IPage page, string path, string who, List<string> problems)
    {
        await page.GotoAsync(path);
        var landed = Ui.PathOf(page.Url);
        if (!landed.StartsWith(path, StringComparison.OrdinalIgnoreCase))
            problems.Add($"{who}: {path} did not open (landed on {landed})");
        else if (await page.Locator("text=Failed to load report data").CountAsync() > 0)
            problems.Add($"{who}: {path} opened but says \"Failed to load report data\"");
    }

    private static async Task ExpectStatusAsync(ApiCaller api, string path, int expected, string who, List<string> problems)
    {
        var r = await api.GetAsync(path);
        if (r.Status != expected)
            problems.Add($"{who}: GET {path} answered {r.Status} (expected {expected}). {ApiResponse.Short(r.Body, 100)}");
    }
}
