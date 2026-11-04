// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/LocalAssetTests.cs
//
// NEW (115). 115-update1: the super admin form is opened for workspace A
// (?tenantId=…) — without it the page goes back to the list — and a page
// that redirects is reported as such.
//
// Every page's scripts, stylesheets and fonts come from Merkai
// itself (wwwroot/lib), never from another website.
//
// WHY: until 115 every page loaded Bootstrap, its icons and the fonts from
// jsDelivr and Google, and the report charts from cdnjs. On 3 Nov a webkit
// run waited 26 seconds for jsDelivr while Merkai had answered in 0.3 —
// and a client demo on bad Wi-Fi would have looked just as broken.
//
// For each kind of page — the sign-in page (signed out), the dashboard, a
// report with a chart, a super admin page with form checking (jQuery), the
// customer's quote link (signed out):
//   • no script, stylesheet or font is asked for from another host;
//   • every file asked for under /lib answers 200 (nothing missing);
//   • what the page needs actually arrived: Bootstrap's script, the two
//     fonts, Chart.js on the report, jQuery on the super admin page;
//   • a /lib file may be kept by the browser (Cache-Control immutable).
//
// Every problem is collected and reported together.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class LocalAssetTests : E2ETest
{
    private static readonly string[] AssetTypes = { "script", "stylesheet", "font" };

    [Test]
    public async Task Pages_load_their_scripts_styles_and_fonts_from_merkai_only()
    {
        var a = World.A;
        var site = new Uri(E2ESettings.BaseUrl);
        var problems = new List<string>();
        var tenantA = await Db.TenantIdAsync(a.Name);

        var quoteToken = await Step("A sent quote with a customer link (through the API)", async () =>
        {
            var api = await Api.AsAsync(a.Admin);
            api.WaitWhenPaced = true;
            var tenantId = tenantA;
            var m = Random.Shared.Next(10000, 99999).ToString();
            var contact = await ApiRecords.ContactAsync(api, tenantId, $"Assets {m}", "assets" + m);
            var deal = await ApiRecords.DealAsync(api, tenantId, contact, $"Assets deal {m}");
            var quote = await ApiRecords.QuoteAsync(api, deal, $"Assets line {m}");
            await ApiRecords.SendQuoteAsync(api, quote);
            var t = await Db.ValueAsync("Quotes", "PublicLinkToken", quote);
            Assert.That(t, Is.Not.Null.And.Not.Empty, "The quote has no customer link after being sent.");
            return t!;
        });

        async Task CheckAsync(string label, IPage page, string path, params (string Script, string What)[] needs)
        {
            var asked = new List<IRequest>();
            var answers = new List<IResponse>();
            void OnRequest(object? _, IRequest r) { lock (asked) asked.Add(r); }
            void OnResponse(object? _, IResponse r) { lock (answers) answers.Add(r); }
            page.Request += OnRequest;
            page.Response += OnResponse;
            try
            {
                await page.GotoAsync(path);

                // 115-update1: a page that sent us elsewhere was checked as the
                // wrong page — say so plainly instead.
                var wanted = path.Split('?')[0];
                var landed = Ui.PathOf(page.Url);
                if (!landed.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{label}: {path} sent the browser to {landed}, so it could not be checked");
                    return;
                }
                await page.EvaluateAsync("() => document.fonts.ready.then(() => true)");
                await page.WaitForTimeoutAsync(500);   // late script tags at the end of the body

                List<IRequest> mine;
                lock (asked) mine = asked.ToList();
                foreach (var r in mine.Where(r => AssetTypes.Contains(r.ResourceType)))
                {
                    if (!Uri.TryCreate(r.Url, UriKind.Absolute, out var u) || u.Scheme is "data" or "blob") continue;
                    if (!string.Equals(u.Host, site.Host, StringComparison.OrdinalIgnoreCase) || u.Port != site.Port)
                        problems.Add($"{label}: a {r.ResourceType} came from another website: {u.GetLeftPart(UriPartial.Path)}");
                }

                List<IResponse> got;
                lock (answers) got = answers.ToList();
                foreach (var r in got.Where(r => new Uri(r.Url).AbsolutePath.StartsWith("/lib/", StringComparison.OrdinalIgnoreCase)))
                {
                    if (r.Status is not (200 or 304))
                        problems.Add($"{label}: {new Uri(r.Url).AbsolutePath} answered {r.Status}");
                }

                foreach (var (script, what) in needs)
                    if (!await page.EvaluateAsync<bool>(script))
                        problems.Add($"{label}: {what} did not arrive");
            }
            finally
            {
                page.Request -= OnRequest;
                page.Response -= OnResponse;
            }
        }

        const string Bootstrap = "() => typeof window.bootstrap !== 'undefined'";
        const string Inter = "() => document.fonts.check('400 16px \"Inter\"') && [...document.fonts].some(f => f.family.replace(/\"/g,'') === 'Inter' && f.status === 'loaded')";
        const string Jakarta = "() => [...document.fonts].some(f => f.family.replace(/\"/g,'') === 'Plus Jakarta Sans' && f.status === 'loaded')";
        const string Icons = "() => [...document.fonts].some(f => f.family.replace(/\"/g,'') === 'bootstrap-icons' && f.status === 'loaded')";

        await Step("The sign-in page (signed out)", async () =>
        {
            var page = await NewPageAsync("assets-anonymous");
            await CheckAsync("sign-in page", page, "/Account/Login");
        });

        var admin = await SignedInAsync(a.Admin);
        await Step("The dashboard: Bootstrap, its icons and both fonts, all from Merkai", async () =>
            await CheckAsync("dashboard", admin, "/Dashboard",
                (Bootstrap, "Bootstrap's script"), (Inter, "the Inter font"),
                (Jakarta, "the Plus Jakarta Sans font"), (Icons, "the Bootstrap icons font")));

        await Step("A report with a chart: Chart.js from Merkai", async () =>
            await CheckAsync("Pipeline report", admin, "/Reports/Pipeline",
                ("() => typeof window.Chart !== 'undefined'", "Chart.js")));

        await Step("A super admin page with form checking: jQuery and validation from Merkai", async () =>
        {
            var sa = await SignedInAsync(World.SuperAdmin);
            // 115-update1: the form belongs to a workspace — without ?tenantId it
            // goes back to the list (which loads no jQuery), as it did in the
            // first run.
            await CheckAsync("super admin: new user", sa, $"/Admin/Users/Create?tenantId={tenantA}",
                (Bootstrap, "Bootstrap's script"),
                ("() => typeof window.jQuery !== 'undefined' && typeof window.jQuery.validator !== 'undefined'", "jQuery and its validation"));
        });

        await Step("The customer's quote link (signed out)", async () =>
        {
            var page = await NewPageAsync("assets-customer");
            await CheckAsync("customer quote link", page, "/q/" + quoteToken, (Bootstrap, "Bootstrap's script"));
        });

        await Step("A /lib file may be kept by the browser", async () =>
        {
            using var web = new WebSession();
            var r = await web.GetAsync("/lib/bootstrap-5.3.3/css/bootstrap.min.css");
            var cache = r.Header("Cache-Control") ?? "";
            if (r.Status != 200) problems.Add($"/lib/bootstrap-5.3.3/css/bootstrap.min.css answered {r.Status}");
            else if (!cache.Contains("immutable", StringComparison.OrdinalIgnoreCase))
                problems.Add($"a /lib file has Cache-Control '{cache}' (expected public, max-age=31536000, immutable)");
        });

        if (problems.Count > 0)
            Assert.Fail($"{problems.Count} page asset problem(s):\n  • " + string.Join("\n  • ", problems));
    }
}
