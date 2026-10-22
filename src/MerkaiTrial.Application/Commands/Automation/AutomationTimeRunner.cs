// =====================================================================
// AutomationTimeRunner.cs
// Location: MerkaiTrial.Application/Commands/Automation/AutomationTimeRunner.cs
//
// NEW FILE (099 — time-based automation rules).
//
//   AutomationTimeRunner.RunTenantAsync(tenantId)
//       Every switched-on time-based rule in one workspace: find the
//       records it is due on, leave out the episodes it has already acted
//       on, and hand the rest to AutomationEngine.RunTimeRuleAsync.
//
//   Called by
//     • AutomationWorker (WebApi) every few minutes, once per workspace that
//       has a time-based rule, in a scope set to that workspace;
//     • "Check time-based rules now" on Settings → Automation, for the
//       signed-in workspace — the same code, so what the button shows is
//       exactly what the worker would have done.
//
// HOW "DUE" IS WORKED OUT — the EPISODE (AutomationTimeFire)
//
//   noActivity   anchor = the latest of: the record being added, an
//                activity or task completed on it, a note on it.
//                Due when the anchor is N days old.
//   stuck        anchor = when it entered its current status (the latest
//                LeadStatusChanged audit row) or stage (the latest
//                DealStageHistory row); the record being added if neither.
//                Due when the anchor is N days old.
//   closeSoon    anchor = the deal's expected close date. Due from N days
//                before it (in the WORKSPACE's calendar) until it passes.
//   closePassed  anchor = the deal's expected close date. Due from N days
//                after it (1 = the day after).
//
//   A record is due once per anchor: a new call, a stage move or a moved
//   close date gives it a new anchor, so the rule can fire again later.
//
//   Leads that became deals and deals that are won or lost are never due —
//   nobody wants "this deal has gone cold" about a deal that closed.
//
// WRITTEN AS SIMPLE QUERIES ON PURPOSE
//   One query for the candidates (a few columns), then per batch of ids a
//   GROUP BY for the latest activity / note / move, and the episodes
//   already acted on — all worked out in memory. Each query is the plain
//   kind EF has translated the same way for years; one clever correlated
//   query would be shorter and much harder to trust.
// =====================================================================

using MerkaiTrial.Application.Commands.Activities;
using MerkaiTrial.Application.Commands.PipelineStages;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Automation;

/// <summary>What one check of a workspace did, for the log line and the "Check now" message.</summary>
public sealed record AutomationTimeCheckResult(int RulesChecked, int RecordsDue, int RulesRan);

public class AutomationTimeRunner : ICommandHandler
{
    /// <summary>Records one rule acts on in one check. The rest wait for the next one.</summary>
    public const int MaxPerRulePerCheck = 200;

    /// <summary>Candidates one rule looks at in one check (a few columns each).</summary>
    public const int MaxCandidates = 20000;

    private const int IdBatch = 1000;

    private readonly FlowDbContext _db;
    private readonly IStageResolver _stages;
    private readonly AutomationEngine _engine;
    private readonly ILogger<AutomationTimeRunner> _logger;

    public AutomationTimeRunner(
        FlowDbContext db, IStageResolver stages, AutomationEngine engine, ILogger<AutomationTimeRunner> logger)
    {
        _db = db;
        _stages = stages;
        _engine = engine;
        _logger = logger;
    }

    /// <summary>Workspaces with at least one switched-on time-based rule — for the worker.</summary>
    public static Task<List<Guid>> TenantsWithTimeRulesAsync(FlowDbContext db, CancellationToken ct)
        => db.AutomationRules.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.Trigger == AutomationTrigger.TimeBased && r.IsActive && !r.IsDeleted)
            .Select(r => r.TenantId)
            .Distinct()
            .ToListAsync(ct);

    public async Task<AutomationTimeCheckResult> RunTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        var rules = await _db.AutomationRules.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Trigger == AutomationTrigger.TimeBased && r.IsActive && !r.IsDeleted)
            .OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Id)
            .ToListAsync(ct);

        if (rules.Count == 0) return new AutomationTimeCheckResult(0, 0, 0);

        var zone = await ZoneAsync(tenantId, ct);
        var stages = await _stages.GetAsync(tenantId, ct);
        var closedStages = stages.All.Where(s => stages.IsTerminal(s.Key)).Select(s => s.Key).ToList();

        int due = 0, ran = 0;
        foreach (var rule in rules)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var list = await FindDueAsync(rule, zone, closedStages, DateTime.UtcNow, ct);
                if (list.Count == 0) continue;

                due += list.Count;
                ran += await _engine.RunTimeRuleAsync(tenantId, rule.Id, list, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One rule's problem never stops the others.
                _logger.LogError(ex, "Time-based rule {RuleId} could not be checked in tenant {TenantId}", rule.Id, tenantId);
            }
        }

        return new AutomationTimeCheckResult(rules.Count, due, ran);
    }

    // =================================================================
    // WHICH RECORDS ARE DUE
    // =================================================================

    public async Task<List<AutomationDue>> FindDueAsync(
        AutomationRule rule, TimeZoneInfo zone, IReadOnlyCollection<string> closedStages,
        DateTime nowUtc, CancellationToken ct)
    {
        var t = rule.TenantId;
        var days = Math.Max(rule.TimeDays ?? 0, 0);
        var isDeal = rule.EntityType == AutomationEntity.Deal;

        // ── Candidates: id, when added, and what the kind needs ─────────
        List<(Guid Id, DateTime Created, DateTime? Close)> candidates;

        if (isDeal)
        {
            var q = _db.Deals.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.TenantId == t && !d.IsDeleted && !closedStages.Contains(d.Stage));

            var cutoff = nowUtc.AddDays(-days);
            if (rule.TimeKind is AutomationTimeKinds.NoActivity or AutomationTimeKinds.Stuck)
                q = q.Where(d => d.CreatedAtUtc <= cutoff);   // can't be N days of anything if newer

            candidates = (await q.OrderBy(d => d.CreatedAtUtc).Take(MaxCandidates)
                    .Select(d => new { d.Id, d.CreatedAtUtc, d.ExpectedCloseDateUtc })
                    .ToListAsync(ct))
                .Select(d => (d.Id, d.CreatedAtUtc, (DateTime?)d.ExpectedCloseDateUtc))
                .ToList();
        }
        else
        {
            if (rule.TimeKind is not (AutomationTimeKinds.NoActivity or AutomationTimeKinds.Stuck))
                return new List<AutomationDue>();         // close dates are deals-only

            var cutoff = nowUtc.AddDays(-days);
            candidates = (await _db.Leads.IgnoreQueryFilters().AsNoTracking()
                    .Where(l => l.TenantId == t && !l.IsDeleted && !l.IsConverted && l.CreatedAtUtc <= cutoff)
                    .OrderBy(l => l.CreatedAtUtc).Take(MaxCandidates)
                    .Select(l => new { l.Id, l.CreatedAtUtc })
                    .ToListAsync(ct))
                .Select(l => (l.Id, l.CreatedAtUtc, (DateTime?)null))
                .ToList();
        }

        if (candidates.Count == 0) return new List<AutomationDue>();

        var result = new List<AutomationDue>();

        foreach (var batch in candidates.Chunk(IdBatch))
        {
            var ids = batch.Select(c => c.Id).ToList();

            Dictionary<Guid, DateTime> anchors = rule.TimeKind switch
            {
                AutomationTimeKinds.NoActivity => await LastActivityAsync(t, isDeal, ids, ct),
                AutomationTimeKinds.Stuck      => await LastMoveAsync(t, isDeal, ids, ct),
                _                              => new Dictionary<Guid, DateTime>()
            };

            var fired = (await _db.AutomationTimeFires.IgnoreQueryFilters().AsNoTracking()
                    .Where(f => f.TenantId == t && f.RuleId == rule.Id && ids.Contains(f.EntityId))
                    .Select(f => new { f.EntityId, f.EpisodeAtUtc })
                    .ToListAsync(ct))
                .Select(f => (f.EntityId, f.EpisodeAtUtc))
                .ToHashSet();

            foreach (var c in batch)
            {
                DateTime? episode = rule.TimeKind switch
                {
                    AutomationTimeKinds.NoActivity or AutomationTimeKinds.Stuck =>
                        DueSince(Later(c.Created, anchors.TryGetValue(c.Id, out var a) ? a : (DateTime?)null), days, nowUtc),

                    AutomationTimeKinds.CloseSoon when c.Close is { } close =>
                        IsCloseSoon(close, days, nowUtc, zone) ? close : null,

                    AutomationTimeKinds.ClosePassed when c.Close is { } close =>
                        IsClosePassed(close, days, nowUtc, zone) ? close : null,

                    _ => null
                };

                if (episode is { } e && !fired.Contains((c.Id, e)))
                {
                    result.Add(new AutomationDue(c.Id, e));
                    if (result.Count >= MaxPerRulePerCheck) return result;
                }
            }
        }

        return result;
    }

    private static DateTime Later(DateTime a, DateTime? b) => b is { } x && x > a ? x : a;

    /// <summary>The anchor, when it is at least N days old; otherwise not due.</summary>
    private static DateTime? DueSince(DateTime anchor, int days, DateTime nowUtc)
        => anchor <= nowUtc.AddDays(-days) ? anchor : null;

    /// <summary>Today, in the workspace, is within N days before the close date — and not past it.</summary>
    public static bool IsCloseSoon(DateTime closeUtc, int days, DateTime nowUtc, TimeZoneInfo zone)
    {
        var today = Today(nowUtc, zone);
        var closeDay = CloseDay(closeUtc, zone);
        return today <= closeDay && today >= closeDay.AddDays(-days);
    }

    /// <summary>Today, in the workspace, is N or more days after the close date (1 = the day after).</summary>
    public static bool IsClosePassed(DateTime closeUtc, int days, DateTime nowUtc, TimeZoneInfo zone)
        => Today(nowUtc, zone) >= CloseDay(closeUtc, zone).AddDays(Math.Max(days, 1));

    private static DateOnly Today(DateTime nowUtc, TimeZoneInfo zone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone));

    /// <summary>
    /// A close date is a DAY. It is stored as a UTC instant; read in the
    /// workspace's time so 31 March in Bangkok is 31 March, not 30.
    /// </summary>
    private static DateOnly CloseDay(DateTime closeUtc, TimeZoneInfo zone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(closeUtc, DateTimeKind.Utc), zone));

    /// <summary>Latest completed activity / task, or note, per record.</summary>
    private async Task<Dictionary<Guid, DateTime>> LastActivityAsync(
        Guid t, bool isDeal, List<Guid> ids, CancellationToken ct)
    {
        var type = isDeal ? ActivityEntityType.Deal : ActivityEntityType.Lead;

        var acts = await _db.Activities.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.TenantId == t && a.EntityType == type && ids.Contains(a.EntityId)
                     && !a.IsDeleted && a.IsCompleted)
            .GroupBy(a => a.EntityId)
            .Select(g => new { Id = g.Key, Last = g.Max(a => a.CompletedAtUtc ?? a.ActivityDate) })
            .ToListAsync(ct);

        var result = acts.ToDictionary(x => x.Id, x => x.Last);

        var notes = isDeal
            ? await _db.DealNotes.IgnoreQueryFilters().AsNoTracking()
                .Where(n => n.TenantId == t && ids.Contains(n.DealId) && !n.IsDeleted)
                .GroupBy(n => n.DealId)
                .Select(g => new { Id = g.Key, Last = g.Max(n => n.CreatedAtUtc) })
                .ToListAsync(ct)
            : await _db.LeadNotes.IgnoreQueryFilters().AsNoTracking()
                .Where(n => n.TenantId == t && ids.Contains(n.LeadId) && !n.IsDeleted)
                .GroupBy(n => n.LeadId)
                .Select(g => new { Id = g.Key, Last = g.Max(n => n.CreatedAtUtc) })
                .ToListAsync(ct);

        foreach (var n in notes)
            if (!result.TryGetValue(n.Id, out var cur) || n.Last > cur) result[n.Id] = n.Last;

        return result;
    }

    /// <summary>When each record last changed status (lead) or stage (deal).</summary>
    private async Task<Dictionary<Guid, DateTime>> LastMoveAsync(
        Guid t, bool isDeal, List<Guid> ids, CancellationToken ct)
    {
        if (isDeal)
        {
            return (await _db.DealStageHistory.IgnoreQueryFilters().AsNoTracking()
                    .Where(h => h.TenantId == t && ids.Contains(h.DealId))
                    .GroupBy(h => h.DealId)
                    .Select(g => new { Id = g.Key, Last = g.Max(h => h.ChangedAtUtc) })
                    .ToListAsync(ct))
                .ToDictionary(x => x.Id, x => x.Last);
        }

        // AuditLogs has no tenant filter — the TenantId here is what scopes it.
        // Its EntityId is nullable, so it is compared as Guid?.
        var idsN = ids.Select(i => (Guid?)i).ToList();
        return (await _db.Set<AuditLog>().AsNoTracking()
                .Where(l => l.TenantId == t && l.EntityType == AuditEntityType.Lead
                         && l.Action == AuditAction.LeadStatusChanged && idsN.Contains((Guid?)l.EntityId))
                .GroupBy(l => (Guid?)l.EntityId)
                .Select(g => new { Id = g.Key, Last = g.Max(l => l.CreatedAtUtc) })
                .ToListAsync(ct))
            .Where(x => x.Id != null)
            .ToDictionary(x => x.Id!.Value, x => x.Last);
    }

    private async Task<TimeZoneInfo> ZoneAsync(Guid tenantId, CancellationToken ct)
    {
        var tz = await _db.Tenants.AsNoTracking()
            .Where(x => x.Id == tenantId).Select(x => x.Timezone).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(tz)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(tz); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }
}
