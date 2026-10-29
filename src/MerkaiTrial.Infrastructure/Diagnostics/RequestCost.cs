// =====================================================================
// FILE: MerkaiTrial.Infrastructure/Diagnostics/RequestCost.cs
//
// NEW (107 — performance tests). How much work one request did:
//
//   • database commands this host ran for it, and how long they took
//   • (Admin.Web) calls it made to the WebApi, how long they took, and the
//     database commands the WebApi ran for those calls
//
// WHY. "The leads page takes 900 ms" says something is slow; "the leads
// page runs 214 database queries" says WHAT is slow — and it does not
// depend on how fast the test computer is. The performance tests compare
// a list's query count at 10 rows and at 50 rows: if it grows with the
// rows, the page runs a query per row (the classic "N+1" problem that
// makes CRM pages slow down as customers add data).
//
// HOW IT WORKS
//   1. UseRequestCost (both hosts) starts a RequestCost for each request.
//      It lives in an AsyncLocal, so everything the request awaits —
//      including work started in parallel (the dashboard loads several
//      things at once) — adds to the same one.
//   2. RequestCostInterceptor, added to every FlowDbContext (OnConfiguring),
//      adds each finished database command to the current RequestCost.
//      Outside a counted request (background workers, start-up) there is
//      none, and it does nothing.
//   3. The WebApi puts its numbers on each answer in the X-E2E-Cost
//      header. Admin.Web's RequestCostHandler (on its API clients) reads
//      that header and adds the API's database work to the PAGE's count.
//   4. When the request carried an X-E2E-Cost-Id header, the finished
//      numbers are kept under that id and GET /__e2e/cost/{id} returns
//      them — AFTER the page has finished rendering, so work done while
//      the view was being written (the layout, the notification bell) is
//      included, which a response header could not include.
//
// ONLY ON THE TEST SERVERS. UseRequestCost and MapRequestCost do nothing
// unless the environment is Development AND Diagnostics:RequestCost is
// true — run-e2e.ps1 sets both, nothing else does. The interceptor is
// always attached, but with no RequestCost in flight it returns at once.
// The numbers say how much work was done; they never carry a query's
// text or any data.
// =====================================================================

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MerkaiTrial.Infrastructure.Diagnostics;

/// <summary>The work one request did. Thread-safe: parallel awaits add to it.</summary>
public sealed class RequestCost
{
    public const string Setting = "Diagnostics:RequestCost";
    public const string HeaderName = "X-E2E-Cost";
    public const string IdHeaderName = "X-E2E-Cost-Id";
    public const string EndpointPrefix = "/__e2e/cost";

    private static readonly AsyncLocal<RequestCost?> CurrentSlot = new();

    private int _dbQueries;
    private long _dbTicks;
    private int _apiCalls;
    private long _apiTicks;
    private int _apiDbQueries;
    private long _apiDbTicks;

    // 107-update: each WebApi call, so a slow page says WHICH call was slow.
    private readonly ConcurrentQueue<(string Call, double Ms, int Queries)> _calls = new();

    /// <summary>The request in flight on this async flow, or null.</summary>
    public static RequestCost? Current => CurrentSlot.Value;

    internal static RequestCost Begin()
    {
        var cost = new RequestCost();
        CurrentSlot.Value = cost;
        return cost;
    }

    internal static void End() => CurrentSlot.Value = null;

    public void AddDb(TimeSpan duration)
    {
        Interlocked.Increment(ref _dbQueries);
        Interlocked.Add(ref _dbTicks, duration.Ticks);
    }

    public void AddApiCall(TimeSpan duration, int apiDbQueries, double apiDbMs, string? call = null)
    {
        if (call is not null && _calls.Count < 100) _calls.Enqueue((call, duration.TotalMilliseconds, apiDbQueries));
        Interlocked.Increment(ref _apiCalls);
        Interlocked.Add(ref _apiTicks, duration.Ticks);
        Interlocked.Add(ref _apiDbQueries, apiDbQueries);
        Interlocked.Add(ref _apiDbTicks, TimeSpan.FromMilliseconds(apiDbMs).Ticks);
    }

    public Snapshot Read(double totalMs) => new(
        DbQueries: Volatile.Read(ref _dbQueries),
        DbMs: Math.Round(TimeSpan.FromTicks(Interlocked.Read(ref _dbTicks)).TotalMilliseconds, 1),
        ApiCalls: Volatile.Read(ref _apiCalls),
        ApiMs: Math.Round(TimeSpan.FromTicks(Interlocked.Read(ref _apiTicks)).TotalMilliseconds, 1),
        ApiDbQueries: Volatile.Read(ref _apiDbQueries),
        ApiDbMs: Math.Round(TimeSpan.FromTicks(Interlocked.Read(ref _apiDbTicks)).TotalMilliseconds, 1),
        TotalMs: Math.Round(totalMs, 1))
    {
        // 107-update: the five slowest WebApi calls, "GET api/leads/… 1234 ms, 5 queries".
        SlowestCalls = _calls.OrderByDescending(c => c.Ms).Take(5)
            .Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Call} {c.Ms:0} ms, {c.Queries} queries"))
            .ToList(),
    };

    /// <summary>What a finished request did. AllQueries = this host's plus the WebApi's for it.</summary>
    public sealed record Snapshot(int DbQueries, double DbMs, int ApiCalls, double ApiMs,
                                  int ApiDbQueries, double ApiDbMs, double TotalMs)
    {
        public int AllQueries => DbQueries + ApiDbQueries;

        /// <summary>107-update. Admin.Web only: its slowest WebApi calls (not in the header).</summary>
        public List<string> SlowestCalls { get; init; } = new();

        /// <summary>"db=12;dbms=40.5;api=3;apims=210.0;apidb=20;apidbms=95.2;ms=260.1" — invariant culture.</summary>
        public string ToHeader() => string.Create(CultureInfo.InvariantCulture,
            $"db={DbQueries};dbms={DbMs};api={ApiCalls};apims={ApiMs};apidb={ApiDbQueries};apidbms={ApiDbMs};ms={TotalMs}");

        public static Snapshot? Parse(string? header)
        {
            if (string.IsNullOrWhiteSpace(header)) return null;
            var parts = header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);

            int I(string k) => parts.TryGetValue(k, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            double D(string k) => parts.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;

            return new Snapshot(I("db"), D("dbms"), I("api"), D("apims"), I("apidb"), D("apidbms"), D("ms"));
        }
    }

    // ── Kept for GET /__e2e/cost/{id} ─────────────────────────────────

    private static readonly ConcurrentDictionary<string, Snapshot> Finished = new(StringComparer.Ordinal);

    internal static void Keep(string id, Snapshot snapshot)
    {
        // A test asks for each one once, right after its request. A run
        // makes a few thousand at most; this only stops a forgotten
        // server from growing without end.
        if (Finished.Count > 5000) Finished.Clear();
        Finished[id] = snapshot;
    }

    internal static Snapshot? Take(string id) => Finished.TryRemove(id, out var s) ? s : null;

    /// <summary>Development + Diagnostics:RequestCost = true. The test servers only.</summary>
    public static bool IsOn(IConfiguration config, IHostEnvironment env) =>
        env.IsDevelopment() && bool.TryParse(config[Setting], out var on) && on;
}

/// <summary>
/// Adds every finished database command to the current request's cost.
/// One shared instance; attached to every FlowDbContext.
/// </summary>
public sealed class RequestCostInterceptor : DbCommandInterceptor
{
    public static readonly RequestCostInterceptor Instance = new();

    private RequestCostInterceptor() { }

    private static void Count(CommandEventData data, TimeSpan duration)
    {
        if (RequestCost.Current is { } cost) cost.AddDb(duration);
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Count(eventData, eventData.Duration);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default)
    {
        Count(eventData, eventData.Duration);
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Count(eventData, eventData.Duration);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        int result, CancellationToken cancellationToken = default)
    {
        Count(eventData, eventData.Duration);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Count(eventData, eventData.Duration);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        object? result, CancellationToken cancellationToken = default)
    {
        Count(eventData, eventData.Duration);
        return ValueTask.FromResult(result);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) =>
        Count(eventData, eventData.Duration);

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Count(eventData, eventData.Duration);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Admin.Web's API clients: times each call to the WebApi and adds the
/// WebApi's own database work (its X-E2E-Cost header) to the page's cost.
/// Does nothing when no request is being counted.
/// </summary>
public sealed class RequestCostHandler : DelegatingHandler
{
    /// <summary>The path with ids shortened and no query values — "api/deals/{id}?page&amp;pageSize".</summary>
    private static string Shorten(Uri? uri)
    {
        if (uri is null) return "?";
        var path = System.Text.RegularExpressions.Regex.Replace(uri.AbsolutePath,
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "{id}");
        var names = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=')[0]).Where(n => n.Length > 0).Distinct();
        var q = string.Join("&", names);
        return q.Length > 0 ? $"{path}?{q}" : path;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var cost = RequestCost.Current;
        if (cost is null) return await base.SendAsync(request, cancellationToken);

        var watch = Stopwatch.StartNew();
        var response = await base.SendAsync(request, cancellationToken);
        watch.Stop();

        var api = response.Headers.TryGetValues(RequestCost.HeaderName, out var values)
            ? RequestCost.Snapshot.Parse(values.FirstOrDefault())
            : null;
        cost.AddApiCall(watch.Elapsed, api?.AllQueries ?? 0, (api?.DbMs ?? 0) + (api?.ApiDbMs ?? 0),
            $"{request.Method} {Shorten(request.RequestUri)}");
        return response;
    }
}

public static class RequestCostSetup
{
    /// <summary>
    /// Counts each request's work. Put it early (after static files), so
    /// sign-in checks are counted too. Does nothing unless IsOn.
    /// </summary>
    public static IApplicationBuilder UseRequestCost(this IApplicationBuilder app, IConfiguration config, IHostEnvironment env)
    {
        if (!RequestCost.IsOn(config, env)) return app;

        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(RequestCost.EndpointPrefix))
            {
                await next();
                return;
            }

            var cost = RequestCost.Begin();
            var watch = Stopwatch.StartNew();

            context.Response.OnStarting(() =>
            {
                // What was done before the answer started: everything, for
                // the WebApi's JSON answers — which is what Admin.Web reads.
                context.Response.Headers[RequestCost.HeaderName] = cost.Read(watch.Elapsed.TotalMilliseconds).ToHeader();
                return Task.CompletedTask;
            });

            try
            {
                await next();
            }
            finally
            {
                watch.Stop();
                var id = context.Request.Headers[RequestCost.IdHeaderName].ToString();
                if (id.Length is > 0 and <= 64) RequestCost.Keep(id, cost.Read(watch.Elapsed.TotalMilliseconds));
                RequestCost.End();
            }
        });
    }

    /// <summary>GET /__e2e/cost/{id}: the finished numbers of the request that carried that id. Test servers only.</summary>
    public static IEndpointRouteBuilder MapRequestCost(this IEndpointRouteBuilder endpoints, IConfiguration config, IHostEnvironment env)
    {
        if (!RequestCost.IsOn(config, env)) return endpoints;

        endpoints.MapGet(RequestCost.EndpointPrefix + "/{id}", (string id) =>
                RequestCost.Take(id) is { } s ? Results.Ok(s) : Results.NotFound())
            .AllowAnonymous()
            .ExcludeFromDescription();
        return endpoints;
    }
}
