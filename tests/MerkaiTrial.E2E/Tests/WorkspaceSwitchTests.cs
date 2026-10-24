// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/WorkspaceSwitchTests.cs
//
// TEST 84 (round 102) —
//   Changing_the_active_workspace_does_not_leak_previous_workspace_data
//
// In this app a person belongs to one workspace; the one way to CHANGE the
// active workspace is the super admin's View As (Admin → View As), which
// re-issues the session for the chosen workspace. This test switches from
// workspace A to workspace B and looks for A everywhere it could linger:
//
//   • the header names B, not A;
//   • the leads list and search show B's lead and never A's;
//   • A's lead, opened by its address while in B, is not shown;
//   • every app page says Cache-Control: no-store (round 102), so the
//     browser keeps no copy of A's pages;
//   • pressing Back through the history — which runs through A's pages —
//     never shows A's lead again, because each step is asked of the
//     server, which answers for B;
//   • after leaving View As, Back still shows neither workspace's lead.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture]
public class WorkspaceSwitchTests : E2ETest
{
    [Test]
    public async Task Changing_the_active_workspace_does_not_leak_previous_workspace_data()
    {
        var marker = "Leak Probe " + World.Stamp + "-" + Random.Shared.Next(100, 999);
        var leadA = marker + " A";
        var leadB = marker + " B";
        var tenantA = await Db.TenantIdAsync(World.A.Name);
        var tenantB = await Db.TenantIdAsync(World.B.Name);

        Guid leadAId = default;
        await Step("Workspace A adds a lead, and workspace B adds one", async () =>
        {
            var pa = await SignedInAsync(World.A.Admin);
            leadAId = await Flows.CreateLeadAsync(pa, leadA, World.EmailFor("A", "leakprobe"), 1000m, World.A);
            var pb = await SignedInAsync(World.B.Admin);
            await Flows.CreateLeadAsync(pb, leadB, World.EmailFor("B", "leakprobe"), 2000m, World.B);
        });

        var super = await SignedInAsync(World.SuperAdmin);
        var leadAUrl = $"/Leads/Detail/{leadAId}";

        // ── In workspace A ───────────────────────────────────────────
        await Step("View As workspace A's admin", () =>
            StartViewAsAsync(super, tenantA, World.A.Admin));

        await Step("In A: the header names A, the list shows A's lead, and its page opens", async () =>
        {
            await ExpectWorkspaceNameAsync(super, World.A.Name, World.B.Name);

            await super.GotoAsync($"/Leads?SearchTerm={Uri.EscapeDataString(marker)}");
            var main = await MainTextAsync(super);
            Assert.That(main, Does.Contain(leadA), "Viewing as A, the leads search did not find A's lead.");
            Assert.That(main, Does.Not.Contain(leadB), "Viewing as A, the leads search showed B's lead.");

            var response = await super.GotoAsync(leadAUrl);
            Assert.That(await MainTextAsync(super), Does.Contain(leadA), "Viewing as A, A's lead page did not open.");
            ExpectNoStore(response, leadAUrl);
        });

        // ── Switch to workspace B ────────────────────────────────────
        await Step("Leave View As, then View As workspace B's admin", async () =>
        {
            await ExitViewAsAsync(super);
            await StartViewAsAsync(super, tenantB, World.B.Admin);
        });

        await Step("In B: the header names B, not A", () =>
            ExpectWorkspaceNameAsync(super, World.B.Name, World.A.Name));

        await Step("In B: the leads list and search show B's lead and never A's", async () =>
        {
            var response = await super.GotoAsync($"/Leads?SearchTerm={Uri.EscapeDataString(marker)}");
            var main = await MainTextAsync(super);
            Assert.That(main, Does.Contain(leadB), "Viewing as B, the leads search did not find B's lead.");
            Assert.That(main, Does.Not.Contain(leadA), "Viewing as B, the leads search still showed A's lead.");
            ExpectNoStore(response, "/Leads");

            await super.GotoAsync("/Leads");
            Assert.That(await MainTextAsync(super), Does.Not.Contain(leadA), "Viewing as B, the leads list showed A's lead.");
        });

        await Step("In B: A's lead opened by its address is not shown", async () =>
        {
            await super.GotoAsync(leadAUrl);
            Assert.That(await MainTextAsync(super), Does.Not.Contain(leadA),
                $"Viewing as B, {leadAUrl} showed workspace A's lead.");
        });

        await Step("In B: pressing Back through the history never shows A's lead", async () =>
        {
            // The history now runs back through A's lead page and A's
            // search. Each Back must be answered by the server — for B.
            for (var i = 0; i < 8; i++)
            {
                var before = super.Url;
                try
                {
                    await super.GoBackAsync(new PageGoBackOptions { WaitUntil = WaitUntilState.Commit, Timeout = E2ESettings.ActionTimeoutMs });
                }
                catch (PlaywrightException) { break; }      // nothing further back
                if (super.Url == before) break;
                await Ui.WaitForDocumentAsync(super);

                var text = await MainTextAsync(super);
                Assert.That(text, Does.Not.Contain(leadA),
                    $"Back #{i + 1} ({Ui.PathOf(super.Url)}) showed workspace A's lead while viewing as B — " +
                    "the browser served a kept copy of A's page.");
            }
        });

        // ── Leave View As ────────────────────────────────────────────
        await Step("After leaving View As, neither workspace's lead is reachable", async () =>
        {
            await super.GotoAsync("/Dashboard");      // back to the present before leaving
            await ExitViewAsAsync(super);

            await super.GotoAsync(leadAUrl);
            var text = await MainTextAsync(super);
            Assert.That(text, Does.Not.Contain(leadA), "After leaving View As, A's lead page still showed the lead.");
        });
    }

    // ── helpers ──────────────────────────────────────────────────────

    /// <summary>Admin → View As, with the workspace and person chosen — the page's own form, posted.</summary>
    private static async Task StartViewAsAsync(IPage page, Guid tenantId, TestUser asUser)
    {
        var userId = await Db.UserIdAsync(asUser.Email);
        await page.GotoAsync("/Admin/ViewAs");
        await Assertions.Expect(page.Locator("#viewAsForm")).ToHaveCountAsync(1);

        // The two drop-downs only fill these hidden inputs; the form itself
        // (with its anti-forgery token) is what goes to the server.
        await page.EvaluateAsync<bool>(
            @"args => {
                document.getElementById('tenantIdInput').value = args[0];
                document.getElementById('userIdInput').value = args[1];
                document.getElementById('viewAsForm').submit();
                return true;
              }", new[] { tenantId.ToString(), userId.ToString() });

        await Ui.WaitForAddressAsync(page, u => Ui.PathOf(u).StartsWith("/Dashboard", StringComparison.OrdinalIgnoreCase));
        await Assertions.Expect(ExitButton(page)).ToBeVisibleAsync();
    }

    /// <summary>
    /// The top bar's "Exit View As" — the layout's one sign of View As, shown
    /// only while it is on. (Shared/_ViewAsBanner.cshtml exists but the
    /// layout does not include it.)
    /// </summary>
    private static ILocator ExitButton(IPage page) =>
        page.Locator("form[action*='ViewAs'][action*='handler=Exit'] button[type=submit]").First;

    private static async Task ExitViewAsAsync(IPage page)
    {
        if (await ExitButton(page).CountAsync() == 0)
            await page.GotoAsync("/Dashboard");
        await ExitButton(page).ClickAsync();
        // Exit lands on the super admin's own area (/Admin/Plans).
        await Ui.WaitForAddressAsync(page, u => Ui.PathOf(u).StartsWith("/Admin", StringComparison.OrdinalIgnoreCase));
        await Assertions.Expect(page.Locator("form[action*='ViewAs'][action*='handler=Exit']")).ToHaveCountAsync(0);
    }

    private static async Task ExpectWorkspaceNameAsync(IPage page, string expected, string mustNotShow)
    {
        await page.GotoAsync("/Dashboard");
        var names = page.Locator(".lf-tenant-title, .lf-tenant-name");
        await Assertions.Expect(names.First).ToContainTextAsync(expected);
        var all = string.Join(" | ", await names.AllInnerTextsAsync());
        Assert.That(all, Does.Not.Contain(mustNotShow), $"The header still named the other workspace: {all}");
    }

    private static async Task<string> MainTextAsync(IPage page) =>
        await page.Locator("main").CountAsync() > 0
            ? await page.Locator("main").First.InnerTextAsync()
            : await page.Locator("body").InnerTextAsync();

    private static void ExpectNoStore(IResponse? response, string what)
    {
        Assert.That(response, Is.Not.Null, $"No response for {what}.");
        response!.Headers.TryGetValue("cache-control", out var cc);
        Assert.That(cc ?? "", Does.Contain("no-store"),
            $"{what} answered with Cache-Control \"{cc ?? "(none)"}\" — the browser may keep a copy of this workspace's page.");
    }
}
