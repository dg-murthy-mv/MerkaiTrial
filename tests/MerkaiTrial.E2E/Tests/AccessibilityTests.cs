// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/AccessibilityTests.cs
//
// NEW (110). Can someone who can't use a mouse, or can't see the screen
// well, use Merkai?
//
// For each main page (signed in as workspace A's admin; the sign-in page
// and the customer's quote page signed out):
//   1. axe-core scans it against WCAG 2.x A/AA. A test FAILS on:
//        • a field, button, link or image with no name a screen reader
//          can read out (missing labels)
//        • text with too little contrast against its background
//        • something a keyboard can't reach or escape
//      Other findings are listed as advice, not failures.
//   2. Tab is pressed through the whole page: focus must move on, and
//      eventually leave the page or come back round — never go round a
//      few elements forever (a keyboard trap).
//
// Every page writes a full report to
//   bin\Debug\net8.0\e2e-artifacts\accessibility\<page>.txt
// (every finding and every place it was found), whether it passes or not.
//
// And one journey with the keyboard alone — no mouse, no clicks: sign
// in, open New deal from the pipeline, fill the title, find the contact
// by typing, enter the value and close date, press Create deal. The deal
// must be saved with that contact and that close date.
// =====================================================================

using System.Globalization;
using System.Text;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Accessibility")]
public class AccessibilityTests : E2ETest
{
    private static TestTenant A => World.A;

    /// <summary>The main pages. Words in place of a path are records made for the test.</summary>
    public static readonly string[] Pages =
    {
        "/Account/Login",
        "/Dashboard",
        "/Leads", "/Leads/Create", "a lead",
        "/Pipeline", "/Pipeline/Create", "a deal",
        "/Quotes", "/Quotes/Create", "a quote",
        "/Invoices", "an invoice",
        "/Contacts", "/Companies",
        "/Tasks", "/Reports", "/Settings/Users",
        "the customer's quote page",
    };

    // One set of records for every page that needs one, made once.
    private sealed record Records(Guid Lead, Guid Deal, Guid Quote, Guid Invoice, string QuoteToken);
    private static Records? _records;
    private static readonly SemaphoreSlim RecordsGate = new(1, 1);

    private static async Task<Records> RecordsAsync()
    {
        if (_records is not null) return _records;
        await RecordsGate.WaitAsync();
        try
        {
            if (_records is not null) return _records;
            var tenantId = await Db.TenantIdAsync(A.Name);
            var api = await Api.AsAsync(A.Admin);
            api.WaitWhenPaced = true;
            var m = Random.Shared.Next(10000, 99999).ToString(CultureInfo.InvariantCulture);

            var lead = await ApiRecords.LeadAsync(api, tenantId, $"A11y Lead {m}", "a11ylead" + m);
            var contact = await ApiRecords.ContactAsync(api, tenantId, $"A11y {m}", "a11ycontact" + m);
            var deal = await ApiRecords.DealAsync(api, tenantId, contact, $"A11y deal {m}");
            var quote = await ApiRecords.QuoteAsync(api, deal, $"A11y line {m}");
            await ApiRecords.SendQuoteAsync(api, quote);
            var token = await Db.ValueAsync("Quotes", "PublicLinkToken", quote);
            if (string.IsNullOrEmpty(token)) throw new AssertionException("The quote made for the scan has no customer link.");
            var invoice = await ApiRecords.InvoiceAsync(api, deal, $"A11y invoice line {m}");

            return _records = new Records(lead, deal, quote, invoice, token);
        }
        finally { RecordsGate.Release(); }
    }

    private static async Task<(string Path, bool SignedIn)> ResolveAsync(string which)
    {
        if (which.StartsWith('/')) return (which, which != "/Account/Login");
        var r = await RecordsAsync();
        return which switch
        {
            "a lead"     => ($"/Leads/Detail/{r.Lead}", true),
            "a deal"     => ($"/Pipeline/Detail/{r.Deal}", true),
            "a quote"    => ($"/Quotes/Detail?id={r.Quote}", true),
            "an invoice" => ($"/Invoices/Detail?id={r.Invoice}", true),
            "the customer's quote page" => ($"/q/{r.QuoteToken}", false),
            _ => throw new ArgumentOutOfRangeException(nameof(which), which, null),
        };
    }

    [TestCaseSource(nameof(Pages))]
    public async Task Page_has_labels_enough_contrast_and_no_keyboard_trap(string which)
    {
        var (path, signedIn) = await Step($"Find the address of {which}", () => ResolveAsync(which));
        var page = signedIn ? await SignedInAsync(A.Admin) : await NewPageAsync("visitor");

        await Step($"Open {which}", async () =>
        {
            var response = await page.GotoAsync(path);
            await Ui.WaitForDocumentAsync(page);
            if (response is not null && response.Status >= 400)
                Assert.Fail($"{path} answered HTTP {response.Status}, so there is nothing to check.");
            if (signedIn && Ui.PathOf(page.Url).StartsWith("/Account/Login", StringComparison.OrdinalIgnoreCase))
                Assert.Fail($"{path} sent the admin back to the sign-in page.");
        });

        var violations = await Step("Scan it with axe-core (WCAG 2 A and AA)", () => Accessibility.ScanAsync(page));
        var walk = await Step("Press Tab through the whole page", () => Accessibility.WalkAsync(page));

        var blocking = violations.Where(v => v.Blocks).ToList();
        var advice = violations.Where(v => !v.Blocks).ToList();

        // The full report, kept whatever happens.
        var report = new StringBuilder()
            .AppendLine($"Accessibility — {which} ({path}) — {DateTime.Now:yyyy-MM-dd HH:mm}")
            .AppendLine()
            .AppendLine($"Keyboard: {(walk.Trapped ? "TRAP — " : "")}{walk.Detail}")
            .AppendLine()
            .AppendLine(blocking.Count == 0 ? "Failing findings: none." : "Failing findings:")
            .Append(Accessibility.Describe(blocking, int.MaxValue))
            .AppendLine()
            .AppendLine(advice.Count == 0 ? "Advice: none." : "Advice (does not fail the test):")
            .Append(Accessibility.Describe(advice, int.MaxValue));
        var file = WriteReport(which, report.ToString());

        if (advice.Count > 0)
            TestContext.Progress.WriteLine(
                $"  [a11y advice] {which}: {string.Join(", ", advice.Select(a => $"{a.Id} ×{a.Nodes.Count}"))}");

        var problems = new List<string>();
        foreach (var g in blocking.GroupBy(v => v.Group).OrderBy(g => g.Key))
        {
            var what = g.Key switch
            {
                "label"    => "Missing labels (nothing for a screen reader to read out)",
                "contrast" => "Low contrast (text too faint against its background)",
                _          => "Keyboard (can't be reached or escaped with the keyboard)",
            };
            problems.Add(what + ":\n" + Accessibility.Describe(g, 6).TrimEnd());
        }
        if (walk.Trapped) problems.Add("Keyboard trap: " + walk.Detail);

        if (problems.Count > 0)
            Assert.Fail($"{which} ({path}) has accessibility problems.\n\n" + string.Join("\n\n", problems) +
                        $"\n\nFull report: {file}");
    }

    [Test]
    public async Task A_deal_can_be_created_with_the_keyboard_alone()
    {
        var tenantId = await Db.TenantIdAsync(A.Name);
        var api = await Api.AsAsync(A.Admin);
        api.WaitWhenPaced = true;

        var mark = World.Stamp.Replace("-", "") + Random.Shared.Next(100, 999);
        var lastName = $"Keys {mark}";
        var title = $"Keyboard deal {mark}";
        var closeDate = new DateTime(2027, 1, 15);

        var contactId = await Step("A contact for the deal, made through the API", async () =>
        {
            var p = await api.PostAsync("/api/contacts", new
            {
                tenantId, firstName = "Kiran", lastName, email = World.EmailFor("A", "keys" + mark),
            });
            Assert.That(p.IsSuccess, Is.True, $"Adding the contact failed: {p}");
            return ApiIds.IdOf(p);
        });

        var page = await NewPageAsync("keyboard");

        await Step("Sign in with the keyboard: Tab to the email, type, Tab to the password, type, Enter", async () =>
        {
            await page.GotoAsync("/Account/Login");
            await Ui.WaitForDocumentAsync(page);
            await Accessibility.TabToAsync(page, "#Email", "the email box");
            await page.Keyboard.TypeAsync(A.Admin.Email);
            await Accessibility.TabToAsync(page, "#Password", "the password box");
            await page.Keyboard.TypeAsync(A.Admin.Password);
            await page.Keyboard.PressAsync("Enter");
            await Ui.WaitForPageAsync(page, u => Ui.PathOf(u).StartsWith("/Dashboard", StringComparison.OrdinalIgnoreCase),
                "The dashboard, after signing in with the keyboard");
        });

        await Step("On the pipeline, Tab to \"New deal\" and press Enter", async () =>
        {
            await page.GotoAsync("/Pipeline");      // typing an address is keyboard too
            await Ui.WaitForDocumentAsync(page);
            await Accessibility.TabToAsync(page, "a[href$='/Pipeline/Create']", "the New deal link", 250);
            await page.Keyboard.PressAsync("Enter");
            await Ui.WaitForPageAsync(page, u => Ui.PathOf(u).StartsWith("/Pipeline/Create", StringComparison.OrdinalIgnoreCase),
                "New deal, opened with Enter");
        });

        await Step("Tab to the title and type it", async () =>
        {
            await Accessibility.TabToAsync(page, "#Input_Title", "the deal title");
            await page.Keyboard.TypeAsync(title);
        });

        await Step("Tab to the contact search and type the contact's name — the match is chosen", async () =>
        {
            await Accessibility.TabToAsync(page, "#pcContactSearch", "the contact search box");
            await page.Keyboard.TypeAsync(lastName);
            await Ui.WaitUntilAsync(page,
                async () => string.Equals(await page.Locator("#pcContact").InputValueAsync(), contactId.ToString(), StringComparison.OrdinalIgnoreCase),
                $"Typing \"{lastName}\" in the contact search did not choose that contact.");
        });

        await Step("Tab to the value and type it", async () =>
        {
            await Accessibility.TabToAsync(page, "#Input_ExpectedValue", "the expected value");
            await page.Keyboard.TypeAsync("45000");
        });

        await Step("Tab to the close date and type 15/01/2027", async () =>
        {
            await Accessibility.TabToAsync(page, "#Input_ExpectedCloseDate", "the expected close date");
            // The browser runs in en-GB, so the date box reads day, month, year.
            await page.Keyboard.TypeAsync(closeDate.ToString("ddMMyyyy", CultureInfo.InvariantCulture));
            var typed = await page.Locator("#Input_ExpectedCloseDate").InputValueAsync();
            Assert.That(typed, Is.EqualTo("2027-01-15"),
                $"Typing 15012027 into the close date box gave \"{typed}\", not 2027-01-15.");
        });

        await Step("Tab to \"Create deal\" and press Enter", async () =>
        {
            await Accessibility.TabToAsync(page, ".pc-bar button[type=submit]", "the Create deal button");
            await page.Keyboard.PressAsync("Enter");
            await Ui.WaitForPageAsync(page, u => Ui.PathOf(u).StartsWith("/Pipeline/Detail", StringComparison.OrdinalIgnoreCase),
                "The new deal's page");
        });

        await Step("The deal is saved with that contact and close date", async () =>
        {
            var dealId = await Db.DealIdByTitleAsync(tenantId, title);
            var (savedContact, _) = await Db.DealContactAsync(dealId);
            Assert.That(savedContact, Is.EqualTo(contactId), "The deal was saved with a different contact.");
            var close = await Db.DealCloseDateAsync(dealId);
            Assert.That(close?.Date, Is.EqualTo(closeDate.Date), "The deal's close date isn't the one typed.");
        });
    }

    private static string WriteReport(string which, string text)
    {
        var dir = Path.Combine(E2ESettings.ArtifactsDir, "accessibility");
        var file = Path.Combine(dir, Accessibility.SafeName(which) + ".txt");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, text);
        }
        catch (Exception ex)
        {
            TestContext.Progress.WriteLine($"[a11y] Couldn't write {file}: {ex.Message}");
        }
        return file;
    }
}
