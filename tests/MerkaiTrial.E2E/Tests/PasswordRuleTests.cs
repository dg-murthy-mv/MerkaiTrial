// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/PasswordRuleTests.cs
//
// VUL-26 (round 106) — weak passwords are refused, and changing a
// password can't be used to guess the current one.
//
//   Weak_passwords_are_refused_and_guessing_the_current_one_locks_the_account
//
// Agreed in 106, on Change password AND on a reset link:
//   • refused: a common password padded to length ("Sunshine2026!!"),
//     one with a banned word, one character repeated, digits only, a
//     keyboard run, a short piece repeated, one containing the person's
//     own first name, and one containing their email's name part;
//   • accepted: a few unrelated words ("Copper-Willow-Signal-58!").
// Change password also:
//   • needs the current password;
//   • counts a wrong current password like a wrong password at sign-in —
//     the fifth signs the person out everywhere and locks the account for
//     15 minutes, so the right password doesn't work either, for now.
// Uses a person of its own, who ends up locked.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class PasswordRuleTests : E2ETest
{
    [Test]
    public async Task Weak_passwords_are_refused_and_guessing_the_current_one_locks_the_account()
    {
        var a = World.A;
        var m = Random.Shared.Next(10000, 99999).ToString();
        var problems = new List<string>();

        var person = await Step("A person of this test's own, first name Priyanka", () =>
            WorldBuilder.AddPersonForTestAsync(a, "pwrule" + m, "Priyanka", "Viewer"));
        var emailName = person.Email.Split('@')[0];

        var weak = new (string What, string Password)[]
        {
            ("a common password padded to length", "Sunshine2026!!"),
            ("a banned word", "MyPassword-2026x"),
            ("one character repeated", "aaaaaaaaaaaaaaaa"),
            ("digits only", "123987456321009"),
            ("a keyboard run", "qwertyuiopasdfg"),
            ("an alphabet run", "abcdefghijklmn"),
            ("a short piece repeated", "ab12ab12ab12ab12"),
            ("their own first name", "Priyanka-Lighthouse-9"),
            ("their email's name part", emailName + "-Harbour-7"),
        };

        await Step($"Change password refuses {weak.Length} weak passwords, each with a reason", async () =>
        {
            var page = await SignedInAsync(person);
            foreach (var (what, pw) in weak)
            {
                var error = await ChangeAsync(page, person.Password, pw);
                if (error is null) { problems.Add($"Change password ACCEPTED {what} (\"{pw}\")."); break; }
                if (error.Contains("current password is incorrect", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"Change password said the current password was wrong while trying {what} — the test's own sign-in is off.");
            }
        });

        await Step("A reset link refuses a common password too (and still works for a good one)", async () =>
        {
            var since = DateTime.UtcNow.AddSeconds(-2);
            var forgot = await NewPageAsync("forgot");
            await forgot.GotoAsync("/Account/ForgotPassword");
            await forgot.Locator("#Email").FillAsync(person.Email);
            await forgot.Locator("#forgotSubmit").ClickAsync();
            await Assertions.Expect(forgot.Locator("h1")).ToContainTextAsync("Check your email");
            var link = await Db.WaitForSetPasswordLinkAsync(person.Email, since, TimeSpan.FromSeconds(15));

            var p = await NewPageAsync("reset");
            await p.GotoAsync(Ui.Rebase(link));
            await p.Locator("#NewPassword").FillAsync("Sunshine2026!!");
            await p.Locator("#ConfirmPassword").FillAsync("Sunshine2026!!");
            await p.Locator("form:has(#NewPassword) button[type=submit]").ClickAsync();
            await Ui.WaitForDocumentAsync(p);
            if (await p.Locator("#NewPassword").CountAsync() == 0)
                problems.Add("A reset link ACCEPTED a common password padded to length (\"Sunshine2026!!\").");

            const string good = "Amber-Pylon-Meadow-63!";
            await Ui.SetPasswordFromLinkAsync(p, link, good);
            person.Password = good;
        });

        await Step("Change password accepts a few unrelated words", async () =>
        {
            var page = await SignedInAsync(person);
            const string good = "Copper-Willow-Signal-58!";
            var error = await ChangeAsync(page, person.Password, good);
            if (error is not null) { problems.Add($"Change password refused a good password: {error}"); return; }
            person.Password = good;
        });

        await Step("Five wrong current passwords: signed out everywhere and locked — the right password doesn't work either", async () =>
        {
            var page = await SignedInAsync(person);
            using var otherBrowser = await WebSession.SignedInAsync(person);

            for (var i = 1; i <= 5; i++)
            {
                await page.GotoAsync("/Account/ChangePassword");
                if (await page.Locator("#CurrentPassword").CountAsync() == 0)
                {
                    if (i < 5) problems.Add($"After only {i - 1} wrong current passwords the person was already signed out.");
                    break;
                }
                await ChangeAsync(page, "Not-My-Password-" + i, "Granite-Ferry-Orchid-" + i + "4!");
            }

            var lockedUntil = await Db.ValueAsync("Users", "LockoutEndUtc", await Db.UserIdAsync(person.Email));
            if (string.IsNullOrEmpty(lockedUntil) || DateTime.Parse(lockedUntil, null, System.Globalization.DateTimeStyles.RoundtripKind) <= DateTime.UtcNow)
                problems.Add($"Five wrong current passwords did not lock the account (LockoutEndUtc = '{lockedUntil}').");

            if (!Ui.PathOf(page.Url).StartsWith("/Account/Login", StringComparison.OrdinalIgnoreCase))
                problems.Add($"After the fifth wrong current password the person is still on {Ui.PathOf(page.Url)} (expected signed out).");

            var other = await otherBrowser.GetAsync("/Dashboard");
            if (!other.ToSignIn) problems.Add($"The person's other browser is still signed in after the lock: {other}");

            var (s, answer) = await WebSession.SignInAsync(person);
            using (s)
                if (answer.IsRedirect && !answer.ToSignIn)
                    problems.Add("While locked, the person signed in with the right password.");
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    /// <summary>Fills Change password and submits. Returns the page's error, or null when it went through.</summary>
    private static async Task<string?> ChangeAsync(IPage page, string current, string next)
    {
        if (!Ui.PathOf(page.Url).StartsWith("/Account/ChangePassword", StringComparison.OrdinalIgnoreCase))
            await page.GotoAsync("/Account/ChangePassword");
        await page.Locator("#CurrentPassword").FillAsync(current);
        await page.Locator("#NewPassword").FillAsync(next);
        await page.Locator("#ConfirmPassword").FillAsync(next);
        await page.Locator("form button[type=submit]").ClickAsync();
        await Ui.WaitForDocumentAsync(page);

        if (!Ui.PathOf(page.Url).StartsWith("/Account/ChangePassword", StringComparison.OrdinalIgnoreCase))
            return null;                                   // went through (signed out, to sign in again)
        var alert = page.Locator(".alert-error");
        return await alert.CountAsync() > 0 ? (await alert.First.InnerTextAsync()).Trim() : "(no message)";
    }
}
