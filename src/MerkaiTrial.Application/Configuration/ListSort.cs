// =====================================================================
// FILE: MerkaiTrial.Application/Configuration/ListSort.cs
//
// NEW FILE (096). Column sorting for the list endpoints, written once.
//
// Every list (leads, contacts, companies, products, invoices, the deal
// table) takes the same two query values:
//
//     sort   the column key, e.g. "value", "name", "created"
//     dir    "asc" or "desc"
//
// THE KEY IS CHECKED AGAINST A FIXED LIST, never used as a column name.
// Each handler names the keys it accepts and maps each one to a typed
// expression in a switch; an unknown or empty key quietly falls back to
// that list's default order. So a hand-edited ?sort=Password or
// ?sort=1;DROP is simply the default order — nothing is interpolated
// into SQL and nothing throws.
//
// EVERY ORDER ENDS WITH THE ROW'S Id. Sorting by value, two deals worth
// the same could swap places between two page loads, so paging showed
// one twice and skipped the other. The Id tiebreak makes every order
// total. The handlers add it; this file only provides the two helpers.
//
// PhoneDigits: "98765 43210", "+91 98765-43210" and "(098) 765 43210"
// are the same number to a person and three different strings to SQL.
// A search is matched against the stored phone with the usual
// separators stripped — see the handlers.
// =====================================================================

using System.Linq.Expressions;

namespace MerkaiTrial.Application.Configuration
{
    public static class ListSort
    {
        public const string Asc  = "asc";
        public const string Desc = "desc";

        /// <summary>
        /// The accepted key that matches <paramref name="sort"/> (any case),
        /// or null when it matches none — the caller then uses its default.
        /// </summary>
        public static string? Key(string? sort, params string[] allowed)
        {
            if (string.IsNullOrWhiteSpace(sort)) return null;
            var wanted = sort.Trim();
            foreach (var key in allowed)
                if (string.Equals(key, wanted, StringComparison.OrdinalIgnoreCase))
                    return key;
            return null;
        }

        /// <summary>"desc" → true, "asc" → false, anything else → <paramref name="fallback"/>.</summary>
        public static bool IsDescending(string? dir, bool fallback)
        {
            if (string.Equals(dir?.Trim(), Desc, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(dir?.Trim(), Asc,  StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        public static IOrderedQueryable<T> OrderByDir<T, TKey>(
            this IQueryable<T> source, Expression<Func<T, TKey>> key, bool descending)
            => descending ? source.OrderByDescending(key) : source.OrderBy(key);

        public static IOrderedQueryable<T> ThenByDir<T, TKey>(
            this IOrderedQueryable<T> source, Expression<Func<T, TKey>> key, bool descending)
            => descending ? source.ThenByDescending(key) : source.ThenBy(key);

        /// <summary>
        /// The digits of a search, for matching phone numbers whatever
        /// separators they were stored with. Null when there are fewer than
        /// four — "a1" must not turn into "every number containing a 1".
        /// </summary>
        public static string? PhoneDigits(string? search)
        {
            if (string.IsNullOrWhiteSpace(search)) return null;
            var digits = new string(search.Where(char.IsAsciiDigit).ToArray());
            return digits.Length >= 4 ? digits : null;
        }
    }
}
