// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/PriorityReportTests.cs
//
// NEW (112). The five new reports, checked figure by figure on data whose
// right answers are known. Workspace S (India / INR, admin only) is used
// by this test alone.
//
// T = today. Period = the first day of the month two months ago → T.
//
// DEALS (made through the API, then put in a known state)
//   s1  Proposal     ₹40,000  40%  moved to Proposal 60 days ago, nothing since   → STALLED (stage and silence)
//   s2  Discovery    ₹20,000  20%  opened 10 days ago, close date 5 days ago      → STALLED (past close)
//   s3  Negotiation  ₹30,000  60%  moved 5 days ago, a note 2 days ago            → fine
//   s4  Closed Won   ₹15,000       won today                                      → "won this month"
//   s5  Qualification ₹10,000 30%  opened 3 days ago, close in 300 days           → fine; forecast "Later"
//
// QUOTES on s3 (issued in the period)
//   q1 Accepted ₹10,000 · q2 Rejected ₹5,000 · q3 Sent ₹8,000 (₹10,000 less ₹2,000,
//   expires in 3 days) · q4 Draft ₹3,000 · q5 Viewed ₹6,000 (expired 2 days ago)
//
// INVOICES on s3, ₹3,000 each
//   i1 issued T−30, due T−16, paid in full T−20 (bank transfer) → 10 days, on time
//   i2 issued T−25, due T−10, paid in full T−5  (cash)          → 20 days, late
//   i3 issued T−20, due T−6,  ₹1,000 paid T−8  (bank transfer)  → ₹2,000 overdue, 6 days
//
// LEADS
//   one Qualified (T−40); two Converted (T−30 → T−20, T−10 → T−6); one Open (T−5);
//   one Open received 400 days ago (outside the period)
// =====================================================================

using System.Globalization;
using System.Text.Json;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Reports")]
public class PriorityReportTests : E2ETest
{
    [Test]
    public async Task The_five_new_reports_add_up_on_known_data()
    {
        var w = await Step("Workspace S (India, admin only) — built the first time it is needed",
            WorldBuilder.EnsureWorkspaceSAsync);
        var tenantId = await Db.TenantIdAsync(w.Name);
        var api = await Api.AsAsync(w.Admin);
        api.WaitWhenPaced = true;
        var m = Random.Shared.Next(10000, 99999).ToString(CultureInfo.InvariantCulture);

        var now = DateTime.UtcNow;
        var T = now.Date;
        var thisMonth = new DateTime(T.Year, T.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var from = thisMonth.AddMonths(-2);
        var period = $"from={from:yyyy-MM-dd}&to={T:yyyy-MM-dd}";
        var customerLast = $"Priority {m}";

        // ── records, through the API ─────────────────────────────────
        var d = await Step("Five deals", async () =>
        {
            var contact = await ApiRecords.ContactAsync(api, tenantId, customerLast, "pc" + m);
            var ids = new Dictionary<string, Guid>();
            foreach (var key in new[] { "s1", "s2", "s3", "s4", "s5" })
                ids[key] = await ApiRecords.DealAsync(api, tenantId, contact, $"Priority {key} {m}");
            return ids;
        });

        var q = await Step("Five quotes on s3", async () =>
        {
            var ids = new Dictionary<string, Guid>();
            foreach (var key in new[] { "q1", "q2", "q3", "q4", "q5" })
                ids[key] = await ApiRecords.QuoteAsync(api, d["s3"], $"Priority {key} {m}");
            return ids;
        });

        var inv = await Step("Three invoices on s3, issued and paid (two in full, one ₹1,000)", async () =>
        {
            var ids = new Dictionary<string, Guid>();
            foreach (var key in new[] { "i1", "i2", "i3" })
            {
                ids[key] = await ApiRecords.InvoiceAsync(api, d["s3"], $"Priority {key} {m}");
                var issued = await api.PostAsync($"/api/invoices/{ids[key]}/issue");
                Assert.That(issued.IsSuccess, Is.True, $"Issuing {key} failed: {issued}");
            }
            await PayAsync(api, ids["i1"], 3000m, "BankTransfer");
            await PayAsync(api, ids["i2"], 3000m, "Cash");
            await PayAsync(api, ids["i3"], 1000m, "BankTransfer");
            return ids;
        });

        var leads = await Step("Five leads", async () =>
        {
            var ids = new Dictionary<string, Guid>();
            foreach (var key in new[] { "L1", "L2", "L3", "L4", "L5" })
                ids[key] = await ApiRecords.LeadAsync(api, tenantId, $"Priority {key} {m}", "pl" + key + m);
            return ids;
        });

        // ── their known state ────────────────────────────────────────
        await Step("Put every record in its known state (dates, stages, statuses, history)", async () =>
        {
            // Deals last: paying invoice 1 in full may have moved s3 to won.
            await ReportData.SetDealAsync(d["s1"], "Proposal", 40, 40000m, T.AddDays(-90), null, T.AddDays(-60), expectedCloseUtc: T.AddDays(30));
            await ReportData.SetDealAsync(d["s2"], "Discovery", 20, 20000m, T.AddDays(-10), null, T.AddDays(-10), expectedCloseUtc: T.AddDays(-5));
            await ReportData.SetDealAsync(d["s3"], "Negotiation", 60, 30000m, T.AddDays(-50), null, T.AddDays(-2), expectedCloseUtc: T.AddDays(20));
            await ReportData.SetDealAsync(d["s4"], "ClosedWon", 100, 15000m, T.AddDays(-40), T, T, expectedCloseUtc: T);
            await ReportData.SetDealAsync(d["s5"], "Qualification", 30, 10000m, T.AddDays(-3), null, T.AddDays(-3), expectedCloseUtc: T.AddDays(300));

            foreach (var id in d.Values) await ReportData.ClearDealTouchesAsync(id);
            await ReportData.AddStageChangeAsync(tenantId, d["s1"], "Discovery", "Proposal", T.AddDays(-60));
            await ReportData.AddStageChangeAsync(tenantId, d["s3"], "Proposal", "Negotiation", T.AddDays(-5));
            await ReportData.AddDealNoteAsync(tenantId, d["s3"], T.AddDays(-2));

            // QuoteStatus: 0 Draft, 1 Sent, 2 Viewed, 3 Accepted, 4 Rejected
            await ReportData.SetQuoteAsync(q["q1"], 3, 10000m, 0m, 10000m, T.AddDays(-10), T.AddDays(20));
            await ReportData.SetQuoteAsync(q["q2"], 4, 5000m, 0m, 5000m, T.AddDays(-12), T.AddDays(18));
            await ReportData.SetQuoteAsync(q["q3"], 1, 10000m, 2000m, 8000m, T.AddDays(-3), T.AddDays(3));
            await ReportData.SetQuoteAsync(q["q4"], 0, 3000m, 0m, 3000m, T.AddDays(-1), T.AddDays(29));
            await ReportData.SetQuoteAsync(q["q5"], 2, 6000m, 0m, 6000m, T.AddDays(-20), T.AddDays(-2));

            await ReportData.SetInvoiceDatesAsync(inv["i1"], T.AddDays(-30), T.AddDays(-16));
            await ReportData.SetPaymentDateAsync(inv["i1"], T.AddDays(-20));
            await ReportData.SetInvoiceDatesAsync(inv["i2"], T.AddDays(-25), T.AddDays(-10));
            await ReportData.SetPaymentDateAsync(inv["i2"], T.AddDays(-5));
            await ReportData.SetInvoiceDatesAsync(inv["i3"], T.AddDays(-20), T.AddDays(-6));
            await ReportData.SetPaymentDateAsync(inv["i3"], T.AddDays(-8));

            var open = await ReportData.LeadStatusKeyAsync(tenantId, 0);
            var qualified = await ReportData.LeadStatusKeyAsync(tenantId, 1);
            var converted = await ReportData.LeadStatusKeyAsync(tenantId, 3);
            await ReportData.SetLeadStateAsync(leads["L1"], qualified, T.AddDays(-40), null);
            await ReportData.SetLeadStateAsync(leads["L2"], converted, T.AddDays(-30), T.AddDays(-20));
            await ReportData.SetLeadStateAsync(leads["L3"], converted, T.AddDays(-10), T.AddDays(-6));
            await ReportData.SetLeadStateAsync(leads["L4"], open, T.AddDays(-5), null);
            await ReportData.SetLeadStateAsync(leads["L5"], open, T.AddDays(-400), null);
        });

        var problems = new List<string>();
        void Expect(string what, object? actual, object? expected)
        {
            if (!Equals(Norm(actual), Norm(expected))) problems.Add($"{what}: {actual ?? "(missing)"} (expected {expected})");
        }

        // ── 11. Stalled Deals ────────────────────────────────────────
        await Step("Stalled Deals (30 days): s1 and s2, ₹60,000", async () =>
        {
            var j = await ReportAsync(api, "/api/reports/stalled-deals?days=30");
            Expect("stalled: open deals", Int(j, "openDeals"), 4);
            Expect("stalled: count", Int(j, "stalledCount"), 2);
            Expect("stalled: value", Dec(j, "stalledValue"), 60000m);
            Expect("stalled: past close date", Int(j, "overdueCloseCount"), 1);
            var s1 = Row(j, "deals", "dealId", d["s1"].ToString());
            Expect("stalled: s1 days in stage", s1 is null ? null : Int(s1.Value, "daysInStage"), 60);
            var s2 = Row(j, "deals", "dealId", d["s2"].ToString());
            Expect("stalled: s2 days past close", s2 is null ? null : Int(s2.Value, "daysPastClose"), 5);
            if (Row(j, "deals", "dealId", d["s3"].ToString()) is not null) problems.Add("stalled: s3 (moved 5 days ago, a note 2 days ago) is listed as stalled");
        });

        // ── 12. Lead Funnel & Conversion ─────────────────────────────
        await Step("Lead Funnel: 4 leads in the period, 2 converted in 7 days on average", async () =>
        {
            var j = await ReportAsync(api, $"/api/reports/lead-funnel?{period}");
            Expect("lead funnel: leads", Int(j, "totalLeads"), 4);
            Expect("lead funnel: converted", Int(j, "converted"), 2);
            Expect("lead funnel: conversion rate", Dbl(j, "conversionRate"), 50.0);
            Expect("lead funnel: qualified or better", Int(j, "qualifiedOrBetter"), 3);
            Expect("lead funnel: average days to convert", Dbl(j, "avgDaysToConvert"), 7.0);
            Expect("lead funnel: disqualified", Int(j, "disqualified"), 0);
        });

        // ── 13. Quote Performance ────────────────────────────────────
        await Step("Quote Performance: 4 sent, 1 accepted (25%), discount 6.5%, one expiring, one past expiry", async () =>
        {
            var j = await ReportAsync(api, $"/api/reports/quote-performance?{period}");
            Expect("quotes: total", Int(j, "totalQuotes"), 5);
            Expect("quotes: sent", Int(j, "sentQuotes"), 4);
            Expect("quotes: accepted", Int(j, "accepted"), 1);
            Expect("quotes: rejected", Int(j, "rejected"), 1);
            Expect("quotes: open", Int(j, "open"), 2);
            Expect("quotes: not sent", Int(j, "notSent"), 1);
            Expect("quotes: acceptance rate", Dbl(j, "acceptanceRate"), 25.0);
            Expect("quotes: value sent", Dec(j, "quotedValue"), 29000m);
            Expect("quotes: accepted value", Dec(j, "acceptedValue"), 10000m);
            Expect("quotes: open value", Dec(j, "openValue"), 14000m);
            Expect("quotes: average discount", Dbl(j, "avgDiscountPct"), 6.5);
            Expect("quotes: expiring in 7 days", Int(j, "expiringSoon"), 1);
            Expect("quotes: past expiry, still open", Int(j, "pastExpiry"), 1);
        });

        // ── 14. Invoice & Collections ────────────────────────────────
        await Step("Invoice & Collections: ₹9,000 invoiced, ₹7,000 in, 15 days to pay, 50% on time, ₹2,000 overdue", async () =>
        {
            var j = await ReportAsync(api, $"/api/reports/invoice-collections?{period}");
            Expect("collections: invoices issued", Int(j, "invoicesIssued"), 3);
            Expect("collections: invoiced", Dec(j, "invoiced"), 9000m);
            Expect("collections: paid on these", Dec(j, "paidOnThese"), 7000m);
            Expect("collections: collection rate", Dbl(j, "collectionRate"), 77.8);
            Expect("collections: money received", Dec(j, "collected"), 7000m);
            Expect("collections: paid in full", Int(j, "paidInvoices"), 2);
            Expect("collections: average days to pay", Dbl(j, "avgDaysToPay"), 15.0);
            Expect("collections: paid on time", Dbl(j, "paidOnTimePct"), 50.0);
            Expect("collections: overdue invoices", Int(j, "overdueCount"), 1);
            Expect("collections: overdue amount", Dec(j, "overdueAmount"), 2000m);
            var bank = Row(j, "byMethod", "method", "BankTransfer");
            Expect("collections: bank transfer", bank is null ? null : Dec(bank.Value, "amount"), 4000m);
            var cash = Row(j, "byMethod", "method", "Cash");
            Expect("collections: cash", cash is null ? null : Dec(cash.Value, "amount"), 3000m);
            var cust = Row(j, "overdueCustomers", "customerName", $"E2E {customerLast}");
            Expect($"collections: overdue for E2E {customerLast}", cust is null ? null : Dec(cust.Value, "amount"), 2000m);
            Expect("collections: oldest overdue (days)", cust is null ? null : Int(cust.Value, "oldestDaysOverdue"), 6);
        });

        // ── 15. Revenue Forecast ─────────────────────────────────────
        await Step("Revenue Forecast (6 months): ₹1,00,000 open, ₹41,000 weighted, s2 overdue, s5 later, ₹15,000 won this month", async () =>
        {
            var j = await ReportAsync(api, "/api/reports/revenue-forecast?months=6");
            Expect("forecast: open deals", Int(j, "openDeals"), 4);
            Expect("forecast: open value", Dec(j, "totalValue"), 100000m);
            Expect("forecast: weighted", Dec(j, "weightedValue"), 41000m);
            Expect("forecast: won this month", Dec(j, "wonThisMonth"), 15000m);

            var overdue = Row(j, "buckets", "key", "overdue");
            Expect("forecast: overdue deals", overdue is null ? null : Int(overdue.Value, "count"), 1);
            Expect("forecast: overdue weighted", overdue is null ? null : Dec(overdue.Value, "weighted"), 4000m);
            var later = Row(j, "buckets", "key", "later");
            Expect("forecast: later deals", later is null ? null : Int(later.Value, "count"), 1);

            // s1 closes in 30 days (₹16,000 weighted), s3 in 20 (₹18,000) — same month or not.
            var expected = new Dictionary<string, decimal>();
            void Add(DateTime close, decimal weighted)
            {
                var key = close.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                expected[key] = expected.GetValueOrDefault(key) + weighted;
            }
            Add(T.AddDays(30), 16000m);
            Add(T.AddDays(20), 18000m);
            foreach (var (key, weighted) in expected)
            {
                var b = Row(j, "buckets", "key", key);
                Expect($"forecast: {key} weighted", b is null ? null : Dec(b.Value, "weighted"), weighted);
            }
        });

        // ── the pages ────────────────────────────────────────────────
        var page = await SignedInAsync(w.Admin);
        await Step("Each page shows its figures", async () =>
        {
            await page.GotoAsync("/Reports/StalledDeals/Index?Days=30");
            await Assertions.Expect(page.Locator("[data-figure='stalled-count']")).ToHaveTextAsync("2");
            await Assertions.Expect(page.Locator($"#stalledDeals tr[data-deal='{d["s1"]}'] [data-col='days-in-stage']")).ToHaveTextAsync("60");

            await page.GotoAsync($"/Reports/LeadFunnel/Index?FromDate={from:yyyy-MM-dd}&ToDate={T:yyyy-MM-dd}");
            await Assertions.Expect(page.Locator("[data-figure='leads']")).ToHaveTextAsync("4");

            await page.GotoAsync($"/Reports/QuotePerformance/Index?FromDate={from:yyyy-MM-dd}&ToDate={T:yyyy-MM-dd}");
            await Assertions.Expect(page.Locator("[data-figure='sent']")).ToHaveTextAsync("4");
            await Assertions.Expect(page.Locator("#quotesAttention tbody tr")).ToHaveCountAsync(2);

            await page.GotoAsync($"/Reports/InvoiceCollections/Index?FromDate={from:yyyy-MM-dd}&ToDate={T:yyyy-MM-dd}");
            await Assertions.Expect(page.Locator("[data-figure='days-to-pay']")).ToHaveTextAsync("15 d");
            await Assertions.Expect(page.Locator("#overdueCustomers")).ToContainTextAsync(customerLast);

            await page.GotoAsync("/Reports/Forecast/Index?Months=6");
            await Assertions.Expect(page.Locator("#forecastBuckets tr[data-bucket='overdue'] [data-col='count']")).ToHaveTextAsync("1");

            await page.GotoAsync("/Reports");
            foreach (var key in new[] { "stalled", "forecast", "leadfunnel", "quoteperformance", "collections" })
                await Assertions.Expect(page.Locator($"[data-report='{key}']")).ToHaveCountAsync(1);
        });

        if (problems.Count > 0)
            Assert.Fail($"{problems.Count} report figure(s) are wrong:\n  • " + string.Join("\n  • ", problems));
    }

    // ── helpers ───────────────────────────────────────────────────────

    private static async Task PayAsync(ApiCaller api, Guid invoiceId, decimal amount, string method)
    {
        var p = await api.PostAsync($"/api/invoices/{invoiceId}/payments", new { amount, method, paidAtUtc = DateTime.UtcNow });
        Assert.That(p.IsSuccess, Is.True, $"Recording a {method} payment of {amount} on invoice {invoiceId} failed: {p}");
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
            if (string.Equals(Str(e, field), value, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    private static string? Str(JsonElement j, string p) =>
        j.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement j, string p) =>
        j.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static decimal? Dec(JsonElement j, string p) =>
        j.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
    private static double? Dbl(JsonElement j, string p) =>
        j.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Round(v.GetDouble(), 1) : null;

    private static object? Norm(object? x) => x switch
    {
        null => null,
        decimal dm => dm.ToString("0.##", CultureInfo.InvariantCulture),
        double db => db.ToString("0.##", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        _ => x.ToString(),
    };
}
