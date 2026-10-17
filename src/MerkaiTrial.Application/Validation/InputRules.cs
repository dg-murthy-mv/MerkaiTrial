// =====================================================================
// FILE: MerkaiTrial.Application/Validation/InputRules.cs
//
// NEW (095). The rules for what a person types, written ONCE.
//
// Three places check the same things and must never disagree:
//   1. the browser, as you type (Views/Shared/_ValidationScriptsPartial
//      carries a JavaScript copy of Phone and Email — kept line for line
//      with this file; change one, change the other);
//   2. the Admin.Web page, on the server, before it calls the API
//      (Pages/Shared/ValidationAttributes.cs and the page models);
//   3. the API's command handlers, before anything is saved.
//
// Every method returns NULL when the value is fine, or ONE SENTENCE for
// the person when it is not — written to say what to type, not what
// rule was broken.
//
// ── PHONE NUMBERS ──────────────────────────────────────────────────────
//
//   Anyone: digits, with spaces, dashes, dots, slashes or brackets between
//   them, and a + (or 00) in front for the country code. 7 to 15 digits —
//   15 is the international maximum.
//
//   The four markets, when the number says which one it is (+91, +66,
//   +63, +971) or when the record's country does:
//
//       India        +91   10 digits (11 for 1800 / 1860 toll-free)
//       Thailand     +66   8 or 9 digits
//       Philippines  +63   8 to 10 digits
//       UAE          +971  8 or 9 digits
//
//   A local number may start with the trunk 0 (098765 43210, 081 234 5678);
//   it is left out before counting. A number typed with the code but no +
//   ("919876543210") is accepted too — that is how many people type it.
//
//   The BROWSER checks the +-numbers against their market but does not
//   know the record's country, so a number without + is only counted
//   (7–15 digits) there. The server knows the country and checks it fully.
//   The browser is never stricter than the server.
//
// ── WHAT IS NOT CHECKED HERE, ON PURPOSE ───────────────────────────────
//
//   Whether the number or address EXISTS. Formats only.
// =====================================================================

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MerkaiTrial.Application.Commands.CustomFields;

namespace MerkaiTrial.Application.Validation
{
    public static class InputRules
    {
        // ── Limits (the database columns) ─────────────────────────────
        public const int PhoneMax = 32;
        public const int EmailMax = 320;

        /// <summary>
        /// The largest amount any money field takes: just under one
        /// trillion. Large enough for a ₹500-crore project (5×10⁹) many
        /// times over, small enough to catch a slipped finger adding three
        /// zeros. The columns themselves hold far more.
        /// </summary>
        public const decimal MaxMoney = 999_999_999_999m;

        // ── Email ─────────────────────────────────────────────────────

        // Something before the @, a domain of letters, digits and hyphens
        // in dot-separated parts, and a top-level part of 2+ letters. The
        // JavaScript copy uses the same pattern.
        private static readonly Regex EmailPattern = new(
            @"^[^\s@<>()\[\]\\,;:""]+@[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)*\.[A-Za-z]{2,}$",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

        public const string EmailMessage = "Enter an email address like name@company.com.";

        /// <summary>Null when empty or fine.</summary>
        public static string? Email(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var s = value.Trim();
            if (s.Length > EmailMax) return $"An email address can be up to {EmailMax} characters.";
            return EmailPattern.IsMatch(s) ? null : EmailMessage;
        }

        // ── Phone ─────────────────────────────────────────────────────

        public sealed record PhoneMarket(
            string Code, string Dial, int Min, int Max, string Name, string Example)
        {
            public bool Fits(string national)
            {
                if (national.Length == 0 || national[0] == '0') return false;
                if (national.Length >= Min && national.Length <= Max) return true;

                // India's toll-free numbers are one digit longer.
                return Code == "IN" && national.Length == 11
                       && (national.StartsWith("1800") || national.StartsWith("1860"));
            }

            /// <summary>"An Indian number has 10 digits after +91 (leave out the first 0)."</summary>
            public string Message => Code switch
            {
                "IN" => "An Indian number has 10 digits after +91 (leave out the first 0).",
                "TH" => "A Thai number has 8 or 9 digits after +66 (leave out the first 0).",
                "PH" => "A Philippine number has 8 to 10 digits after +63 (leave out the first 0).",
                "AE" => "A UAE number has 8 or 9 digits after +971 (leave out the first 0).",
                _    => $"Check the number after +{Dial}."
            };
        }

        /// <summary>The four markets. Dial codes match Countries.DialCode.</summary>
        public static readonly IReadOnlyList<PhoneMarket> Markets = new[]
        {
            new PhoneMarket("IN", "91",  10, 10, "India",       "+91 98765 43210"),
            new PhoneMarket("TH", "66",  8,  9,  "Thailand",    "+66 81 234 5678"),
            new PhoneMarket("PH", "63",  8,  10, "Philippines", "+63 917 123 4567"),
            new PhoneMarket("AE", "971", 8,  9,  "UAE",         "+971 50 123 4567"),
        };

        public static PhoneMarket? MarketFor(string? countryCode) =>
            string.IsNullOrWhiteSpace(countryCode)
                ? null
                : Markets.FirstOrDefault(m => string.Equals(m.Code, countryCode.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>A placeholder in the workspace's own format: "Include the country code, e.g. +91 98765 43210".</summary>
        public static string PhonePlaceholder(string? countryCode) =>
            "Include the country code, e.g. " + (MarketFor(countryCode)?.Example ?? "+91 98765 43210");

        public const string PhoneCharactersMessage =
            "Use digits only — spaces, dashes, brackets and a + for the country code are fine.";

        /// <summary>
        /// Null when empty or fine. <paramref name="countryCode"/> is the
        /// RECORD's country (IN, TH, PH, AE…) — used only for a number typed
        /// without a country code. Unknown or null: only the general rule.
        /// </summary>
        public static string? Phone(string? value, string? countryCode = null)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var s = value.Trim();
            if (s.Length > PhoneMax) return $"A phone number can be up to {PhoneMax} characters.";

            var plus = s[0] == '+';
            var digits = new StringBuilder(s.Length);
            for (var i = plus ? 1 : 0; i < s.Length; i++)
            {
                var ch = s[i];
                if (ch >= '0' && ch <= '9') digits.Append(ch);
                else if (ch is ' ' or '-' or '.' or '(' or ')' or '/' or '\u00A0') continue;
                else return PhoneCharactersMessage;
            }

            var d = digits.ToString();

            // 00 is the international prefix written without the +.
            if (!plus && d.StartsWith("00", StringComparison.Ordinal))
            {
                plus = true;
                d = d[2..];
            }

            if (d.Length < 7) return "That is too short for a phone number — it needs at least 7 digits.";
            if (d.Length > 15) return "That is too long for a phone number — it can have at most 15 digits.";

            if (plus)
            {
                var market = Markets.FirstOrDefault(m => d.StartsWith(m.Dial, StringComparison.Ordinal));
                if (market is null) return null;                       // another country: the general rule only

                var national = d[market.Dial.Length..];
                if (national.StartsWith('0')) national = national[1..]; // "+91 0987…" — the 0 crept in
                return market.Fits(national) ? null : market.Message;
            }

            var home = MarketFor(countryCode);
            if (home is null) return null;

            // Typed with the code but without the +: "919876543210".
            if (d.StartsWith(home.Dial, StringComparison.Ordinal) && home.Fits(d[home.Dial.Length..]))
                return null;

            var local = d.StartsWith('0') ? d[1..] : d;
            return home.Fits(local) ? null : home.Message;
        }

        // ── Money ─────────────────────────────────────────────────────

        /// <summary>
        /// Null when fine (or null and not required). <paramref name="label"/>
        /// names the field in the sentence: "The expected value…".
        /// </summary>
        public static string? Money(decimal? value, string label, bool allowZero = true, bool required = false)
        {
            if (value is null) return required ? $"Enter {label}." : null;
            if (value < 0) return $"{Capitalise(label)} can't be negative.";
            if (!allowZero && value == 0) return $"{Capitalise(label)} must be more than 0.";
            if (value > MaxMoney)
                return $"{Capitalise(label)} can be at most {MaxMoney.ToString("N0", CultureInfo.InvariantCulture)}. Check for an extra digit.";
            return null;
        }

        // ── Text ──────────────────────────────────────────────────────

        /// <summary>Null when fine. Whitespace-only counts as empty.</summary>
        public static string? Text(string? value, string label, int max, bool required = false)
        {
            if (string.IsNullOrWhiteSpace(value))
                return required ? $"Enter {label}." : null;
            return value.Trim().Length > max
                ? $"{Capitalise(label)} can be up to {max} characters."
                : null;
        }

        /// <summary>Null when fine — 0 to 100.</summary>
        public static string? Percent(decimal? value, string label)
        {
            if (value is null) return null;
            return value < 0 || value > 100 ? $"{Capitalise(label)} must be between 0 and 100." : null;
        }

        // ── Dates ─────────────────────────────────────────────────────

        /// <summary>
        /// Null when fine. A calendar date (a close date, a renewal) between
        /// 1 Jan 2000 and twenty years from now — wide enough for any real
        /// plan, narrow enough to catch 2062 for 2026 or a year typed as 0026.
        /// </summary>
        public static string? DateInRange(DateTime? value, string label)
        {
            if (value is null) return null;
            var last = DateTime.UtcNow.Year + 20;
            return value.Value.Year < 2000 || value.Value.Year > last
                ? $"{Capitalise(label)} should be between 2000 and {last}. Check the year."
                : null;
        }

        // ── Collecting ────────────────────────────────────────────────

        /// <summary>
        /// "Phone: That is too short…" — for the API's list of problems,
        /// which reaches the page as one banner rather than under each box.
        /// </summary>
        public static string? For(string field, string? error) =>
            error is null ? null : $"{field}: {error}";


        /// <summary>Adds the sentence to the list when there is one.</summary>
        public static void AddIf(this List<string> errors, string? error)
        {
            if (!string.IsNullOrEmpty(error)) errors.Add(error);
        }

        /// <summary>Throws InputValidationException listing every problem, when there are any.</summary>
        public static void ThrowIfAny(List<string> errors)
        {
            if (errors.Count > 0) throw new InputValidationException(errors);
        }

        private static string Capitalise(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }

    /// <summary>
    /// 095. What a person typed was refused. Derived from
    /// CustomFieldValidationException ON PURPOSE: every handler already
    /// re-throws that without logging it as a fault, and every controller
    /// already turns it into a 400 whose sentence reaches the page. A new
    /// unrelated exception type would need a change in each of those
    /// places to get the same treatment.
    /// </summary>
    public sealed class InputValidationException : CustomFieldValidationException
    {
        public InputValidationException(IReadOnlyList<string> errors) : base(errors) { }
    }
}
