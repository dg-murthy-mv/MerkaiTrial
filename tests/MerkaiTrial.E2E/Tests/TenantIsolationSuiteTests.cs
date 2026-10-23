// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/TenantIsolationSuiteTests.cs
//
// NEW (100) — test 76, Critical · Security.
//
//   A_workspace_cannot_access_another_workspaces_records_by_changing_the_id
//
// A REUSABLE SUITE: one test case per module — Leads, Contacts, Companies,
// Deals, Quotes, Invoices, Tasks, Notes, Attachments, Products, Activities.
// Each case is workspace B's ADMIN (an admin, so no role limit can explain
// a refusal — only the workspace boundary can) trying to reach workspace
// A's records by putting A's ids where B's would go:
//
//   1. API reads      — GET by id, sub-lists, PDFs and file downloads, and
//                       B's own lists and searches.
//   2. API writes     — edit, delete, status changes, conversions, notes,
//                       payments, approvals, uploads… on A's ids, and B's
//                       OWN records pointed at A's ("convert my lead into
//                       their company", "delete note X on my lead" where X
//                       is A's note, "a quote line for their product").
//   3. Direct URLs    — the Admin.Web pages for A's records, typed into
//                       B's browser: detail, edit, PDF, file download.
//   4. Direct posts   — the pages' own forms (with B's valid anti-forgery
//                       token) aimed at A's records.
//
// HOW A LEAK IS SPOTTED
//   • Every A record carries a marker that exists nowhere else
//     (IsolationWorld). Any answer B gets that contains one is a leak.
//   • A file (PDF, download) coming back to B for A's record is a leak.
//   • Every write must be refused (not 2xx).
//   • A's records are read from the database before and after — every
//     column — and the rows that hang off them are counted, deleted ones
//     included. Any difference means B changed A's data, whatever status
//     code it was given.
//   A server error (5xx) on B's request is not a leak, but it is listed in
//   the output as worth a look: the right answer is a plain 404.
//
// Every problem in a module is collected and reported together, so one run
// shows the whole picture for that module.
//
// SETUP. The first case to run builds both workspaces' records
// (IsolationWorld) — a minute or two through the screens. The other cases
// reuse them. If that build fails, every case reports the first failure.
//
// 100b (after the first run):
//   • The leads list echoes the search term into its search box, so
//     searching for A's email "found" it. The term B typed itself is no
//     longer counted; every other marker still is.
//   • "A note on B's own lead, with A's workspace in the body" may be
//     accepted — the API now ignores the body's workspace, so the note
//     lands on B's lead. What matters is checked in the database: no new
//     note anywhere in workspace A.
//   • Removed "invoice A's quote on B's own deal": a manual invoice never
//     carries a quote (the API drops it), so that was B invoicing its own
//     deal. Invoice bodies now carry a due date.
//
// 101: every request is timed. Any that takes more than 5 seconds is
// listed after the module's steps, with the time of day — not a failure,
// a clue for the slow Invoices case (see 101's SETUP).
//
// Run just this suite:
//   .\run-e2e.ps1 -Filter "FullyQualifiedName~TenantIsolationSuite"
// =====================================================================

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class TenantIsolationSuiteTests : E2ETest
{
    /// <summary>The modules, one test case each.</summary>
    public static readonly string[] Modules =
    {
        "Leads", "Contacts", "Companies", "Deals", "Quotes", "Invoices",
        "Tasks", "Notes", "Attachments", "Products", "Activities",
    };

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IsolationWorld? _world;
    private static Exception? _buildFailure;

    [TestCaseSource(nameof(Modules))]
    public async Task A_workspace_cannot_access_another_workspaces_records_by_changing_the_id(string module)
    {
        var w = await Step("Both workspaces' records are in place", WorldAsync);

        var b = await SignedInAsync(World.B.Admin);
        var apiA = await Api.AsAsync(World.A.Admin);
        var apiB = await Api.AsAsync(World.B.Admin);
        await Api.EnsureTokensWorkAsync(apiB);

        var ctx = await Step("Workspace B's own lead page: its form token and a status of B's", () => ContextAsync(b, w));
        var probe = await Step($"Plan the {module} attempts", () => BuildProbeAsync(module, w, apiA, ctx));

        var before = await Step("Read A's records from the database", () => SnapshotAsync(probe));

        var leaks = new List<string>();
        var warnings = new List<string>();

        await Step($"{probe.ApiReads.Count} API reads as workspace B", async () =>
        {
            foreach (var (what, path) in probe.ApiReads)
                CheckRead(what, "GET " + path, await Timed("GET " + path, () => apiB.GetAsync(path)), w, leaks, warnings);
        });

        await Step($"{probe.ApiWrites.Count + probe.Uploads.Count} API writes as workspace B", async () =>
        {
            foreach (var (what, method, path, body, maySucceed) in probe.ApiWrites)
                CheckWrite(what, $"{method.Method} {path}", await Timed($"{method.Method} {path}", () => apiB.SendAsync(method, path, body)), w, leaks, warnings, maySucceed);

            foreach (var (what, path) in probe.Uploads)
                CheckWrite(what, "POST " + path,
                    await Timed("POST " + path, () => apiB.UploadAsync(path, $"b-{Guid.NewGuid():N}.pdf", Files.SmallPdf("B upload"), "application/pdf")),
                    w, leaks, warnings);
        });

        await Step($"{probe.PageReads.Count} addresses typed into workspace B's browser", async () =>
        {
            foreach (var (what, path, isFile, ignore) in probe.PageReads)
            {
                var r = await Timed("page GET " + path, () => FetchAsync(b, "GET", path, null));
                var found = MarkersIn(r.Text, w, ignore);
                if (found.Count > 0)
                    leaks.Add($"{what}: {path} showed workspace A's data ({string.Join(", ", found)}).");
                else if (isFile && r.Status is >= 200 and < 300 && LooksLikeFile(r.Type))
                    leaks.Add($"{what}: {path} handed workspace B a file ({r.Type}).");
                if (r.Status >= 500) warnings.Add($"{what}: {path} → HTTP {r.Status}.");
            }
        });

        await Step($"{probe.PagePosts.Count} page forms posted at A's records", async () =>
        {
            foreach (var (what, path, fields) in probe.PagePosts)
            {
                var all = new List<string[]> { new[] { "__RequestVerificationToken", ctx.FormToken } };
                all.AddRange(fields.Select(f => new[] { f.Key, f.Value }));
                var r = await Timed("page POST " + path, () => FetchAsync(b, "POST", path, all.ToArray()));
                var found = MarkersIn(r.Text, w);
                if (found.Count > 0)
                    leaks.Add($"{what}: posting to {path} showed workspace A's data ({string.Join(", ", found)}).");
                if (r.Status >= 500) warnings.Add($"{what}: POST {path} → HTTP {r.Status}.");
            }
        });

        await Step("A's records are exactly as they were", async () =>
        {
            var after = await SnapshotAsync(probe);
            foreach (var (key, was) in before)
            {
                var now = after[key];
                if (now != was)
                    leaks.Add($"Workspace A's data CHANGED: {key}" +
                              (key.StartsWith("rows", StringComparison.Ordinal)
                                  ? $" — was {ApiResponse.Short(was, 300)}, now {ApiResponse.Short(now, 300)}."
                                  : $" — was {was}, now {now}."));
            }
        });

        if (Slow.Count > 0)
            TestContext.Progress.WriteLine(
                $"  [{module}] Slow requests (over {SlowAfter.TotalSeconds:0}s) — not a failure, a clue:\n    " + string.Join("\n    ", Slow));
        Slow.Clear();

        if (warnings.Count > 0)
            TestContext.Progress.WriteLine(
                $"  [{module}] Refused, but with a server error instead of a plain 404 (worth a look, not a leak):\n    " +
                string.Join("\n    ", warnings));

        Assert.That(leaks, Is.Empty,
            $"Workspace B reached workspace A's {module} ({leaks.Count} problem(s)):\n  • " + string.Join("\n  • ", leaks));
    }

    // =================================================================
    // 101: timing — which request is slow, when one is
    // =================================================================

    private static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(5);
    private static readonly List<string> Slow = new();

    /// <summary>
    /// Runs one request and notes it when it takes longer than
    /// <see cref="SlowAfter"/>, with the time of day — so a slow stretch
    /// in the run can be matched against webapi.out.log and db-waits.txt.
    /// </summary>
    private static async Task<T> Timed<T>(string what, Func<Task<T>> call)
    {
        var started = DateTime.Now;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return await call();
        }
        finally
        {
            if (watch.Elapsed > SlowAfter)
                Slow.Add($"{started:HH:mm:ss} {watch.Elapsed.TotalSeconds,5:0.0}s  {what}");
        }
    }

    // =================================================================
    // The world, built once
    // =================================================================

    private async Task<IsolationWorld> WorldAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_world is not null) return _world;
            if (_buildFailure is not null)
                throw new AssertionException(
                    "Building the two workspaces' records failed earlier in this run, so this case was not tried. " +
                    "The first failure: " + _buildFailure.Message, _buildFailure);

            try
            {
                var a = await SignedInAsync(World.A.Admin);
                var customer = await NewPageAsync("customer");
                var b = await SignedInAsync(World.B.Admin);
                _world = await IsolationWorld.BuildAsync(a, customer, b);
                return _world;
            }
            catch (Exception ex)
            {
                _buildFailure = ex;
                throw;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    // =================================================================
    // What workspace B tries, per module
    // =================================================================

    private sealed record Context(string FormToken, string BStatusKey);

    private sealed class Probe
    {
        public List<(string What, string Path)> ApiReads { get; } = new();
        public List<(string What, HttpMethod Method, string Path, object? Body, bool MaySucceed)> ApiWrites { get; } = new();
        public List<(string What, string Path)> Uploads { get; } = new();
        public List<(string What, string Path, bool IsFile, string? Ignore)> PageReads { get; } = new();
        public List<(string What, string Path, Dictionary<string, string> Fields)> PagePosts { get; } = new();
        public List<(string Table, Guid Id)> Rows { get; } = new();
        public List<(string Table, string Column, Guid Id)> Counts { get; } = new();

        public void Read(string what, string path) => ApiReads.Add((what, path));
        public void Write(string what, HttpMethod method, string path, object? body = null, bool maySucceed = false) =>
            ApiWrites.Add((what, method, path, body, maySucceed));
        public void Page(string what, string path, bool isFile = false, string? ignore = null) =>
            PageReads.Add((what, path, isFile, ignore));
        public void Post(string what, string path, params (string Key, string Value)[] fields) =>
            PagePosts.Add((what, path, fields.ToDictionary(f => f.Key, f => f.Value)));
        public void Row(string table, Guid id) => Rows.Add((table, id));
        public void Count(string table, string column, Guid id) => Counts.Add((table, column, id));
    }

    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Put = HttpMethod.Put,
                                       Patch = HttpMethod.Patch, Delete = HttpMethod.Delete;

    private static async Task<Probe> BuildProbeAsync(string module, IsolationWorld w, ApiCaller apiA, Context ctx)
    {
        var p = new Probe();
        var soon = DateTime.UtcNow.Date.AddDays(7);
        var hijack = "Hijacked by workspace B " + World.Stamp;

        switch (module)
        {
            // ── Leads ────────────────────────────────────────────────────
            case "Leads":
            {
                var open = w.OpenLeadId;
                p.Read("Open the lead", $"/api/leads/{open}");
                p.Read("Open the converted lead", $"/api/leads/{w.ConvertedLeadId}");
                p.Read("Its notes", $"/api/leads/{open}/notes");
                p.Read("Its notes, naming A's workspace", $"/api/leads/{open}/notes?tenantId={w.TenantA}");
                p.Read("Its old-style activities", $"/api/leads/{open}/activities?tenantId={w.TenantA}");
                p.Read("Its reminders", $"/api/leads/{open}/reminders?tenantId={w.TenantA}");
                p.Read("Its timeline", $"/api/leads/{open}/timeline?tenantId={w.TenantA}");
                p.Read("Its files", $"/api/leads/{open}/attachments");
                p.Read("Its activities", $"/api/activities?entityType=Lead&entityId={open}");
                p.Read("Search B's leads for A's email", $"/api/leads?pageNumber=1&pageSize=100&searchTerm={Uri.EscapeDataString(w.OpenLeadEmail)}");
                p.Read("B's whole lead list", "/api/leads?pageNumber=1&pageSize=100");
                p.Read("Duplicate check against A's lead", $"/api/leads/duplicates?email={Uri.EscapeDataString(w.OpenLeadEmail)}");

                p.Write("Edit the lead", Put, $"/api/leads/{open}",
                    await BodyFromAsync(apiA, $"/api/leads/{open}", j =>
                    {
                        j["leadId"] = open.ToString();
                        j["tenantId"] = w.TenantA.ToString();
                        j["fullName"] = hijack;
                    }, new { leadId = open, tenantId = w.TenantA, fullName = hijack, currency = "INR" }));
                p.Write("Change its status", Put, $"/api/leads/{open}/status?status={Uri.EscapeDataString(ctx.BStatusKey)}");
                p.Write("Delete it", Delete, $"/api/leads/{open}");
                p.Write("Give it to B's admin", Patch, $"/api/leads/{open}/assign",
                    new { tenantId = w.TenantA, leadId = open, ownerUserId = w.BAdminUserId.ToString() });
                p.Write("Convert it to a deal", Post, $"/api/leads/{open}/convert-to-deal", new
                {
                    tenantId = w.TenantA, leadId = open, dealTitle = hijack, stage = "Qualification",
                    expectedValue = 1m, currency = "INR", expectedCloseDateUtc = soon,
                });
                p.Write("Convert it to a contact and company", Post, $"/api/leads/{open}/convert", new
                {
                    tenantId = w.TenantA, leadId = open, createCompany = true, companyName = hijack,
                    createDeal = false, convertedBy = "B",
                });
                p.Write("Merge it into B's lead", Post, "/api/leads/merge", new { keepLeadId = w.BLeadId, mergeLeadId = open });
                p.Write("Merge B's lead into it", Post, "/api/leads/merge", new { keepLeadId = open, mergeLeadId = w.BSpareLeadId });
                p.Write("Add a note to it", Post, $"/api/leads/{open}/notes",
                    new { tenantId = w.TenantA, leadId = open, note = hijack, createdBy = "B" });
                p.Write("Log an old-style activity on it", Post, $"/api/leads/{open}/activities", new
                {
                    tenantId = w.TenantA, leadId = open, activityType = "Call", subject = hijack,
                    duration = 5, activityDate = DateTime.UtcNow,
                });
                p.Write("Add a reminder to it", Post, $"/api/leads/{open}/reminders",
                    new { tenantId = w.TenantA, leadId = open, title = hijack, reminderDate = soon });
                p.Write("Convert B's OWN lead into A's company", Post, $"/api/leads/{w.BSpareLeadId}/convert", new
                {
                    tenantId = w.TenantB, leadId = w.BSpareLeadId, createCompany = false,
                    existingCompanyId = w.CompanyId, createDeal = false, convertedBy = "B",
                });
                p.Uploads.Add(("Upload a file to it", $"/api/leads/{open}/attachments"));

                p.Page("Lead page", $"/Leads/Detail/{open}");
                p.Page("Lead edit page", $"/Leads/Edit/{open}");
                p.Page("Lead conversion page", $"/Leads/ConvertToDeal/{open}");
                p.Page("Converted lead page", $"/Leads/Detail/{w.ConvertedLeadId}");
                p.Page("Leads list searched for A's email", $"/Leads?SearchTerm={Uri.EscapeDataString(w.OpenLeadEmail)}",
                    ignore: w.OpenLeadEmail);   // the page echoes the search term into its search box

                p.Post("Status form", $"/Leads/Detail/{open}?handler=ChangeStatus", ("status", ctx.BStatusKey));
                p.Post("Note form", $"/Leads/Detail/{open}?handler=AddNote", ("note", hijack), ("NoteText", hijack));
                p.Post("Delete form", $"/Leads/Detail/{open}?handler=Delete");
                p.Post("Edit form", $"/Leads/Edit/{open}", ("Input.FullName", hijack), ("Input.Email", "hijack@example.com"));

                p.Row("Leads", open);
                p.Row("Leads", w.ConvertedLeadId);
                p.Row("Companies", w.CompanyId);
                p.Count("LeadNotes", "LeadId", open);
                p.Count("LeadActivities", "LeadId", open);
                p.Count("LeadReminders", "LeadId", open);
                p.Count("Activities", "EntityId", open);
                p.Count("Attachments", "EntityId", open);
                p.Count("Deals", "LeadId", open);
                p.Count("Contacts", "CompanyId", w.CompanyId);
                break;
            }

            // ── Contacts ─────────────────────────────────────────────────
            case "Contacts":
            {
                var c = w.ContactId;
                p.Read("Open the contact", $"/api/contacts/{c}");
                p.Read("Contacts of A's company", $"/api/contacts/by-company/{w.CompanyId}");
                p.Read("B's contacts filtered by A's company", $"/api/contacts?pageNumber=1&pageSize=100&companyId={w.CompanyId}");
                p.Read("B's whole contact list", "/api/contacts?pageNumber=1&pageSize=100");
                p.Read("Deals of A's contact", $"/api/deals/by-contact/{c}");
                p.Read("Activities on A's contact", $"/api/activities?entityType=Contact&entityId={c}");

                p.Write("Edit the contact", Put, $"/api/contacts/{c}",
                    await BodyFromAsync(apiA, $"/api/contacts/{c}", j =>
                    {
                        j["id"] = c.ToString();
                        j["firstName"] = hijack;
                    }, new { id = c, firstName = hijack }));
                p.Write("Delete the contact", Delete, $"/api/contacts/{c}");
                p.Write("Log a call on the contact", Post, "/api/activities",
                    new { entityType = "Contact", entityId = c, activityType = "Call", subject = hijack, duration = 1 });
                p.Write("A deal of B's for A's contact", Post, "/api/deals", new
                {
                    contactId = c, title = hijack, expectedValue = 1m, currency = "INR", expectedCloseDateUtc = soon,
                });

                p.Page("Contact page", $"/Contacts/Detail?id={c}");
                p.Page("Contact page (route form)", $"/Contacts/Detail/{c}");
                p.Page("Contact edit page", $"/Contacts/Edit?id={c}");

                p.Post("Delete form on the contact page", $"/Contacts/Detail?id={c}&handler=Delete", ("id", c.ToString()));
                p.Post("Delete form on the contacts list", "/Contacts?handler=Delete", ("id", c.ToString()));

                p.Row("Contacts", c);
                p.Count("Activities", "EntityId", c);
                p.Count("Deals", "ContactId", c);
                break;
            }

            // ── Companies ────────────────────────────────────────────────
            case "Companies":
            {
                var co = w.CompanyId;
                p.Read("Open the company", $"/api/companies/{co}");
                p.Read("B's whole company list", "/api/companies?pageNumber=1&pageSize=100");
                p.Read("B's company lookup", "/api/companies/lookup");
                p.Read("Activities on A's company", $"/api/activities?entityType=Company&entityId={co}");

                p.Write("Edit the company", Put, $"/api/companies/{co}",
                    await BodyFromAsync(apiA, $"/api/companies/{co}", j =>
                    {
                        j["id"] = co.ToString();
                        j["name"] = hijack;
                    }, new { id = co, name = hijack, country = "IN", vertical = "Generic" }));
                p.Write("Delete the company", Delete, $"/api/companies/{co}");
                p.Write("A contact of B's in A's company", Post, "/api/contacts",
                    new { companyId = co, firstName = hijack, email = $"b{Guid.NewGuid():N}@example.com" });
                p.Write("A deal of B's for A's company", Post, "/api/deals", new
                {
                    companyId = co, title = hijack, expectedValue = 1m, currency = "INR", expectedCloseDateUtc = soon,
                });

                p.Page("Company page", $"/Companies/Detail/{co}");
                p.Page("Company edit page", $"/Companies/Edit/{co}");

                p.Post("Delete form on the company page", $"/Companies/Detail/{co}?handler=Delete", ("id", co.ToString()));
                p.Post("Delete form on the companies list", "/Companies?handler=Delete", ("id", co.ToString()));

                p.Row("Companies", co);
                p.Count("Contacts", "CompanyId", co);
                p.Count("Deals", "CompanyId", co);
                break;
            }

            // ── Deals ────────────────────────────────────────────────────
            case "Deals":
            {
                var d = w.DealId;
                p.Read("Open the deal", $"/api/deals/{d}");
                p.Read("Its stage moves", $"/api/deals/{d}/transitions");
                p.Read("Its stage history", $"/api/deals/{d}/stage-history");
                p.Read("Its notes", $"/api/deals/{d}/notes");
                p.Read("Its old-style activities", $"/api/deals/{d}/activities");
                p.Read("Its reminders", $"/api/deals/{d}/reminders");
                p.Read("Its files", $"/api/deals/{d}/attachments");
                p.Read("Its activities and tasks", $"/api/activities?entityType=Deal&entityId={d}");
                p.Read("Its quotes", $"/api/quotes?dealId={d}");
                p.Read("Its invoices", $"/api/invoices?dealId={d}");
                p.Read("B's whole deal list", "/api/deals?page=1&pageSize=100");

                p.Write("Edit the deal", Put, $"/api/deals/{d}",
                    await BodyFromAsync(apiA, $"/api/deals/{d}", j => j["title"] = hijack,
                        new { title = hijack, stage = "Negotiation", expectedValue = 1m, currency = "INR", expectedCloseDateUtc = soon }));
                p.Write("Delete the deal", Delete, $"/api/deals/{d}");
                p.Write("Move its stage", Put, $"/api/deals/{d}/stage", new { stage = "Negotiation", note = hijack });
                p.Write("Move its stage (board)", Patch, $"/api/deals/{d}/stage", new { stage = "Negotiation", probability = 60 });
                p.Write("Add a note to it", Post, $"/api/deals/{d}/notes", new { tenantId = w.TenantA, dealId = d, note = hijack });
                p.Write("Log an old-style activity on it", Post, $"/api/deals/{d}/activities", new
                {
                    tenantId = w.TenantA, dealId = d, activityType = "Call", subject = hijack, activityDate = DateTime.UtcNow,
                });
                p.Write("Add a reminder to it", Post, $"/api/deals/{d}/reminders",
                    new { tenantId = w.TenantA, dealId = d, title = hijack, reminderDate = soon });
                p.Write("Add a task to it", Post, "/api/activities", new
                {
                    entityType = "Deal", entityId = d, activityType = "Task", subject = hijack, isTask = true, dueDate = soon,
                });
                p.Write("Raise a quote on it", Post, "/api/quotes", new
                {
                    dealId = d, expiresAtUtc = soon, currency = "INR",
                    items = new[] { new { name = hijack, unitPrice = 1m, quantity = 1m, taxRate = 0m } },
                });
                p.Write("Raise an invoice on it", Post, "/api/invoices", new
                {
                    dealId = d, issueDateUtc = DateTime.UtcNow.Date, dueDateUtc = soon, currency = "INR",
                    lines = new[] { new { name = hijack, unitPrice = 1m, quantity = 1m, taxRate = 0m } },
                });
                p.Write("The old deal-quote route (removed in 100)", Post, $"/api/deals/{d}/quotes", new
                {
                    tenantId = w.TenantA, dealId = d, expiresAtUtc = soon, currency = "INR",
                    items = new[] { new { name = hijack, unitPrice = 1m, quantity = 1m, taxRate = 0m } },
                });
                p.Uploads.Add(("Upload a file to it", $"/api/deals/{d}/attachments"));

                p.Page("Deal page", $"/Pipeline/Detail/{d}");
                p.Page("Deal edit page", $"/Pipeline/Edit/{d}");
                p.Page("New quote for A's deal", $"/Quotes/Create?dealId={d}");

                p.Post("Note form", $"/Pipeline/Detail/{d}?handler=AddNote", ("note", hijack), ("NoteText", hijack));
                p.Post("Task form", $"/Pipeline/Detail/{d}?handler=AddTask", ("subject", hijack), ("dueDate", soon.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                p.Post("Delete form on the deal page", $"/Pipeline/Detail/{d}?handler=Delete");
                p.Post("Delete form on the edit page", $"/Pipeline/Edit/{d}?handler=Delete");

                p.Row("Deals", d);
                p.Count("DealNotes", "DealId", d);
                p.Count("DealActivities", "DealId", d);
                p.Count("DealReminders", "DealId", d);
                p.Count("DealStageHistory", "DealId", d);
                p.Count("Quotes", "DealId", d);
                p.Count("Invoices", "DealId", d);
                p.Count("Activities", "EntityId", d);
                p.Count("Attachments", "EntityId", d);
                break;
            }

            // ── Quotes ───────────────────────────────────────────────────
            case "Quotes":
            {
                var q = w.QuoteId;
                p.Read("Open the quote", $"/api/quotes/{q}");
                p.Read("Its PDF", $"/api/quotes/{q}/pdf");
                p.Read("Its files", $"/api/quotes/{q}/attachments");
                p.Read("Its approval state", $"/api/quote-approvals/quotes/{q}");
                p.Read("Its payment schedule", $"/api/quotes/{q}/billing-schedule");
                p.Read("Quotes of A's deal", $"/api/quotes?dealId={w.DealId}");
                p.Read("B's whole quote list", "/api/quotes");
                p.Read("B's approval queue", "/api/quote-approvals/pending-chain");

                p.Write("Edit the quote", Put, $"/api/quotes/{q}",
                    await BodyFromAsync(apiA, $"/api/quotes/{q}", j =>
                    {
                        if (j["items"] is JsonArray items)
                            foreach (var item in items.OfType<JsonObject>()) item["name"] = hijack;
                    }, new { expiresAtUtc = soon, currency = "INR", items = new[] { new { name = hijack, unitPrice = 1m, quantity = 1m } } }));
                p.Write("Change its status", Put, $"/api/quotes/{q}/status", new { status = "Rejected" });
                p.Write("Delete it", Delete, $"/api/quotes/{q}");
                p.Write("Email it to the customer", Post, $"/api/quotes/{q}/send", new { baseUrl = E2ESettings.BaseUrl });
                p.Write("Submit it for approval", Post, $"/api/quote-approvals/quotes/{q}/submit", new { comment = hijack });
                p.Write("Approve it", Post, $"/api/quote-approvals/quotes/{q}/approve", new { comment = hijack });
                p.Write("Send it back", Post, $"/api/quote-approvals/quotes/{q}/request-changes", new { comment = hijack });
                p.Write("Recall its approval", Post, $"/api/quote-approvals/quotes/{q}/recall");
                p.Write("Change its payment schedule", Put, $"/api/quotes/{q}/billing-schedule", new { milestones = Array.Empty<object>() });
                p.Write("Invoice it", Post, "/api/invoices/from-quote", new { quoteId = q });
                p.Uploads.Add(("Upload a file to it", $"/api/quotes/{q}/attachments"));

                p.Page("Quote page", $"/Quotes/Detail?id={q}");
                p.Page("Quote edit page", $"/Quotes/Edit?id={q}");
                p.Page("Quote PDF", $"/Quotes/Detail?id={q}&handler=DownloadPdf", isFile: true);

                p.Post("Status form", $"/Quotes/Detail?id={q}&handler=UpdateStatus", ("id", q.ToString()), ("newStatus", "Rejected"));
                p.Post("Delete form", $"/Quotes/Detail?id={q}&handler=Delete", ("id", q.ToString()));
                p.Post("Create-invoice form", $"/Quotes/Detail?id={q}&handler=CreateInvoice", ("id", q.ToString()));
                p.Post("Approve form", $"/Quotes/Detail?id={q}&handler=Approve", ("id", q.ToString()), ("comment", hijack));

                p.Row("Quotes", q);
                p.Count("QuoteItems", "QuoteId", q);
                p.Count("QuoteApprovalRequests", "QuoteId", q);
                p.Count("Invoices", "QuoteId", q);
                p.Count("Attachments", "EntityId", q);
                break;
            }

            // ── Invoices ─────────────────────────────────────────────────
            case "Invoices":
            {
                var i = w.InvoiceId;
                p.Read("Open the invoice", $"/api/invoices/{i}");
                p.Read("Its PDF", $"/api/invoices/{i}/pdf");
                p.Read("Its buttons", $"/api/invoices/{i}/workflow");
                p.Read("Its email form", $"/api/invoices/{i}/email-prefill");
                p.Read("Invoices of A's quote", $"/api/invoices?quoteId={w.QuoteId}");
                p.Read("B's whole invoice list", "/api/invoices");

                p.Write("Edit the invoice", Put, $"/api/invoices/{i}",
                    new { notes = hijack, lines = new[] { new { name = hijack, unitPrice = 1m, quantity = 1m, taxRate = 0m } } });
                p.Write("Mark it paid", Put, $"/api/invoices/{i}/status", new { status = "Paid" });
                p.Write("Record a payment on it", Post, $"/api/invoices/{i}/payments",
                    new { amount = 1m, method = "Cash", paidAtUtc = DateTime.UtcNow, notes = hijack });
                p.Write("Delete it", Delete, $"/api/invoices/{i}");
                p.Write("Issue it", Post, $"/api/invoices/{i}/issue");
                p.Write("Void it", Post, $"/api/invoices/{i}/void", new { reason = hijack });
                p.Write("Reverse its payment", Post, $"/api/invoices/{i}/payments/{w.PaymentId}/reverse", new { reason = hijack });
                p.Write("Email it", Post, $"/api/invoices/{i}/email", new { toAddress = "hijack@example.com", message = hijack });
                p.Write("Start an online checkout on it", Post,
                    $"/api/payments/checkout/{Uri.EscapeDataString(w.InvoiceNumber)}", new { provider = "Stripe" });

                p.Page("Invoice page", $"/Invoices/Detail?id={i}");
                p.Page("Invoice edit page", $"/Invoices/Edit?id={i}");
                p.Page("Invoice PDF", $"/Invoices/Detail?id={i}&handler=DownloadPdf", isFile: true);

                p.Post("Void form", $"/Invoices/Detail?id={i}&handler=Void", ("id", i.ToString()), ("reason", hijack));
                p.Post("Reverse-payment form", $"/Invoices/Detail?id={i}&handler=ReversePayment",
                    ("id", i.ToString()), ("paymentId", w.PaymentId.ToString()), ("reason", hijack));
                p.Post("Payment form", $"/Invoices/Detail?id={i}&handler=RecordPayment", ("Id", i.ToString()),
                    ("paymentKey", Guid.NewGuid().ToString()), ("amount", "1"), ("paymentMethod", "Cash"),
                    ("paymentDate", DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

                p.Row("Invoices", i);
                p.Row("Payments", w.PaymentId);
                p.Count("Payments", "InvoiceId", i);
                p.Count("InvoiceLines", "InvoiceId", i);
                break;
            }

            // ── Tasks ────────────────────────────────────────────────────
            case "Tasks":
            {
                var t = w.TaskId;
                p.Read("B's task list (everyone's, 90 days)", "/api/activities/upcoming-tasks?daysAhead=90");
                p.Read("B's task list, assigned to A's admin",
                    $"/api/activities/upcoming-tasks?daysAhead=90&assignedToUserId={(await Db.UserIdAsync(World.A.Admin.Email))}");
                p.Read("Tasks on A's deal", $"/api/activities?entityType=Deal&entityId={w.DealId}&tasksOnly=true");

                p.Write("Complete the task", Post, $"/api/activities/{t}/complete", new { outcome = hijack });
                p.Write("Set its outcome", Post, $"/api/activities/{t}/outcome", new { outcome = hijack });
                p.Write("Edit it", Put, $"/api/activities/{t}", new { subject = hijack, activityDate = DateTime.UtcNow, dueDate = soon });
                p.Write("Give it to B's admin", Post, $"/api/activities/{t}/reassign", new { assignedToUserId = w.BAdminUserId.ToString() });
                p.Write("Delete it", Delete, $"/api/activities/{t}");

                p.Page("Tasks page", "/Tasks");

                p.Post("Complete form on the tasks page", "/Tasks?handler=Complete", ("activityId", t.ToString()));
                p.Post("Reassign form on the tasks page", "/Tasks?handler=Reassign",
                    ("activityId", t.ToString()), ("assignedToUserId", w.BAdminUserId.ToString()));
                p.Post("Complete form on B's own deal page", $"/Pipeline/Detail/{w.BDealId}?handler=CompleteTask", ("activityId", t.ToString()));
                p.Post("Delete form on B's own deal page", $"/Pipeline/Detail/{w.BDealId}?handler=DeleteEntry", ("activityId", t.ToString()));

                p.Row("Activities", t);
                break;
            }

            // ── Notes ────────────────────────────────────────────────────
            case "Notes":
            {
                p.Read("Notes on A's lead", $"/api/leads/{w.OpenLeadId}/notes?tenantId={w.TenantA}");
                p.Read("Notes on A's deal", $"/api/deals/{w.DealId}/notes");
                p.Read("Notes on B's own lead", $"/api/leads/{w.BLeadId}/notes?tenantId={w.TenantB}");
                p.Read("A's lead timeline", $"/api/leads/{w.OpenLeadId}/timeline?tenantId={w.TenantA}");

                p.Write("Delete A's lead note", Delete, $"/api/leads/notes/{w.LeadNoteId}");
                p.Write("Delete A's lead note, naming A's workspace", Delete, $"/api/leads/notes/{w.LeadNoteId}?tenantId={w.TenantA}");
                p.Write("Add a note to A's lead", Post, $"/api/leads/{w.OpenLeadId}/notes",
                    new { tenantId = w.TenantA, leadId = w.OpenLeadId, note = hijack });
                p.Write("A note on B's own lead, with A's workspace in the body", Post, $"/api/leads/{w.BLeadId}/notes",
                    new { tenantId = w.TenantA, leadId = w.BLeadId, note = hijack }, maySucceed: true);
                p.Write("Add a note to A's deal", Post, $"/api/deals/{w.DealId}/notes",
                    new { tenantId = w.TenantA, dealId = w.DealId, note = hijack });

                p.Post("Delete-note form on B's own lead", $"/Leads/Detail/{w.BLeadId}?handler=DeleteNote", ("noteId", w.LeadNoteId.ToString()));
                p.Post("Delete-note form on A's lead", $"/Leads/Detail/{w.OpenLeadId}?handler=DeleteNote", ("noteId", w.LeadNoteId.ToString()));
                p.Post("Note form on A's lead", $"/Leads/Detail/{w.OpenLeadId}?handler=AddNote", ("note", hijack), ("NoteText", hijack));
                p.Post("Note form on A's deal", $"/Pipeline/Detail/{w.DealId}?handler=AddNote", ("note", hijack), ("NoteText", hijack));

                p.Row("LeadNotes", w.LeadNoteId);
                p.Row("DealNotes", w.DealNoteId);
                p.Count("LeadNotes", "LeadId", w.OpenLeadId);
                p.Count("DealNotes", "DealId", w.DealId);
                p.Count("LeadNotes", "TenantId", w.TenantA);   // 100b — nothing new anywhere in A
                break;
            }

            // ── Attachments ──────────────────────────────────────────────
            case "Attachments":
            {
                foreach (var (what, id) in new[] { ("lead", w.LeadAttachmentId), ("deal", w.DealAttachmentId), ("quote", w.QuoteAttachmentId) })
                {
                    p.Read($"Download A's {what} file", $"/api/attachments/{id}/download");
                    p.Page($"Download A's {what} file", $"/Attachments/Download/{id}", isFile: true);
                }
                p.Read("Files on A's lead", $"/api/leads/{w.OpenLeadId}/attachments");
                p.Read("Files on A's deal", $"/api/deals/{w.DealId}/attachments");
                p.Read("Files on A's quote", $"/api/quotes/{w.QuoteId}/attachments");

                p.Write("Delete A's lead file", Delete, $"/api/leads/attachments/{w.LeadAttachmentId}");
                p.Write("Delete A's deal file through B's deal", Delete, $"/api/deals/{w.BDealId}/attachments/{w.DealAttachmentId}");
                p.Write("Delete A's deal file", Delete, $"/api/deals/attachments/{w.DealAttachmentId}");
                p.Write("Delete A's quote file", Delete, $"/api/quotes/attachments/{w.QuoteAttachmentId}");
                p.Write("Delete A's quote file through its quote", Delete, $"/api/quotes/{w.QuoteId}/attachments/{w.QuoteAttachmentId}");
                p.Write("Delete A's deal file as a lead file", Delete, $"/api/leads/attachments/{w.DealAttachmentId}");
                p.Uploads.Add(("Upload to A's lead", $"/api/leads/{w.OpenLeadId}/attachments"));
                p.Uploads.Add(("Upload to A's deal", $"/api/deals/{w.DealId}/attachments"));
                p.Uploads.Add(("Upload to A's quote", $"/api/quotes/{w.QuoteId}/attachments"));

                p.Post("Delete-file form on B's own lead", $"/Leads/Detail/{w.BLeadId}?handler=DeleteAttachment", ("attachmentId", w.LeadAttachmentId.ToString()));
                p.Post("Delete-file form on B's own deal", $"/Pipeline/Detail/{w.BDealId}?handler=DeleteAttachment", ("attachmentId", w.DealAttachmentId.ToString()));
                p.Post("Delete-file form on A's quote", $"/Quotes/Detail?id={w.QuoteId}&handler=DeleteAttachment", ("attachmentId", w.QuoteAttachmentId.ToString()));

                p.Row("Attachments", w.LeadAttachmentId);
                p.Row("Attachments", w.DealAttachmentId);
                p.Row("Attachments", w.QuoteAttachmentId);
                p.Count("Attachments", "EntityId", w.OpenLeadId);
                p.Count("Attachments", "EntityId", w.DealId);
                p.Count("Attachments", "EntityId", w.QuoteId);
                break;
            }

            // ── Products ─────────────────────────────────────────────────
            case "Products":
            {
                var pr = w.ProductId;
                p.Read("Open the product", $"/api/products/{pr}");
                p.Read("B's whole catalogue", "/api/products?pageNumber=1&pageSize=200");
                p.Read("B's catalogue, inactive too", "/api/products?pageNumber=1&pageSize=200&isActive=false");

                p.Write("Edit the product", Put, $"/api/products/{pr}",
                    await BodyFromAsync(apiA, $"/api/products/{pr}", j => j["name"] = hijack,
                        new { name = hijack, listPrice = 1m, currency = "INR", type = "Product", unitOfMeasure = "unit" }));
                p.Write("Switch it off", Patch, $"/api/products/{pr}/toggle-active");
                p.Write("Delete it", Delete, $"/api/products/{pr}");
                p.Write("A quote line of B's for A's product", Post, "/api/quotes", new
                {
                    dealId = w.BDealId, expiresAtUtc = soon, currency = "INR",
                    items = new[] { new { productId = pr, name = "B line", unitPrice = 1m, quantity = 1m, taxRate = 0m } },
                });
                p.Write("An invoice line of B's for A's product", Post, "/api/invoices", new
                {
                    dealId = w.BDealId, issueDateUtc = DateTime.UtcNow.Date, dueDateUtc = soon, currency = "INR",
                    lines = new[] { new { productId = pr, name = "B line", unitPrice = 1m, quantity = 1m, taxRate = 0m } },
                });

                p.Page("Product page", $"/Products/Detail/{pr}");
                p.Page("Product edit page", $"/Products/Edit/{pr}");

                p.Post("Switch-off form on the product page", $"/Products/Detail/{pr}?handler=ToggleActive", ("id", pr.ToString()));
                p.Post("Delete form on the product page", $"/Products/Detail/{pr}?handler=Delete", ("id", pr.ToString()));
                p.Post("Delete form on the catalogue", "/Products?handler=Delete", ("id", pr.ToString()));
                p.Post("Edit form", $"/Products/Edit/{pr}", ("Input.Name", hijack), ("Input.ListPrice", "1"));

                p.Row("Products", pr);
                p.Count("QuoteItems", "ProductId", pr);
                p.Count("InvoiceLines", "ProductId", pr);
                break;
            }

            // ── Activities ───────────────────────────────────────────────
            case "Activities":
            {
                var call = w.CallId;
                p.Read("Activities on A's lead", $"/api/activities?entityType=Lead&entityId={w.OpenLeadId}");
                p.Read("Old-style activities on A's lead", $"/api/leads/{w.OpenLeadId}/activities?tenantId={w.TenantA}");
                p.Read("Activities on B's own lead", $"/api/activities?entityType=Lead&entityId={w.BLeadId}");
                p.Read("B's assignee list", "/api/activities/assignees");

                p.Write("Edit A's call", Put, $"/api/activities/{call}", new { subject = hijack, activityDate = DateTime.UtcNow, duration = 1 });
                p.Write("Delete A's call", Delete, $"/api/activities/{call}");
                p.Write("Set A's call outcome", Post, $"/api/activities/{call}/outcome", new { outcome = hijack });
                p.Write("Complete A's call", Post, $"/api/activities/{call}/complete", new { outcome = hijack });
                p.Write("Log a call on A's lead", Post, "/api/activities",
                    new { entityType = "Lead", entityId = w.OpenLeadId, activityType = "Call", subject = hijack, duration = 1 });
                p.Write("Log an old-style activity on A's lead", Post, $"/api/leads/{w.OpenLeadId}/activities", new
                {
                    tenantId = w.TenantA, leadId = w.OpenLeadId, activityType = "Call", subject = hijack, activityDate = DateTime.UtcNow,
                });

                p.Post("Edit form on B's own lead", $"/Leads/Detail/{w.BLeadId}?handler=UpdateEntry",
                    ("activityId", call.ToString()), ("subject", hijack));
                p.Post("Delete form on B's own lead", $"/Leads/Detail/{w.BLeadId}?handler=DeleteEntry", ("activityId", call.ToString()));
                p.Post("Outcome form on B's own lead", $"/Leads/Detail/{w.BLeadId}?handler=SetOutcome",
                    ("activityId", call.ToString()), ("outcome", hijack));
                p.Post("Activity form on A's lead", $"/Leads/Detail/{w.OpenLeadId}?handler=AddActivity",
                    ("activityType", "Call"), ("subject", hijack));

                p.Row("Activities", call);
                p.Row("LeadActivities", w.LeadActivityId);
                p.Count("Activities", "EntityId", w.OpenLeadId);
                p.Count("LeadActivities", "LeadId", w.OpenLeadId);
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(module), module, "No attempts are planned for this module.");
        }

        return p;
    }

    /// <summary>
    /// A request body that would really work: A's own record, read by A's
    /// admin, with <paramref name="change"/> applied — so when B sends it,
    /// the only thing wrong with it is whose record it is. Falls back to
    /// <paramref name="fallback"/> if A's read fails (the attempt still
    /// runs; the database check still decides).
    /// </summary>
    private static async Task<object> BodyFromAsync(ApiCaller apiA, string path, Action<JsonObject> change, object fallback)
    {
        var r = await apiA.GetAsync(path);
        if (!r.IsSuccess) return fallback;
        try
        {
            if (JsonNode.Parse(r.Body) is JsonObject j)
            {
                change(j);
                return j;
            }
        }
        catch (JsonException) { }
        return fallback;
    }

    // =================================================================
    // Workspace B's own page: its form token and one of its statuses
    // =================================================================

    private static async Task<Context> ContextAsync(IPage b, IsolationWorld w)
    {
        await b.GotoAsync($"/Leads/Detail/{w.BLeadId}");
        var token = b.Locator("input[name=__RequestVerificationToken]");
        if (await token.CountAsync() == 0)
            throw new AssertionException("Workspace B's lead page has no anti-forgery token to post forms with.");
        var formToken = await token.First.InputValueAsync();

        // A status that is not the one the lead has now, and not Converted.
        var key = await b.EvaluateAsync<string?>(
            @"() => {
                const s = document.querySelector('#statusChangeForm select[name=status]');
                if (!s) return null;
                const o = Array.from(s.options).find(o => o.value && o.value !== s.value && !/convert/i.test(o.text + ' ' + o.value));
                return o ? o.value : null;
            }");

        return new Context(formToken, key ?? "Contacted");
    }

    // =================================================================
    // Looking for leaks
    // =================================================================

    /// <param name="ignore">
    /// 100b. A marker B itself typed into the request — a search term the
    /// page echoes back into its own search box. Its appearing proves
    /// nothing; every OTHER marker still counts.
    /// </param>
    private static List<string> MarkersIn(string? text, IsolationWorld w, string? ignore = null)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();
        return w.Markers
            .Where(m => ignore is null || !string.Equals(m, ignore, StringComparison.OrdinalIgnoreCase))
            .Where(m => text.Contains(m, StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
    }

    private static bool LooksLikeFile(string? type) =>
        type is not null &&
        (type.Contains("pdf", StringComparison.OrdinalIgnoreCase)
         || type.Contains("octet-stream", StringComparison.OrdinalIgnoreCase)
         || type.Contains("spreadsheet", StringComparison.OrdinalIgnoreCase)
         || type.StartsWith("image/", StringComparison.OrdinalIgnoreCase));

    private static void CheckRead(string what, string call, ApiResponse r, IsolationWorld w, List<string> leaks, List<string> warnings)
    {
        var found = MarkersIn(r.Body, w);
        if (found.Count == 0 && r.Bytes.Length > 0 && LooksLikeFile(r.ContentType))
            found = MarkersIn(Encoding.Latin1.GetString(r.Bytes), w);

        if (found.Count > 0)
            leaks.Add($"{what}: {call} → {r.Status} with workspace A's data ({string.Join(", ", found)}).");
        else if (r.IsSuccess && LooksLikeFile(r.ContentType))
            leaks.Add($"{what}: {call} → {r.Status} handed workspace B a file ({r.ContentType}).");
        else if (r.Status >= 500)
            warnings.Add($"{what}: {call} → {r}");
    }

    /// <param name="maySucceed">
    /// 100b. The request names B's OWN record and only the body names A's
    /// workspace. The API now ignores the body's workspace, so this lands —
    /// correctly — on B's record. Accepting it is fine; the database check
    /// (nothing new in A's workspace) is what decides.
    /// </param>
    private static void CheckWrite(string what, string call, ApiResponse r, IsolationWorld w, List<string> leaks, List<string> warnings,
        bool maySucceed = false)
    {
        var found = MarkersIn(r.Body, w);
        if (found.Count > 0)
            leaks.Add($"{what}: {call} → {r.Status} answered with workspace A's data ({string.Join(", ", found)}).");
        if (r.IsSuccess && maySucceed)
            return;
        if (r.IsSuccess)
            leaks.Add($"{what}: {call} was ACCEPTED ({r}). It must be refused — the record is another workspace's.");
        else if (r.Status >= 500)
            warnings.Add($"{what}: {call} → {r}");
    }

    // =================================================================
    // A's records as stored
    // =================================================================

    private static async Task<Dictionary<string, string>> SnapshotAsync(Probe p)
    {
        var snap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (table, id) in p.Rows.Distinct())
            snap[$"rows {table} {id}"] = await Db.RowJsonAsync(table, id);
        foreach (var (table, column, id) in p.Counts.Distinct())
            snap[$"count of {table} rows with {column} = {id}"] =
                (await Db.CountWhereAsync(table, column, id)).ToString(CultureInfo.InvariantCulture);
        return snap;
    }

    // =================================================================
    // Requests from B's browser, with B's cookies
    // =================================================================

    private sealed record FetchResult(int Status, string? Type, string Url, string Text);

    /// <summary>
    /// fetch() inside workspace B's own page, so the request carries B's
    /// sign-in cookie exactly as typing the address (or a form posting)
    /// would. Follows redirects. The body is read as Latin-1, so a PDF's
    /// bytes come back too.
    /// </summary>
    private static async Task<FetchResult> FetchAsync(IPage page, string method, string path, string[][]? form)
    {
        var url = E2ESettings.BaseUrl + path;
        var json = await page.EvaluateAsync<string>(
            @"async (a) => {
                const init = { method: a.method, credentials: 'same-origin', redirect: 'follow' };
                if (a.form) init.body = new URLSearchParams(a.form);
                try {
                    const r = await fetch(a.url, init);
                    const buf = await r.arrayBuffer();
                    const text = new TextDecoder('latin1').decode(buf);
                    return JSON.stringify({ status: r.status, type: r.headers.get('content-type'), url: r.url, text });
                } catch (e) {
                    return JSON.stringify({ status: 0, type: null, url: a.url, text: String(e) });
                }
            }",
            new { method, url, form });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new FetchResult(
            root.GetProperty("status").GetInt32(),
            root.GetProperty("type").ValueKind == JsonValueKind.String ? root.GetProperty("type").GetString() : null,
            root.GetProperty("url").GetString() ?? url,
            root.GetProperty("text").GetString() ?? "");
    }
}
