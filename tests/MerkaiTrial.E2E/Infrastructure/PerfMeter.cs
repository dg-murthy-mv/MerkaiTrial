// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/PerfMeter.cs
//
// NEW (107). Measuring, judging and reporting for the performance tests.
//
// MEASURING
//   • A page: GET over plain HTTP as a signed-in person (WebSession), once
//     to warm up, then 3 times. The time is the whole answer — Admin.Web
//     building the page, every WebApi call it makes, every query. Each GET
//     carries an X-E2E-Cost-Id; afterwards GET /__e2e/cost/{id} says how
//     many database queries the page needed (Admin.Web's own plus the
//     WebApi's for it) and how many WebApi calls it made.
//   • An API call (a save): its time, and the queries the WebApi ran for
//     it (the X-E2E-Cost header on the answer).
//
// JUDGING — two rules, both must pass
//   1. A CEILING: a fixed limit no page or save may cross on any computer
//      ("clearly broken"). Generous on purpose: the test servers run in
//      Debug, in Development mode, on a laptop, next to SQL Server.
//   2. THE BASELINE: perf-baseline.json (next to the test project) keeps
//      each measurement from the first good run. A later run fails if it
//      is more than 50% slower AND more than 1000 ms slower than that (both,
//      so a 400 ms page going to 700 ms is not a failure — 107-update3: on
//      a busy laptop the same page swings by a few hundred ms between
//      runs, and 300 ms flagged that noise), or needs clearly
//      more queries than it did. New measurements are added to the file
//      automatically. To accept new numbers on purpose (a new computer, a
//      deliberate change), run run-e2e.ps1 -UpdatePerfBaseline once.
//      Only measurements that passed their ceiling are ever written, so a
//      slow page can never become "normal".
//
// REPORTING
//   Every measurement is printed and appended to
//   e2e-artifacts/perf-report.txt: median, slowest, queries, API calls and
//   the baseline, so a run can be compared with the last at a glance.
// =====================================================================

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace MerkaiTrial.E2E.Infrastructure;

/// <summary>The fixed ceilings (rule 1). Milliseconds.</summary>
public static class PerfLimits
{
    /// <summary>A list page or the dashboard, built by the server (median of 3).</summary>
    public const double PageMs = 3_000;

    /// <summary>A record's own page (median of 3).</summary>
    public const double DetailMs = 3_000;

    /// <summary>A page in a real browser until it has finished loading (median).</summary>
    public const double BrowserLoadMs = 6_000;

    /// <summary>A save, typical (median of 15) …</summary>
    public const double SaveMedianMs = 1_000;

    /// <summary>… and the slowest of the 15.</summary>
    public const double SaveMaxMs = 3_000;

    /// <summary>Database queries one page may need, all told. Far above normal: this catches runaways.</summary>
    public const int PageQueries = 100;

    /// <summary>A list showing 50 rows may need at most this many more queries than the same list showing 10.</summary>
    public const int ExtraQueriesForMoreRows = 5;

    /// <summary>20 people at once: the slowest 5% of answers.</summary>
    public const double LoadP95Ms = 6_000;
}

/// <summary>One thing measured several times.</summary>
public sealed class PerfMetric
{
    public required string Key { get; init; }
    public required string Label { get; init; }

    /// <summary>
    /// 107-update5. Judged by its fixed limit only, never against the
    /// baseline. For the 20-people test: its numbers depend on what ran just
    /// before it (alone: 2.6 s slowest 5%; after 130 other tests in the full
    /// suite: 4.6 s), so a baseline comparison flagged the run order, not
    /// the app. The 6-second limit still applies.
    /// </summary>
    public bool CeilingOnly { get; init; }

    public List<double> Ms { get; } = new();
    public List<int> Queries { get; } = new();
    public List<int> ApiCalls { get; } = new();

    /// <summary>107-update. The slowest run's slowest WebApi calls, for a page that was slow.</summary>
    public List<string> SlowCalls { get; set; } = new();
    private double _slowestRunMs;

    public void NoteCalls(double runMs, List<string> calls)
    {
        if (runMs < _slowestRunMs || calls.Count == 0) return;
        _slowestRunMs = runMs;
        SlowCalls = calls;
    }

    public double CeilingMs { get; init; }
    public double? MaxCeilingMs { get; init; }

    public double Median => Percentile(0.5);
    public double Max => Ms.Count == 0 ? 0 : Ms.Max();
    public double P95 => Percentile(0.95);

    /// <summary>The typical query count (median), or null when the server did not count.</summary>
    public int? QueryCount => Queries.Count == 0 ? null : Queries.OrderBy(q => q).ElementAt(Queries.Count / 2);
    public int? ApiCallCount => ApiCalls.Count == 0 ? null : ApiCalls.OrderBy(q => q).ElementAt(ApiCalls.Count / 2);

    public bool WithinCeiling => Median <= CeilingMs && (MaxCeilingMs is null || Max <= MaxCeilingMs);

    private double Percentile(double p)
    {
        if (Ms.Count == 0) return 0;
        var sorted = Ms.OrderBy(x => x).ToList();
        var index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}

public static class PerfMeter
{
    private static readonly HttpClient CostClient = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        AllowAutoRedirect = false,
    })
    { BaseAddress = new Uri(E2ESettings.BaseUrl), Timeout = TimeSpan.FromSeconds(20) };

    private static bool _warnedNoCost;

    /// <summary>
    /// A page over HTTP: one warm-up, then <paramref name="runs"/> timed GETs.
    /// Anything but 200 fails the step — a slow error page is not a fast page.
    /// </summary>
    public static async Task<PerfMetric> PageAsync(WebSession session, string key, string label, string path,
        double ceilingMs = PerfLimits.PageMs, int runs = 3)
    {
        var metric = new PerfMetric { Key = key, Label = label, CeilingMs = ceilingMs };

        await GetOkAsync(session, path, label, null);   // warm-up: compiled views, cached plans

        for (var i = 0; i < runs; i++)
        {
            var id = Guid.NewGuid().ToString("N");
            var watch = Stopwatch.StartNew();
            await GetOkAsync(session, path, label, id);
            watch.Stop();
            metric.Ms.Add(watch.Elapsed.TotalMilliseconds);

            if (await CostAsync(id) is { } cost)
            {
                metric.Queries.Add(cost.AllQueries);
                metric.ApiCalls.Add(cost.ApiCalls);
                metric.NoteCalls(watch.Elapsed.TotalMilliseconds, cost.SlowestCalls);
            }
        }
        return metric;
    }

    private static async Task GetOkAsync(WebSession session, string path, string label, string? costId)
    {
        var headers = costId is null ? null : new Dictionary<string, string> { ["X-E2E-Cost-Id"] = costId };
        WebSession.Answer answer;
        try
        {
            answer = await session.GetAsync(path, headers);
        }
        catch (TaskCanceledException)
        {
            throw new AssertionException($"{label} ({path}) did not answer within 60 seconds.");
        }
        if (answer.Status != 200)
            throw new AssertionException($"{label} ({path}) answered {answer} — expected the page (200).");
    }

    public sealed record ApiTiming(double Ms, int? Queries, ApiResponse Response);

    /// <summary>One API call: how long, and how many queries the WebApi ran for it.</summary>
    public static async Task<ApiTiming> ApiAsync(Func<Task<ApiResponse>> call)
    {
        var watch = Stopwatch.StartNew();
        var r = await call();
        watch.Stop();
        var cost = Parse(r.Header("X-E2E-Cost"));
        return new ApiTiming(watch.Elapsed.TotalMilliseconds, cost?.AllQueries, r);
    }

    // ── /__e2e/cost ─────────────────────────────────────────────────────

    public sealed record Cost(int DbQueries, int ApiCalls, int ApiDbQueries)
    {
        public int AllQueries => DbQueries + ApiDbQueries;
        public List<string> SlowestCalls { get; init; } = new();
    }

    private static async Task<Cost?> CostAsync(string id)
    {
        try
        {
            // The server files the numbers as the request finishes, which can
            // be a moment after the last byte reached us — so ask a few times.
            HttpResponseMessage? found = null;
            for (var attempt = 0; attempt < 6 && found is null; attempt++)
            {
                var r0 = await CostClient.GetAsync($"/__e2e/cost/{id}");
                if (r0.StatusCode != System.Net.HttpStatusCode.NotFound) { found = r0; break; }
                r0.Dispose();
                await Task.Delay(50);
            }
            if (found is null)
            {
                WarnNoCost();
                return null;
            }
            using var r = found;
            r.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var j = doc.RootElement;
            var calls = j.TryGetProperty("slowestCalls", out var sc) && sc.ValueKind == JsonValueKind.Array
                ? sc.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : new List<string>();
            return new Cost(Int(j, "dbQueries"), Int(j, "apiCalls"), Int(j, "apiDbQueries")) { SlowestCalls = calls };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            WarnNoCost();
            return null;
        }
    }

    private static int Int(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

    /// <summary>The WebApi's X-E2E-Cost header: "db=12;dbms=40.5;api=0;…".</summary>
    private static Cost? Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var parts = header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
        int I(string k) => parts.TryGetValue(k, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return new Cost(I("db"), I("api"), I("apidb"));
    }

    private static void WarnNoCost()
    {
        if (_warnedNoCost) return;
        _warnedNoCost = true;
        TestContext.Progress.WriteLine(
            "    (the test servers are not counting queries — start them with this round's run-e2e.ps1, which sets " +
            "Diagnostics__RequestCost=true. Times are still measured; query counts are left out.)");
    }

    // ── Report ──────────────────────────────────────────────────────────

    public static void Report(string section, IEnumerable<PerfMetric> metrics)
    {
        var lines = new List<string> { "", $"── {section} ── {DateTime.Now:yyyy-MM-dd HH:mm}" };
        var baseline = PerfBaseline.Snapshot();
        foreach (var m in metrics)
        {
            var b = baseline.TryGetValue(m.Key, out var e) ? $"baseline {e.Ms,6:0} ms{(e.Queries is { } bq ? $" / {bq} q" : "")}" : "baseline —";
            var q = m.QueryCount is { } qc ? $"{qc,4} queries" : "   ? queries";
            var a = m.ApiCallCount is { } ac ? $"{ac,3} API calls" : "";
            var verdict = m.WithinCeiling ? "" : $"  OVER THE {m.CeilingMs:0} ms LIMIT";
            lines.Add($"  {m.Label,-58} {m.Median,7:0} ms (slowest {m.Max,6:0})  {q}  {a,-13} {b}{verdict}");
            // 107-update: where a slow page spent its time.
            if (m.Median > 1000 || !m.WithinCeiling)
                foreach (var call in m.SlowCalls)
                    lines.Add($"      ↳ {call}");
        }

        foreach (var l in lines) TestContext.Progress.WriteLine(l);
        try
        {
            Directory.CreateDirectory(E2ESettings.ArtifactsDir);
            File.AppendAllLines(Path.Combine(E2ESettings.ArtifactsDir, "perf-report.txt"), lines);
        }
        catch (IOException) { /* the console has it */ }
    }

    public static void ReportLines(string section, IEnumerable<string> lines, string file)
    {
        var all = new List<string> { "", $"── {section} ── {DateTime.Now:yyyy-MM-dd HH:mm}" };
        all.AddRange(lines.Select(l => "  " + l));
        foreach (var l in all) TestContext.Progress.WriteLine(l);
        try
        {
            Directory.CreateDirectory(E2ESettings.ArtifactsDir);
            File.AppendAllLines(Path.Combine(E2ESettings.ArtifactsDir, file), all);
        }
        catch (IOException) { }
    }

    /// <summary>
    /// Both rules for a set of measurements: ceilings, then the baseline.
    /// Returns every problem in words; the baseline file is updated with
    /// whatever is new (or everything, with -UpdatePerfBaseline).
    /// </summary>
    public static List<string> Judge(IEnumerable<PerfMetric> metrics)
    {
        var list = metrics.ToList();
        var problems = new List<string>();

        foreach (var m in list)
        {
            if (m.Median > m.CeilingMs)
                problems.Add($"{m.Label}: typically {m.Median:0} ms — over the {m.CeilingMs:0} ms limit.");
            if (m.MaxCeilingMs is { } maxLimit && m.Max > maxLimit)
                problems.Add($"{m.Label}: the slowest took {m.Max:0} ms — over the {maxLimit:0} ms limit.");
        }

        problems.AddRange(PerfBaseline.CompareAndRecord(list));
        return problems;
    }
}

/// <summary>perf-baseline.json — see the file header (rule 2).</summary>
public static class PerfBaseline
{
    public sealed record Entry(double Ms, int? Queries, string Recorded);

    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>run-e2e.ps1 -UpdatePerfBaseline: record this run's numbers over the old ones.</summary>
    public static bool Updating =>
        Environment.GetEnvironmentVariable("E2E_PERF_UPDATE_BASELINE") is "1" or "true" or "True";

    public static string FilePath
    {
        get
        {
            if (Environment.GetEnvironmentVariable("E2E_PERF_BASELINE") is { Length: > 0 } custom) return custom;
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            for (var d = dir; d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "MerkaiTrial.E2E.csproj")))
                    return Path.Combine(d.FullName, "perf-baseline.json");
            return Path.Combine(TestContext.CurrentContext.TestDirectory, "perf-baseline.json");
        }
    }

    public static Dictionary<string, Entry> Snapshot()
    {
        lock (Lock) return Load();
    }

    public static List<string> CompareAndRecord(IReadOnlyList<PerfMetric> metrics)
    {
        var problems = new List<string>();
        lock (Lock)
        {
            var file = Load();
            var changed = false;
            var today = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            foreach (var m in metrics)
            {
                if (m.CeilingOnly) continue;   // 107-update5

                if (file.TryGetValue(m.Key, out var b) && !Updating)
                {
                    var allowedMs = Math.Max(b.Ms * 1.5, b.Ms + 1000);   // 107-update3: was + 300
                    if (m.Median > allowedMs)
                        problems.Add($"{m.Label}: {m.Median:0} ms now, {b.Ms:0} ms in the baseline ({b.Recorded}) — " +
                                     $"{(m.Median / Math.Max(b.Ms, 1) - 1) * 100:0}% slower.");

                    if (m.QueryCount is { } q && b.Queries is { } bq && q > bq + Math.Max(3, (int)Math.Ceiling(bq * 0.25)))
                        problems.Add($"{m.Label}: {q} database queries now, {bq} in the baseline — the page does more work than it did.");
                    continue;
                }

                // New (or -UpdatePerfBaseline): record it, but only a good number.
                if (m.WithinCeiling)
                {
                    file[m.Key] = new Entry(Math.Round(m.Median, 0), m.QueryCount, today);
                    changed = true;
                }
            }

            if (changed) Save(file);
        }
        return problems;
    }

    private static Dictionary<string, Entry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath), Json)
                   ?? new(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            TestContext.Progress.WriteLine($"    ({FilePath} could not be read — starting a new baseline)");
            return new(StringComparer.Ordinal);
        }
    }

    private static void Save(Dictionary<string, Entry> file)
    {
        var sorted = new SortedDictionary<string, Entry>(file, StringComparer.Ordinal);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(sorted, Json));
    }
}
