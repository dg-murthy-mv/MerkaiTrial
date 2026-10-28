// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/AttemptLimitTests.cs
//
// VUL-10 (round 104) — sign-in and password reset are rate limited.
//
//   Sign_in_and_password_reset_abuse_is_limited
//
// The limits (agreed for round 104, Application/Security/AttemptLimiter.cs):
//   Sign in          20 failed attempts a minute from one network address,
//                    and (as before) 5 wrong passwords lock that account
//                    for 15 minutes.
//   Forgot password  10 requests an hour from one network address, and 3
//                    reset emails an hour to one account. The answer is
//                    the same "check your email" either way.
//   Set password     10 links that don't work per 15 minutes from one
//                    network address.
//
// The test browser is always on this machine, so each "network address"
// here is a browser session given an address of its own (X-Forwarded-For,
// which Admin.Web believes only from this machine — see BrowserSession).
// A limit must stop THAT address and leave every other one alone.
//
// Uses people of its own, so locking an account touches nobody else.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class AttemptLimitTests : E2ETest
{
    [Test]
    public async Task Sign_in_and_password_reset_abuse_is_limited()
    {
        var m = Random.Shared.Next(10000, 99999).ToString();
        var signer = await Step("Two people of this test's own", () =>
            WorldBuilder.AddPersonForTestAsync(World.A, "limit" + m, "Lim", Ui.RolePatterns.Viewer));
        var resetter = await WorldBuilder.AddPersonForTestAsync(World.A, "limitreset" + m, "Rei", Ui.RolePatterns.Viewer);
        var resetterId = await Db.UserIdAsync(resetter.Email);

        try
        {
            // ── Sign in: 20 failures a minute per address ───────────
            var x = await NewPageAsync("address-x", networkAddress: BrowserSession.NewNetworkAddress());
            await Step("From one address: 20 failed sign-ins (made-up accounts) are each just \"incorrect\"", async () =>
            {
                await x.GotoAsync("/Account/Login");
                var token = await PageHttp.FormTokenAsync(x);
                for (var i = 1; i <= 20; i++)
                {
                    var r = await SignInPostAsync(x, token, World.EmailFor("A", $"nobody{m}x{i}"), "Wrong-Password-1!");
                    Assert.That(r.Status, Is.EqualTo(200), $"Failed sign-in #{i} answered {r} (expected the sign-in page again).");
                }
            });

            await Step("…the 21st, with a REAL password, is refused (429) before the password is even checked", async () =>
            {
                var token = await PageHttp.FormTokenAsync(x);
                var r = await SignInPostAsync(x, token, signer.Email, signer.Password);
                Assert.That(r.Status, Is.EqualTo(429), $"The 21st sign-in from the same address answered {r}.");
                Assert.That(r.Text, Does.Contain("Too many sign-in attempts"), "The refusal doesn't say why.");
            });

            await Step("…while the same person signs in fine from another address", async () =>
            {
                Assert.That(await SignsInAsync(signer.Email, signer.Password), Is.True,
                    "The limit on one address stopped a sign-in from another.");
            });

            // ── Sign in: 5 wrong passwords lock the account ─────────
            await Step("5 wrong passwords for one account (from five addresses) lock it — even the right password is refused", async () =>
            {
                for (var i = 1; i <= 5; i++)
                    Assert.That(await SignsInAsync(signer.Email, "Wrong-Password-" + i + "!"), Is.False, $"Wrong password #{i} was accepted.");

                var p = await NewPageAsync("locked");
                await p.GotoAsync("/Account/Login");
                var r = await SignInPostAsync(p, await PageHttp.FormTokenAsync(p), signer.Email, signer.Password);
                Assert.That(r.Redirected, Is.False, "The right password got in straight after 5 wrong ones — the account didn't lock.");
                Assert.That(r.Text, Does.Contain("locked"), "The locked account wasn't told so.");
            });

            // ── Forgot password: 10 an hour per address ─────────────
            var since = DateTime.UtcNow.AddSeconds(-2);
            var z = await NewPageAsync("address-z", networkAddress: BrowserSession.NewNetworkAddress());
            await Step("From one address: 10 reset requests (made-up addresses) each say \"check your email\"", async () =>
            {
                await z.GotoAsync("/Account/ForgotPassword");
                var token = await PageHttp.FormTokenAsync(z);
                for (var i = 1; i <= 10; i++)
                {
                    var r = await ForgotPostAsync(z, token, World.EmailFor("A", $"nobody{m}z{i}"));
                    Assert.That(r.Text, Does.Contain("Check your email"), $"Reset request #{i} answered {r}.");
                }
            });

            await Step("…the 11th, for a REAL person, says the same — and sends nothing", async () =>
            {
                var r = await ForgotPostAsync(z, await PageHttp.FormTokenAsync(z), resetter.Email);
                Assert.That(r.Text, Does.Contain("Check your email"), "The limited answer differs from the normal one — it gives the limit away.");
                await z.WaitForTimeoutAsync(1500);
                Assert.That(await Db.CountResetLinksAsync(resetterId, since), Is.EqualTo(0),
                    "The 11th request from one address still sent a reset email.");
            });

            // ── Forgot password: 3 an hour per account ──────────────
            await Step("From four other addresses, the same person asks four times: three emails, not four", async () =>
            {
                for (var i = 1; i <= 4; i++)
                {
                    var p = await NewPageAsync("forgot-" + i);
                    await p.GotoAsync("/Account/ForgotPassword");
                    var r = await ForgotPostAsync(p, await PageHttp.FormTokenAsync(p), resetter.Email);
                    Assert.That(r.Text, Does.Contain("Check your email"), $"Request #{i} answered {r}.");
                }
                await Task.Delay(1500);
                Assert.That(await Db.CountResetLinksAsync(resetterId, since), Is.EqualTo(3),
                    "One person should get at most three reset emails an hour.");
            });

            // ── Set password: 10 bad links per 15 minutes per address ─
            var link = await Db.WaitForSetPasswordLinkAsync(resetter.Email, since, TimeSpan.FromSeconds(15));
            var v = await NewPageAsync("address-v", networkAddress: BrowserSession.NewNetworkAddress());
            await Step("From one address: 10 made-up links are each \"invalid or has expired\"", async () =>
            {
                await v.GotoAsync("/Account/Login");
                for (var i = 1; i <= 10; i++)
                {
                    var r = await PageHttp.FetchAsync(v, "GET", $"/Account/SetPassword?token=made-up-{m}-{i}&purpose=PasswordReset");
                    Assert.That(r.Text, Does.Contain("invalid or has expired"), $"Made-up link #{i} answered {r}.");
                }
            });

            await Step("…the 11th, a GENUINE link, is refused (429) from that address", async () =>
            {
                var r = await PageHttp.FetchAsync(v, "GET", Ui.Rebase(link));
                Assert.That(r.Status, Is.EqualTo(429), $"A genuine link from the limited address answered {r}.");
                Assert.That(r.Text, Does.Contain("Too many attempts"), "The refusal doesn't say why.");
            });

            await Step("…and opens the form from any other address", async () =>
            {
                var p = await NewPageAsync("genuine");
                await p.GotoAsync(Ui.Rebase(link));
                Assert.That(await p.Locator("#NewPassword").CountAsync(), Is.EqualTo(1),
                    "The limit on one address stopped a genuine link from another.");
            });
        }
        finally
        {
            var admin = await Api.AsAsync(World.A.Admin);
            var tenantId = await Db.TenantIdAsync(World.A.Name);
            foreach (var p in new[] { signer, resetter })
            {
                await admin.DeleteAsync($"/api/users/{await Db.UserIdAsync(p.Email)}?tenantId={tenantId}");
                World.A.People.Remove(p.Key);
                World.A.RoleOf.Remove(p.Key);
            }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static Task<PageHttp.Result> SignInPostAsync(IPage page, string token, string email, string password) =>
        PageHttp.FetchAsync(page, "POST", "/Account/Login", new[]
        {
            KeyValuePair.Create("__RequestVerificationToken", token),
            KeyValuePair.Create("Email", email),
            KeyValuePair.Create("Password", password),
        }, follow: false);

    private static Task<PageHttp.Result> ForgotPostAsync(IPage page, string token, string email) =>
        PageHttp.FetchAsync(page, "POST", "/Account/ForgotPassword", new[]
        {
            KeyValuePair.Create("__RequestVerificationToken", token),
            KeyValuePair.Create("Email", email),
        }, follow: false);

    /// <summary>Signs in on a fresh session (a fresh address); true when it reaches the app.</summary>
    private async Task<bool> SignsInAsync(string email, string password)
    {
        var p = await NewPageAsync("sign-in");
        await p.GotoAsync("/Account/Login");
        var r = await SignInPostAsync(p, await PageHttp.FormTokenAsync(p), email, password);
        return r.Redirected;
    }
}
