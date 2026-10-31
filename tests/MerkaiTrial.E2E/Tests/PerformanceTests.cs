// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/PerformanceTests.cs
//
// NEW (107 — performance). Is Merkai still quick when a workspace is BIG?
// All in the performance workspace (PerfWorld): 10,000 companies, 30,000
// contacts, 50,000 leads, 20,000 deals, 10,000 quotes, 5,000 invoices and
// 200,000 audit rows, spread over ten people, every stage and two years.
//
//   PERF-01  The dashboard, for the admin and for a rep.
//   PERF-02  Every list: first page, a search, a sort, a page far down.
//            And the "one query per row" check: the same list at 10 rows
//            and at 50 rows must need (almost) the same number of queries.
//   PERF-03  A record's own page: lead, deal, contact, company, quote,
//            invoice — each with years of history behind it.
//   PERF-04  Saving: add, edit, move and delete deals; add, edit and
//            delete leads; add contacts; add and delete quotes — 15 of
//            each, typical and slowest time, and the queries per save.
//   PERF-05  In a real browser: the main pages until they finish loading
//            (scripts, styles, everything the person waits for).
//   PERF-06  20 people at once for two minutes (ten people, two tabs
//            each): no errors, and the slowest 5% of answers stay quick.
//   PERF-07  SQL Server's own list of indexes it wished it had — a report
//            to read, never a failure.
//
// How a measurement is judged (fixed limits + the baseline file) is in
// Infrastructure/PerfMeter.cs. Every number goes to
// e2e-artifacts/perf-report.txt.
//
// Run just these: run-e2e.ps1 -Filter "Category=Performance"
// Everything else: run-e2e.ps1 -Filter "Category!=Performance"
// =====================================================================

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Performance")]
public class PerformanceTests : E2ETest
{
    private static string Q(string text) => Uri.EscapeDataString(text);

    private static Task ReadyAsync() => Step("The performance workspace is ready (filled on the first run only)", PerfWorld.EnsureAsync);

    // =================================================================
    // PERF-01
    // =================================================================

    [Test, Order(1)]
    public async Task Dashboard_loads_quickly_in_a_big_workspace()
    {
        await ReadyAsync();
        var metrics = new List<PerfMetric>();
        var problems = new List<string>();

        await Step("The admin's dashboard (everyone's numbers)", async () =>
        {
            using var admin = await WebSession.SignedInAsync(PerfWorld.P.Admin);
            metrics.Add(await PerfMeter.PageAsync(admin, "dashboard.admin", "Dashboard — admin", "/Dashboard"));
        });

        await Step("A rep's dashboard (their own numbers)", async () =>
        {
            using var rep = await WebSession.SignedInAsync(PerfWorld.P["rep01"]);
            metrics.Add(await PerfMeter.PageAsync(rep, "dashboard.rep", "Dashboard — rep", "/Dashboard"));
        });

        Finish("PERF-01 Dashboard", metrics, problems);
    }

    // =================================================================
    // PERF-02
    // =================================================================

    [Test, Order(2)]
    public async Task Every_list_stays_quick_first_page_search_sort_and_far_pages()
    {
        await ReadyAsync();
        var s = PerfWorld.Samples;
        var metrics = new List<PerfMetric>();
        var problems = new List<string>();

        var pages = new (string Key, string Label, string Path)[]
        {
            ("leads.first",        "Leads — first page",                       "/Leads?StatusFilter=all"),
            ("leads.search",       "Leads — search by name",                   $"/Leads?StatusFilter=all&SearchTerm={Q(s.LeadName)}"),
            ("leads.sort",         "Leads — sorted by value",                  "/Leads?StatusFilter=all&Sort=value&Dir=desc"),
            ("leads.far",          "Leads — page 500 of 10 per page",          "/Leads?StatusFilter=all&PageNumber=500&PageSize=10"),
            ("contacts.first",     "Contacts — first page",                    "/Contacts"),
            ("contacts.search",    "Contacts — search by name",                $"/Contacts?SearchTerm={Q(s.ContactName)}"),
            ("contacts.sort",      "Contacts — sorted by company",             "/Contacts?Sort=company&Dir=asc"),
            ("contacts.far",       "Contacts — page 300 of 10 per page",       "/Contacts?PageNumber=300&PageSize=10"),
            ("companies.first",    "Companies — first page",                   "/Companies"),
            ("companies.search",   "Companies — search by name",               $"/Companies?SearchTerm={Q(s.CompanyName)}"),
            ("companies.sort",     "Companies — sorted by number of contacts", "/Companies?Sort=contacts&Dir=desc"),
            ("companies.far",      "Companies — page 100 of 10 per page",      "/Companies?PageNumber=100&PageSize=10"),
            ("pipeline.board",     "Pipeline — board",                         "/Pipeline"),
            ("pipeline.search",    "Pipeline — board, search",                 $"/Pipeline?SearchTerm={Q(s.DealTitle)}"),
            ("pipeline.table",     "Pipeline — table",                         "/Pipeline?View=table"),
            ("pipeline.sort",      "Pipeline — table sorted by value",         "/Pipeline?View=table&Sort=value&Dir=desc"),
            ("pipeline.far",       "Pipeline — table page 200 of 10 per page", "/Pipeline?View=table&PageNumber=200&PageSize=10"),
            ("quotes.list",        "Quotes — list",                            "/Quotes"),
            ("quotes.board",       "Quotes — board",                           "/Quotes?View=board"),
            ("invoices.first",     "Invoices — first page",                    "/Invoices"),
            ("invoices.search",    "Invoices — search by number",              $"/Invoices?SearchTerm={Q(s.InvoiceNumber)}"),
            ("invoices.sort",      "Invoices — sorted by amount",              "/Invoices?Sort=total&Dir=desc"),
            ("invoices.far",       "Invoices — page 50 of 10 per page",        "/Invoices?PageNumber=50&PageSize=10"),
            ("inbox",              "Inbox",                                    "/Inbox"),
            ("tasks",              "Tasks — everyone's",                       "/Tasks?Scope=all"),
            ("audit.first",        "Audit log — last 30 days",                 "/Settings/AuditLogs"),
            ("audit.year",         "Audit log — last year",                    "/Settings/AuditLogs?Range=365"),
            ("audit.search",       "Audit log — search",                       $"/Settings/AuditLogs?Range=365&SearchTerm={Q("Perf Template")}"),
            ("audit.far",          "Audit log — page 100",                     "/Settings/AuditLogs?Range=365&Page=100"),
        };

        using (var admin = await WebSession.SignedInAsync(PerfWorld.P.Admin))
        {
            foreach (var (key, label, path) in pages)
                await MeasureAsync(admin, key, label, path, metrics, problems);
        }

        await Step("As a rep, who sees only their own leads and deals", async () =>
        {
            using var rep = await WebSession.SignedInAsync(PerfWorld.P["rep01"]);
            await MeasureAsync(rep, "leads.rep", "Leads — a rep's", "/Leads?StatusFilter=all", metrics, problems);
            await MeasureAsync(rep, "pipeline.rep", "Pipeline — a rep's board", "/Pipeline", metrics, problems);
        });

        // ── One query per row? 10 rows vs 50 rows ───────────────────
        var growth = new List<string>();
        await Step("Showing 50 rows needs (almost) no more queries than showing 10", async () =>
        {
            using var admin = await WebSession.SignedInAsync(PerfWorld.P.Admin);
            foreach (var (key, label, path10, path50) in new[]
                     {
                         ("leads",     "Leads",          "/Leads?StatusFilter=all&PageSize=10",   "/Leads?StatusFilter=all&PageSize=50"),
                         ("contacts",  "Contacts",       "/Contacts?PageSize=10",                 "/Contacts?PageSize=50"),
                         ("companies", "Companies",      "/Companies?PageSize=10",                "/Companies?PageSize=50"),
                         ("pipeline",  "Pipeline table", "/Pipeline?View=table&PageSize=10",      "/Pipeline?View=table&PageSize=50"),
                         ("invoices",  "Invoices",       "/Invoices?PageSize=10",                 "/Invoices?PageSize=50"),
                         ("audit",     "Audit log",      "/Settings/AuditLogs?Range=365&PageSize=10", "/Settings/AuditLogs?Range=365&PageSize=50"),
                     })
            {
                PerfMetric ten, fifty;
                try
                {
                    ten = await PerfMeter.PageAsync(admin, $"{key}.rows10", $"{label} — 10 rows", path10, runs: 1);
                    fifty = await PerfMeter.PageAsync(admin, $"{key}.rows50", $"{label} — 50 rows", path50, runs: 1);
                }
                catch (AssertionException ex)
                {
                    problems.Add(ex.Message);
                    continue;
                }

                if (ten.QueryCount is { } q10 && fifty.QueryCount is { } q50)
                {
                    growth.Add($"{label,-16} 10 rows: {q10,3} queries   50 rows: {q50,3} queries");
                    if (q50 - q10 > PerfLimits.ExtraQueriesForMoreRows)
                        problems.Add($"{label}: {q10} queries for 10 rows but {q50} for 50 — it runs a query per row " +
                                     "(gets slower the more a person shows).");
                }
            }
        });
        if (growth.Count > 0) PerfMeter.ReportLines("PERF-02 Queries at 10 and 50 rows", growth, "perf-report.txt");

        Finish("PERF-02 Lists", metrics, problems);
    }

    // =================================================================
    // PERF-03
    // =================================================================

    [Test, Order(3)]
    public async Task A_records_own_page_opens_quickly()
    {
        await ReadyAsync();
        var s = PerfWorld.Samples;
        var metrics = new List<PerfMetric>();
        var problems = new List<string>();

        using (var admin = await WebSession.SignedInAsync(PerfWorld.P.Admin))
        {
            foreach (var (key, label, path) in new[]
                     {
                         ("detail.lead",    "A lead's page",    $"/Leads/Detail/{s.Lead}"),
                         ("detail.deal",    "A deal's page",    $"/Pipeline/Detail/{s.Deal}"),
                         ("detail.contact", "A contact's page", $"/Contacts/Detail?id={s.Contact}"),
                         ("detail.company", "A company's page", $"/Companies/Detail/{s.Company}"),
                         ("detail.quote",   "A quote's page",   $"/Quotes/Detail?id={s.Quote}"),
                         ("detail.invoice", "An invoice's page", $"/Invoices/Detail?id={s.Invoice}"),
                     })
                await MeasureAsync(admin, key, label, path, metrics, problems, PerfLimits.DetailMs);
        }

        Finish("PERF-03 Record pages", metrics, problems);
    }

    // =================================================================
    // PERF-04
    // =================================================================

    [Test, Order(4)]
    public async Task Saving_pipeline_records_is_quick()
    {
        await ReadyAsync();
        var tenantId = PerfWorld.TenantId;
        var api = await Api.AsAsync(PerfWorld.P.Admin);
        await Api.EnsureTokensWorkAsync(api);
        var problems = new List<string>();
        var m = Random.Shared.Next(10000, 99999).ToString();
        const int N = 15;

        PerfMetric Metric(string key, string label) =>
            new() { Key = key, Label = label, CeilingMs = PerfLimits.SaveMedianMs, MaxCeilingMs = PerfLimits.SaveMaxMs };

        var leadAdd = Metric("save.lead.add", "Add a lead");
        var leadEdit = Metric("save.lead.edit", "Edit a lead");
        var leadDelete = Metric("save.lead.delete", "Delete a lead");
        var contactAdd = Metric("save.contact.add", "Add a contact");
        var dealAdd = Metric("save.deal.add", "Add a deal");
        var dealEdit = Metric("save.deal.edit", "Edit a deal");
        var dealMove = Metric("save.deal.move", "Move a deal to another stage");
        var quoteAdd = Metric("save.quote.add", "Add a quote with a line");
        var quoteDelete = Metric("save.quote.delete", "Delete a quote");
        var dealDelete = Metric("save.deal.delete", "Delete a deal");

        void Note(PerfMetric metric, PerfMeter.ApiTiming t, string what)
        {
            if (!t.Response.IsSuccess)
            {
                problems.Add($"{what} failed: {t.Response}");
                return;
            }
            metric.Ms.Add(t.Ms);
            if (t.Queries is { } q) metric.Queries.Add(q);
        }

        // ── Leads ────────────────────────────────────────────────────
        var leads = new List<Guid>();
        await Step($"Add, edit and delete {N} leads", async () =>
        {
            for (var i = 0; i < N; i++)
            {
                var t = await PerfMeter.ApiAsync(() => api.PostAsync("/api/leads", new
                {
                    tenantId, fullName = $"Perf Save Lead {m}-{i}", email = PerfWorld.EmailOf($"perf.save.{m}.{i}"),
                    phone = "+9198" + Random.Shared.Next(10000000, 99999999), companyName = "Perf Save Co",
                    estimatedValue = 1500m, currency = "INR",
                }));
                Note(leadAdd, t, "Adding a lead");
                if (t.Response.IsSuccess) leads.Add(ApiIds.IdOf(t.Response));
            }

            foreach (var id in leads)
            {
                var d = (await api.GetAsync($"/api/leads/{id}")).Json();
                var t = await PerfMeter.ApiAsync(() => api.PutAsync($"/api/leads/{id}", new
                {
                    tenantId, leadId = id, fullName = $"Perf Save Lead {m} edited", email = Str(d, "email"),
                    phone = Str(d, "phone"), companyName = Str(d, "companyName"), estimatedValue = 2500m,
                    currency = "INR", ownerUserId = Str(d, "ownerUserId"), version = Str(d, "version"),
                }));
                Note(leadEdit, t, "Editing a lead");
            }

            foreach (var id in leads)
                Note(leadDelete, await PerfMeter.ApiAsync(() => api.DeleteAsync($"/api/leads/{id}")), "Deleting a lead");
        });

        // ── Contacts ─────────────────────────────────────────────────
        await Step($"Add {N} contacts", async () =>
        {
            for (var i = 0; i < N; i++)
            {
                var t = await PerfMeter.ApiAsync(() => api.PostAsync("/api/contacts", new
                {
                    tenantId, firstName = "Perf", lastName = $"Save Contact {m}-{i}", email = PerfWorld.EmailOf($"perf.savec.{m}.{i}"),
                }));
                Note(contactAdd, t, "Adding a contact");
            }
        });

        // ── Deals: add, edit, move, quote, delete ───────────────────
        var deals = new List<Guid>();
        await Step($"Add {N} deals, edit them and move each to another stage", async () =>
        {
            for (var i = 0; i < N; i++)
            {
                var body = new JsonObject
                {
                    ["tenantId"] = tenantId.ToString(),
                    ["contactId"] = PerfWorld.Samples.Contact.ToString(),
                    ["title"] = $"Perf Save Deal {m}-{i}",
                    ["expectedValue"] = 25000m,
                    ["currency"] = "INR",
                    ["expectedCloseDateUtc"] = DateTime.UtcNow.Date.AddDays(30),
                };
                var t = await PerfMeter.ApiAsync(() => api.PostAsync("/api/deals", body));
                Note(dealAdd, t, "Adding a deal");
                if (t.Response.IsSuccess) deals.Add(ApiIds.IdOf(t.Response));
            }

            foreach (var id in deals)
            {
                var got = await api.GetAsync($"/api/deals/{id}");
                if (!got.IsSuccess) { problems.Add($"Reading a deal back failed: {got}"); continue; }
                var j = JsonNode.Parse(got.Body) as JsonObject;
                if (j is null) { problems.Add("A deal read back as something other than an object."); continue; }
                j["title"] = $"Perf Save Deal {m} edited";
                Note(dealEdit, await PerfMeter.ApiAsync(() => api.PutAsync($"/api/deals/{id}", j)), "Editing a deal");
            }

            foreach (var id in deals)
            {
                var moves = await api.GetAsync($"/api/deals/{id}/transitions");
                string? to = null;
                if (moves.IsSuccess && moves.Json().TryGetProperty("transitions", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    to = list.EnumerateArray()
                        .Where(x => x.GetProperty("isAllowed").GetBoolean() && !x.GetProperty("requiresNote").GetBoolean())
                        .Select(x => x.GetProperty("toStageKey").GetString())
                        .FirstOrDefault(k => !string.IsNullOrEmpty(k));
                }
                if (to is null) { problems.Add($"Deal {id} offered no stage to move to without a note — the move wasn't timed."); continue; }
                Note(dealMove, await PerfMeter.ApiAsync(() => api.PutAsync($"/api/deals/{id}/stage", new { stage = to })), "Moving a deal");
            }
        });

        await Step("Add a quote to 10 of them, then delete the quotes and all the deals", async () =>
        {
            var quotes = new List<Guid>();
            foreach (var id in deals.Take(10))
            {
                var t = await PerfMeter.ApiAsync(() => api.PostAsync("/api/quotes", new
                {
                    dealId = id, issueDateUtc = DateTime.UtcNow.Date, expiresAtUtc = DateTime.UtcNow.Date.AddDays(30), currency = "INR",
                    items = new[] { new { name = "Perf save line " + m, unitPrice = 5000m, quantity = 2m, taxRate = 0m } },
                }));
                Note(quoteAdd, t, "Adding a quote");
                if (t.Response.IsSuccess) quotes.Add(ApiIds.IdOf(t.Response));
            }

            foreach (var id in quotes)
                Note(quoteDelete, await PerfMeter.ApiAsync(() => api.DeleteAsync($"/api/quotes/{id}")), "Deleting a quote");

            foreach (var id in deals)
                Note(dealDelete, await PerfMeter.ApiAsync(() => api.DeleteAsync($"/api/deals/{id}")), "Deleting a deal");
        });

        var metrics = new[] { leadAdd, leadEdit, leadDelete, contactAdd, dealAdd, dealEdit, dealMove, quoteAdd, quoteDelete, dealDelete }
            .Where(x => x.Ms.Count > 0).ToList();
        Finish("PERF-04 Saving", metrics, problems);
    }

    // =================================================================
    // PERF-05
    // =================================================================

    [Test, Order(5)]
    public async Task Main_pages_finish_loading_quickly_in_a_real_browser()
    {
        await ReadyAsync();
        var page = await SignedInAsync(PerfWorld.P.Admin);
        var metrics = new List<PerfMetric>();
        var problems = new List<string>();

        foreach (var (key, label, path) in new[]
                 {
                     ("browser.dashboard", "Browser — dashboard",      "/Dashboard"),
                     ("browser.leads",     "Browser — leads",          "/Leads?StatusFilter=all"),
                     ("browser.pipeline",  "Browser — pipeline board", "/Pipeline"),
                     ("browser.contacts",  "Browser — contacts",       "/Contacts"),
                     ("browser.quotes",    "Browser — quotes",         "/Quotes"),
                 })
        {
            var metric = new PerfMetric { Key = key, Label = label, CeilingMs = PerfLimits.BrowserLoadMs };
            try
            {
                for (var run = 0; run < 3; run++)
                {
                    var watch = Stopwatch.StartNew();
                    var response = await page.GotoAsync(path, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 60_000 });
                    try
                    {
                        // Anything the page fetches after loading (charts, counts).
                        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 10_000 });
                    }
                    catch (System.TimeoutException) { /* still busy after 10 s — the time says so */ }
                    watch.Stop();

                    if (response is null || response.Status != 200)
                        throw new AssertionException($"{label} answered {response?.Status.ToString() ?? "nothing"}.");
                    if (run > 0) metric.Ms.Add(watch.Elapsed.TotalMilliseconds);   // the first is the warm-up
                }
                metrics.Add(metric);
            }
            catch (Exception ex) when (ex is AssertionException or PlaywrightException or System.TimeoutException)
            {
                problems.Add($"{label}: {ex.Message.Split('\n')[0]}");
            }
        }

        Finish("PERF-05 In the browser", metrics, problems);
    }

    // =================================================================
    // PERF-06
    // =================================================================

    [Test, Order(6)]
    public async Task Twenty_people_working_at_once_stay_quick_without_errors()
    {
        await ReadyAsync();
        var s = PerfWorld.Samples;
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("E2E_PERF_LOAD_SECONDS"), out var sec) && sec > 0 ? sec : 120;

        // 108 — the admin takes away the leads the load adds. Reps may not
        // delete leads (leads.delete), so each rep's own delete was refused
        // and about 170 leads were left behind every run.
        var cleaner = await Api.AsAsync(PerfWorld.P.Admin);
        cleaner.WaitWhenPaced = true;

        // Ten people, two tabs each.
        var workers = new List<(TestUser Who, WebSession Web, ApiCaller Api, bool IsAdmin)>();
        await Step("Ten people sign in twice each (20 sessions)", async () =>
        {
            foreach (var person in PerfWorld.People)
            {
                var api = await Api.AsAsync(person);
                for (var tab = 0; tab < 2; tab++)
                    workers.Add((person, await WebSession.SignedInAsync(person), api, ReferenceEquals(person, PerfWorld.P.Admin)));
            }
        });

        var samples = new ConcurrentBag<(string What, double Ms)>();
        var errors = new ConcurrentBag<string>();
        var refused = new ConcurrentBag<string>();

        try
        {
            await Step($"20 sessions work for {seconds} seconds: lists, searches, the board, records and new leads", async () =>
            {
                var deadline = DateTime.UtcNow.AddSeconds(seconds);
                var tasks = workers.Select((w, index) => Task.Run(async () =>
                {
                    var rnd = new Random(7000 + index);
                    var turn = 0;
                    while (DateTime.UtcNow < deadline)
                    {
                        turn++;
                        if (turn % 6 == 0)
                        {
                            // Every sixth step: add a lead, then take it away again.
                            var t = await PerfMeter.ApiAsync(() => w.Api.PostAsync("/api/leads", new
                            {
                                tenantId = PerfWorld.TenantId, fullName = $"Perf Load Lead {index}-{turn}",
                                email = PerfWorld.EmailOf($"perf.load.{index}.{turn}.{rnd.Next(1000, 9999)}"),
                                phone = "+9197" + rnd.Next(10000000, 99999999), companyName = "Perf Load Co",
                                estimatedValue = 1000m, currency = "INR",
                            }));
                            samples.Add(("API — add a lead", t.Ms));
                            if (!t.Response.IsSuccess) errors.Add($"{w.Who.Key}: adding a lead answered {t.Response}");
                            else
                            {
                                // 108 — by the admin (see above), and a refusal is reported.
                                var gone = await cleaner.DeleteAsync($"/api/leads/{ApiIds.IdOf(t.Response)}");
                                if (!gone.IsSuccess) errors.Add($"admin: taking away the load test's lead answered {gone}");
                            }
                        }
                        else
                        {
                            var (what, path) = Pick(rnd, w.IsAdmin, s);
                            var watch = Stopwatch.StartNew();
                            try
                            {
                                var a = await w.Web.GetAsync(path);
                                watch.Stop();
                                samples.Add((what, watch.Elapsed.TotalMilliseconds));
                                if (a.Status >= 500 || a.Status == 429 || a.ToSignIn)
                                    errors.Add($"{w.Who.Key}: {what} ({path}) answered {a}");
                                else if (a.Status != 200)
                                    refused.Add($"{w.Who.Key}: {what} answered {a.Status}");
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"{w.Who.Key}: {what} ({path}) — {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
                            }
                        }

                        await Task.Delay(rnd.Next(500, 1500));
                    }
                })).ToList();

                await Task.WhenAll(tasks);
            });
        }
        finally
        {
            foreach (var w in workers) w.Web.Dispose();
        }

        var all = samples.Select(x => x.Ms).ToList();
        // 107-update5 — CeilingOnly: judged by the fixed limits, not the baseline (see PerfMetric).
        var everything = new PerfMetric { Key = "load.median", Label = "20 at once — every answer, typical", CeilingMs = PerfLimits.PageMs, CeilingOnly = true };
        everything.Ms.AddRange(all);
        var p95 = new PerfMetric { Key = "load.p95", Label = "20 at once — the slowest 5%", CeilingMs = PerfLimits.LoadP95Ms, CeilingOnly = true };
        p95.Ms.Add(everything.P95);

        var byPage = samples.GroupBy(x => x.What)
            .Select(g =>
            {
                var m = new PerfMetric { Key = "", Label = g.Key, CeilingMs = double.MaxValue };
                m.Ms.AddRange(g.Select(x => x.Ms));
                return m;
            })
            .OrderByDescending(m => m.P95)
            .Select(m => $"{m.Label,-34} {m.Ms.Count,5} answers   typical {m.Median,6:0} ms   slowest 5% {m.P95,6:0} ms   slowest {m.Max,6:0} ms")
            .ToList();
        byPage.Insert(0, $"{all.Count:N0} answers in {seconds} s ({all.Count * 60.0 / seconds:0} a minute), " +
                         $"{errors.Count} errors, {refused.Count} refused (403/404 — a rep opening someone else's record)");
        PerfMeter.ReportLines("PERF-06 20 people at once", byPage, "perf-report.txt");

        var problems = new List<string>();
        if (all.Count == 0) problems.Add("Nothing was answered at all during the load test.");
        foreach (var e in errors.Take(15)) problems.Add("Error under load — " + e);
        if (errors.Count > 15) problems.Add($"… and {errors.Count - 15} more errors.");

        Finish("PERF-06 20 people at once", new List<PerfMetric> { everything, p95 }, problems);
    }

    /// <summary>What a person does next: mostly lists and the board, sometimes a search or a record.</summary>
    private static (string What, string Path) Pick(Random rnd, bool isAdmin, PerfData.Samples s)
    {
        var size = PerfWorld.Size;
        var options = new List<(string, string)>
        {
            ("Dashboard", "/Dashboard"),
            ("Leads", "/Leads?StatusFilter=all"),
            ("Leads — search", $"/Leads?StatusFilter=all&SearchTerm={Q("Perf Lead " + rnd.Next(1, size.Leads))}"),
            ("Leads — another page", $"/Leads?StatusFilter=all&PageNumber={rnd.Next(1, 50)}"),
            ("Pipeline board", "/Pipeline"),
            ("Pipeline table", $"/Pipeline?View=table&PageNumber={rnd.Next(1, 20)}"),
            ("Contacts", "/Contacts"),
            ("Contacts — search", $"/Contacts?SearchTerm={Q("Contact " + rnd.Next(1, size.Contacts))}"),
            ("Companies", "/Companies"),
            ("Invoices", "/Invoices"),
            ("Quotes", "/Quotes"),
            ("Tasks", "/Tasks"),
        };
        if (isAdmin)
        {
            options.Add(("A lead's page", $"/Leads/Detail/{s.Lead}"));
            options.Add(("A deal's page", $"/Pipeline/Detail/{s.Deal}"));
            options.Add(("A contact's page", $"/Contacts/Detail?id={s.Contact}"));
        }
        return options[rnd.Next(options.Count)];
    }

    // =================================================================
    // PERF-07
    // =================================================================

    [Test, Order(7)]
    public async Task Index_suggestions_from_SQL_Server_are_reported()
    {
        await ReadyAsync();
        await Step("SQL Server's own list of indexes it wished it had (report only — read before adding any)", async () =>
        {
            List<string> lines;
            try
            {
                lines = await PerfWorld.Data.MissingIndexesAsync();
            }
            catch (Microsoft.Data.SqlClient.SqlException ex)
            {
                lines = new List<string> { $"(could not read SQL Server's suggestions: {ex.Message.Split('\n')[0]})" };
            }
            if (lines.Count == 0) lines.Add("(none — SQL Server found nothing it wanted)");
            lines.Insert(0, "Collected by SQL Server since it last started, for every query on this database — the whole run, not only this test.");
            PerfMeter.ReportLines("PERF-07 Index suggestions", lines, "perf-missing-indexes.txt");
        });
    }

    // =================================================================

    private static async Task MeasureAsync(WebSession session, string key, string label, string path,
        List<PerfMetric> metrics, List<string> problems, double ceilingMs = PerfLimits.PageMs)
    {
        try
        {
            var metric = await PerfMeter.PageAsync(session, key, label, path, ceilingMs);
            metrics.Add(metric);
            if (metric.QueryCount is { } q && q > PerfLimits.PageQueries)
                problems.Add($"{label}: {q} database queries for one page — over the {PerfLimits.PageQueries} limit.");
        }
        catch (AssertionException ex)
        {
            problems.Add(ex.Message);
        }
    }

    /// <summary>Report everything, judge it, and fail with every problem at once.</summary>
    private static void Finish(string section, List<PerfMetric> metrics, List<string> problems)
    {
        PerfMeter.Report(section, metrics);
        problems.AddRange(PerfMeter.Judge(metrics));
        Assert.That(problems, Is.Empty,
            $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems) +
            "\n(Every number is in e2e-artifacts/perf-report.txt.)");
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
