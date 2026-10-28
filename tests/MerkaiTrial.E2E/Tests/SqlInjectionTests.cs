// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/SqlInjectionTests.cs
//
// VUL-01 (round 104) — SQL injection in search, filter and sort.
//
//   Search_filter_and_sort_inputs_cannot_inject_SQL
//
// Workspace B's admin sends the classic attack strings through every
// search box, filter and sort the lists accept — leads, deals, contacts,
// companies, products, invoices, quotes and the audit log, through the API
// and through the pages. Workspace A holds a "canary" lead that B must
// never see.
//
// A FAILURE IS ANY OF
//   • a server error (5xx): SQL that broke is SQL that was built from text;
//   • the canary — workspace A's lead — in any answer B gets;
//   • a "WAITFOR DELAY '0:0:5'" answer taking 5 seconds or more (blind
//     injection: nothing shows, but the database obeyed);
//   • "' OR '1'='1" FINDING leads, as A searching its own list — a search
//     that was really a condition;
//   • any table losing or gaining rows ("DROP TABLE", "DELETE FROM"), or
//     B's admin becoming a super admin ("UPDATE Users SET IsSuperAdmin").
//
// The app builds every query through Entity Framework with parameters and
// lets sort keys through a fixed list, so this is expected to pass. It is
// here so it keeps passing.
// =====================================================================

using System.Diagnostics;
using System.Text.Json;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class SqlInjectionTests : E2ETest
{
    private static readonly string[] Payloads =
    {
        "' OR '1'='1",
        "' OR 1=1 --",
        "\" OR \"\"=\"",
        "'; DROP TABLE Leads; --",
        "'; DELETE FROM Leads; --",
        "1; UPDATE Users SET IsSuperAdmin = 1; --",
        "') UNION SELECT Id, Email, PasswordHash FROM Users --",
        "%' AND 1=CONVERT(int,(SELECT TOP 1 Name FROM Tenants)) --",
        "x'; WAITFOR DELAY '0:0:5' --",
        "%", "_", "[", "\\", "'", "--", "/*",
    };

    private static readonly string[] Tables = { "Leads", "Deals", "Contacts", "Companies", "Users", "Tenants", "Quotes", "Invoices", "Products" };

    [Test]
    public async Task Search_filter_and_sort_inputs_cannot_inject_SQL()
    {
        var m = Random.Shared.Next(10000, 99999).ToString();
        var canary = "Canary " + World.Stamp + "-" + m;
        var tenantA = await Db.TenantIdAsync(World.A.Name);
        var apiA = await Api.AsAsync(World.A.Admin);
        var apiB = await Api.AsAsync(World.B.Admin);
        await Api.EnsureTokensWorkAsync(apiB);

        await Step("Workspace A has a canary lead", async () =>
        {
            var r = await apiA.PostAsync("/api/leads", new
            {
                tenantId = tenantA, fullName = canary, email = World.EmailFor("A", "canary" + m),
                phone = "+9198" + m + "123", companyName = canary + " Co", estimatedValue = 1000m, currency = "INR",
            });
            Assert.That(r.IsSuccess, Is.True, $"Adding the canary lead failed: {r}");
        });

        var before = new Dictionary<string, int>();
        foreach (var t in Tables) before[t] = await Db.CountAllRowsAsync(t);
        var bAdminId = await Db.UserIdAsync(World.B.Admin.Email);

        var problems = new List<string>();
        var guid = Guid.NewGuid();

        await Step($"{Payloads.Length} attack strings through every list's search, filter and sort (API, as workspace B)", async () =>
        {
            foreach (var p in Payloads)
            {
                var e = Uri.EscapeDataString(p);
                var paths = new[]
                {
                    $"/api/leads?pageNumber=1&pageSize=50&searchTerm={e}",
                    $"/api/leads?pageNumber=1&pageSize=50&status={e}&assignedTo={e}",
                    $"/api/leads?pageNumber=1&pageSize=50&sort={e}&dir={e}",
                    $"/api/leads?pageNumber=1&pageSize=50&cf={Uri.EscapeDataString($"{guid}~eq~{p}")}&cf={e}",
                    $"/api/deals?page=1&pageSize=50&search={e}&stage={e}&ownerUserId={e}",
                    $"/api/deals?page=1&pageSize=50&sort={e}&dir={e}&cf={e}",
                    $"/api/contacts?pageNumber=1&pageSize=50&searchTerm={e}&sort={e}&dir={e}",
                    $"/api/companies?pageNumber=1&pageSize=50&searchTerm={e}&vertical={e}&country={e}&sort={e}",
                    $"/api/products?pageNumber=1&pageSize=50&search={e}&category={e}&currency={e}&sort={e}",
                    $"/api/invoices/paged?pageNumber=1&pageSize=50&search={e}&status={e}&sort={e}&dir={e}",
                    $"/api/quotes?status={e}",
                    $"/api/audit?search={e}&entityType={e}&action={e}",
                };

                foreach (var path in paths)
                {
                    var watch = Stopwatch.StartNew();
                    var r = await apiB.GetAsync(path);
                    watch.Stop();
                    Judge("GET " + path, r.Status, r.Body, watch.ElapsedMilliseconds, p, canary, problems);
                }
            }
        });

        await Step("The same through the pages' own search boxes (as workspace B, in the browser)", async () =>
        {
            var b = await SignedInAsync(World.B.Admin);
            await b.GotoAsync("/Dashboard");
            foreach (var p in Payloads)
            {
                var e = Uri.EscapeDataString(p);
                foreach (var path in new[]
                {
                    $"/Leads?SearchTerm={e}&Status={e}&Sort={e}&Dir={e}",
                    $"/Contacts?SearchTerm={e}&Sort={e}",
                    $"/Companies?SearchTerm={e}&Sort={e}",
                    $"/Pipeline?Search={e}",
                    $"/Products?Search={e}&Sort={e}",
                    $"/Invoices?Search={e}&Status={e}",
                    $"/Quotes?Status={e}",
                })
                {
                    var r = await PageHttp.FetchAsync(b, "GET", path);
                    Judge("page " + path, r.Status, r.Text, r.Millis, p, canary, problems);
                }
            }
        });

        await Step("Workspace A searching its own leads for \"' OR '1'='1\" finds nothing (it is text, not a condition)", async () =>
        {
            var r = await apiA.GetAsync("/api/leads?pageNumber=1&pageSize=50&searchTerm=" + Uri.EscapeDataString("' OR '1'='1"));
            Assert.That(r.IsSuccess, Is.True, $"The search failed: {r}");
            var total = TotalOf(r.Json());
            Assert.That(total, Is.EqualTo(0),
                $"Searching for ' OR '1'='1 found {total} lead(s) — the search term became part of the SQL condition.");
        });

        await Step("Nothing was dropped, deleted or changed, and B's admin is no super admin", async () =>
        {
            foreach (var t in Tables)
            {
                var now = await Db.CountAllRowsAsync(t);
                if (now < before[t])
                    problems.Add($"The {t} table LOST rows during the test: {before[t]} → {now}.");
            }
            Assert.That(await Db.CountContainingAsync("Leads", tenantA, canary), Is.EqualTo(1), "The canary lead is gone.");
            Assert.That(await Db.ValueAsync("Users", "IsSuperAdmin", bAdminId), Is.EqualTo("False"),
                "Workspace B's admin became a super admin.");
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems.Take(40)));
    }

    private static void Judge(string call, int status, string body, long millis, string payload, string canary, List<string> problems)
    {
        if (status >= 500)
            problems.Add($"{call} → HTTP {status} (a server error on an attack string).");
        if (body.Contains(canary, StringComparison.OrdinalIgnoreCase))
            problems.Add($"{call} showed workspace A's canary lead to workspace B.");
        if (payload.Contains("WAITFOR", StringComparison.Ordinal) && millis >= 5000)
            problems.Add($"{call} took {millis} ms — the database may have run the WAITFOR DELAY.");
    }

    private static int TotalOf(JsonElement j)
    {
        foreach (var name in new[] { "totalCount", "totalItems", "total", "count" })
            if (j.ValueKind == JsonValueKind.Object && j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
                return v.GetInt32();
        if (j.ValueKind == JsonValueKind.Object && j.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            return items.GetArrayLength();
        if (j.ValueKind == JsonValueKind.Array) return j.GetArrayLength();
        throw new AssertionException("The leads list answer has no total or items: " + ApiResponse.Short(j.GetRawText(), 200));
    }
}
