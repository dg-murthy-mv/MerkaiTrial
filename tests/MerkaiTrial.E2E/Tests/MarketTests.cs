// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/MarketTests.cs
//
// NEW (109). The other three markets — Thailand, the Philippines and the
// UAE. Every other money and date test runs in India (₹, lakh grouping,
// GST, IST); these run the same journey in a workspace of each market:
//
//   TH  workspace C  Thailand     THB  ฿    VAT  7%  Asia/Bangkok  UTC+7
//   PH  workspace PH Philippines  PHP  ₱    VAT 12%  Asia/Manila   UTC+8
//   AE  workspace AE UAE          AED  د.إ  VAT  5%  Asia/Dubai    UTC+4
//
// For each:
//   1. The workspace is set up for its market: currency, symbol, two
//      decimals, VAT and its rate (the Countries row), its time zone.
//   2. A deal's close date is kept as the day picked — not a day early or
//      late (all three are ahead of UTC, which is how that goes wrong).
//   3. A quote of 2 × 125,000 at the market's VAT: the editor calls the tax
//      VAT, and the quote page, the customer's page and the quote PDF show
//      the right sums in Western grouping (250,000.00 — never 2,50,000.00)
//      with the market's symbol (or currency code).
//   4. The customer accepts; the issued invoice and its PDF say the same.
//   5. Tasks typed in the workspace's own time — an ordinary morning, and
//      02:00, which is still the PREVIOUS day in UTC — are stored at the
//      right moment and shown at the time typed.
//
// Expected figures are worked out here; the formatting rules (symbol,
// grouping) come from what the market should be, not from the database,
// so a wrongly configured Countries row fails with a sentence saying so.
// =====================================================================

using System.Globalization;
using System.Text.RegularExpressions;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Markets")]
public class MarketTests : E2ETest
{
    /// <summary>What each market should look like.</summary>
    public sealed record MarketCase(
        string Key, string Name, string Currency, string[] Symbols, decimal VatRate,
        string IanaZone, string WindowsZone, int UtcHours,
        decimal Tax, decimal Total);

    // 2 × 125,000 = 250,000 before tax in every market.
    private static readonly MarketCase[] Cases =
    {
        new("TH", "Thailand",    "THB", new[] { "฿" },          7m,  "Asia/Bangkok", "SE Asia Standard Time",       7, 17_500m, 267_500m),
        new("PH", "Philippines", "PHP", new[] { "₱" },          12m, "Asia/Manila",  "Singapore Standard Time",     8, 30_000m, 280_000m),
        new("AE", "UAE",         "AED", new[] { "د.إ", "AED" }, 5m,  "Asia/Dubai",   "Arabian Standard Time",       4, 12_500m, 262_500m),
    };

    public static IEnumerable<TestCaseData> Markets() =>
        Cases.Select(c => new TestCaseData(c.Key).SetName($"A_quote_invoice_and_tasks_follow_the_market({c.Key})"));

    private const decimal Subtotal = 250_000m;

    [TestCaseSource(nameof(Markets))]
    public async Task A_quote_invoice_and_tasks_follow_the_market(string key)
    {
        var m = Cases.Single(c => c.Key == key);
        var ws = await Step($"Workspace {key} ({m.Name}) — built the first time it is needed",
            () => WorldBuilder.EnsureMarketWorkspaceAsync(key));
        var tenantId = await Db.TenantIdAsync(ws.Name);

        // ── 1. Set up for the market ─────────────────────────────────
        var setup = await Step($"The workspace is set up for {m.Name}: {m.Currency}, VAT {m.VatRate}%, {m.IanaZone}", async () =>
        {
            var s = await Db.MarketSetupAsync(tenantId);
            var problems = new List<string>();

            if (!string.Equals(s.WorkspaceCurrency, m.Currency, StringComparison.OrdinalIgnoreCase))
                problems.Add($"the workspace's currency is '{s.WorkspaceCurrency}', not {m.Currency}");
            if (!string.Equals(s.CurrencyCode, m.Currency, StringComparison.OrdinalIgnoreCase))
                problems.Add($"Countries.CurrencyCode for {s.Country} is '{s.CurrencyCode}', not {m.Currency}");
            if (s.CurrencySymbol is null || !m.Symbols.Contains(s.CurrencySymbol.Trim()))
                problems.Add($"Countries.CurrencySymbol for {s.Country} is '{s.CurrencySymbol}', expected {string.Join(" or ", m.Symbols)}");
            if (s.CurrencyDecimals != 2)
                problems.Add($"Countries.CurrencyDecimals for {s.Country} is {s.CurrencyDecimals}, not 2");
            if (!string.Equals(s.TaxLabel?.Trim(), "VAT", StringComparison.OrdinalIgnoreCase))
                problems.Add($"Countries.TaxLabel for {s.Country} is '{s.TaxLabel}', not VAT");
            if (s.DefaultTaxRate != m.VatRate)
                problems.Add($"Countries.DefaultTaxRate for {s.Country} is {s.DefaultTaxRate}, not {m.VatRate}");
            if (!string.Equals(s.WorkspaceTimezone, m.IanaZone, StringComparison.OrdinalIgnoreCase))
                problems.Add($"the workspace's time zone is '{s.WorkspaceTimezone}', not {m.IanaZone} " +
                             "(Admin → Tenants → Create did not offer or store it)");

            if (problems.Count > 0)
                Assert.Fail($"{m.Name} is not set up right: " + string.Join("; ", problems) + ".");
            return s;
        });

        var symbol = setup.CurrencySymbol!.Trim();
        var admin = await SignedInAsync(ws.Admin);
        var tag = Random.Shared.Next(10000, 99999).ToString(CultureInfo.InvariantCulture);
        var dealTitle = World.Unique($"{key} market deal");

        // ── 2. The close date stays the day picked ───────────────────
        var (leadId, dealId) = await Step("Add, qualify and convert a lead", () =>
            Flows.LeadToDealAsync(admin, ws, World.Unique($"{key} Market Lead"), World.EmailFor(ws.Key, "market" + tag),
                dealTitle, Subtotal, $"{m.Name} Market Co {tag}"));

        await Step("The deal's close date is the day picked — stored as that date, shown as that date", async () =>
        {
            // ConvertLeadAsync picks today + 30 (this computer's date).
            var picked = DateTime.Today.AddDays(30);
            var stored = await Db.DealCloseDateAsync(dealId);
            Assert.That(stored, Is.Not.Null, "The deal has no close date.");
            Assert.That(stored!.Value.Date, Is.EqualTo(picked.Date),
                $"The close date picked was {picked:dd/MM/yyyy} but {stored:dd/MM/yyyy HH:mm} UTC was stored — a day off " +
                $"for a workspace {m.UtcHours} hours ahead of UTC.");

            await admin.GotoAsync($"/Pipeline/Detail/{dealId}");
            var text = await admin.Locator("main").First.InnerTextAsync();
            Assert.That(FindDates(text), Does.Contain(picked.Date),
                $"The deal page does not show the close date {picked:dd/MM/yyyy} (in any day/month order).");
        });

        // ── 3. The quote ─────────────────────────────────────────────
        await Step($"Fill a quote line: 2 × 125,000 at {m.VatRate}% VAT", () =>
            Flows.FillQuoteAsync(admin, dealTitle, new QuoteLine("Fit-out works", 2, 125_000m, TaxRate: m.VatRate)));

        await Step("The editor calls the tax VAT and totals it in Western grouping", async () =>
        {
            await Assertions.Expect(admin.Locator("#qeTable thead")).ToContainTextAsync("VAT");
            await Assertions.Expect(admin.Locator("#qeSubtotal")).ToContainTextAsync(N(Subtotal, 0));
            await Assertions.Expect(admin.Locator("#qeTax")).ToContainTextAsync(N(m.Tax, 0));
            await Assertions.Expect(admin.Locator("#qeGrand")).ToContainTextAsync(N(m.Total, 0));
        });

        await Step("Send it", () => Flows.SubmitQuoteAsync(admin));
        var quoteId = Ui.IdFromUrl(admin.Url);

        await Step($"The quote page shows {symbol}{N(Subtotal)} + {symbol}{N(m.Tax)} = {symbol}{N(m.Total)}", async () =>
        {
            var text = await admin.Locator("main").First.InnerTextAsync();
            ExpectMoney(text, symbol, m.Currency, Subtotal, "subtotal");
            ExpectMoney(text, symbol, m.Currency, m.Tax, "VAT");
            ExpectMoney(text, symbol, m.Currency, m.Total, "total");
        });

        await Step("The quote PDF says VAT and has the same sums", async () =>
        {
            var pdf = await PdfText.DownloadAsync(admin, $"/Quotes/Detail?id={quoteId}&handler=DownloadPdf", "quote PDF");
            ExpectPdf(pdf, m, symbol, "quote PDF");
        });

        var link = await Step("The quote has a customer link", () => Flows.CustomerLinkAsync(admin));

        await Step($"The customer sees the same total, in {m.Currency}", async () =>
        {
            var customer = await NewPageAsync("customer");
            await customer.GotoAsync(link);
            var text = await customer.Locator("body").InnerTextAsync();
            ExpectMoney(text, symbol, m.Currency, m.Total, "total on the customer's page");
            Assert.That(text, Does.Contain(m.Currency), $"The customer's page does not say {m.Currency}.");
        });

        // ── 4. The invoice ───────────────────────────────────────────
        var invoiceId = await Step("The customer accepts; create and issue the invoice", async () =>
            await Flows.IssuedInvoiceAsync(admin, await NewPageAsync("customer-accepts"), quoteId));

        await Step("The invoice page shows the same sums", async () =>
        {
            await admin.GotoAsync($"/Invoices/Detail?id={invoiceId}");
            var text = await admin.Locator("main").First.InnerTextAsync();
            ExpectMoney(text, symbol, m.Currency, Subtotal, "invoice subtotal");
            ExpectMoney(text, symbol, m.Currency, m.Tax, "invoice VAT");
            ExpectMoney(text, symbol, m.Currency, m.Total, "invoice total");
            Assert.That(text, Does.Contain("VAT"), "The invoice page does not call the tax VAT.");
        });

        await Step("The invoice PDF says VAT and has the same sums", async () =>
        {
            var pdf = await PdfText.DownloadAsync(admin, $"/Invoices/Detail?id={invoiceId}&handler=DownloadPdf", "invoice PDF");
            ExpectPdf(pdf, m, symbol, "invoice PDF");
        });

        // ── 5. Tasks in the workspace's own time ─────────────────────
        // update4: on a NEW lead. The lead above is converted now, and a
        // converted lead's Tasks tab has no "Plan what's next" form (it is
        // read-only by design), so the tasks have to go on an open lead.
        var taskLeadId = await Step("Add an open lead for the tasks (a converted lead takes no new tasks)", () =>
            Flows.CreateLeadAsync(admin, World.Unique($"{key} Task Lead"), World.EmailFor(ws.Key, "tasks" + tag),
                10000m, ws, $"{m.Name} Task Co {tag}"));

        var zone = Zone(m.IanaZone, m.WindowsZone);
        var cases = new (string Label, DateTime Local)[]
        {
            ("an ordinary morning, 09:00",                       At(2027, 1, 15, 9, 0)),
            ("02:00 — still the previous day in UTC",            At(2027, 1, 16, 2, 0)),
        };

        foreach (var c in cases)
        {
            var utc = TimeZoneInfo.ConvertTimeToUtc(c.Local, zone);
            var subject = $"{key} clock {tag} {c.Local:yyyyMMdd-HHmm}";

            await Step($"A task due {c.Label} ({c.Local:dd/MM/yyyy HH:mm} {m.Name}) is stored as {utc:dd/MM HH:mm} UTC and shown as typed", async () =>
            {
                await AddLeadTaskAsync(admin, taskLeadId, subject, c.Local);

                var stored = await Db.TaskDueTimesAsync(taskLeadId, subject);
                Assert.That(stored, Has.Count.EqualTo(1), $"Expected one task \"{subject}\".");
                Assert.That(stored[0], Is.EqualTo(utc),
                    $"{c.Local:dd/MM/yyyy HH:mm} in {m.Name} was stored as {stored[0]:dd/MM/yyyy HH:mm} UTC, " +
                    $"not {utc:dd/MM/yyyy HH:mm} UTC (UTC+{m.UtcHours}).");

                await ExpectShownAsync(admin, taskLeadId, subject, c.Local);
            });
        }
    }

    // ── money ─────────────────────────────────────────────────────────

    /// <summary>Western grouping, as every one of these markets writes it: 267,500.00.</summary>
    private static string N(decimal amount, int places = 2) =>
        amount.ToString("N" + places, CultureInfo.InvariantCulture);

    /// <summary>Indian (lakh) grouping of the same amount, which must NOT appear: 2,67,500.</summary>
    private static string Lakh(decimal amount) =>
        amount.ToString("#,##,##0", CultureInfo.GetCultureInfo("en-IN"));

    /// <summary>
    /// The amount printed with the market's symbol (or its currency code)
    /// in front, a space allowed between — and if not, why not.
    /// </summary>
    private static void ExpectMoney(string pageText, string symbol, string code, decimal amount, string what)
    {
        var text = Regex.Replace(pageText, @"[ ​-‏‪-‮⁦-⁩]", " ");
        var number = N(amount);
        if (Regex.IsMatch(text, $@"(?:{Regex.Escape(symbol)}|{Regex.Escape(code)})\s?{Regex.Escape(number)}")
            || Regex.IsMatch(text, $@"{Regex.Escape(number)}\s?(?:{Regex.Escape(symbol)}|{Regex.Escape(code)})"))
            return;

        var hint = text.Contains(Lakh(amount))
            ? $" It is printed with Indian lakh grouping ({Lakh(amount)}) — check this country's Countries.NumberFormat."
            : text.Contains(number)
                ? $" The number {number} is there, but not next to {symbol} or {code}."
                : $" {number} is not on the page at all.";
        Assert.Fail($"Expected the {what} as {symbol}{number}.{hint}");
    }

    /// <summary>The PDF (squashed text) says VAT, has the three sums, names the currency, and has no lakh grouping.</summary>
    private static void ExpectPdf(string pdf, MarketCase m, string symbol, string what)
    {
        var problems = new List<string>();
        if (!pdf.Contains("VAT", StringComparison.OrdinalIgnoreCase)) problems.Add("it does not say VAT");
        foreach (var (amount, label) in new[] { (Subtotal, "subtotal"), (m.Tax, "VAT"), (m.Total, "total") })
            if (!pdf.Contains(N(amount))) problems.Add($"the {label} {N(amount)} is missing");
        if (pdf.Contains(Lakh(m.Total))) problems.Add($"the total is in Indian lakh grouping ({Lakh(m.Total)})");

        // The currency: its symbol (either reading order — a PDF may store
        // right-to-left text backwards) or its code.
        var sym = PdfText.Squash(symbol);
        var reversed = new string(sym.Reverse().ToArray());
        if (!pdf.Contains(sym) && !pdf.Contains(reversed) && !pdf.Contains(m.Currency))
            problems.Add($"neither {symbol} nor {m.Currency} appears — the currency is not named, or its sign did not print");

        if (problems.Count > 0) Assert.Fail($"The {what}: " + string.Join("; ", problems) + ".");
    }

    // ── dates and times ───────────────────────────────────────────────

    private static DateTime At(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Unspecified);

    private static TimeZoneInfo Zone(string iana, string windows)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById(windows); }
    }

    /// <summary>
    /// Every date written d/m/yyyy or m/d/yyyy (/, . or -) in the text, read
    /// both ways round; a Thai Buddhist-era year (2570) is read as 2027.
    /// </summary>
    private static List<DateTime> FindDates(string text)
    {
        var found = new List<DateTime>();
        foreach (Match x in Regex.Matches(text, @"\b(\d{1,2})[/.\-](\d{1,2})[/.\-](\d{4})\b"))
        {
            int a = int.Parse(x.Groups[1].Value), b = int.Parse(x.Groups[2].Value), y = int.Parse(x.Groups[3].Value);
            if (y > 2400) y -= 543;
            foreach (var (d, mo) in new[] { (a, b), (b, a) })
                if (mo is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, mo))
                    found.Add(new DateTime(y, mo, d));
        }

        // Also "15 Jan 2027" / "Jan 15, 2027".
        foreach (Match x in Regex.Matches(text, @"\b(\d{1,2}\s+[A-Za-z]{3,9}\s+\d{4}|[A-Za-z]{3,9}\s+\d{1,2},?\s+\d{4})\b"))
            if (DateTime.TryParse(x.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                found.Add(d.Date);
        return found;
    }

    /// <summary>The lead's Tasks tab → a task for me, due <paramref name="dueLocal"/> (workspace time).</summary>
    private static async Task AddLeadTaskAsync(IPage page, Guid leadId, string subject, DateTime dueLocal)
    {
        await page.GotoAsync($"/Leads/Detail/{leadId}");
        await page.Locator("#tasks-tab").ClickAsync();
        await Assertions.Expect(page.Locator("#tasks")).ToBeVisibleAsync();

        await page.Locator("#TaskInput_ActivityType").SelectOptionAsync("Task");
        await page.Locator("#TaskInput_Subject").FillAsync(subject);
        await page.Locator("#TaskInput_DueDate").FillAsync(dueLocal.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture));

        var assign = page.Locator("#TaskInput_AssignedToUserId");
        if (await assign.CountAsync() > 0) await assign.SelectOptionAsync("");

        await page.Locator("#tasks form button[type=submit]").First.ClickAsync();
        await Ui.ExpectSuccessAsync(page, "Task created");
    }

    /// <summary>The lead's Tasks tab lists the task with <paramref name="shown"/> as its due time.</summary>
    private static async Task ExpectShownAsync(IPage page, Guid leadId, string subject, DateTime shown)
    {
        await page.GotoAsync($"/Leads/Detail/{leadId}");
        await page.Locator("#tasks-tab").ClickAsync();
        var item = page.Locator("#tasks .list-group-item", new() { HasText = subject });
        await Assertions.Expect(item).ToHaveCountAsync(1);

        var text = Regex.Replace(await item.InnerTextAsync(), @"\s+", " ").Trim();
        Assert.That(Ui.FindDateTimes(text).Contains(shown), Is.True,
            $"Expected \"{subject}\" to show as due {shown:dd/MM/yyyy HH:mm} (workspace time), but it shows: \"{text}\"");
    }
}
