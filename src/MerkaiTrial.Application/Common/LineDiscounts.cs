// =====================================================================
// LineDiscounts.cs
// Location: MerkaiTrial.Application/Common/LineDiscounts.cs
//
// NEW FILE (055). A line discount entered as a PERCENTAGE.
//
// ── WHAT WAS MISSING ─────────────────────────────────────────────────
//
// A line discount could only be an AMOUNT. Reps do not sell that way —
// they agree "10% off" and then work out what that is on a calculator,
// line by line, and type the answer in. Every one of those is a chance
// to fat-finger a figure onto a document a customer signs.
//
// ── WHAT IS STORED, AND WHY BOTH ─────────────────────────────────────
//
//   LineDiscount    the AMOUNT. Unchanged, and still the only thing any
//                   total is worked out from. Nothing about the maths on
//                   a quote, an invoice or the approval engine moves.
//
//   DiscountPercent NULL when the discount was typed as an amount (and
//                   when there is no discount). Set when it was typed as
//                   a percentage.
//
// The percentage is not decoration. Without it, a quote reopened a week
// later shows "1,234.50" where the rep typed "10%", and nobody can tell
// whether that was a round percentage or a negotiated figure. With it,
// the document can print "-10%" the way the customer was told.
//
// ── THE PERCENTAGE WINS, AND THAT IS DELIBERATE ──────────────────────
//
// When a percentage is supplied, the server RECOMPUTES the amount from
// it and ignores whatever amount came with it. The browser computes the
// same number for display, but a request is not a browser: a caller that
// posts "10%" with an amount of 0 — by accident or otherwise — must not
// get a line at full price with a 10% badge on it, and one that posts
// "5%" with an amount equal to the whole line must not get it free.
//
// There is exactly one subtraction in the system, and it is here.
// =====================================================================

namespace MerkaiTrial.Application.Common;

public static class LineDiscounts
{
    /// <summary>
    /// Decimal places kept on a percentage. Matches the column,
    /// DECIMAL(5,2) — 0.00 to 100.00.
    /// </summary>
    public const int PercentDecimals = 2;

    /// <summary>Money rounding. Two places, and half-up, like an invoice.</summary>
    public const int AmountDecimals = 2;

    /// <summary>
    /// What <paramref name="percent"/> is worth on a line. Rounded to the
    /// currency's places here rather than left long, so the stored amount
    /// is exactly the amount the totals were built from — a discount that
    /// rounds differently in two places is how a grand total ends up a
    /// cent off its own lines.
    /// </summary>
    public static decimal AmountFrom(decimal unitPrice, decimal quantity, decimal percent)
        => decimal.Round(unitPrice * quantity * percent / 100m,
                         AmountDecimals, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The (amount, percent) pair to store for one line.
    ///
    /// Throws rather than silently correcting an out-of-range percentage:
    /// a 150% discount is not a rounding problem, it is a mistake, and the
    /// person who typed it should be told.
    /// </summary>
    public static (decimal Amount, decimal? Percent) Resolve(
        string? lineName,
        decimal unitPrice,
        decimal quantity,
        decimal suppliedAmount,
        decimal? suppliedPercent)
    {
        var name = string.IsNullOrWhiteSpace(lineName) ? "An item" : $"\"{lineName.Trim()}\"";

        if (suppliedPercent is not decimal pct)
            return (suppliedAmount, null);

        if (pct < 0)
            throw new InvalidOperationException($"{name}: a discount percentage can't be negative.");

        if (pct > 100)
            throw new InvalidOperationException(
                $"{name}: a discount percentage can't be more than 100%.");

        pct = decimal.Round(pct, PercentDecimals, MidpointRounding.AwayFromZero);

        // Zero percent is the same as no percentage discount. Storing 0.00
        // rather than NULL would make "discounted by a round number" and
        // "not discounted" indistinguishable on every screen below.
        if (pct == 0m)
            return (0m, null);

        return (AmountFrom(unitPrice, quantity, pct), pct);
    }

    /// <summary>
    /// The percentage a stored amount represents, for a line that was
    /// typed as an amount. Display only — never stored, because it would
    /// turn every amount into a fake percentage and lose the distinction
    /// the column exists to keep.
    /// </summary>
    public static decimal? ImpliedPercent(decimal unitPrice, decimal quantity, decimal amount)
    {
        var gross = unitPrice * quantity;
        if (gross <= 0 || amount <= 0) return null;
        return decimal.Round(amount / gross * 100m, PercentDecimals, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// How a discount should READ on a document: "-10%" when it was agreed
    /// as a percentage, the amount otherwise. The caller formats the money,
    /// because only it knows the tenant's currency and culture.
    /// </summary>
    public static string? PercentLabel(decimal? percent)
        => percent is decimal p && p > 0
            ? "-" + p.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%"
            : null;
}
