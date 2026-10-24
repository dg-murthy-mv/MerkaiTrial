// =====================================================================
// WorkspaceTime.cs
// Location: MerkaiTrial.Application/Common/WorkspaceTime.cs
//
// NEW FILE (103). One answer to "what time is it in this workspace, and
// what does 09:00 there mean in UTC?", used by the pages, the automation
// engine and the time-based rule runner alike.
//
// WHAT WAS WRONG BEFORE
//   • Three places worked out a workspace's zone three ways. The pages
//     (CurrentTenantService) used the workspace's zone, then its
//     country's, then India. The automation engine and the time-based
//     runner used the workspace's zone, then UTC. A workspace with no
//     zone of its own but a country of Thailand saw its tasks in Bangkok
//     time while its rules worked out "today" in London.
//   • Daylight saving. On the morning the clocks go forward, 02:30 does
//     not exist in New York. .NET refuses to convert it (an exception),
//     and CurrentTenantService caught that and stored 02:30 AS UTC: the
//     task showed as 21:30 the evening before. On the night the clocks go
//     back, 01:30 happens twice; .NET quietly picked the second one.
//
// THE RULES NOW
//   • Zone: the workspace's own, else its country's, else Asia/Kolkata
//     (the same default the pages always used). A zone id this server
//     does not know is skipped, never treated as UTC.
//   • A time the clock skips (spring forward) moves forward by the jump:
//     02:30 on that morning in New York is 03:30 EDT. That is what a
//     person setting an alarm for 02:30 would get, and what Java, Python's
//     zoneinfo and the calendar apps do.
//   • A time that happens twice (fall back) is the FIRST of the two:
//     01:30 EDT, not 01:30 EST an hour later. A reminder should not be an
//     hour late.
// =====================================================================

using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Common;

public static class WorkspaceTime
{
    /// <summary>The zone used when neither the workspace nor its country names one.</summary>
    public const string DefaultZoneId = "Asia/Kolkata";

    /// <summary>The zone with this id, or null when the id is blank or unknown here.</summary>
    public static TimeZoneInfo? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }

    /// <summary>True when this server can work with the zone id.</summary>
    public static bool IsKnown(string? id) => Find(id) is not null;

    /// <summary>The workspace's zone, else its country's, else <see cref="DefaultZoneId"/>.</summary>
    public static TimeZoneInfo Resolve(string? workspaceZone, string? countryZone)
        => Find(workspaceZone) ?? Find(countryZone) ?? Find(DefaultZoneId) ?? TimeZoneInfo.Utc;

    /// <summary>
    /// The zone of one workspace, read from the database — for code that
    /// has no signed-in person (the automation engine, the worker).
    /// </summary>
    public static async Task<TimeZoneInfo> ForTenantAsync(FlowDbContext db, Guid tenantId, CancellationToken ct = default)
    {
        var row = await db.Tenants.AsNoTracking().IgnoreQueryFilters()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.Timezone, t.CountryId })
            .FirstOrDefaultAsync(ct);

        string? countryZone = null;
        if (row?.CountryId is Guid countryId)
        {
            countryZone = await db.Countries.AsNoTracking().IgnoreQueryFilters()
                .Where(c => c.Id == countryId)
                .Select(c => c.Timezone)
                .FirstOrDefaultAsync(ct);
        }

        return Resolve(row?.Timezone, countryZone);
    }

    /// <summary>A UTC instant as the workspace's wall clock shows it.</summary>
    public static DateTime ToLocal(DateTime utc, TimeZoneInfo zone)
        => DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone),
            DateTimeKind.Unspecified);

    /// <summary>
    /// The UTC instant for a wall-clock time in the workspace. A skipped
    /// time moves forward by the jump; a doubled time is the first one.
    /// </summary>
    public static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(wall))
        {
            // The clock jumped over this time. Read it with the offset that
            // was in force just before the jump: 02:30 − (−5h) = 07:30 UTC,
            // which is 03:30 EDT. Three hours back is safely before any
            // real-world jump starts.
            var before = zone.GetUtcOffset(wall.AddHours(-3));
            return DateTime.SpecifyKind(wall - before, DateTimeKind.Utc);
        }

        if (zone.IsAmbiguousTime(wall))
        {
            // Two offsets; the larger one (summer time) is the earlier instant.
            var first = zone.GetAmbiguousTimeOffsets(wall).Max();
            return DateTime.SpecifyKind(wall - first, DateTimeKind.Utc);
        }

        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(wall, zone), DateTimeKind.Utc);
    }
}
