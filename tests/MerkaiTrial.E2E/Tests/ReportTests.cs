// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/ReportTests.cs
//
// NEW (111). The reports add up — checked figure by figure on data whose
// right answers are known.
//
// Workspace R (India / INR, an admin and the rep Ravi) is used by this
// test only, so every record in it is one made here. Through the API:
//
//   d1  open, Discovery, ₹1,00,000, 20% — opened 420 days ago, still open
//   d2  open, Proposal,  ₹50,000,  40% — Ravi's; has the Vertical
//   d3  WON  (Closed Won)        ₹30,000 — closed 15 days ago, EDITED today;
//                                           came from a lead made 6 days
//                                           before the deal
//   d4  WON  ("Contract Signed", the workspace's OWN won stage) ₹20,000 — Ravi's
//   d5  LOST (Closed Lost)       ₹10,000 — closed 5 days ago
//   d6  won 400 days ago, EDITED today — must NOT count as won in the period
//   d7  open, in USD — must be left out of the money, and said so
//   Invoice 1  ₹3,000 issued two months ago, paid in full TODAY
//   Invoice 2  ₹3,000 issued last month, ₹1,000 paid last month, due 40 days ago
//   Leads      one converted (INR), one open (INR), one in USD
//
// Period: the first day of the month two months ago → today.
//
// Each report's figures are read from the API and compared with the right
// answer; the Pipeline page is opened to see the same figures, the stage
// names and the currency note; the Reports page shows the categories.
//
// And the currency rule (one currency per workspace): a lead, deal, quote
// and invoice SENT in USD to an INR workspace are stored in INR, and the
// lead form has no currency picker.
// =====================================================================

using System.Globalization;
using System.Text.Json;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Reports")]
public class ReportTests : E2ETest
{
    private const string CustomWonStage = "Contract Signed";

    [Test]
    public async Task Every_report_adds_up_on_known_data()
    {
        var r = await Step("Workspace R (India, admin + Ravi) — built the first time it is needed",
            WorldBuilder.EnsureWorkspaceRAsync);
        var tenantId = await Db.TenantIdAsync(r.Name);
        var api = await Api.AsAsync(r.Admin);
        api.WaitWhenPaced = true;
        var rep = r.People["rep"];
        var adminId = (await Db.UserIdAsync(r.Admin.Email)).ToString();
        var repId = (await Db.UserIdAsync(rep.Email)).ToString();
        var m = Random.Shared.Next(10000, 99999).ToString(CultureInfo.InvariantCulture);

        var today = DateTime.UtcNow.Date;
        var thisMonth = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var lastMonth = thisMonth.AddMonths(-1);
        var twoMonthsAgo = thisMonth.AddMonths(-2);
        var query = $"from={twoMonthsAgo:yyyy-MM-dd}&to={today:yyyy-MM-dd}";

        // ── the records ───────────────────────────────────────────────
        var customKey = await Step($"The workspace's own won stage, \"{CustomWonStage}\"", async () =>
        {
            var s = await api.PostAsync("/api/pipeline-stages", new { name = CustomWonStage, probability = 100, category = "Won" });
            Assert.That(s.IsSuccess, Is.True, $"Adding the stage failed: {s}");
            return await ReportData.StageKeyAsync(tenantId, CustomWonStage);
        });

        var vertical = await ReportData.AnyVerticalAsync();

        var (l1, l2, l3) = await Step("Three leads: one converted, one open, one in USD", async () =>
        {
            var a = await ApiRecords.LeadAsync(api, tenantId, $"Report lead converted {m}", "rl1" + m);
            var b = await ApiRecords.LeadAsync(api, tenantId, $"Report lead open {m}", "rl2" + m);
            var c = await ApiRecords.LeadAsync(api, tenantId, $"Report lead USD {m}", "rl3" + m);
            return (a, b, c);
        });

        var d = await Step("Seven deals (made through the API)", async () =>
        {
            var contact = await ApiRecords.ContactAsync(api, tenantId, $"Report {m}", "rc" + m);
            var ids = new Dictionary<string, Guid>();
            foreach (var (key, owner) in new[] { ("d1", adminId), ("d2", repId), ("d3", adminId), ("d4", repId),
                                                 ("d5", adminId), ("d6", adminId), ("d7", adminId) })
                ids[key] = await ApiRecords.DealAsync(api, tenantId, contact, $"Report {key} {m}", owner);
            return ids;
        });

        var (inv1, inv2) = await Step("Two invoices on d2, issued; invoice 1 paid in full, invoice 2 ₹1,000", async () =>
        {
            var i1 = await ApiRecords.InvoiceAsync(api, d["d2"], $"Report line 1 {m}");
            var i2 = await ApiRecords.InvoiceAsync(api, d["d2"], $"Report line 2 {m}");
            foreach (var i in new[] { i1, i2 })
            {
                var issued = await api.PostAsync($"/api/invoices/{i}/issue");
                Assert.That(issued.IsSuccess, Is.True, $"Issuing invoice {i} failed: {issued}");
            }
            await PayAsync(api, i1, 3000m);
            await PayAsync(api, i2, 1000m);
            return (i1, i2);
        });

        await Step("Put the records in their known state (dates, stages, values, one USD record each)", async () =>
        {
            var d3Created = today.AddDays(-45);
            await ReportData.SetLeadAsync(l1, d3Created.AddDays(-6), converted: true);
            await ReportData.SetLeadAsync(l2, today.AddDays(-2));
            await ReportData.SetLeadAsync(l3, today.AddDays(-2), currency: "USD");

            // Paying invoice 1 in full may have moved d2 to won on its own — put it back.
            await ReportData.SetDealAsync(d["d1"], "Discovery", 20, 100000m, today.AddDays(-420), null, today.AddDays(-10));
            await ReportData.SetDealAsync(d["d2"], "Proposal", 40, 50000m, today.AddDays(-20), null, today, verticalId: vertical.Id);
            await ReportData.SetDealAsync(d["d3"], "ClosedWon", 100, 30000m, d3Created, today.AddDays(-15), today, leadId: l1);
            await ReportData.SetDealAsync(d["d4"], customKey, 100, 20000m, today.AddDays(-32), today.AddDays(-12), today.AddDays(-12));
            await ReportData.SetDealAsync(d["d5"], "ClosedLost", 0, 10000m, today.AddDays(-15), today.AddDays(-5), today.AddDays(-5));
            await ReportData.SetDealAsync(d["d6"], "ClosedWon", 100, 99000m, today.AddDays(-430), today.AddDays(-400), today);
            await ReportData.SetDealAsync(d["d7"], "Discovery", 20, 7000m, today.AddDays(-3), null, today.AddDays(-3), currency: "USD");

            await ReportData.SetInvoiceDatesAsync(inv1, twoMonthsAgo.AddDays(9), twoMonthsAgo.AddDays(39));
            await ReportData.SetPaymentDateAsync(inv1, today);
            await ReportData.SetInvoiceDatesAsync(inv2, lastMonth.AddDays(9), today.AddDays(-40));
            await ReportData.SetPaymentDateAsync(inv2, lastMonth.AddDays(11));
        });

        var problems = new List<string>();
        void Expect(string what, object? actual, object? expected)
        {
            if (!Equals(Norm(actual), Norm(expected))) problems.Add($"{what}: {actual} (expected {expected})");
        }

        // ── pipeline ─────────────────────────────────────────────────
        await Step("Pipeline: every open deal (the 420-day-old one too), won in the period by stage TYPE, USD left out", async () =>
        {
            var j = await ReportAsync(api, $"/api/reports/pipeline-summary?{query}");
            Expect("pipeline: open deals", Int(j, "totalActiveDeals"), 2);
            Expect("pipeline: open value", Dec(j, "totalPipelineValue"), 150000m);
            Expect("pipeline: weighted value", Dec(j, "weightedPipelineValue"), 40000m);
            Expect("pipeline: won in period (Closed Won + Contract Signed; not the deal won 400 days ago)", Int(j, "wonCount"), 2);
            Expect("pipeline: won value", Dec(j, "wonValue"), 50000m);
            Expect("pipeline: lost in period", Int(j, "lostCount"), 1);
            Expect("pipeline: records left out (USD)", Int(j, "excludedOtherCurrency"), 1);
            Expect("pipeline: currency", Str(j, "currency"), "INR");

            var stage = j.GetProperty("stages").EnumerateArray()
                .FirstOrDefault(s => Str(s, "stageName") == CustomWonStage);
            if (stage.ValueKind != JsonValueKind.Object) problems.Add($"pipeline: no \"{CustomWonStage}\" stage in the report");
            else
            {
                Expect($"pipeline: \"{CustomWonStage}\" type", Str(stage, "category"), "Won");
                Expect($"pipeline: \"{CustomWonStage}\" deals", Int(stage, "count"), 1);
            }
        });

        await Step($"Pipeline, Vertical = {vertical.Name}: only d2", async () =>
        {
            var j = await ReportAsync(api, $"/api/reports/pipeline-summary?{query}&vertical={Uri.EscapeDataString(vertical.Name)}");
            Expect("pipeline (vertical): open deals", Int(j, "totalActiveDeals"), 1);
            Expect("pipeline (vertical): open value", Dec(j, "totalPipelineValue"), 50000m);
            Expect("pipeline (vertical): won", Int(j, "wonCount"), 0);
        });

        // ── win / loss and reps ──────────────────────────────────────
        await Step("Win / Loss: by the date each deal CLOSED, by owner", async () =>
        {
            var j = await ReportAsync(api, $"/api/reports/win-loss?{query}");
            Expect("win/loss: won", Int(j, "totalWon"), 2);
            Expect("win/loss: lost", Int(j, "totalLost"), 1);
            Expect("win/loss: win rate", Dbl(j, "overallWinRate"), 66.7);
            Expect("win/loss: won value", Dec(j, "totalWonValue"), 50000m);
            var admin = Row(j, "byOwner", "ownerName", r.Admin.FullName);
            var ravi = Row(j, "byOwner", "ownerName", rep.FullName);
            Expect($"win/loss: {r.Admin.FullName} won/lost (rows: {Names(j, "byOwner", "ownerName")})",
                admin is null ? null : $"{Int(admin.Value, "won")}/{Int(admin.Value, "lost")}", "1/1");
            Expect($"win/loss: {rep.FullName} won/lost", ravi is null ? null : $"{Int(ravi.Value, "won")}/{Int(ravi.Value, "lost")}", "1/0");
        });

        await Step("Sales performance by rep: open today, won/lost in the period", async () =>
        {
            var j = await ReportAsync(api, $"/api/reports/rep-performance?{query}");
            var admin = Row(j, "reps", "ownerName", r.Admin.FullName);
            var ravi = Row(j, "reps", "ownerName", rep.FullName);
            if (admin is null) problems.Add($"reps: no row for {r.Admin.FullName} (rows: {Names(j, "reps", "ownerName")})");
            else
            {
                Expect("reps: admin active", Int(admin.Value, "active"), 1);
                Expect("reps: admin won", Int(admin.Value, "won"), 1);
                Expect("reps: admin lost", Int(admin.Value, "lost"), 1);
                Expect("reps: admin pipeline", Dec(admin.Value, "pipelineValue"), 100000m);
                Expect("reps: admin won value", Dec(admin.Value, "wonValue"), 30000m);
            }
            if (ravi is null) problems.Add($"reps: no row for {rep.FullName} (rows: {Names(j, "reps", "ownerName")})");
            else
            {
                Expect("reps: Ravi active", Int(ravi.Value, "active"), 1);
                Expect("reps: Ravi won (own won stage)", Int(ravi.Value, "won"), 1);
                Expect("reps: Ravi pipeline", Dec(ravi.Value, "pipelineValue"), 50000m);
                Expect("reps: Ravi won value", Dec(ravi.Value, "wonValue"), 20000m);
            }
        });

        // ── money ────────────────────────────────────────────────────
        await Step("Invoiced by issue month; collected by the month the money ARRIVED", async () =>
        {
            var j = await ReportAsync(api, $"/api/reports/revenue-by-period?{query}");
            Expect("revenue: invoiced", Dec(j, "totalInvoiced"), 6000m);
            Expect("revenue: collected", Dec(j, "totalCollected"), 4000m);
            Expect("revenue: outstanding", Dec(j, "totalOutstanding"), 2000m);

            string Label(DateTime month) => month.ToString("MMM yyyy", CultureInfo.InvariantCulture);
            foreach (var (month, invoiced, collected) in new[]
                     { (twoMonthsAgo, 3000m, 0m), (lastMonth, 3000m, 1000m), (thisMonth, 0m, 3000m) })
            {
                var p = Row(j, "periods", "label", Label(month));
                if (p is null) { problems.Add($"revenue: no row for {Label(month)}"); continue; }
                Expect($"revenue: {Label(month)} invoiced", Dec(p.Value, "invoiced"), invoiced);
                Expect($"revenue: {Label(month)} collected", Dec(p.Value, "collected"), collected);
            }
        });

        await Step("Outstanding: invoice 2's ₹2,000, 40 days overdue", async () =>
        {
            var j = await ReportAsync(api, $"/api/reports/outstanding-invoices?{query}");
            Expect("outstanding: total", Dec(j, "totalOutstanding"), 2000m);
            Expect("outstanding: invoices", Int(j, "totalCount"), 1);
            var bucket = Row(j, "buckets", "label", "31–60 days");
            Expect("outstanding: 31–60 days", bucket is null ? null : Dec(bucket.Value, "amount"), 2000m);
        });

        await Step("Revenue by vertical: d2's vertical has its pipeline and both invoices", async () =>
        {
            var j = await ReportAsync(api, $"/api/reports/revenue-by-vertical?{query}");
            var v = Row(j, "verticals", "verticalName", vertical.Name);
            if (v is null) { problems.Add($"by vertical: no row for {vertical.Name} (rows: {Names(j, "verticals", "verticalName")})"); return; }
            Expect("by vertical: deals", Int(v.Value, "dealCount"), 1);
            Expect("by vertical: pipeline", Dec(v.Value, "pipelineValue"), 50000m);
            Expect("by vertical: invoiced", Dec(v.Value, "invoicedValue"), 6000m);
            Expect("by vertical: collected", Dec(v.Value, "collectedValue"), 4000m);
        });

        // ── leads and speed ──────────────────────────────────────────
        await Step("Lead sources: the two INR leads, one converted; the USD lead left out", async () =>
        {
            var j = await ReportAsync(api, $"/api/reports/lead-sources?{query}");
            Expect("lead sources: leads", Int(j, "totalLeads"), 2);
            Expect("lead sources: converted", Int(j, "totalConverted"), 1);
            Expect("lead sources: left out (USD)", Int(j, "excludedOtherCurrency"), 1);
        });

        await Step("Deal velocity: 3 deals closed in the period; deal → close 20 days, lead → deal 6, lead → close 36", async () =>
        {
            var j = await ReportAsync(api, $"/api/reports/deal-velocity?{query}");
            Expect("velocity: deals", Int(j, "dealsAnalyzed"), 3);
            Expect("velocity: deal → close", Dbl(j, "avgDaysDealToClose"), 20.0);
            Expect("velocity: lead → deal", Dbl(j, "avgDaysLeadToDeal"), 6.0);
            Expect("velocity: lead → close", Dbl(j, "avgDaysLeadToClose"), 36.0);
        });

        await Step("Funnel: created / issued in the period", async () =>
        {
            var j = await ReportAsync(api, $"/api/reports/sales-funnel?{query}");
            Expect("funnel: leads", Int(j, "totalLeads"), 3);
            Expect("funnel: converted", Int(j, "convertedLeads"), 1);
            Expect("funnel: deals created in the period (not the two old ones)", Int(j, "totalDeals"), 5);
            Expect("funnel: invoices issued", Int(j, "totalInvoices"), 2);
            Expect("funnel: paid", Int(j, "paidInvoices"), 1);
        });

        // ── the pages ────────────────────────────────────────────────
        var page = await SignedInAsync(r.Admin);
        await Step("The Pipeline page shows the stage names, the figures and the USD note", async () =>
        {
            await page.GotoAsync($"/Reports/Pipeline/Index?FromDate={twoMonthsAgo:yyyy-MM-dd}&ToDate={today:yyyy-MM-dd}");
            await Assertions.Expect(page.Locator("[data-figure='open-count']")).ToHaveTextAsync("2");
            var row = page.Locator("#pipelineStages tr", new() { HasText = CustomWonStage });
            await Assertions.Expect(row).ToHaveCountAsync(1);
            await Assertions.Expect(row.Locator("[data-col='count']")).ToHaveTextAsync("1");
            await Assertions.Expect(page.Locator("#reportCurrencyNote")).ToContainTextAsync("USD");
            await Assertions.Expect(page.Locator("#reportPeriodHint")).ToContainTextAsync("every open deal");
        });

        await Step("The Reports page groups the reports by category", async () =>
        {
            await page.GotoAsync("/Reports");
            foreach (var c in new[] { "Sales Performance", "Lead Analytics", "Revenue & Finance", "Customer Insights" })
                await Assertions.Expect(page.Locator($"section[data-category='{c}']")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("section[data-category='Revenue & Finance'] [data-report='revenue']")).ToHaveCountAsync(1);
        });

        if (problems.Count > 0)
            Assert.Fail($"{problems.Count} report figure(s) are wrong:\n  • " + string.Join("\n  • ", problems));
    }

    [Test]
    public async Task Everything_is_stored_in_the_workspace_currency()
    {
        var a = World.A;   // India / INR
        var tenantId = await Db.TenantIdAsync(a.Name);
        var api = await Api.AsAsync(a.Admin);
        api.WaitWhenPaced = true;
        var m = Random.Shared.Next(10000, 99999).ToString(CultureInfo.InvariantCulture);
        var problems = new List<string>();

        async Task Check(string what, string table, Guid id)
        {
            var stored = await ReportData.CurrencyAsync(table, id);
            if (!string.Equals(stored, a.Currency, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{what} sent in USD was stored in '{stored}', not {a.Currency}");
        }

        await Step("A lead, deal, quote and invoice SENT in USD are stored in INR", async () =>
        {
            var lead = await api.PostAsync("/api/leads", new
            {
                tenantId, fullName = $"USD lead {m}", email = World.EmailFor("A", "usdlead" + m),
                phone = "+9198" + Random.Shared.Next(10000000, 99999999), companyName = $"USD Co {m}",
                estimatedValue = 1000m, currency = "USD",
            });
            Assert.That(lead.IsSuccess, Is.True, $"Adding the lead failed: {lead}");
            await Check("A lead", "Leads", ApiIds.IdOf(lead));

            var contact = await ApiRecords.ContactAsync(api, tenantId, $"USD {m}", "usdc" + m);
            var deal = await api.PostAsync("/api/deals", new
            {
                tenantId = tenantId.ToString(), contactId = contact.ToString(), title = $"USD deal {m}",
                expectedValue = 5000m, currency = "USD", expectedCloseDateUtc = DateTime.UtcNow.Date.AddDays(30),
            });
            Assert.That(deal.IsSuccess, Is.True, $"Adding the deal failed: {deal}");
            var dealId = ApiIds.IdOf(deal);
            await Check("A deal", "Deals", dealId);

            var quote = await api.PostAsync("/api/quotes", new
            {
                dealId, issueDateUtc = DateTime.UtcNow.Date, expiresAtUtc = DateTime.UtcNow.Date.AddDays(30), currency = "USD",
                items = new[] { new { name = $"USD line {m}", unitPrice = 100m, quantity = 1m, taxRate = 0m } },
            });
            Assert.That(quote.IsSuccess, Is.True, $"Adding the quote failed: {quote}");
            await Check("A quote", "Quotes", ApiIds.IdOf(quote));

            var invoice = await api.PostAsync("/api/invoices", new
            {
                dealId, issueDateUtc = DateTime.UtcNow.Date, dueDateUtc = DateTime.UtcNow.Date.AddDays(14), currency = "USD",
                lines = new[] { new { name = $"USD invoice line {m}", unitPrice = 100m, quantity = 1m, taxRate = 0m } },
                sendImmediately = false,
            });
            Assert.That(invoice.IsSuccess, Is.True, $"Adding the invoice failed: {invoice}");
            await Check("An invoice", "Invoices", ApiIds.IdOf(invoice));
        });

        await Step("The lead form has no currency picker; it shows the workspace's currency", async () =>
        {
            var page = await SignedInAsync(a.Admin);
            await page.GotoAsync("/Leads/Create");
            await Assertions.Expect(page.Locator("#leadCurrency")).ToContainTextAsync(a.Currency);
            if (await page.Locator("select#Input_CurrencyId").CountAsync() > 0)
                problems.Add("The lead form still has a currency drop-down.");
        });

        if (problems.Count > 0) Assert.Fail(string.Join("\n", problems));
    }

    // ── helpers ───────────────────────────────────────────────────────

    private static async Task PayAsync(ApiCaller api, Guid invoiceId, decimal amount)
    {
        var p = await api.PostAsync($"/api/invoices/{invoiceId}/payments",
            new { amount, method = "BankTransfer", paidAtUtc = DateTime.UtcNow });
        Assert.That(p.IsSuccess, Is.True, $"Recording a payment of {amount} on invoice {invoiceId} failed: {p}");
    }

    private static async Task<JsonElement> ReportAsync(ApiCaller api, string path)
    {
        var res = await api.GetAsync(path);
        Assert.That(res.IsSuccess, Is.True, $"{path} failed: {res}");
        return res.Json();
    }

    private static JsonElement? Row(JsonElement j, string list, string field, string value)
    {
        if (!j.TryGetProperty(list, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var e in arr.EnumerateArray())
            if (string.Equals(Str(e, field), value, StringComparison.Ordinal)) return e;
        return null;
    }

    private static string Names(JsonElement j, string list, string field) =>
        j.TryGetProperty(list, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? string.Join(", ", arr.EnumerateArray().Select(e => Str(e, field) ?? "?"))
            : "none";

    private static string? Str(JsonElement j, string p) =>
        j.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement j, string p) =>
        j.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static decimal? Dec(JsonElement j, string p) =>
        j.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
    private static double? Dbl(JsonElement j, string p) =>
        j.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Round(v.GetDouble(), 1) : null;

    /// <summary>Numbers compare by value (30000 == 30000.00); everything else as text.</summary>
    private static object? Norm(object? x) => x switch
    {
        null => null,
        decimal dm => dm.ToString("0.##", CultureInfo.InvariantCulture),
        double db => db.ToString("0.##", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        _ => x.ToString(),
    };
}
