// =====================================================================
// FiscalYearService.cs
// Location: MerkaiTrial.Application/Services/Fiscal/FiscalYearService.cs
//
// NEW FILE — 035. Contains IFiscalYearService and its implementation.
//
// WHAT IT DOES
//   Turns "this tenant, this instant" into "this financial year" — the one
//   piece that needs the database, because the start month and the timezone
//   both live there.
//
//   Resolution order, per tenant:
//       start month : TenantSettings.FiscalYearStartMonth
//                     ?? Countries.FiscalYearStartMonth
//                     ?? 1 (calendar year)
//       timezone    : Tenants.Timezone
//                     ?? Countries.Timezone
//                     ?? UTC
//
// ASYNC ALL THE WAY — ON PURPOSE
//   CurrentTenantService resolves its tenant with
//   LoadAsync().GetAwaiter().GetResult(), which blocks a thread-pool thread
//   on every FormatCurrency and FormatDate call. This service is called
//   once per row in a report, so the same shortcut here would be far worse.
//   Everything is async, and the per-tenant answer is cached in the
//   instance so a thousand calls in one request cost one query.
//
// THE BUG THIS EXISTS TO PREVENT
//   An Indian tenant closes a deal at 00:30 IST on 1 April 2026. It is
//   stored as 2026-03-31 19:00 UTC. A query written as
//
//       WHERE ActualCloseDateUtc >= '2026-04-01'
//
//   files that deal in the PREVIOUS financial year, and the April sales
//   number is wrong by one deal — the kind of error an accountant finds
//   and you do not. TimestampRangeAsync returns 2026-03-31 18:30 UTC as
//   the start of FY 2026-27 for that tenant, which is correct.
//
// TWO RANGE METHODS, AND THE DIFFERENCE MATTERS
//   TimestampRangeAsync  — for real instants: CreatedAtUtc, UpdatedAtUtc,
//                          ActualCloseDateUtc, invoice issue timestamps.
//                          Converts the tenant's local day boundaries to
//                          UTC instants.
//
//   DateOnlyRangeAsync   — for date-ONLY columns: ExpectedCloseDateUtc,
//                          IssueDateUtc, ExpiresAtUtc. After round 034
//                          these hold the picked calendar date at midnight
//                          with no offset, so they must be compared against
//                          plain midnight, NOT against a timezone-shifted
//                          instant. Using the wrong one here puts every
//                          boundary day in the wrong year.
//
//   Both are HALF-OPEN: use  >= FromUtc && < ToUtcExclusive.  Never
//   BETWEEN, and never <= an end date — that is how the last day of a
//   period gets silently dropped or double-counted.
// =====================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Services.Fiscal;

// ── What a tenant's fiscal setup amounts to ───────────────────────────

/// <summary>
/// A tenant's resolved fiscal settings. Everything needed to answer any
/// fiscal question without going back to the database.
/// </summary>
public sealed record TenantFiscalContext(
    Guid TenantId,
    int StartMonth,
    TimeZoneInfo TimeZone,

    /// <summary>
    /// True when the tenant row or country row could not be read and the
    /// defaults were used. The service logs a warning; this flag lets a
    /// settings page show it too, because silently reporting an Indian
    /// workspace on a January year is worse than saying so.
    /// </summary>
    bool UsedFallback)
{
    public bool IsCalendarYear => StartMonth == FiscalCalendar.CalendarYearStartMonth;

    /// <summary>The calendar day this instant falls on, in the tenant's own country.</summary>
    public DateOnly LocalDateOf(DateTime utc)
    {
        var asUtc = utc.Kind == DateTimeKind.Utc
            ? utc
            : DateTime.SpecifyKind(utc, DateTimeKind.Utc);

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(asUtc, TimeZone));
    }

    /// <summary>
    /// Midnight at the start of a local calendar day, as a UTC instant.
    ///
    /// Spring-forward gaps are stepped over rather than thrown on. None of
    /// India, Thailand, the Philippines or the UAE observes daylight saving,
    /// so this will not fire in your markets today — but it would be a
    /// once-a-year crash in a market that does, and the guard is four lines.
    /// </summary>
    public DateTime StartOfLocalDayUtc(DateOnly localDate)
    {
        var local = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        for (var i = 0; i < 4 && TimeZone.IsInvalidTime(local); i++)
            local = local.AddHours(1);

        return TimeZoneInfo.ConvertTimeToUtc(local, TimeZone);
    }

    public DateTime NowLocalDayStartUtc() => StartOfLocalDayUtc(LocalDateOf(DateTime.UtcNow));
}

/// <summary>
/// A half-open UTC range. Query it as  &gt;= FromUtc &amp;&amp; &lt; ToUtcExclusive.
/// </summary>
public readonly record struct UtcRange(DateTime FromUtc, DateTime ToUtcExclusive)
{
    public bool Contains(DateTime utc) => utc >= FromUtc && utc < ToUtcExclusive;
}

/// <summary>One entry in a period picker.</summary>
public sealed record FiscalPeriodOption(
    string Key,             // "fy:2026", "fy:2026:q1", "all"
    string Label,           // "FY 2026-27", "Q1 (Apr-Jun 2026)"
    string Group,           // "Financial years" / "Quarters"
    bool IsCurrent,
    UtcRange Timestamps,    // for CreatedAtUtc etc.
    UtcRange Dates);        // for ExpectedCloseDateUtc etc.

// ── The service ───────────────────────────────────────────────────────

public interface IFiscalYearService
{
    Task<TenantFiscalContext> GetContextAsync(Guid tenantId, CancellationToken ct = default);

    Task<FiscalYearInfo> CurrentAsync(Guid tenantId, CancellationToken ct = default);
    Task<FiscalQuarterInfo> CurrentQuarterAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Which financial year a stored UTC instant belongs to.</summary>
    Task<FiscalYearInfo> ForUtcAsync(Guid tenantId, DateTime utc, CancellationToken ct = default);

    /// <summary>
    /// Which financial year a date-ONLY value belongs to — a value from
    /// ExpectedCloseDateUtc or IssueDateUtc, which after round 034 carries
    /// the picked calendar date at midnight and no offset.
    /// </summary>
    Task<FiscalYearInfo> ForDateOnlyAsync(Guid tenantId, DateTime dateAtMidnight, CancellationToken ct = default);

    /// <summary>UTC bounds for a TIMESTAMP column. Half-open.</summary>
    Task<UtcRange> TimestampRangeAsync(Guid tenantId, FiscalYearInfo year, CancellationToken ct = default);
    Task<UtcRange> TimestampRangeAsync(Guid tenantId, FiscalQuarterInfo quarter, CancellationToken ct = default);

    /// <summary>
    /// Bounds for a DATE-ONLY column. Half-open, and NOT timezone shifted.
    /// </summary>
    UtcRange DateOnlyRange(FiscalYearInfo year);
    UtcRange DateOnlyRange(FiscalQuarterInfo quarter);

    /// <summary>
    /// The contents of a period dropdown: the current financial year, its
    /// four quarters, and the previous <paramref name="pastYears"/> years.
    /// </summary>
    Task<IReadOnlyList<FiscalPeriodOption>> PeriodOptionsAsync(
        Guid tenantId, int pastYears = 3, CancellationToken ct = default);

    /// <summary>
    /// The financial year code for an invoice or quote number — "2026-27"
    /// for India, "2026" for a calendar-year tenant. Stored on the document,
    /// unlike everything else here, because Indian GST requires a serial
    /// that is unique within the financial year.
    /// </summary>
    Task<string> YearCodeForNumberingAsync(
        Guid tenantId, DateTime? issuedAtUtc = null, CancellationToken ct = default);
}

public sealed class FiscalYearService : IFiscalYearService
{
    private readonly FlowDbContext _db;
    private readonly ILogger<FiscalYearService> _logger;

    // Scoped service, so this cache lives for one request. A report that
    // asks the same question per row pays for one query, not N.
    private readonly Dictionary<Guid, TenantFiscalContext> _cache = new();

    public FiscalYearService(FlowDbContext db, ILogger<FiscalYearService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // =================================================================
    // RESOLUTION
    // =================================================================

    public async Task<TenantFiscalContext> GetContextAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(tenantId, out var cached))
            return cached;

        // One query, both rows. Explicit TenantId predicates rather than
        // IgnoreQueryFilters: the same shape StageResolver uses, so the
        // global tenant filter stays in force.
        var tenant = await _db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new
            {
                t.Timezone,
                CountryFiscalStart = t.Country != null ? (int?)t.Country.FiscalYearStartMonth : null,
                CountryTimezone = t.Country != null ? t.Country.Timezone : null
            })
            .FirstOrDefaultAsync(ct);

        var settingsStart = await _db.Set<TenantSettings>().AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .Select(s => s.FiscalYearStartMonth)
            .FirstOrDefaultAsync(ct);

        var usedFallback = false;

        // ── start month ───────────────────────────────────────────────
        int startMonth;
        if (settingsStart is >= 1 and <= 12)
        {
            startMonth = settingsStart.Value;
        }
        else if (tenant?.CountryFiscalStart is >= 1 and <= 12)
        {
            startMonth = tenant.CountryFiscalStart.Value;
        }
        else
        {
            startMonth = FiscalCalendar.CalendarYearStartMonth;

            // Loud, because a wrong fiscal year is invisible until someone
            // reconciles against their accounts. An Indian workspace
            // silently reporting on January-December is the failure here.
            usedFallback = true;
            _logger.LogWarning(
                "Tenant {TenantId} has no fiscal year start month on its settings or its " +
                "country — defaulting to January. Set Countries.FiscalYearStartMonth " +
                "(India = 4) or TenantSettings.FiscalYearStartMonth.", tenantId);
        }

        // ── timezone ──────────────────────────────────────────────────
        var zoneId = FirstNonBlank(tenant?.Timezone, tenant?.CountryTimezone);
        var zone = ResolveZone(zoneId, tenantId, ref usedFallback);

        var context = new TenantFiscalContext(tenantId, startMonth, zone, usedFallback);
        _cache[tenantId] = context;
        return context;
    }

    private static string? FirstNonBlank(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();

    /// <summary>
    /// IANA identifiers work on Windows as well as Linux from .NET 6
    /// onward, so "Asia/Kolkata" is fine on both. try/catch rather than
    /// TryFindSystemTimeZoneById so this compiles on any target, and an
    /// unknown zone degrades to UTC with a warning instead of a 500.
    /// </summary>
    private TimeZoneInfo ResolveZone(string? zoneId, Guid tenantId, ref bool usedFallback)
    {
        if (!string.IsNullOrWhiteSpace(zoneId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(zoneId);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException
                                       or InvalidTimeZoneException)
            {
                _logger.LogWarning(ex,
                    "Tenant {TenantId} has an unrecognised timezone '{Zone}' — falling back " +
                    "to UTC. Fiscal period boundaries will be off by that zone's offset.",
                    tenantId, zoneId);
            }
        }
        else
        {
            _logger.LogWarning(
                "Tenant {TenantId} has no timezone on its row or its country — falling back " +
                "to UTC. For an Indian workspace this shifts every financial year boundary " +
                "by 5 hours 30 minutes.", tenantId);
        }

        usedFallback = true;
        return TimeZoneInfo.Utc;
    }

    // =================================================================
    // QUESTIONS
    // =================================================================

    public async Task<FiscalYearInfo> CurrentAsync(Guid tenantId, CancellationToken ct = default)
    {
        var ctx = await GetContextAsync(tenantId, ct);
        return FiscalCalendar.For(ctx.LocalDateOf(DateTime.UtcNow), ctx.StartMonth);
    }

    public async Task<FiscalQuarterInfo> CurrentQuarterAsync(Guid tenantId, CancellationToken ct = default)
    {
        var ctx = await GetContextAsync(tenantId, ct);
        return FiscalCalendar.QuarterFor(ctx.LocalDateOf(DateTime.UtcNow), ctx.StartMonth);
    }

    public async Task<FiscalYearInfo> ForUtcAsync(
        Guid tenantId, DateTime utc, CancellationToken ct = default)
    {
        var ctx = await GetContextAsync(tenantId, ct);
        return FiscalCalendar.For(ctx.LocalDateOf(utc), ctx.StartMonth);
    }

    public async Task<FiscalYearInfo> ForDateOnlyAsync(
        Guid tenantId, DateTime dateAtMidnight, CancellationToken ct = default)
    {
        var ctx = await GetContextAsync(tenantId, ct);

        // Deliberately NOT converted through the timezone. This value is
        // already a calendar date — converting it would move it a day.
        return FiscalCalendar.For(DateOnly.FromDateTime(dateAtMidnight), ctx.StartMonth);
    }

    // =================================================================
    // RANGES
    // =================================================================

    public async Task<UtcRange> TimestampRangeAsync(
        Guid tenantId, FiscalYearInfo year, CancellationToken ct = default)
    {
        var ctx = await GetContextAsync(tenantId, ct);
        return new UtcRange(
            ctx.StartOfLocalDayUtc(year.Start),
            ctx.StartOfLocalDayUtc(year.EndExclusive));
    }

    public async Task<UtcRange> TimestampRangeAsync(
        Guid tenantId, FiscalQuarterInfo quarter, CancellationToken ct = default)
    {
        var ctx = await GetContextAsync(tenantId, ct);
        return new UtcRange(
            ctx.StartOfLocalDayUtc(quarter.Start),
            ctx.StartOfLocalDayUtc(quarter.EndExclusive));
    }

    public UtcRange DateOnlyRange(FiscalYearInfo year)
        => new(year.Start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
               year.EndExclusive.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

    public UtcRange DateOnlyRange(FiscalQuarterInfo quarter)
        => new(quarter.Start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
               quarter.EndExclusive.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

    // =================================================================
    // PERIOD PICKER
    // =================================================================

    public async Task<IReadOnlyList<FiscalPeriodOption>> PeriodOptionsAsync(
        Guid tenantId, int pastYears = 3, CancellationToken ct = default)
    {
        var ctx = await GetContextAsync(tenantId, ct);
        var current = FiscalCalendar.For(ctx.LocalDateOf(DateTime.UtcNow), ctx.StartMonth);
        var currentQuarter = FiscalCalendar.QuarterFor(ctx.LocalDateOf(DateTime.UtcNow), ctx.StartMonth);

        var options = new List<FiscalPeriodOption>();

        // Current year first — it is what the lists default to.
        options.Add(YearOption(ctx, current, isCurrent: true));

        foreach (var q in FiscalCalendar.QuartersOf(current))
        {
            options.Add(new FiscalPeriodOption(
                Key: $"fy:{current.StartYear}:q{q.Number}",
                Label: q.Label,
                Group: "Quarters",
                IsCurrent: q.Number == currentQuarter.Number,
                Timestamps: new UtcRange(
                    ctx.StartOfLocalDayUtc(q.Start),
                    ctx.StartOfLocalDayUtc(q.EndExclusive)),
                Dates: DateOnlyRange(q)));
        }

        var year = current;
        for (var i = 0; i < Math.Max(0, pastYears); i++)
        {
            year = FiscalCalendar.Previous(year);
            options.Add(YearOption(ctx, year, isCurrent: false));
        }

        return options;
    }

    private FiscalPeriodOption YearOption(
        TenantFiscalContext ctx, FiscalYearInfo year, bool isCurrent)
        => new(
            Key: $"fy:{year.StartYear}",
            Label: year.Label,
            Group: "Financial years",
            IsCurrent: isCurrent,
            Timestamps: new UtcRange(
                ctx.StartOfLocalDayUtc(year.Start),
                ctx.StartOfLocalDayUtc(year.EndExclusive)),
            Dates: DateOnlyRange(year));

    // =================================================================
    // NUMBERING
    // =================================================================

    public async Task<string> YearCodeForNumberingAsync(
        Guid tenantId, DateTime? issuedAtUtc = null, CancellationToken ct = default)
    {
        var ctx = await GetContextAsync(tenantId, ct);
        var local = ctx.LocalDateOf(issuedAtUtc ?? DateTime.UtcNow);
        return FiscalCalendar.For(local, ctx.StartMonth).Code;
    }
}
