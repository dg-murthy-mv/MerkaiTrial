// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/AdminCrossBrowserTests.cs
//
// NEW (121). The super admin console in the two engines other than
// Chromium:
//   firefox  on a desktop-sized window
//   webkit   (Safari, and every browser on an iPhone / iPad) on an
//            iPad-sized window
//
// In each: the console's main pages open with NO script errors; the
// workspace search and the Audit Log filters work; a row's Help menu opens
// (Bootstrap's dropdown); the permission grid on Starting Roles shows; and
// a confirmation (the browser's own confirm()) lets a form through — the
// workspace delete with a WRONG name, so the server refuses it and nothing
// is deleted.
//
// The engines are started only here and closed straight after (memory),
// as CrossBrowserTests does. Run only these:
//   run-e2e.ps1 -Filter "Category=CrossBrowser"
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("CrossBrowser"), Category("SuperAdmin")]
public class AdminCrossBrowserTests : E2ETest
{
    [TestCase("firefox")]
    [TestCase("webkit")]
    public async Task The_console_works_in(string engine)
    {
        var passed = false;
        try
        {
            await ConsoleAsync(engine);
            passed = true;
        }
        finally
        {
            await CloseSessionsNowAsync(passed);
            await GlobalSetup.CloseOtherBrowserAsync(engine);
        }
    }

    private async Task ConsoleAsync(string engine)
    {
        var browser = await Step($"Start {engine}", () => GlobalSetup.OtherBrowserAsync(engine));
        var size = engine == "webkit" ? new ViewportSize { Width = 820, Height = 1180 } : new ViewportSize { Width = 1366, Height = 900 };
        var page = await Step($"Sign in as the super admin in {engine}", () => SignedInAsync(World.SuperAdmin, size, browser));

        var tenantA = await Db.TenantIdAsync(World.A.Name);
        var tenantB = await Db.TenantIdAsync(World.B.Name);
        var scriptErrors = new List<string>();
        page.PageError += (_, error) => scriptErrors.Add($"{Ui.PathOf(page.Url)}: {error}");
        var problems = new List<string>();

        await Step("The main pages open", async () =>
        {
            foreach (var path in new[]
            {
                "/Admin", "/Admin/Tenants", $"/Admin/Tenants/Detail/{tenantA}", $"/Admin/Tenants/People/{tenantA}",
                "/Admin/Plans", "/Admin/Countries", "/Admin/Countries/Detail/IN?tab=tax", "/Admin/Verticals",
                "/Admin/StartingRoles", "/Admin/StartingRoles/Edit", "/Admin/Team", "/Admin/AuditLog",
                "/Admin/WhatsAppTemplates", "/Admin/ViewAs",
            })
            {
                var r = await page.GotoAsync(path);
                await Ui.WaitForDocumentAsync(page, 15_000, complete: true);
                if (r?.Status != 200) problems.Add($"{path} answered HTTP {r?.Status} in {engine}.");
            }
        });

        await Step("The workspace search finds workspace A by its rep's email", async () =>
        {
            await page.GotoAsync("/Admin/Tenants");
            await page.Locator("#tSearch").FillAsync(World.A["rep"].Email);
            await page.Locator("#tApply").ClickAsync();
            await Assertions.Expect(page.Locator($"#tenantsTable tr[data-tenant='{World.A.Name}']")).ToBeVisibleAsync();
        });

        await Step("The Audit Log's filters apply", async () =>
        {
            await page.GotoAsync("/Admin/AuditLog");
            await page.Locator("#fDays").SelectOptionAsync("365");
            await page.Locator("#fKind").SelectOptionAsync("changes");
            await page.Locator("#auditApply").ClickAsync();
            await Ui.WaitForAddressAsync(page, u => u.Contains("Kind=changes") && u.Contains("Days=365"));
            await Assertions.Expect(page.Locator("#platformAudit, #auditEmpty").First).ToBeVisibleAsync();
        });

        await Step("A row's Help menu opens on People", async () =>
        {
            await page.GotoAsync($"/Admin/Tenants/People/{tenantA}");
            var row = page.Locator($"#workspacePeople tr[data-person='{World.A["rep"].Email}']");
            await row.Locator("[data-bs-toggle=dropdown]").ClickAsync();
            await Assertions.Expect(row.Locator("button[data-action=SendReset]")).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
        });

        await Step("Starting Roles shows its permission grid", async () =>
        {
            await page.GotoAsync("/Admin/StartingRoles/Edit");
            if (await page.Locator("input[name='Permissions_leads_read']").CountAsync() != 1)
                problems.Add($"The permission grid did not show in {engine}.");
        });

        await Step("A confirmation lets the form through, and the server still refuses a wrong name", async () =>
        {
            await page.GotoAsync($"/Admin/Tenants/Detail/{tenantB}");
            await page.Locator("#confirmName").FillAsync("not the right name");
            await page.Locator("#deleteWorkspaceButton").ClickAsync();   // confirm() is accepted for us
            await Assertions.Expect(page.Locator(".alert-danger").Filter(new() { HasText = "Nothing was deleted" })).ToBeVisibleAsync();
        });

        if (scriptErrors.Count > 0) problems.Add($"Script errors in {engine}:\n    " + string.Join("\n    ", scriptErrors.Distinct().Take(15)));
        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }
}
