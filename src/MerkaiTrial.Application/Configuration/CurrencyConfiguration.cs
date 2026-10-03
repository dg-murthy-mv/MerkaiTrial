// =====================================================================
// CurrencyConfiguration.cs
// Location: MerkaiTrial.Application/Configuration/CurrencyConfiguration.cs
//
// COMPLETE FILE — 069.
//
// THE ELEVEN CURRENCIES ARE UNCHANGED. Same codes, same names, same
// symbols, same order. Nothing that renders today changes.
//
// ─────────────────────────────────────────────────────────────────────
// THREE THINGS FIXED, ALL OF THEM THE SAME SHAPE OF BUG: a lookup that
// fails quietly and returns something plausible.
//
// 1. THE LOOKUPS WERE CASE-SENSITIVE.
//
//        Currencies.FirstOrDefault(c => c.Code == code)?.Symbol ?? code
//
//    `==` on string is ordinal, so GetCurrencySymbol("inr") returned
//    "inr" — the code itself, because of the ?? fallback. A price then
//    printed as "inr45,000.00". Nothing threw and nothing logged.
//
//    That mattered more from 069 onward than it did before: a currency
//    code now arrives from Deals.Currency, Quotes.Currency and
//    ProductPrices.CurrencyCode — three nvarchar columns, any of which
//    can hold whatever an import or a hand-written UPDATE put there.
//    Before this round the only source was Country.CurrencyCode, which
//    is seeded upper-case.
//
// 2. THERE WAS NO DECIMALS FIELD, even though Country.CurrencyDecimals
//    has existed since the localisation round. So anything formatting a
//    price from a currency CODE rather than from the tenant's country
//    had to assume 2. True for all eleven of these — and not true for
//    JPY, KRW or VND, which are the obvious next ones for a product
//    selling into Asia. Declaring it now means the first zero-decimal
//    currency is a one-line addition rather than a bug hunt.
//
// 3. GetCurrencySymbol FELL BACK TO THE CODE AND SAID NOTHING.
//    Keeping that behaviour — it is the right fallback, "AED1,200" beats
//    a crash — but IsKnown() now exists so a caller that needs to REFUSE
//    an unknown currency can, instead of discovering it on a printed
//    document. ProductPricing uses it to reject a bad CurrencyCode on
//    write.
//
// AED's "symbol" is the string "AED" on purpose: the dirham sign (د.إ)
// renders inconsistently in the PDF fonts and reads as a box on some
// Windows installs. Left exactly as it was.
// =====================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace MerkaiTrial.Application.Configuration
{
    public static class CurrencyConfiguration
    {
        /// <summary>
        /// The currencies Merkai can price in. Codes, names and symbols
        /// are unchanged from before 069; Decimals is new and is 2 for
        /// all eleven.
        ///
        /// ORDER MATTERS: this is the order the price grid and any
        /// currency dropdown show them in, so the four markets come
        /// first after the majors people recognise.
        /// </summary>
        public static readonly List<Currency> Currencies = new()
        {
            new Currency { Code = "USD", Name = "US Dollar",          Symbol = "$",   Decimals = 2 },
            new Currency { Code = "EUR", Name = "Euro",               Symbol = "€",   Decimals = 2 },
            new Currency { Code = "GBP", Name = "British Pound",      Symbol = "£",   Decimals = 2 },
            new Currency { Code = "INR", Name = "Indian Rupee",       Symbol = "₹",   Decimals = 2 },
            new Currency { Code = "THB", Name = "Thai Baht",          Symbol = "฿",   Decimals = 2 },
            new Currency { Code = "PHP", Name = "Philippine Peso",    Symbol = "₱",   Decimals = 2 },
            new Currency { Code = "AED", Name = "UAE Dirham",         Symbol = "AED", Decimals = 2 },
            new Currency { Code = "SGD", Name = "Singapore Dollar",   Symbol = "S$",  Decimals = 2 },
            new Currency { Code = "MYR", Name = "Malaysian Ringgit",  Symbol = "RM",  Decimals = 2 },
            new Currency { Code = "AUD", Name = "Australian Dollar",  Symbol = "A$",  Decimals = 2 },
            new Currency { Code = "CAD", Name = "Canadian Dollar",    Symbol = "C$",  Decimals = 2 }
        };

        public static List<string> GetCurrencyCodes()
            => Currencies.Select(c => c.Code).ToList();

        /// <summary>
        /// 069: case-insensitive. Was `c.Code == code`, which is ordinal,
        /// so "inr" fell through to the ?? and printed as "inr45,000.00".
        /// </summary>
        public static string GetCurrencySymbol(string? code)
            => Find(code)?.Symbol ?? Normalise(code);

        /// <summary>069: case-insensitive — see GetCurrencySymbol.</summary>
        public static string GetCurrencyName(string? code)
            => Find(code)?.Name ?? Normalise(code);

        /// <summary>
        /// 069. How many decimal places this currency is written with.
        /// Two for all eleven today. Falls back to 2 rather than throwing,
        /// which is right for every currency Merkai can currently price
        /// in — and is the line to revisit the day JPY is added.
        /// </summary>
        public static int GetCurrencyDecimals(string? code)
            => Find(code)?.Decimals ?? 2;

        /// <summary>
        /// 069. True when this is a currency Merkai knows how to show.
        ///
        /// For callers that must REFUSE rather than fall back: a price
        /// being saved against a currency, a quote being created in one.
        /// The display helpers above deliberately still degrade — a
        /// document that prints "AED1,200" is better than one that 500s —
        /// but a WRITE should not accept a currency nothing can render.
        /// </summary>
        public static bool IsKnown(string? code)
            => Find(code) is not null;

        /// <summary>
        /// 069. The stored form of a currency code: trimmed and
        /// upper-cased, or null when there is nothing there.
        ///
        /// One place, so ProductPrices.CurrencyCode can never end up
        /// holding both "usd" and "USD" for the same product — which the
        /// unique index would happily allow and which would give one
        /// product two prices in one currency.
        /// </summary>
        public static string? NormaliseCode(string? code)
            => string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();

        private static Currency? Find(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;

            var wanted = code.Trim();
            return Currencies.FirstOrDefault(c =>
                string.Equals(c.Code, wanted, StringComparison.OrdinalIgnoreCase));
        }

        private static string Normalise(string? code)
            => string.IsNullOrWhiteSpace(code) ? string.Empty : code.Trim();
    }

    public class Currency
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// 069. Decimal places. 2 for all eleven currencies here; the
        /// field exists so the first zero-decimal one (JPY, KRW, VND) is
        /// a one-line addition instead of a bug hunt through every
        /// ToString("N2") in the product.
        /// </summary>
        public int Decimals { get; set; } = 2;
    }
}
