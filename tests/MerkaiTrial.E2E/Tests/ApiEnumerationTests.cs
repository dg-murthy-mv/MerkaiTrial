// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/ApiEnumerationTests.cs
//
// VUL-18 (round 105) — the API can't be walked wholesale.
//
//   Lists_are_bounded_and_one_person_is_held_to_a_pace
//
// LIST BOUNDS (agreed: at most 1,000 rows a page)
//   Every list asked for pageSize=2147483647, for the last possible page,
//   for a negative page, for a zero or negative size — answers, quickly,
//   with no server error and never more than 1,000 rows. (An overflowing
//   page number used to make a negative skip, which is a server error.)
//
// PACE (agreed: 1,200 calls a minute per person)
//   A person of this test's own fires 1,260 calls as fast as the server
//   takes them:
//     • the first ones are answered;
//     • once past the limit the answer is 429 Too Many Requests, with
//       Retry-After and a sentence for people, and nothing else;
//     • another person of the same workspace is NOT slowed at the same
//       moment — it is per person;
//     • the health check is never limited.
// =====================================================================

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class ApiEnumerationTests : E2ETest
{
    private const int MaxPageSize = 1000;
    private const int PerMinute = 1200;

    private static readonly string[] Lists =
    {
        "/api/leads?pageNumber={page}&pageSize={size}",
        "/api/contacts?pageNumber={page}&pageSize={size}",
        "/api/companies?pageNumber={page}&pageSize={size}",
        "/api/deals?page={page}&pageSize={size}",
        "/api/products?pageNumber={page}&pageSize={size}",
        "/api/invoices/paged?pageNumber={page}&pageSize={size}",
        "/api/audit?pageNumber={page}&pageSize={size}",
        "/api/roles?pageNumber={page}&pageSize={size}",
        "/api/users?pageNumber={page}&pageSize={size}",
        "/api/notifications?page={page}&pageSize={size}",
    };

    [Test]
    public async Task Lists_are_bounded_and_one_person_is_held_to_a_pace()
    {
        var a = World.A;
        var apiA = await Api.AsAsync(a.Admin);
        var m = Random.Shared.Next(10000, 99999).ToString();
        var problems = new List<string>();

        await Step("Every list, asked for absurd pages and sizes: answered quickly, no server error, never over 1,000 rows", async () =>
        {
            var extremes = new (string Page, string Size)[]
            {
                ("1", "2147483647"),
                ("2147483647", "2147483647"),
                ("2147483647", "50"),
                ("-5", "50"),
                ("0", "0"),
                ("1", "-100"),
                ("1", "9999999999"),      // too big for a number: a plain 400 is right
            };

            foreach (var list in Lists)
                foreach (var (page, size) in extremes)
                {
                    var path = list.Replace("{page}", page).Replace("{size}", size);
                    var watch = Stopwatch.StartNew();
                    var r = await apiA.GetAsync(path);
                    watch.Stop();

                    if (r.Status == 404 && page == "1" && size == "2147483647")
                    {
                        TestContext.Progress.WriteLine($"    {list.Split('?')[0]} isn't on this API — skipped");
                        break;
                    }
                    if (r.Status >= 500) { problems.Add($"GET {path} → server error: {r}"); continue; }
                    if (watch.Elapsed > TimeSpan.FromSeconds(15)) problems.Add($"GET {path} took {watch.Elapsed.TotalSeconds:0}s.");
                    if (!r.IsSuccess) continue;     // a 400 for nonsense is fine

                    var rows = RowsIn(r);
                    if (rows > MaxPageSize) problems.Add($"GET {path} returned {rows} rows (most allowed {MaxPageSize}).");
                    if (ReportedPageSize(r) is { } reported && reported > MaxPageSize)
                        problems.Add($"GET {path} says its page size is {reported} (most allowed {MaxPageSize}).");
                }
        });

        var runner = await Step("A person of this test's own (only they will be slowed down)", () =>
            WorldBuilder.AddPersonForTestAsync(a, "pace" + m, "Pace", "Viewer"));
        var apiRunner = await Api.AsAsync(runner);

        await Step($"{PerMinute + 60} calls as fast as the server takes them: the first are answered, then 429 with Retry-After", async () =>
        {
            var statuses = new ConcurrentBag<int>();
            ApiResponse? firstRefusal = null;
            var refusalLock = new object();
            var watch = Stopwatch.StartNew();

            var next = 0;
            var total = PerMinute + 60;
            var workers = Enumerable.Range(0, 40).Select(async _ =>
            {
                while (Interlocked.Increment(ref next) <= total)
                {
                    var r = await apiRunner.GetAsync("/api/meta/countries");
                    statuses.Add(r.Status);
                    if (r.Status == 429)
                        lock (refusalLock) firstRefusal ??= r;
                }
            });
            await Task.WhenAll(workers);
            watch.Stop();

            var answered = statuses.Count(s => s != 429);
            var refused = statuses.Count(s => s == 429);
            var errors = statuses.Count(s => s >= 500);
            TestContext.Progress.WriteLine($"    {statuses.Count} calls in {watch.Elapsed.TotalSeconds:0.0}s: {answered} answered, {refused} refused (429)");

            if (watch.Elapsed > TimeSpan.FromSeconds(55))
                Assert.Inconclusive($"The {total} calls took {watch.Elapsed.TotalSeconds:0}s — too slow to reach a per-minute limit on this machine.");

            if (errors > 0) problems.Add($"{errors} of the fast calls got a server error.");
            if (refused == 0) problems.Add($"{total} calls within a minute by one person were ALL answered — nothing holds a script to a pace.");
            if (answered < PerMinute - 50) problems.Add($"Only {answered} calls were answered before the limit — it should be about {PerMinute}.");

            if (firstRefusal is { } f)
            {
                if (!int.TryParse(f.Header("Retry-After"), out var wait) || wait < 1 || wait > 60)
                    problems.Add($"The 429 answer's Retry-After is '{f.Header("Retry-After")}' (expected 1–60 seconds).");
                if (!f.Body.Contains("Too many requests", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"The 429 answer has no sentence for people: {f}");
            }
        });

        await Step("At the same moment, another person of the workspace is not slowed down", async () =>
        {
            var r = await apiA.GetAsync("/api/meta/countries");
            if (r.Status == 429) problems.Add("Workspace A's admin was refused (429) because ANOTHER person went too fast.");
            else if (!r.IsSuccess) problems.Add($"Workspace A's admin's call failed: {r}");

            var again = await apiRunner.GetAsync("/api/meta/countries");
            if (again.Status != 429) problems.Add($"The person who went too fast was let straight back in ({again}).");
        });

        await Step("The health check is never limited", async () =>
        {
            for (var i = 0; i < 5; i++)
            {
                var r = await Api.Anonymous.GetAsync("/health");
                if (r.Status != 200) { problems.Add($"GET /health → {r}"); break; }
            }
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    /// <summary>The rows in a list answer: the root array, or the first array property of the root.</summary>
    private static int RowsIn(ApiResponse r)
    {
        try
        {
            var j = r.Json();
            if (j.ValueKind == JsonValueKind.Array) return j.GetArrayLength();
            if (j.ValueKind != JsonValueKind.Object) return 0;
            foreach (var name in new[] { "items", "data", "results", "rows" })
                if (j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array) return v.GetArrayLength();
            return j.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array)
                    .Select(p => p.Value.GetArrayLength()).DefaultIfEmpty(0).Max();
        }
        catch (JsonException) { return 0; }
    }

    private static long? ReportedPageSize(ApiResponse r)
    {
        try
        {
            var j = r.Json();
            if (j.ValueKind == JsonValueKind.Object && j.TryGetProperty("pageSize", out var v) && v.ValueKind == JsonValueKind.Number)
                return v.GetInt64();
        }
        catch (JsonException) { }
        return null;
    }
}
