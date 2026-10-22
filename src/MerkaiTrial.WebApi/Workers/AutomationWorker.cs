// =====================================================================
// AutomationWorker.cs
// Location: MerkaiTrial.WebApi/Workers/AutomationWorker.cs
//
// NEW FILE (099). Two jobs, the OutboundMessageWorker pattern for both:
//
//   1. Every few minutes (AutomationWorker:CheckMinutes, default 15):
//      every workspace with a switched-on TIME-BASED rule gets its rules
//      checked — AutomationTimeRunner, the same code "Check time-based
//      rules now" on the Automation page runs.
//
//   2. Once a day: the rule log keeps the last KeepRunLogDays (90) days;
//      "already acted on" rows go when their rule is deleted, or after
//      KeepFiresDays (400) — longer than any sensible N days, so an
//      episode is never forgotten while it could still matter.
//
// THE SAME THREE RULES AS OutboundMessageWorker
//   • A hosted service is a singleton: every pass makes its own scope.
//   • No HttpContext, so no tenant: workspace-wide queries here use
//     IgnoreQueryFilters with an explicit TenantId.
//   • ONE SCOPE PER WORKSPACE, and that scope is TOLD its workspace
//     (OverridableTenantProvider) before anything else is resolved. The engine
//     calls the lead-status and stage resolvers and the custom-field
//     reader, which go through the normal tenant filter; without this
//     they would see no statuses, no stages and no values, and every rule
//     that sets a status would fail with "that status no longer exists".
//
// SCALING OUT
//   Two instances may check the same workspace at the same moment. The
//   unique (rule, record, episode) index on AutomationTimeFires means only
//   one of them can save its run; the other's save fails and is logged.
//   Rare, harmless, and the reason that index is unique.
// =====================================================================

using MerkaiTrial.Application.Commands.Automation;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MerkaiTrial.WebApi.Workers;

public sealed class AutomationWorkerOptions
{
    public const string SectionName = "AutomationWorker";

    /// <summary>How often time-based rules are checked. Minimum 1.</summary>
    public int CheckMinutes { get; set; } = 15;

    /// <summary>Rule log lines older than this are removed. 0 keeps them for ever.</summary>
    public int KeepRunLogDays { get; set; } = 90;

    /// <summary>"Already acted on" rows older than this are removed. 0 keeps them for ever.</summary>
    public int KeepFiresDays { get; set; } = 400;
}

public sealed class AutomationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AutomationWorker> _logger;
    private readonly AutomationWorkerOptions _options;
    private DateTime _lastCleanupUtc = DateTime.MinValue;

    public AutomationWorker(
        IServiceScopeFactory scopes, ILogger<AutomationWorker> logger, IOptions<AutomationWorkerOptions> options)
    {
        _scopes = scopes;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var every = TimeSpan.FromMinutes(Math.Max(_options.CheckMinutes, 1));
        _logger.LogInformation("Automation worker started (time-based rules every {Minutes} min)", every.TotalMinutes);

        // Let the app finish starting before touching the database.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAllAsync(stoppingToken);
                await MaybeCleanUpAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad pass never stops the worker.
                _logger.LogError(ex, "Automation worker pass failed");
            }

            try { await Task.Delay(every, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("Automation worker stopped");
    }

    private async Task CheckAllAsync(CancellationToken ct)
    {
        List<Guid> tenants;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowDbContext>();
            tenants = await AutomationTimeRunner.TenantsWithTimeRulesAsync(db, ct);
        }

        foreach (var tenantId in tenants)
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                using var scope = _scopes.CreateScope();

                // FIRST, before FlowDbContext or anything that uses it.
                scope.ServiceProvider.GetRequiredService<OverridableTenantProvider>().UseTenant(tenantId);

                var runner = scope.ServiceProvider.GetRequiredService<AutomationTimeRunner>();
                var result = await runner.RunTenantAsync(tenantId, ct);

                if (result.RecordsDue > 0)
                    _logger.LogInformation(
                        "Time-based rules in tenant {TenantId}: {Rules} checked, {Due} record(s) due, {Ran} ran",
                        tenantId, result.RulesChecked, result.RecordsDue, result.RulesRan);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One workspace's problem never stops the next one's rules.
                _logger.LogError(ex, "Time-based rules could not be checked for tenant {TenantId}", tenantId);
            }
        }
    }

    // =================================================================
    // RETENTION — once a day
    // =================================================================

    private async Task MaybeCleanUpAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastCleanupUtc < TimeSpan.FromHours(24)) return;
        _lastCleanupUtc = DateTime.UtcNow;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowDbContext>();

        if (_options.KeepRunLogDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-_options.KeepRunLogDays);
            var removed = await DeleteInBatchesAsync(
                () => db.AutomationRuns.IgnoreQueryFilters()
                        .Where(r => r.CreatedAtUtc < cutoff)
                        .OrderBy(r => r.CreatedAtUtc),
                ct);
            if (removed > 0)
                _logger.LogInformation("Retention: removed {Count} rule log line(s) older than {Days} days", removed, _options.KeepRunLogDays);
        }

        // Rows for deleted rules can never matter again.
        var orphans = await DeleteInBatchesAsync(
            () => db.AutomationTimeFires.IgnoreQueryFilters()
                    .Where(f => db.AutomationRules.IgnoreQueryFilters().Any(r => r.Id == f.RuleId && r.IsDeleted))
                    .OrderBy(f => f.FiredAtUtc),
            ct);

        var old = 0;
        if (_options.KeepFiresDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-_options.KeepFiresDays);
            old = await DeleteInBatchesAsync(
                () => db.AutomationTimeFires.IgnoreQueryFilters()
                        .Where(f => f.FiredAtUtc < cutoff)
                        .OrderBy(f => f.FiredAtUtc),
                ct);
        }

        if (orphans + old > 0)
            _logger.LogInformation("Retention: removed {Count} old time-rule record(s)", orphans + old);
    }

    /// <summary>Batched, ordered first — see OutboundMessageWorker.DeleteInBatchesAsync for why.</summary>
    private static async Task<int> DeleteInBatchesAsync<T>(Func<IQueryable<T>> query, CancellationToken ct) where T : class
    {
        const int batch = 2000;
        const int maxBatches = 25;
        var total = 0;

        for (var i = 0; i < maxBatches; i++)
        {
            var removed = await query().Take(batch).ExecuteDeleteAsync(ct);
            total += removed;
            if (removed < batch) break;
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        }

        return total;
    }
}
