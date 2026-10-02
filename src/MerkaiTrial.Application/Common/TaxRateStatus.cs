// =====================================================================
// TaxRateStatus.cs
// Location: MerkaiTrial.Application/Common/TaxRateStatus.cs
//
// NEW FILE (057). What state a tax rate is in TODAY.
//
// ── WHY THIS IS NOT JUST "IsActive" ──────────────────────────────────
//
// A tax rate has three independent things that decide whether it counts
// right now, and they were being read as one:
//
//   IsActive        a manual switch. Off = retired by hand.
//   EffectiveFrom   the day it starts applying. Null = always has.
//   EffectiveTo     the last day it applies. Null = still does.
//
// The two date columns have existed since the entity was written, and
// GetDefaultTaxRateAsync honours them — a rate whose window has closed is
// correctly ignored. But NOTHING COULD SET THEM. No DTO field, no form.
// So the one place in the system that respects a scheduled rate change
// could only ever see nulls, and the way to change a VAT rate was to edit
// the row on the day, with no record of what it used to be or when it
// changed.
//
// That is the gap this round fills, and the reason a rate needs a STATUS
// rather than a checkbox: with dates in play, "is this rate on?" has four
// answers, not two, and a screen that shows only Active/Inactive would
// hide a scheduled change completely — which is the whole point of
// scheduling one.
//
//   Scheduled   EffectiveFrom is in the future. Not in use YET.
//   Active      in force today.
//   Expired     EffectiveTo has passed. History, and correctly so.
//   Inactive    IsActive is off. Switched off by hand, dates irrelevant.
//
// Inactive wins over the dates on purpose: somebody turned it off, and
// that is a decision, not a schedule.
// =====================================================================

namespace MerkaiTrial.Application.Common;

public enum TaxRateState
{
    /// <summary>Switched off by hand. Dates do not matter.</summary>
    Inactive = 0,

    /// <summary>Starts applying on a future date.</summary>
    Scheduled = 1,

    /// <summary>In force today — this is the one a new quote will use.</summary>
    Active = 2,

    /// <summary>Its window has closed. Kept for the record.</summary>
    Expired = 3
}

public static class TaxRateStatus
{
    /// <summary>
    /// Where a rate stands today. <paramref name="asOfUtc"/> is injectable
    /// so a screen and a test can agree on "today" — a status that reads
    /// DateTime.UtcNow four times in one render can disagree with itself
    /// across midnight.
    /// </summary>
    public static TaxRateState Of(
        bool isActive, DateTime? effectiveFrom, DateTime? effectiveTo, DateTime? asOfUtc = null)
    {
        if (!isActive) return TaxRateState.Inactive;

        var now = asOfUtc ?? DateTime.UtcNow;

        if (effectiveFrom.HasValue && effectiveFrom.Value > now) return TaxRateState.Scheduled;
        if (effectiveTo.HasValue && effectiveTo.Value < now) return TaxRateState.Expired;

        return TaxRateState.Active;
    }

    /// <summary>True when a NEW quote would resolve against this rate.</summary>
    public static bool IsInForce(
        bool isActive, DateTime? effectiveFrom, DateTime? effectiveTo, DateTime? asOfUtc = null)
        => Of(isActive, effectiveFrom, effectiveTo, asOfUtc) == TaxRateState.Active;

    public static string Label(TaxRateState state) => state switch
    {
        TaxRateState.Scheduled => "Scheduled",
        TaxRateState.Active    => "Active",
        TaxRateState.Expired   => "Expired",
        _                      => "Inactive"
    };

    /// <summary>Bootstrap 5.3 badge class. text-bg-* rather than bg-*: it
    /// carries a contrasting foreground, which bg-warning alone does not.</summary>
    public static string BadgeClass(TaxRateState state) => state switch
    {
        TaxRateState.Scheduled => "text-bg-info",
        TaxRateState.Active    => "text-bg-success",
        TaxRateState.Expired   => "text-bg-secondary",
        _                      => "text-bg-danger"
    };

    /// <summary>One line telling the person what the state MEANS for them.</summary>
    public static string Explain(TaxRateState state) => state switch
    {
        TaxRateState.Scheduled => "Not in use yet — it starts on its effective date.",
        TaxRateState.Active    => "In force. New quotes and invoices use this rate.",
        TaxRateState.Expired   => "Its end date has passed. Kept for the record; not used.",
        _                      => "Switched off by hand. Not used, whatever its dates say."
    };

    /// <summary>
    /// Whether a from/to pair makes sense. Returns the problem, or null.
    /// A single method so the page, the API and the command all reject the
    /// same thing with the same wording.
    /// </summary>
    public static string? ValidateWindow(DateTime? effectiveFrom, DateTime? effectiveTo)
    {
        if (effectiveFrom.HasValue && effectiveTo.HasValue && effectiveTo.Value < effectiveFrom.Value)
            return "The end date can't be before the start date.";

        return null;
    }
}
