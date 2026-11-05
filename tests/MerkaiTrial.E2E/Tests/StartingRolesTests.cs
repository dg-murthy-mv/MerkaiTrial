// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/StartingRolesTests.cs
//
// NEW (117). The super admin area, tidied:
//
//   Starting Roles (replaces the old Admin → Roles page)
//     • The page lists the starting roles and the system roles (read only).
//     • A starting role can be added and changed from the page, with its
//       permission ticks kept. The test's own role, e2e_check_role, is left
//       "Not given", so no workspace ever gets it; it is reused every run
//       (starting roles cannot be deleted).
//     • The API refuses a workspace admin (403), a key that breaks the rules
//       (400), and a key that is already taken (400).
//
//   Tax rates now live on each country
//     • /Admin/TaxRates forwards to Countries.
//     • India's page opens on its Tax tab; a rate can be added there and
//       deleted again, and the page comes back on the Tax tab each time.
//       (117-update1: the test database may hold no standard rates for
//       India, so the test no longer expects some to be there already.)
//
// 117-update1: the "saved" message is shown once, by the layout — the
// page showed its own copy too, so there were two.
//
// Every wrong answer is collected and reported together.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("SuperAdmin")]
public class StartingRolesTests : E2ETest
{
    private const string TestKey = "e2e_check_role";

    [Test]
    public async Task Starting_roles_can_be_added_and_changed_by_the_super_admin_only()
    {
        var page = await SignedInAsync(World.SuperAdmin, new ViewportSize { Width = 1366, Height = 900 });
        var stamp = DateTime.UtcNow.ToString("HHmmss");
        var displayName = $"E2E Check Role {stamp}";

        await Step("The page lists the starting roles and the system roles", async () =>
        {
            await page.GotoAsync("/Admin/StartingRoles");
            Assert.That(Ui.PathOf(page.Url), Does.StartWith("/Admin/StartingRoles"), "The Starting Roles page did not open.");
            Assert.That(await page.Locator("#startingRoles tbody tr").CountAsync(), Is.GreaterThan(0),
                "No starting roles listed. New workspaces copy their roles from dbo.RoleTemplates — is it empty?");
            Assert.That(await page.Locator("#systemRoles [data-system-role]").CountAsync(), Is.GreaterThan(0),
                "No system roles shown.");
        });

        await Step("The old Roles item is gone from the menu; Starting Roles is there", async () =>
        {
            Assert.That(await page.Locator("a[href='/Admin/Roles']").CountAsync(), Is.EqualTo(0), "The menu still links to /Admin/Roles.");
            Assert.That(await page.Locator("a[href='/Admin/TaxRates']").CountAsync(), Is.EqualTo(0), "The menu still links to /Admin/TaxRates.");
            Assert.That(await page.Locator("a[href='/Admin/StartingRoles'], a[href='/Admin/StartingRoles/Index']").CountAsync(),
                Is.GreaterThan(0), "No link to Starting Roles.");
        });

        await Step($"Add {TestKey} (or change it, if an earlier run added it) — left 'Not given'", async () =>
        {
            var row = page.Locator($"tr[data-role='{TestKey}']");
            if (await row.CountAsync() == 0)
            {
                await page.Locator("#addStartingRole").ClickAsync();
                await page.Locator("#Input_Name").FillAsync(TestKey);
            }
            else
            {
                await row.GetByRole(AriaRole.Link, new() { Name = "Edit" }).ClickAsync();
                Assert.That(await page.Locator("#Input_Name").CountAsync(), Is.EqualTo(0),
                    "The key can be typed over when editing; it must be fixed once made.");
            }

            await page.Locator("#Input_DisplayName").FillAsync(displayName);
            await page.Locator("#Input_Description").FillAsync("Made by the E2E tests. Never given to a workspace.");
            await page.Locator("#Input_IsActive").SetCheckedAsync(false);
            await page.Locator("input[name='Permissions_leads_read']").SetCheckedAsync(true);
            await page.Locator("input[name='Permissions_leads_delete']").SetCheckedAsync(false);
            await page.Locator("#saveStartingRole").ClickAsync();

            var saved = page.Locator(".alert-success").Filter(new() { HasText = "Starting role" });
            await Assertions.Expect(saved).ToHaveCountAsync(1);   // once, not twice
            row = page.Locator($"tr[data-role='{TestKey}']");
            Assert.That(await row.CountAsync(), Is.EqualTo(1), $"{TestKey} is not in the list after saving.");
            Assert.That(await row.TextContentAsync(), Does.Contain(displayName), "The new name was not saved.");
            Assert.That((await row.Locator("[data-col=status]").TextContentAsync() ?? "").Trim(), Is.EqualTo("Not given"),
                "The test role must not be given to new workspaces.");
        });

        await Step("Its ticks are kept: Leads read on, Leads delete off", async () =>
        {
            await page.Locator($"tr[data-role='{TestKey}']").GetByRole(AriaRole.Link, new() { Name = "Edit" }).ClickAsync();
            Assert.That(await page.Locator("input[name='Permissions_leads_read']").IsCheckedAsync(), Is.True, "Leads read was not kept.");
            Assert.That(await page.Locator("input[name='Permissions_leads_delete']").IsCheckedAsync(), Is.False, "Leads delete came on by itself.");
            Assert.That(await page.Locator("#Input_IsActive").IsCheckedAsync(), Is.False, "'New workspaces get this role' came on by itself.");
        });

        await Step("An empty name is refused on the page, and nothing changes", async () =>
        {
            await page.Locator("#Input_DisplayName").FillAsync("");
            await page.Locator("#saveStartingRole").ClickAsync();
            Assert.That(Ui.PathOf(page.Url), Does.Contain("/Admin/StartingRoles/Edit"), "An empty name was accepted.");
            await page.GotoAsync("/Admin/StartingRoles");
            Assert.That(await page.Locator($"tr[data-role='{TestKey}']").TextContentAsync(), Does.Contain(displayName),
                "The name changed after a refused save.");
        });

        var problems = new List<string>();

        await Step("The API: only the super admin, and only keys that follow the rules", async () =>
        {
            var super = await Api.AsAsync(World.SuperAdmin);
            super.WaitWhenPaced = true;
            var workspaceAdmin = await Api.AsAsync(World.A.Admin);
            workspaceAdmin.WaitWhenPaced = true;

            var list = await super.GetAsync("/api/admin/role-templates");
            if (list.Status != 200) problems.Add($"Super admin, list: expected 200, got {list}");

            var refusedList = await workspaceAdmin.GetAsync("/api/admin/role-templates");
            if (refusedList.Status != 403) problems.Add($"Workspace admin, list: expected 403, got {refusedList}");

            var body = new { name = "e2e_never_made", displayName = "Never made", permissions = "{}", sortOrder = 999, isActive = false };
            var refusedAdd = await workspaceAdmin.PostAsync("/api/admin/role-templates", body);
            if (refusedAdd.Status != 403) problems.Add($"Workspace admin, add: expected 403, got {refusedAdd}");

            var badKey = await super.PostAsync("/api/admin/role-templates",
                new { name = "Bad Key!", displayName = "Bad", permissions = "{}", sortOrder = 999, isActive = false });
            if (badKey.Status != 400) problems.Add($"A key with spaces and capitals: expected 400, got {badKey}");

            var taken = await super.PostAsync("/api/admin/role-templates",
                new { name = TestKey, displayName = "Again", permissions = "{}", sortOrder = 999, isActive = false });
            if (taken.Status != 400) problems.Add($"A key already taken: expected 400, got {taken}");

            var unknownModule = await super.PostAsync("/api/admin/role-templates",
                new { name = "e2e_bad_module", displayName = "Bad module", permissions = "{\"NoSuchModule\":[\"read\"]}", sortOrder = 999, isActive = false });
            if (unknownModule.Status != 400) problems.Add($"A permission for a module that does not exist: expected 400, got {unknownModule}");
        });

        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    }

    [Test]
    public async Task Tax_rates_are_managed_on_each_country()
    {
        var page = await SignedInAsync(World.SuperAdmin, new ViewportSize { Width = 1366, Height = 900 });
        var rateName = $"E2E check rate {DateTime.UtcNow:HHmmss}";

        await Step("/Admin/TaxRates forwards to Countries", async () =>
        {
            await page.GotoAsync("/Admin/TaxRates");
            Assert.That(Ui.PathOf(page.Url), Does.StartWith("/Admin/Countries"), $"Landed on {page.Url} instead of Countries.");
        });

        await Step("India's page opens on its Tax tab", async () =>
        {
            await page.GotoAsync("/Admin/Countries/Detail/IN?tab=tax");
            await Assertions.Expect(page.Locator("#countryTaxRates")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#addTaxRate")).ToBeVisibleAsync();
        });

        await Step("A rate added from there comes back on India's Tax tab", async () =>
        {
            await page.Locator("#addTaxRate").ClickAsync();
            Assert.That(await page.Locator("#Input_CountryCode").InputValueAsync(), Is.EqualTo("IN"),
                "The country was not filled in from the country page.");
            await page.Locator("#Input_Name").FillAsync(rateName);
            await page.Locator("#Input_Rate").FillAsync("1.5");
            // 117-update2: Tax Type starts on "-- Select Type --" and is required;
            // pick the first real type (whatever the database lists first).
            await page.Locator("#Input_TaxType").SelectOptionAsync(new SelectOptionValue { Index = 1 });
            await page.GetByRole(AriaRole.Button, new() { Name = "Create Tax Rate" }).ClickAsync();

            try
            {
                await Ui.WaitForAddressAsync(page,
                    u => Ui.PathOf(u).StartsWith("/Admin/Countries/Detail/IN", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
                var said = string.Join(" | ", await page.Locator(".field-validation-error, .validation-summary-errors li, .alert-danger").AllInnerTextsAsync());
                Assert.Fail($"After saving, still on {page.Url}. The page said: {(said.Length > 0 ? said : "(nothing)")}");
            }
            var row = page.Locator("#countryTaxRates tr[data-rate]").Filter(new() { HasText = rateName });
            await Assertions.Expect(row).ToBeVisibleAsync();
            Assert.That((await row.Locator("[data-col=rate]").TextContentAsync() ?? "").Trim(), Is.EqualTo("1.5%"));
        });

        await Step("…and deleted from there too", async () =>
        {
            var row = page.Locator("#countryTaxRates tr[data-rate]").Filter(new() { HasText = rateName });
            await row.GetByRole(AriaRole.Button, new() { Name = $"Delete {rateName}" }).ClickAsync();   // the confirm() is accepted for us
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            Assert.That(Ui.PathOf(page.Url), Does.StartWith("/Admin/Countries/Detail/IN"), $"After deleting, landed on {page.Url}.");
            await Assertions.Expect(page.Locator("#countryTaxRates tr[data-rate]").Filter(new() { HasText = rateName })).ToHaveCountAsync(0);
        });
    }
}
