// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/PasswordResetTests.cs
//
// Forgot password, end to end (round 086). No email leaves the machine:
// the link is read from the queued email in the test database, which is
// exactly what the person would have clicked in their inbox.
//
// Uses the "resetme" account, so changing its password never breaks a
// test that signs in as someone else.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Smoke")]
public class PasswordResetTests : E2ETest
{
    [Test]
    public async Task Forgot_password_emails_a_link_that_sets_a_new_password()
    {
        var user = World.A["resetme"];
        var since = DateTime.UtcNow.AddSeconds(-1);
        var page = await NewPageAsync();

        await Step("Ask for a reset link", async () =>
        {
            await RequestResetAsync(page, user.Email);
        });

        var link = await Step("Find the reset email in the outbox", () =>
            Db.WaitForSetPasswordLinkAsync(user.Email, since, TimeSpan.FromSeconds(15)));

        Assert.That(link, Does.Contain("purpose=PasswordReset"), "The emailed link is not a reset link.");

        await Step("Choose a new password from the link", async () =>
        {
            await Ui.SetPasswordFromLinkAsync(page, link, E2ESettings.ResetPassword);
            user.Password = E2ESettings.ResetPassword;
        });

        await Step("Sign in with the new password", async () =>
        {
            await Ui.SignInAsync(page, user);
        });

        await Step("The link cannot be used twice", async () =>
        {
            var second = await NewPageAsync("reuse");
            await second.GotoAsync(Ui.Rebase(link));
            Assert.That(await second.Locator("#NewPassword").CountAsync(), Is.EqualTo(0),
                "A used reset link opened the set-password form again.");
        });

        await Step("A 'password changed' email was queued as well", async () =>
        {
            // The reset email, then the confirmation: two account emails.
            var count = await Db.CountAccountEmailsAsync(user.Email, since);
            Assert.That(count, Is.GreaterThanOrEqualTo(2),
                "Expected the reset email and the 'your password was changed' email.");
        });
    }

    [Test]
    public async Task Forgot_password_answers_the_same_for_an_unknown_address()
    {
        var nobody = World.EmailFor("A", "no-such-person");
        var since = DateTime.UtcNow.AddSeconds(-1);
        var page = await NewPageAsync();

        await RequestResetAsync(page, nobody);

        // Give a queued email time to appear if one were (wrongly) made.
        await page.WaitForTimeoutAsync(1500);
        Assert.That(await Db.CountAccountEmailsAsync(nobody, since), Is.EqualTo(0),
            "An email was queued for an address that has no account.");
    }

    /// <summary>Fills Forgot password and checks the neutral answer.</summary>
    private static async Task RequestResetAsync(IPage page, string email)
    {
        await page.GotoAsync("/Account/ForgotPassword");
        await page.Locator("#Email").FillAsync(email);
        await page.Locator("#forgotSubmit").ClickAsync();

        await Assertions.Expect(page.Locator("h1")).ToContainTextAsync("Check your email");
    }
}
