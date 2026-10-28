// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/PasswordResetLinkTests.cs
//
// VUL-08 and VUL-09 (round 104) — the password reset link.
//
//   VUL-08  A_reset_link_works_once
//   VUL-09  An_expired_or_altered_reset_link_is_refused
//
// Each test uses a person of its own (added through Settings → Users),
// so no other test's sign-in changes. Every reset request comes from a
// browser session of its own (its own network address), so the per-address
// limits of VUL-10 never get in the way; a person still gets at most three
// reset emails an hour, and each test here asks for three or fewer.
//
// VUL-08
//   • asking again makes the earlier link stop working;
//   • a link sets a password once — the second visit shows no form;
//   • the same link sent twice AT THE SAME MOMENT sets one password, not
//     two (round 104: the link was marked used after the save, so both
//     went through and the second one won);
//   • setting a password signs the person out of every open session.
//
// VUL-09
//   • the link lasts two hours (its stored expiry);
//   • once that passes it opens no form, and posting the form to it anyway
//     changes nothing — the old password still works;
//   • a link altered to claim another purpose (an invite) is refused, and
//     the genuine link still works afterwards.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class PasswordResetLinkTests : E2ETest
{
    private const string BadLink = "invalid or has expired";

    // ================================================================
    // VUL-08
    // ================================================================

    [Test]
    public async Task A_reset_link_works_once()
    {
        var m = Random.Shared.Next(10000, 99999).ToString();
        var person = await Step("A person of this test's own", () =>
            WorldBuilder.AddPersonForTestAsync(World.A, "reuse" + m, "Rita", Ui.RolePatterns.Viewer));

        try
        {
            var open = await SignedInAsync(person);

            var first = await Step("Ask for a reset link, then ask again", async () =>
            {
                var l1 = await RequestLinkAsync(person.Email);
                var l2 = await RequestLinkAsync(person.Email);
                Assert.That(l2, Is.Not.EqualTo(l1), "Asking again sent the same link.");
                return (l1, l2);
            });

            await Step("The earlier link no longer opens the form", () => ExpectRefusedAsync(first.l1, "the earlier link, after a newer one was sent"));

            const string newPassword = "Orange-River-Falcon-31!";
            await Step("The newer link sets a new password", async () =>
            {
                var p = await NewPageAsync("set");
                await Ui.SetPasswordFromLinkAsync(p, first.l2, newPassword);
                person.Password = newPassword;
                Assert.That(await SignsInAsync(person.Email, newPassword), Is.True, "The new password doesn't work.");
            });

            await Step("The session that was open before is signed out", async () =>
            {
                await open.GotoAsync("/Leads");
                Assert.That(Ui.PathOf(open.Url), Does.StartWith("/Account/Login"),
                    $"A session from before the reset still opened {Ui.PathOf(open.Url)}.");
            });

            await Step("The used link opens no form a second time", () => ExpectRefusedAsync(first.l2, "the link, a second time"));

            await Step("One link sent twice at the same moment sets ONE password", async () =>
            {
                var l3 = await RequestLinkAsync(person.Email);
                var p1 = await NewPageAsync("race-1");
                var p2 = await NewPageAsync("race-2");
                const string pw1 = "Purple-Mountain-Comet-58!", pw2 = "Silver-Meadow-Harbor-64!";

                foreach (var (p, pw) in new[] { (p1, pw1), (p2, pw2) })
                {
                    await p.GotoAsync(Ui.Rebase(l3));
                    await p.Locator("#NewPassword").FillAsync(pw);
                    await p.Locator("#ConfirmPassword").FillAsync(pw);
                }

                await Task.WhenAll(
                    p1.Locator("form:has(#NewPassword) button[type=submit]").ClickAsync(),
                    p2.Locator("form:has(#NewPassword) button[type=submit]").ClickAsync());
                await Ui.WaitForDocumentAsync(p1);
                await Ui.WaitForDocumentAsync(p2);
                await p1.WaitForTimeoutAsync(1500);

                var won = new[] { p1, p2 }.Select(p => Ui.LocalPart(p.Url).Contains("passwordSet=true", StringComparison.OrdinalIgnoreCase)).ToArray();
                Assert.That(won.Count(w => w), Is.EqualTo(1),
                    $"Expected exactly one of the two to set the password; they ended on {Ui.LocalPart(p1.Url)} and {Ui.LocalPart(p2.Url)}.");

                var loserPage = won[0] ? p2 : p1;
                Assert.That(await loserPage.Locator("body").InnerTextAsync(), Does.Contain(BadLink),
                    "The one that lost wasn't told the link no longer works.");

                var (winner, loser) = won[0] ? (pw1, pw2) : (pw2, pw1);
                Assert.That(await SignsInAsync(person.Email, winner), Is.True, "The winning password doesn't work.");
                Assert.That(await SignsInAsync(person.Email, loser), Is.False, "The losing password works too — the link was used twice.");
                person.Password = winner;

                var stored = await Db.LinkAsync(l3);
                Assert.That(stored?.ConsumedAtUtc, Is.Not.Null, "The link is not recorded as used.");
            });
        }
        finally
        {
            await RemoveAsync(person);
        }
    }

    // ================================================================
    // VUL-09
    // ================================================================

    [Test]
    public async Task An_expired_or_altered_reset_link_is_refused()
    {
        var m = Random.Shared.Next(10000, 99999).ToString();
        var person = await Step("A person of this test's own", () =>
            WorldBuilder.AddPersonForTestAsync(World.A, "expiry" + m, "Esha", Ui.RolePatterns.Viewer));

        try
        {
            var link = await Step("Ask for a reset link", () => RequestLinkAsync(person.Email));

            await Step("It lasts two hours", async () =>
            {
                var stored = await Db.LinkAsync(link) ?? throw new AssertionException("The link has no stored record.");
                var lifetime = stored.ExpiresAtUtc - stored.CreatedAtUtc;
                Assert.That(lifetime, Is.InRange(TimeSpan.FromMinutes(119), TimeSpan.FromMinutes(121)),
                    $"The link lasts {lifetime.TotalMinutes:0} minutes, not two hours.");
            });

            await Step("Two hours on (its expiry moved into the past), it opens no form", async () =>
            {
                await Db.ExpireLinkAsync(link);
                await ExpectRefusedAsync(link, "an expired link");
            });

            await Step("Posting the form to the expired link anyway changes nothing — the old password still works", async () =>
            {
                var p = await NewPageAsync("expired-post");
                await p.GotoAsync("/Account/ForgotPassword");
                var token = await PageHttp.FormTokenAsync(p);
                var r = await PageHttp.FetchAsync(p, "POST", Ui.Rebase(link), new[]
                {
                    KeyValuePair.Create("__RequestVerificationToken", token),
                    KeyValuePair.Create("NewPassword", "Brown-Forest-Kettle-19!"),
                    KeyValuePair.Create("ConfirmPassword", "Brown-Forest-Kettle-19!"),
                }, follow: false);
                Assert.That(r.Redirected, Is.False, $"The expired link accepted a new password ({r}).");
                Assert.That(await SignsInAsync(person.Email, "Brown-Forest-Kettle-19!"), Is.False, "The expired link set the password.");
                Assert.That(await SignsInAsync(person.Email, person.Password), Is.True, "The old password stopped working.");
            });

            var fresh = await Step("Ask again", () => RequestLinkAsync(person.Email));

            await Step("The fresh link, altered to claim it's an invite, is refused", async () =>
            {
                var altered = fresh.Replace("purpose=PasswordReset", "purpose=Invite", StringComparison.OrdinalIgnoreCase);
                Assume.That(altered, Is.Not.EqualTo(fresh), "The link has no purpose to alter.");
                await ExpectRefusedAsync(altered, "the link with its purpose changed");
            });

            await Step("…and the genuine fresh link still works", async () =>
            {
                const string pw = "Teal-Canyon-Ember-42!";
                var p = await NewPageAsync("fresh");
                await Ui.SetPasswordFromLinkAsync(p, fresh, pw);
                person.Password = pw;
                Assert.That(await SignsInAsync(person.Email, pw), Is.True, "The new password doesn't work.");
            });
        }
        finally
        {
            await RemoveAsync(person);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────

    /// <summary>Forgot password, from a session (and network address) of its own; returns the emailed link.</summary>
    private async Task<string> RequestLinkAsync(string email)
    {
        var since = DateTime.UtcNow.AddSeconds(-2);
        var page = await NewPageAsync("forgot");
        await page.GotoAsync("/Account/ForgotPassword");
        await page.Locator("#Email").FillAsync(email);
        await page.Locator("#forgotSubmit").ClickAsync();
        await Assertions.Expect(page.Locator("h1")).ToContainTextAsync("Check your email");
        return await Db.WaitForSetPasswordLinkAsync(email, since, TimeSpan.FromSeconds(15));
    }

    private async Task ExpectRefusedAsync(string link, string what)
    {
        var page = await NewPageAsync("refused");
        await page.GotoAsync(Ui.Rebase(link));
        Assert.That(await page.Locator("#NewPassword").CountAsync(), Is.EqualTo(0), $"{what} opened the set-password form.");
        Assert.That(await page.Locator("body").InnerTextAsync(), Does.Contain(BadLink), $"{what} wasn't explained as invalid or expired.");
    }

    /// <summary>Signs in on a fresh session; true when it reaches the app.</summary>
    private async Task<bool> SignsInAsync(string email, string password)
    {
        var p = await NewPageAsync("sign-in");
        await p.GotoAsync("/Account/Login");
        await p.Locator("#Email").FillAsync(email);
        await p.Locator("#Password").FillAsync(password);
        await p.Locator("form button[type=submit]").ClickAsync();

        // Either the app opens, or the sign-in page says no — whichever comes first.
        bool OnLogin() => Ui.PathOf(p.Url).StartsWith("/Account/Login", StringComparison.OrdinalIgnoreCase);
        var refusal = p.Locator(".alert-error, .alert-danger");
        await Ui.WaitUntilAsync(p, async () => !OnLogin() || await refusal.CountAsync() > 0,
            $"Signing in as {email} neither opened the app nor said why not.");
        return !OnLogin();
    }

    private static async Task RemoveAsync(TestUser person)
    {
        var admin = await Api.AsAsync(World.A.Admin);
        var tenantId = await Db.TenantIdAsync(World.A.Name);
        await admin.DeleteAsync($"/api/users/{await Db.UserIdAsync(person.Email)}?tenantId={tenantId}");
        World.A.People.Remove(person.Key);
        World.A.RoleOf.Remove(person.Key);
    }
}
