// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/OpenRedirectTests.cs
//
// VUL-16 (round 105) — the sign-in page's "where to go next" can't send
// anyone to another website.
//
//   Sign_in_only_ever_sends_people_to_this_site
//
// A link like /Account/Login?ReturnUrl=https://evil.example would make the
// real sign-in page hand a freshly signed-in person to a look-alike site.
//
//   • Control: ReturnUrl=/Leads → after signing in, /Leads.
//   • Every other-site form — https://, //, ///, /\, \\, a tab after the
//     slash, "https:" with no slashes, javascript:, data:, a leading space,
//     an encoded //, a look-alike host — signs the person in and sends
//     them to a page on THIS site, never elsewhere.
//   • The sign-in page itself, opened with such a ReturnUrl, carries no
//     link to the other site.
//   • /Account/Denied (which passes ReturnUrl on) and the access-denied page
//     do the same.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class OpenRedirectTests : E2ETest
{
    private const string Evil = "evil.example";

    private static readonly string[] Attempts =
    {
        $"https://{Evil}/phish",
        $"http://{Evil}",
        $"//{Evil}/phish",
        $"///{Evil}/phish",
        $"/\\{Evil}/phish",
        $"\\\\{Evil}/phish",
        $"/\t/{Evil}/phish",
        $"https:{Evil}/phish",
        $"HTTPS://{Evil.ToUpperInvariant()}",
        "javascript:alert(document.cookie)",
        "data:text/html,<script>alert(1)</script>",
        $" //{Evil}/phish",
        $"%2F%2F{Evil}/phish",
        $"https://localhost.{Evil}/",
        $"https://localhost@{Evil}/",
    };

    [Test]
    public async Task Sign_in_only_ever_sends_people_to_this_site()
    {
        var a = World.A;
        var problems = new List<string>();
        var siteHost = new Uri(E2ESettings.BaseUrl).Host;

        await Step("Control: ReturnUrl=/Leads → after signing in, /Leads", async () =>
        {
            var (s, answer) = await WebSession.SignInAsync(a.Admin, returnUrl: "/Leads");
            using (s)
            {
                Assert.That(answer.IsRedirect && !answer.ToSignIn, Is.True, $"Signing in failed: {answer}");
                Assert.That(answer.Location, Does.StartWith("/Leads"),
                    $"A local ReturnUrl wasn't followed ({answer}), so the refusals below would prove nothing.");
            }
        });

        await Step($"{Attempts.Length} other-site ReturnUrls: signed in, and sent somewhere on this site", async () =>
        {
            foreach (var attempt in Attempts)
            {
                var (s, answer) = await WebSession.SignInAsync(a.Admin, returnUrl: attempt);
                using (s)
                {
                    var shown = Shown(attempt);
                    if (!answer.IsRedirect || answer.ToSignIn)
                    {
                        problems.Add($"Signing in with ReturnUrl={shown} didn't work: {answer}");
                        continue;
                    }
                    if (OffSite(answer.Location, siteHost) is { } why)
                        problems.Add($"ReturnUrl={shown} sent the signed-in person to '{answer.Location}' — {why}.");
                }
            }
        });

        await Step("The sign-in page opened with such a ReturnUrl links nowhere else", async () =>
        {
            using var web = new WebSession();
            foreach (var attempt in Attempts)
            {
                var r = await web.GetAsync("/Account/Login?ReturnUrl=" + Uri.EscapeDataString(attempt));
                if (r.Status >= 500) { problems.Add($"The sign-in page with ReturnUrl={Shown(attempt)} gave HTTP {r.Status}."); continue; }
                if (LinksAway(r.Body)) problems.Add($"The sign-in page with ReturnUrl={Shown(attempt)} carries a link or action to the other site.");
            }
        });

        await Step("/Account/Denied and the access-denied page don't pass an other-site address on", async () =>
        {
            using var web = await WebSession.SignedInAsync(a.Admin);
            foreach (var attempt in Attempts.Take(6))
            {
                var denied = await web.GetAsync("/Account/Denied?returnUrl=" + Uri.EscapeDataString(attempt));
                if (denied.IsRedirect && OffSite(denied.Location, siteHost) is { } why)
                    problems.Add($"/Account/Denied with returnUrl={Shown(attempt)} sent the person to '{denied.Location}' — {why}.");
                else if (denied.IsRedirect && (denied.Location ?? "").Contains(Evil, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"/Account/Denied passed returnUrl={Shown(attempt)} on to '{denied.Location}'.");

                var page = await web.GetAsync("/AccessDenied?returnUrl=" + Uri.EscapeDataString(attempt));
                if (page.Status >= 500) problems.Add($"/AccessDenied with returnUrl={Shown(attempt)} gave HTTP {page.Status}.");
                else if (LinksAway(page.Body)) problems.Add($"/AccessDenied with returnUrl={Shown(attempt)} links to the other site.");
            }
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    /// <summary>Null when <paramref name="location"/> stays on this site; otherwise why not.</summary>
    private static string? OffSite(string? location, string siteHost)
    {
        if (string.IsNullOrEmpty(location)) return "no destination at all";
        var l = location.Trim();

        if (l.Contains(Evil, StringComparison.OrdinalIgnoreCase)) return "it names the other site";
        if (l.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return "it is a script or data address";

        if (Uri.TryCreate(l, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https")
            return string.Equals(abs.Host, siteHost, StringComparison.OrdinalIgnoreCase) ? null : $"another host ({abs.Host})";

        if (!l.StartsWith('/')) return "it is not a path on this site";
        if (l.Length > 1 && (l[1] == '/' || l[1] == '\\')) return "a browser reads // or /\\ as another site";
        if (l.Any(char.IsControl)) return "it holds a control character a browser would drop";
        return null;
    }

    // The page's own links (javascript:history.back() on the access-denied
    // page) are fine; what must not appear is anything the address put there:
    // the other site, or the javascript:/data: addresses tried above.
    private static bool LinksAway(string html)
        => System.Text.RegularExpressions.Regex.IsMatch(html,
            $@"(href|action|src|formaction)\s*=\s*[""']\s*(javascript:[^""']*alert|data:text/html|https?:[/\\]*[^""']*{System.Text.RegularExpressions.Regex.Escape(Evil)}|[/\\]{{2}}[^""']*{System.Text.RegularExpressions.Regex.Escape(Evil)})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)
           || html.Contains($"location.href = '//{Evil}", StringComparison.OrdinalIgnoreCase);

    private static string Shown(string s) => s.Replace("\t", "\\t");
}
