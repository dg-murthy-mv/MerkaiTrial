// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/SupportPeopleTests.cs
//
// NEW (118). What MadeeVision support may do for people.
//
// Admin → Tenants → a workspace → People (workspace A, with a person of
// this test's own):
//   • lists the workspace's people, its admin marked, and never a super admin
//   • Unlock clears a wrong-password lockout
//   • "Get a reset link" shows a link once, not emailed
//   • Make admin / Remove admin / Switch off / Switch on work, and each one
//     changes the person's SecurityStamp where access changed
//   • the only active admin can't lose admin or be switched off
//   • a new admin can be added by invite; a bad email is refused
//   • every change is in the workspace's own Activity Log, "by MadeeVision
//     support", and the workspace admin sees it there
//   • the old Admin → Users pages are gone
//
// Admin → MadeeVision Team:
//   • lists the super admins, "You" marked, with no Switch off for yourself
//   • a bad email is refused
//   • e2e-staff@madeevision.test is added the first time (later runs reuse
//     it), then switched on and off again — it is always left OFF, so it
//     can never sign in
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("SuperAdmin")]
public class SupportPeopleTests : E2ETest
{
    private const string StaffEmail = "e2e-staff@madeevision.test";

    [Test]
    public async Task Support_can_help_a_workspace_s_people_and_it_is_recorded()
    {
        var a = World.A;
        var tenantA = await Db.TenantIdAsync(a.Name);
        var since = DateTime.UtcNow.AddSeconds(-5);
        var stamp = DateTime.UtcNow.ToString("HHmmss");

        var person = await Step("A person of this test's own in workspace A", () =>
            WorldBuilder.AddPersonForTestAsync(a, "support" + stamp, "Sunita", Ui.RolePatterns.Rep));
        var personId = await Db.UserIdAsync(person.Email);
        var adminId = await Db.UserIdAsync(a.Admin.Email);

        var page = await SignedInAsync(World.SuperAdmin, new ViewportSize { Width = 1366, Height = 900 });
        var problems = new List<string>();
        ILocator Row(string email) => page.Locator($"#workspacePeople tr[data-person='{email}']");

        async Task ActAsync(string email, string action)
        {
            var row = Row(email);
            await row.Locator("[data-bs-toggle=dropdown]").ClickAsync();
            // Marked, so the wait below is for the NEXT page (the address
            // often stays the same).
            await page.EvaluateAsync("() => { window.__e2eOld = true; }");
            await row.Locator($"button[data-action='{action}']").ClickAsync();   // confirm() is accepted for us
            await page.WaitForFunctionAsync("() => window.__e2eOld !== true");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        }

        async Task<string> MessageAsync()
        {
            var alerts = page.Locator(".alert-success, .alert-danger");
            await Assertions.Expect(alerts.First).ToBeVisibleAsync();
            return string.Join(" | ", await alerts.AllInnerTextsAsync());
        }

        await Step("The People page lists the workspace's people, its admin marked, no super admin", async () =>
        {
            await page.GotoAsync($"/Admin/Tenants/Detail/{tenantA}");
            await page.Locator("#people-link").ClickAsync();
            await Ui.WaitForAddressAsync(page, u => Ui.PathOf(u).StartsWith("/Admin/Tenants/People/", StringComparison.OrdinalIgnoreCase));

            Assert.That(await Row(a.Admin.Email).CountAsync(), Is.EqualTo(1), "Workspace A's admin is not listed.");
            Assert.That(await Row(a.Admin.Email).Locator("[data-col=admin]").TextContentAsync(), Does.Contain("Workspace admin"));
            Assert.That(await Row(person.Email).CountAsync(), Is.EqualTo(1), "The test's person is not listed.");
            Assert.That(await Row(World.SuperAdmin.Email).CountAsync(), Is.EqualTo(0), "A super admin is listed among the workspace's people.");
        });

        await Step("Unlock clears a wrong-password lockout", async () =>
        {
            await Db.SetLockoutAsync(personId, DateTime.UtcNow.AddMinutes(30), 5);
            await page.ReloadAsync();
            Assert.That((await Row(person.Email).Locator("[data-col=status]").TextContentAsync() ?? "").Trim(), Is.EqualTo("Locked out"));

            await ActAsync(person.Email, "Unlock");
            var said = await MessageAsync();
            if (!said.Contains("unlocked", StringComparison.OrdinalIgnoreCase)) problems.Add($"Unlock said: {said}");
            if (await Db.ValueAsync("Users", "LockoutEndUtc", personId) != "") problems.Add("Unlock left LockoutEndUtc set.");
            if (await Db.ValueAsync("Users", "FailedLoginCount", personId) != "0") problems.Add("Unlock left FailedLoginCount above 0.");
        });

        await Step("\"Get a reset link\" shows a link once", async () =>
        {
            await ActAsync(person.Email, "CopyResetLink");
            await Assertions.Expect(page.Locator("#issuedLinkCard")).ToBeVisibleAsync();
            var link = await page.Locator("#issuedLink").InputValueAsync();
            if (!link.Contains("/Account/SetPassword", StringComparison.OrdinalIgnoreCase)) problems.Add($"The reset link looks wrong: {link}");
            if (!(await page.Locator("#issuedLinkCard").InnerTextAsync()).Contains("Not emailed")) problems.Add("The reset link card does not say it was not emailed.");
        });

        await Step("Make admin, then remove admin — the person's sessions end each time", async () =>
        {
            await page.GotoAsync($"/Admin/Tenants/People/{tenantA}");
            var stampBefore = await Db.ValueAsync("Users", "SecurityStamp", personId);

            await ActAsync(person.Email, "MakeAdmin");
            await MessageAsync();
            if (await Db.ValueAsync("Users", "IsTenantAdmin", personId) != "True") problems.Add("Make admin did not make them admin.");
            var stampAfter = await Db.ValueAsync("Users", "SecurityStamp", personId);
            if (stampAfter == stampBefore) problems.Add("Make admin did not end the person's sessions (SecurityStamp unchanged).");

            await ActAsync(person.Email, "RemoveAdmin");
            await MessageAsync();
            if (await Db.ValueAsync("Users", "IsTenantAdmin", personId) != "False") problems.Add("Remove admin did not take admin away.");
        });

        await Step("The only active admin can't lose admin or be switched off", async () =>
        {
            await ActAsync(a.Admin.Email, "RemoveAdmin");
            var said = await MessageAsync();
            if (!said.Contains("only active workspace admin", StringComparison.OrdinalIgnoreCase))
                problems.Add($"Removing admin from the only admin was not refused properly. The page said: {said}");
            if (await Db.ValueAsync("Users", "IsTenantAdmin", adminId) != "True") problems.Add("THE ONLY ADMIN LOST ADMIN.");

            await ActAsync(a.Admin.Email, "SwitchOff");
            said = await MessageAsync();
            if (!said.Contains("only active workspace admin", StringComparison.OrdinalIgnoreCase))
                problems.Add($"Switching off the only admin was not refused properly. The page said: {said}");
            if (await Db.ValueAsync("Users", "IsActive", adminId) != "True") problems.Add("THE ONLY ADMIN WAS SWITCHED OFF.");
        });

        await Step("Switch off, then on", async () =>
        {
            await ActAsync(person.Email, "SwitchOff");
            await MessageAsync();
            if (await Db.ValueAsync("Users", "IsActive", personId) != "False") problems.Add("Switch off did not switch them off.");
            Assert.That((await Row(person.Email).Locator("[data-col=status]").TextContentAsync() ?? "").Trim(), Is.EqualTo("Switched off"));

            await ActAsync(person.Email, "SwitchOn");
            await MessageAsync();
            if (await Db.ValueAsync("Users", "IsActive", personId) != "True") problems.Add("Switch on did not switch them back on.");
        });

        await Step("A new admin by invite: a bad email is refused, a good one is added", async () =>
        {
            await page.Locator("#addAdminCard summary").ClickAsync();
            await page.Locator("#NewAdmin_FirstName").FillAsync("Neha");
            await page.Locator("#NewAdmin_Email").FillAsync("not-an-email@nowhere");
            await page.Locator("#addAdminButton").ClickAsync();
            await Assertions.Expect(page.Locator("#addAdminError")).ToBeVisibleAsync();

            var email = World.EmailFor(a.Key, "newadmin" + stamp);
            await page.Locator("#NewAdmin_FirstName").FillAsync("Neha");
            await page.Locator("#NewAdmin_Email").FillAsync(email);
            await page.Locator("#addAdminButton").ClickAsync();

            await Assertions.Expect(page.Locator("#issuedLinkCard")).ToBeVisibleAsync();
            Assert.That(await page.Locator("#issuedLinkCard").GetAttributeAsync("data-kind"), Is.EqualTo("invite"));
            var row = Row(email);
            Assert.That(await row.CountAsync(), Is.EqualTo(1), "The new admin is not in the list.");
            Assert.That(await row.Locator("[data-col=admin]").TextContentAsync(), Does.Contain("Workspace admin"));
            Assert.That((await row.Locator("[data-col=status]").TextContentAsync() ?? "").Trim(), Is.EqualTo("Invited"));
        });

        await Step("Every change is in workspace A's Activity Log, \"by MadeeVision support\"", async () =>
        {
            foreach (var action in new[] { "AccountUnlocked", "ResetIssued", "PermissionChanged", "InviteIssued" })
            {
                var rows = await Db.AuditAsync(action, since, tenantA);
                if (rows.Count == 0) { problems.Add($"No {action} row in workspace A's Activity Log."); continue; }
                if (!rows.Any(r => (r.Data ?? "").Contains("MadeeVision support")))
                    problems.Add($"The {action} row does not say MadeeVision support did it: {rows[0].Data}");
            }

            var admin = await SignedInAsync(a.Admin);
            await admin.GotoAsync("/Settings/AuditLogs");
            var text = await admin.Locator("body").InnerTextAsync();
            if (!text.Contains("by MadeeVision support", StringComparison.OrdinalIgnoreCase))
                problems.Add("Workspace A's admin does not see \"by MadeeVision support\" in their Activity Log.");
        });

        await Step("The old Admin → Users pages are gone", async () =>
        {
            var r = await page.GotoAsync("/Admin/Users");
            if (r is not null && r.Status == 200 && Ui.PathOf(page.Url).StartsWith("/Admin/Users", StringComparison.OrdinalIgnoreCase))
                problems.Add("/Admin/Users still opens. Delete the Pages/Admin/Users folder (SETUP.md).");
            // 119 — the menu is checked on another page: the old Users page's
            // own buttons link to /Admin/Users, which said "menu" wrongly.
            await page.GotoAsync("/Admin/Tenants");
            if (await page.Locator("a[href='/Admin/Users']").CountAsync() > 0)
                problems.Add("The menu still links to /Admin/Users.");
        });

        await Step("A workspace admin can't open the People page", async () =>
        {
            var admin = await SignedInAsync(a.Admin);
            await Ui.ExpectRefusedAsync(admin, $"/Admin/Tenants/People/{tenantA}");
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    [Test]
    public async Task The_MadeeVision_team_is_managed_by_invite_and_never_left_without_an_admin()
    {
        var page = await SignedInAsync(World.SuperAdmin, new ViewportSize { Width = 1366, Height = 900 });
        var problems = new List<string>();
        ILocator Row(string email) => page.Locator($"#teamMembers tr[data-member='{email}']");

        async Task ActAsync(string email, string action)
        {
            var row = Row(email);
            await row.Locator("[data-bs-toggle=dropdown]").ClickAsync();
            // Marked, so the wait below is for the NEXT page (the address
            // often stays the same).
            await page.EvaluateAsync("() => { window.__e2eOld = true; }");
            await row.Locator($"button[data-action='{action}']").ClickAsync();   // confirm() is accepted for us
            await page.WaitForFunctionAsync("() => window.__e2eOld !== true");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        }

        await Step("The team page lists the super admins, you marked, with no Switch off for yourself", async () =>
        {
            await page.GotoAsync("/Admin/Team");
            var me = Row(World.SuperAdmin.Email);
            Assert.That(await me.CountAsync(), Is.EqualTo(1), "You are not on the team list.");
            Assert.That(await me.InnerTextAsync(), Does.Contain("You"));
            Assert.That(await me.Locator("button[data-action='SwitchOff']").CountAsync(), Is.EqualTo(0),
                "You are offered Switch off for yourself.");
            Assert.That(await page.Locator("a[href='/Admin/Team']").CountAsync(), Is.GreaterThan(0), "No MadeeVision Team link in the menu.");
        });

        await Step("A bad email is refused", async () =>
        {
            await page.Locator("#NewMember_FirstName").FillAsync("Bad");
            await page.Locator("#NewMember_Email").FillAsync("bad@nowhere");
            await page.Locator("#addMemberButton").ClickAsync();
            await Assertions.Expect(page.Locator("#addMemberError")).ToBeVisibleAsync();
        });

        await Step($"{StaffEmail}: added the first time, then switched on and off — left OFF", async () =>
        {
            await page.GotoAsync("/Admin/Team");
            if (await Row(StaffEmail).CountAsync() == 0)
            {
                await page.Locator("#NewMember_FirstName").FillAsync("E2E");
                await page.Locator("#NewMember_LastName").FillAsync("Staff");
                await page.Locator("#NewMember_Email").FillAsync(StaffEmail);
                await page.Locator("#addMemberButton").ClickAsync();
                await Assertions.Expect(page.Locator("#issuedLinkCard")).ToBeVisibleAsync();
                if (await Db.ValueAsync("Users", "IsSuperAdmin", await Db.UserIdAsync(StaffEmail)) != "True")
                    problems.Add("The new member of staff is not a super admin.");
            }

            var staffId = await Db.UserIdAsync(StaffEmail);
            if (await Db.ValueAsync("Users", "IsActive", staffId) == "False")
            {
                await ActAsync(StaffEmail, "SwitchOn");
                if (await Db.ValueAsync("Users", "IsActive", staffId) != "True") problems.Add("Switch on did not work.");
            }

            await page.GotoAsync("/Admin/Team");
            await ActAsync(StaffEmail, "SwitchOff");
            Assert.That(await Db.ValueAsync("Users", "IsActive", staffId), Is.EqualTo("False"),
                $"{StaffEmail} could not be switched off — it must not be left on.");
            Assert.That((await Row(StaffEmail).Locator("[data-col=status]").TextContentAsync() ?? "").Trim(), Is.EqualTo("Switched off"));
        });

        await Step("Switching yourself off is refused, even sent by hand", async () =>
        {
            var token = await PageHttp.FormTokenAsync(page);
            var myId = await Db.UserIdAsync(World.SuperAdmin.Email);
            await PageHttp.FetchAsync(page, "POST", "/Admin/Team?handler=SwitchOff", new[]
            {
                KeyValuePair.Create("__RequestVerificationToken", token),
                KeyValuePair.Create("userId", myId.ToString()),
            });
            if (await Db.ValueAsync("Users", "IsActive", myId) != "True")
                problems.Add("THE SUPER ADMIN SWITCHED THEMSELVES OFF.");
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }
}
