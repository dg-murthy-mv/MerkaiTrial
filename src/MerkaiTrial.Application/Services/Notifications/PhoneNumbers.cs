// =====================================================================
// PhoneNumbers.cs
// Location: MerkaiTrial.Application/Services/Notifications/PhoneNumbers.cs
//
// NEW FILE (044). Static — nothing to register.
//
// WHY THIS EXISTS
//
//   WhatsApp accepts one format and one only: E.164, which is a plus, a
//   country code, and the national number, with nothing else at all —
//   "+919876543210". Everything people actually type is wrong:
//
//     098-765 4321          leading zero, spaces, a dash
//     (0987) 654321         brackets
//     +91 98765 43210       right, but spaced
//     0091 9876543210       international prefix instead of +
//     9876543210            no country code
//
//   Send any of those and Meta returns "invalid recipient", or worse,
//   accepts it and delivers nowhere. So normalisation happens on SAVE, in
//   one place, and the stored value is always already correct.
//
// WHY NOT libphonenumber
//
//   Google's library is the right answer for a product serving everybody.
//   It is also a 250KB dependency with a metadata file that needs
//   updating as numbering plans change. We serve four countries whose
//   rules are simple and stable, and the failure mode here is "ask the
//   person to check the number", not "corrupt data". If a fifth market
//   arrives with genuinely messy numbering, swap the guts of ToE164 for
//   libphonenumber and nothing else in the app needs to know.
//
// WHAT THIS DELIBERATELY DOES NOT DO
//
//   It does not verify the number belongs to the person, or that WhatsApp
//   is installed on it. Nothing short of sending a code can do the first,
//   and only Meta can answer the second. This gets the SHAPE right, which
//   is what stops the silent failures.
// =====================================================================

using System.Text;

namespace MerkaiTrial.Application.Services.Notifications;

public static class PhoneNumbers
{
    /// <summary>
    /// The shortest and longest an E.164 number can be, excluding the "+".
    /// E.164 caps the whole thing at 15 digits; 8 is below any mobile in
    /// our markets and catches a half-typed number.
    /// </summary>
    private const int MinDigits = 8;
    private const int MaxDigits = 15;

    /// <summary>
    /// What ToE164 decided. Number is null unless Ok.
    /// Reason is written for the person who typed it, not for a log.
    /// </summary>
    public readonly record struct Result(bool Ok, string? Number, string? Reason)
    {
        public static Result Fail(string reason) => new(false, null, reason);
        public static Result Pass(string number) => new(true, number, null);
    }

    /// <summary>
    /// Turns what somebody typed into E.164, or explains why it cannot.
    /// </summary>
    /// <param name="input">Whatever was in the box. Null or blank is a
    /// PASS with a null number — "no mobile on file" is a legitimate
    /// state, not an error.</param>
    /// <param name="defaultDialCode">The workspace's country dial code
    /// ("+91"), from Country.DialCode. Used only when the number carries
    /// no country code of its own. Null means a bare national number
    /// cannot be resolved and is refused rather than guessed.</param>
    public static Result ToE164(string? input, string? defaultDialCode)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Result.Pass(null!);          // cleared on purpose

        var raw = input.Trim();

        // Did they write it as international? Both spellings people use.
        var international = raw.StartsWith('+') || raw.StartsWith("00", StringComparison.Ordinal);

        var digits = KeepDigits(raw);

        if (digits.Length == 0)
            return Result.Fail("That doesn't look like a phone number.");

        // "0091 98765 43210" → drop the 00, it means the same as +.
        if (!raw.StartsWith('+') && digits.StartsWith("00", StringComparison.Ordinal))
            digits = digits[2..];

        if (!international)
        {
            var dial = KeepDigits(defaultDialCode ?? "");

            if (dial.Length == 0)
                return Result.Fail(
                    "Include the country code, like +91 98765 43210.");

            // A national number often carries a trunk zero — 098… in
            // Thailand, 0917… in the Philippines. It is not part of the
            // international form and must come off before the country
            // code goes on, or the number gains a digit and silently
            // becomes someone else's.
            digits = digits.TrimStart('0');

            if (digits.Length == 0)
                return Result.Fail("That doesn't look like a phone number.");

            // Someone typing their own country code without a plus —
            // "91 98765 43210" in an Indian workspace. Prepending would
            // give +9198765…, a number in no country at all.
            //
            // BUT "starts with the dial code" is not enough to decide, and
            // getting this wrong sends to a real stranger rather than
            // failing. Two examples from our own markets:
            //
            //   Thailand  066 123 4567 → 661234567 after the trunk zero.
            //             It STARTS with 66 and is not international.
            //   India     91987 65432 is a valid 10-digit national number
            //             that starts with 91.
            //
            // Length is what separates them. A national number of the
            // expected length is national, whatever it starts with.
            var nationalLength = NationalLengthFor(dial);

            var looksInternational =
                digits.StartsWith(dial, StringComparison.Ordinal)
                && digits.Length >= dial.Length + (nationalLength ?? MinDigits);

            if (!looksInternational)
                digits = dial + digits;
        }

        if (digits.Length < MinDigits)
            return Result.Fail("That number looks too short — check the digits.");

        if (digits.Length > MaxDigits)
            return Result.Fail("That number looks too long — check the digits.");

        return Result.Pass("+" + digits);
    }

    /// <summary>
    /// A number for showing back to the person: "+91 98765 43210".
    /// Grouping is cosmetic and deliberately naive — the stored value is
    /// what gets sent, and this never feeds back into it.
    /// </summary>
    public static string Pretty(string? e164)
    {
        if (string.IsNullOrWhiteSpace(e164)) return "";

        var digits = KeepDigits(e164);
        if (digits.Length < MinDigits) return e164;

        // Longest dial code first, so +971 wins over +9 and +91.
        foreach (var dial in KnownDialCodes)
        {
            if (!digits.StartsWith(dial, StringComparison.Ordinal)) continue;

            var rest = digits[dial.Length..];

            return rest.Length > 5
                ? $"+{dial} {rest[..^5]} {rest[^5..]}"
                : $"+{dial} {rest}";
        }

        return "+" + digits;
    }

    /// <summary>
    /// The four markets, longest first. Only used for display grouping —
    /// a number from anywhere else still stores and sends correctly, it
    /// just shows unspaced.
    /// </summary>
    private static readonly string[] KnownDialCodes = { "971", "63", "66", "91" };

    /// <summary>
    /// Digits in a MOBILE number once the trunk zero is off, per dial
    /// code. This is what tells a national number apart from one that
    /// already carries its country code — see the note in ToE164.
    ///
    ///   +91  India        10   98765 43210
    ///   +66  Thailand      9   8 1234 5678   (from 081 234 5678)
    ///   +63  Philippines  10   917 123 4567  (from 0917 123 4567)
    ///   +971 UAE           9   50 123 4567   (from 050 123 4567)
    ///
    /// A dial code not listed here falls back to "at least MinDigits",
    /// which is right often enough and never silently truncates.
    /// </summary>
    private static int? NationalLengthFor(string dialDigits) => dialDigits switch
    {
        "91"  => 10,
        "66"  => 9,
        "63"  => 10,
        "971" => 9,
        _     => null
    };

    private static string KeepDigits(string s)
    {
        var sb = new StringBuilder(s.Length);

        foreach (var c in s)
            if (char.IsAsciiDigit(c)) sb.Append(c);

        return sb.ToString();
    }
}
