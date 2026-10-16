// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/InviteTests.cs
//
// A workspace admin adds a colleague; the colleague's invite is queued as
// an email AND shown once on screen; the link sets a password and works
// only once. Runs in workspace B so it never adds to workspace A's head
// count.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Smoke")]
public class InviteTests : E2ETest
{
    [Test]
    public async Task An_admin_invites_a_colleague_who_can_then_sign_in()
    {
        var since = DateTime.UtcNow.AddSeconds(-1);
        var invitee = new TestUser("invitee", World.EmailFor("B", "invitee" + Random.Shared.Next(1000, 9999)),
            E2ESettings.Password, "Ishaan", "E2E");

        var admin = await SignedInAsync(World.B.Admin);

        var link = await Step("Add the colleague in Settings → Users", async () =>
        {
            await admin.GotoAsync("/Settings/Users/Create");
            await admin.Locator("#Input_FirstName").FillAsync(invitee.FirstName);
            await admin.Locator("#Input_LastName").FillAsync(invitee.LastName);
            await admin.Locator("#Input_Email").FillAsync(invitee.Email);
            await Ui.TickRoleAsync(admin, Ui.RolePatterns.Viewer);
            await admin.GetByRole(AriaRole.Button, new() { Name = "Add person" }).ClickAsync();

            var box = admin.Locator("#inviteUrl");
            await Assertions.Expect(box).ToBeVisibleAsync();
            return await box.InputValueAsync();
        });

        await Step("The invite was queued as an email too", async () =>
        {
            var emailed = await Db.WaitForSetPasswordLinkAsync(invitee.Email, since, TimeSpan.FromSeconds(10));
            Assert.That(emailed, Does.Contain("purpose=Invite"));
        });

        var person = await NewPageAsync("invitee");

        await Step("The colleague opens the link and chooses a password", () =>
            Ui.SetPasswordFromLinkAsync(person, link, invitee.Password));

        await Step("…and signs in", () => Ui.SignInAsync(person, invitee));

        await Step("The link does not work a second time", async () =>
        {
            var again = await NewPageAsync("reuse");
            await again.GotoAsync(Ui.Rebase(link));
            Assert.That(await again.Locator("#NewPassword").CountAsync(), Is.EqualTo(0),
                "A used invite link opened the set-password form again.");
        });
    }
}
