// =====================================================================
// FiscalCalendar.cs
// Location: MerkaiTrial.Application/Services/Fiscal/FiscalCalendar.cs
//
// NEW FILE — 035.
//
// WHAT THIS IS
//   Pure arithmetic on dates. No database, no tenant, no async, no
//   injection. Given a local date and the month a financial year starts
//   in, it tells you which fiscal year and quarter that date falls in.
//
//   Everything here is static and deterministic, which means it can be
//   unit tested without a database and called a million times in a report
//   loop without touching anything.
//
// WHY DateOnly AND NOT DateTime
//   A fiscal year is a question about a CALENDAR DAY in the tenant's own
//   country, not about an instant. Mixing the two is exactly how "closed
//   1 April in Mumbai" ends up filed under the previous financial year.
//   Converting an instant to the tenant's local day is
//   IFiscalYearService's job; once it is a DateOnly, this file takes over.
//
// THE NAMING CONVENTION
//   Start month 1  ->  "2026"      (calendar year: TH, PH, AE)
//   Start month 4  ->  "2026-27"   (April 2026 to March 2027: India)
//
//   The Indian form is what appears on a GST invoice number
//   (INV/2026-27/0001), so it is not merely cosmetic.
// =====================================================================

using System;
using System.Globalization;

namespace MerkaiTrial.Application.Services.Fiscal;

/// <summary>
/// One financial year, fully resolved. A readonly record struct because
/// reports create one of these per row and it should cost nothing.
/// </summary>
public readonly record struct FiscalYearInfo(
    int StartMonth,
    int StartYear,
    DateOnly Start,
    DateOnly EndInclusive,
    string Code,
    string Label)
{
    /// <summary>
    /// The day AFTER the year ends. Use this for half-open range queries —
    /// `&gt;= Start &amp;&amp; &lt; EndExclusive` — which is the only way to
    /// compare a timestamp against a date range without losing the last day.
    /// </summary>
    public DateOnly EndExclusive => EndInclusive.AddDays(1);

    public bool Contains(DateOnly date) => date >= Start && date <= EndInclusive;

    public override string ToString() => Label;
}

/// <summary>One quarter within a financial year.</summary>
public readonly record struct FiscalQuarterInfo(
    int Number,                 // 1-4, counted from the fiscal year start
    DateOnly Start,
    DateOnly EndInclusive,
    string Code,                // "2026-27 Q1"
    string Label)               // "Q1 (Apr-Jun 2026)"
{
    public DateOnly EndExclusive => EndInclusive.AddDays(1);

    public bool Contains(DateOnly date) => date >= Start && date <= EndInclusive;

    public override string ToString() => Label;
}

public static class FiscalCalendar
{
    /// <summary>Used whenever a stored value is missing or out of range.</summary>
    public const int CalendarYearStartMonth = 1;

    /// <summary>
    /// Clamps a stored month into something usable. A bad value in the
    /// database must not throw halfway through a report — falling back to
    /// the calendar year is wrong but harmless and obvious, whereas an
    /// exception on the dashboard is neither.
    /// </summary>
    public static int NormaliseStartMonth(int? startMonth)
        => startMonth is >= 1 and <= 12 ? startMonth.Value : CalendarYearStartMonth;

    // =================================================================
    // FISCAL YEAR
    // =================================================================

    /// <summary>
    /// The financial year that <paramref name="localDate"/> falls in.
    ///
    /// A date in a month BEFORE the start month belongs to the year that
    /// began in the previous calendar year: 15 February 2027, with a start
    /// month of April, is in FY 2026-27.
    /// </summary>
    public static FiscalYearInfo For(DateOnly localDate, int? startMonth)
    {
        var month = NormaliseStartMonth(startMonth);

        var startYear = localDate.Month >= month
            ? localDate.Year
            : localDate.Year - 1;

        return Build(startYear, month);
    }

    /// <summary>The financial year identified by the calendar year it STARTS in.</summary>
    public static FiscalYearInfo ForStartYear(int startYear, int? startMonth)
        => Build(startYear, NormaliseStartMonth(startMonth));

    public static FiscalYearInfo Previous(FiscalYearInfo year)
        => Build(year.StartYear - 1, year.StartMonth);

    public static FiscalYearInfo Next(FiscalYearInfo year)
        => Build(year.StartYear + 1, year.StartMonth);

    private static FiscalYearInfo Build(int startYear, int month)
    {
        // Clamped so a garbage date cannot throw.
        //
        // This is not theoretical: Deal.ExpectedCloseDateUtc is
        // non-nullable, so "never set" arrives as default(DateTime) —
        // 0001-01-01 — as StageTransitionGuard.HasCloseDate already has to
        // allow for. With an April start month that would ask for
        // new DateOnly(0, 4, 1) and throw ArgumentOutOfRangeException, on a
        // report, for one bad row. Clamping turns that into a nonsense year
        // the reader can see, which is the right failure mode here.
        startYear = Math.Clamp(startYear, 1, 9998);

        var start = new DateOnly(startYear, month, 1);

        // One year forward, one day back. Handles a start month of January
        // (31 Dec of the same year) and every other month without a special
        // case, and gets 29 February right by construction.
        var endInclusive = start.AddYears(1).AddDays(-1);

        var code = month == CalendarYearStartMonth
            ? startYear.ToString(CultureInfo.InvariantCulture)
            : $"{startYear}-{(startYear + 1) % 100:00}";

        return new FiscalYearInfo(
            StartMonth: month,
            StartYear: startYear,
            Start: start,
            EndInclusive: endInclusive,
            Code: code,
            Label: $"FY {code}");
    }

    // =================================================================
    // QUARTER
    // =================================================================

    /// <summary>
    /// The quarter <paramref name="localDate"/> falls in, counted from the
    /// financial year's own start. With an April start, Q1 is April to June
    /// — which is what an Indian tenant means by Q1, and what they will not
    /// find in a CRM that hardcodes January.
    /// </summary>
    public static FiscalQuarterInfo QuarterFor(DateOnly localDate, int? startMonth)
    {
        var year = For(localDate, startMonth);
        return QuarterOf(year, QuarterNumber(localDate, year));
    }

    /// <summary>1-4. Assumes the date is inside the given year.</summary>
    public static int QuarterNumber(DateOnly localDate, FiscalYearInfo year)
    {
        var monthsIn = ((localDate.Year - year.StartYear) * 12)
                     + (localDate.Month - year.StartMonth);

        // Clamped rather than trusted: a caller passing a date outside the
        // year would otherwise get quarter 0 or 5 and a nonsense range.
        return Math.Clamp((monthsIn / 3) + 1, 1, 4);
    }

    /// <summary>Quarter <paramref name="number"/> (1-4) of a given year.</summary>
    public static FiscalQuarterInfo QuarterOf(FiscalYearInfo year, int number)
    {
        var n = Math.Clamp(number, 1, 4);

        var start = year.Start.AddMonths((n - 1) * 3);
        var endInclusive = start.AddMonths(3).AddDays(-1);

        var months = $"{Abbrev(start.Month)}-{Abbrev(endInclusive.Month)}";

        return new FiscalQuarterInfo(
            Number: n,
            Start: start,
            EndInclusive: endInclusive,
            Code: $"{year.Code} Q{n}",
            Label: $"Q{n} ({months} {start.Year})");
    }

    /// <summary>All four quarters, in order. For a period picker.</summary>
    public static FiscalQuarterInfo[] QuartersOf(FiscalYearInfo year)
        => new[]
        {
            QuarterOf(year, 1),
            QuarterOf(year, 2),
            QuarterOf(year, 3),
            QuarterOf(year, 4)
        };

    // Invariant culture on purpose. This abbreviation goes into a period
    // label the tenant sees, and it must not change with the thread's
    // culture halfway through a report.
    private static string Abbrev(int month)
        => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(month);
}
