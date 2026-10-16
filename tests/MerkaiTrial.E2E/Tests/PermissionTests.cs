// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/PermissionTests.cs
//
// Roles do what they say. A hidden button is not a control, so each test
// checks BOTH that the button is gone AND that typing the address into
// the browser is refused.
//
// 088: A_sales_rep_cannot_open_workspace_settings — every settings page
// that decides how the workspace works, typed in by a rep.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Smoke")]
public class PermissionTests : E2ETest
{
    [Test]
    public async Task A_viewer_cannot_add_leads()
    {
        var page = await SignedInAsync(World.A["viewer"]);

        await Step("No Add lead button on the leads list", async () =>
        {
            await page.GotoAsync("/Leads");
            await Assertions.Expect(page.Locator("main a[href$='/Leads/Create']")).ToHaveCountAsync(0);
        });

        await Step("Typing /Leads/Create is refused", () => Ui.ExpectRefusedAsync(page, "/Leads/Create"));
    }

    [Test]
    public async Task A_sales_rep_cannot_manage_people()
    {
        var page = await SignedInAsync(World.A["rep"]);
        await Ui.ExpectRefusedAsync(page, "/Settings/Users");
        await Ui.ExpectRefusedAsync(page, "/Settings/Users/Create");
    }

    [Test]
    public async Task A_workspace_admin_cannot_open_the_super_admin_area()
    {
        var page = await SignedInAsync(World.A.Admin);
        await Ui.ExpectRefusedAsync(page, "/Admin/Tenants");
        await Ui.ExpectRefusedAsync(page, "/Admin/Users");
    }

    [Test]
    public async Task A_sales_rep_cannot_open_workspace_settings()
    {
        // The sales process, approval rules, who sees whose records, the
        // letterhead on quotes, stages, statuses, custom fields and roles.
        // Pipeline Rules moved to the Settings module in 024 precisely
        // because reps could read it.
        var paths = new[]
        {
            "/Settings/PipelineRules",
            "/Settings/QuoteApprovals",
            "/Settings/Visibility",
            "/Settings/CompanyProfile",
            "/Settings/Pipeline",
            "/Settings/LeadStatuses",
            "/Settings/CustomFields?entity=Lead",
            "/Settings/Roles",
        };

        var page = await SignedInAsync(World.A["rep"]);
        var opened = new List<string>();

        // Every page is tried, and all the ones that opened are reported
        // together — one run tells you the whole picture.
        // (Not Ui.ExpectRefusedAsync in a try/catch: NUnit records a failed
        // Assert even when the exception is caught.)
        foreach (var path in paths)
        {
            var response = await page.GotoAsync(path);
            var status = response?.Status ?? 0;
            var stayed = Ui.PathOf(page.Url).Equals(Ui.PathOf(E2ESettings.BaseUrl + path), StringComparison.OrdinalIgnoreCase);
            if (stayed && status is not (403 or 404)) opened.Add($"{path} (HTTP {status})");
        }

        Assert.That(opened.Count, Is.EqualTo(0),
            $"A sales rep could open: {string.Join(", ", opened)}");
    }

    [Test]
    public async Task A_sales_manager_can_add_leads()
    {
        // The other side of the viewer test: the button and the page are
        // there for a role that has the permission.
        var page = await SignedInAsync(World.A["manager"]);
        await page.GotoAsync("/Leads");
        await Assertions.Expect(page.Locator("main a[href$='/Leads/Create']").First).ToBeVisibleAsync();

        await page.GotoAsync("/Leads/Create");
        Assert.That(Ui.PathOf(page.Url), Does.EndWith("/Leads/Create"));
        await Assertions.Expect(page.Locator("#Input_FullName")).ToBeVisibleAsync();
    }
}
