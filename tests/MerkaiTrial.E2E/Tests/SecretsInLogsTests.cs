// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/SecretsInLogsTests.cs
//
// VUL-20 (round 105) — no passwords, links, tokens or keys in the logs or
// in what the API and website hand out.
//
//   No_credentials_tokens_or_keys_in_logs_or_answers
//
// A person of the test's own does the things that carry secrets:
//   signs in (the password typed), fails once (a wrong password typed),
//   asks for a reset link, opens it, sets a new password through it, and a
//   search with a word only this test knows is made on the website and the
//   API. Then, in all four server logs (both servers, out and err):
//
//   • none of the passwords typed;
//   • not the reset link's secret part;
//   • not the search word (searches are logged by their NAMES only);
//   • no person's stored password hash or security stamp;
//   • no API token (a JWT, eyJ….eyJ….) and not the token signing key;
//   • not this run's webhook secret, nor the database password.
//
// And in the answers: the people list and a person's record (API, as the
// workspace admin), the workspace as the super admin sees it, the email
// settings, and the website's people page — none carries a password hash,
// a security stamp, a stored link, the signing key or the webhook secret.
// =====================================================================

using System.Text.RegularExpressions;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class SecretsInLogsTests : E2ETest
{
    private static readonly Regex Jwt = new(@"eyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]*", RegexOptions.Compiled);

    [Test]
    public async Task No_credentials_tokens_or_keys_in_logs_or_answers()
    {
        var a = World.A;
        var tenantA = await Db.TenantIdAsync(a.Name);
        var apiA = await Api.AsAsync(a.Admin);
        var m = Random.Shared.Next(10000, 99999).ToString();
        var problems = new List<string>();

        var person = await Step("A person of this test's own", () =>
            WorldBuilder.AddPersonForTestAsync(a, "secret" + m, "Selma", "Viewer"));

        var wrongPassword = $"Wrong-Horse-{m}-Battery!";
        var newPassword = $"Quiet-Meadow-{m}-Lamp!";
        var searchWord = $"Zebracorn{m}";
        string? resetSecret = null;

        await Step("They sign in, and once get the password wrong", async () =>
        {
            using var ok = await WebSession.SignedInAsync(person);
            await ok.SignOutAsync();

            var (wrong, answer) = await WebSession.SignInAsync(person, password: wrongPassword);
            wrong.Dispose();
            Assert.That(answer.Status, Is.EqualTo(200), $"A wrong password should show the sign-in page again: {answer}");
        });

        await Step("They ask for a reset link, open it, and set a new password through it", async () =>
        {
            var since = DateTime.UtcNow.AddSeconds(-2);
            var page = await NewPageAsync("forgot");
            await page.GotoAsync("/Account/ForgotPassword");
            await page.Locator("#Email").FillAsync(person.Email);
            await page.Locator("#forgotSubmit").ClickAsync();
            await Assertions.Expect(page.Locator("h1")).ToContainTextAsync("Check your email");

            var link = await Db.WaitForSetPasswordLinkAsync(person.Email, since, TimeSpan.FromSeconds(15));
            resetSecret = Regex.Match(link, @"token=([^&\s]+)", RegexOptions.IgnoreCase).Groups[1].Value;
            Assert.That(resetSecret, Is.Not.Empty, $"The reset link has no token=…: {Ui.MaskToken(link)}");
            resetSecret = Uri.UnescapeDataString(resetSecret);

            var p2 = await NewPageAsync("reset");
            await Ui.SetPasswordFromLinkAsync(p2, link, newPassword);
            person.Password = newPassword;
        });

        await Step("A search only this test knows the word for — on the website and the API", async () =>
        {
            using var web = await WebSession.SignedInAsync(a.Admin);
            await web.GetAsync("/Leads?SearchTerm=" + Uri.EscapeDataString(searchWord));
            await apiA.GetAsync("/api/leads?pageNumber=1&pageSize=5&searchTerm=" + Uri.EscapeDataString(searchWord));
            await apiA.GetAsync("/api/contacts?pageNumber=1&pageSize=5&searchTerm=" + Uri.EscapeDataString(searchWord));
        });

        var (hash, stamp) = await Db.UserSecretsAsync(person.Email);
        var (adminHash, adminStamp) = await Db.UserSecretsAsync(a.Admin.Email);

        var secrets = new List<(string What, string? Value)>
        {
            ("the password they typed", E2ESettings.Password),
            ("a wrong password they typed", wrongPassword),
            ("the new password they set", newPassword),
            ("the reset link's secret part", resetSecret),
            ("the search word", searchWord),
            ("their stored password hash", hash),
            ("their security stamp", stamp),
            ("the admin's stored password hash", adminHash),
            ("the admin's security stamp", adminStamp),
            ("the token signing key", E2ESettings.JwtSigningKey),
            ("this run's webhook secret", Webhooks.Secret),
            ("this run's webhook secret (its key part)", Webhooks.Secret["whsec_".Length..]),
            ("the database password", DatabasePassword()),
        };

        await Step("None of it is in the servers' logs (both servers, out and err), and no API token is either", async () =>
        {
            await Task.Delay(2000);     // let the servers' last lines reach the files
            var logs = ServerLogs.ReadAll();

            foreach (var (file, text) in logs)
            {
                foreach (var (what, value) in secrets)
                    if (!string.IsNullOrEmpty(value) && value.Length >= 6 && text.Contains(value, StringComparison.Ordinal))
                        problems.Add($"{file} contains {what}: {Around(text, value)}");

                var jwt = Jwt.Match(text);
                if (jwt.Success) problems.Add($"{file} contains an API token: {jwt.Value[..Math.Min(40, jwt.Value.Length)]}…");

                if (Regex.IsMatch(text, @"Authorization:\s*Bearer\s+\S{20,}", RegexOptions.IgnoreCase))
                    problems.Add($"{file} contains an Authorization header with its token.");
            }
        });

        await Step("None of it is in what the API and the website hand out", async () =>
        {
            var personId = await Db.UserIdAsync(person.Email);
            var super = await Api.AsAsync(World.SuperAdmin);

            var answers = new List<(string What, string Body)>
            {
                ("GET /api/users/paginated", (await apiA.GetAsync("/api/users/paginated?pageNumber=1&pageSize=100")).Body),
                ($"GET /api/users/{{person}}", (await apiA.GetAsync($"/api/users/{personId}")).Body),
                ("GET /api/users/lookup", (await apiA.GetAsync("/api/users/lookup")).Body),
                ("GET /api/inbound-emails/settings", (await apiA.GetAsync("/api/inbound-emails/settings")).Body),
                ("GET /api/tenants/{A} (super admin)", (await super.GetAsync($"/api/tenants/{tenantA}")).Body),
                ("GET /api/tenants/paginated (super admin)", (await super.GetAsync("/api/tenants/paginated?pageNumber=1&pageSize=50")).Body),
            };

            using (var web = await WebSession.SignedInAsync(a.Admin))
            {
                answers.Add(("the website's people page", (await web.GetAsync("/Settings/Users")).Body));
                answers.Add(("the website's email settings page", (await web.GetAsync("/Settings/InboundEmail")).Body));
            }

            foreach (var (what, body) in answers)
            {
                foreach (var (secretWhat, value) in secrets.Where(s => s.What != "the search word"))
                    if (!string.IsNullOrEmpty(value) && value.Length >= 6 && body.Contains(value, StringComparison.Ordinal))
                        problems.Add($"{what} hands out {secretWhat}.");

                foreach (var field in new[] { "\"passwordHash\"", "\"securityStamp\"", "\"tokenHash\"", "\"signingKey\"", "\"inboundWebhookSecret\"", "\"apiKey\"" })
                    if (body.Contains(field, StringComparison.OrdinalIgnoreCase))
                        problems.Add($"{what} has a {field} field.");

                if (Jwt.IsMatch(body)) problems.Add($"{what} hands out an API token.");
            }
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    /// <summary>The password in the test database's connection string, when it has one (SQL sign-in).</summary>
    private static string? DatabasePassword()
    {
        var m = Regex.Match(E2ESettings.ConnectionString, @"(?:^|;)\s*(?:Password|Pwd)\s*=\s*([^;]+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string Around(string text, string value)
    {
        var i = text.IndexOf(value, StringComparison.Ordinal);
        var start = text.LastIndexOf('\n', Math.Max(0, i - 1)) + 1;
        var end = text.IndexOf('\n', i);
        var line = text[start..(end < 0 ? text.Length : end)].Replace(value, "«here»");
        return line.Length <= 200 ? line.Trim() : line[..200].Trim() + "…";
    }
}
