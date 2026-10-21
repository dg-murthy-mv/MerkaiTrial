// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Shared/ListComponents.cs
//
// 076 — ListToolbarVm.FormId: an optional id on the toolbar's <form>,
//   so inputs rendered ELSEWHERE on the page (the custom field filter
//   panel) can join the same GET with form="<id>". Without it, a second
//   GET form would throw away the toolbar's search on submit, and the
//   toolbar would throw away the panel's filters. Null on every page that
//   does not set it, and the form renders exactly as before.
//
// 096 — SORTING, DATES AND A REMEMBERED PAGE SIZE
//
//   ListSortVm: the column a list is sorted by and which way, plus every
//     filter, so a header link (_SortHeader) or the phone sort menu
//     (_SortMenu) can change the order without dropping anything. Sort
//     and Dir travel in the URL like every other part of the list's
//     state, so a sorted list can be bookmarked and survives Back. A new
//     sort starts at page 1 — the same rule as a new search.
//
//   ListToolbarVm.Dates: date inputs on the toolbar (the Invoices page's
//     issued-from / issued-to). They post yyyy-MM-dd, which an
//     <input type="date"> always does whatever the browser's language.
//
//   ListPreferences.PageSize: each person's last "Per page" choice is
//     remembered per list in a small cookie on their own browser —
//     choose 50 on Contacts and Contacts opens at 50 next time, from the
//     menu as well. The URL still wins when it says something; the
//     cookie only fills in when it says nothing. Default: 10 everywhere.
//
// NEW FILE (072). The view models behind _ListToolbar.cshtml and
// _Pager.cshtml — the search/filter strip and the pager that every list
// page in the app now shares.
//
// ─────────────────────────────────────────────────────────────────────
// WHY SHARED, AND WHY THE STATE IS IN THE QUERY STRING
//
//   Before this round each list had its own hand-rolled pager. They had
//   already drifted: Contacts offered 5/10/25/50/100 per page, Leads had
//   no selector at all and a hardcoded 10; Contacts' page links listed
//   every filter by hand in five separate asp-route-* attributes on
//   three different anchors, so adding a filter meant editing fifteen
//   places and missing one meant a filter that silently vanished on
//   page 2. That is the bug this file exists to make impossible: the
//   filters are a DICTIONARY, carried whole.
//
//   Everything lives in the URL — search text, every filter, the page
//   and the page size. So a filtered list can be bookmarked, sent to a
//   colleague, and survives the browser's back button. The same
//   reasoning put ?edit={id} in the URL on the categories page in 068.
//
// THE ONE RULE THAT IS EASY TO GET WRONG
//
//   A SEARCH OR FILTER MUST RESET TO PAGE 1. You are on page 7, you
//   type a name, and the result has two pages — without the reset you
//   land on page 7 of 2, which renders empty and reads as "no matches".
//   ListToolbarVm.Carry is deliberately NOT allowed to contain the page
//   field; the toolbar drops it, so submitting the form always starts
//   at page 1.
// ─────────────────────────────────────────────────────────────────────
// =====================================================================

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.Globalization;

namespace MerkaiTrial.Admin.Web.Pages.Shared
{
    /// <summary>One dropdown in the toolbar.</summary>
    public sealed class ListFilterVm
    {
        /// <summary>The query-string / page-model property name, e.g. "CompanyFilter".</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>For screen readers. Not drawn — the toolbar is one line, not a form with labels.</summary>
        public string Label { get; init; } = string.Empty;

        /// <summary>
        /// The options, INCLUDING the "all" option, with the current value
        /// already marked Selected. Built on the page, because only the page
        /// knows what it is filtering by.
        /// </summary>
        public IEnumerable<SelectListItem> Options { get; init; } = Enumerable.Empty<SelectListItem>();

        /// <summary>CSS width for the select. "auto" fits the longest option.</summary>
        public string Width { get; init; } = "auto";
    }

    /// <summary>096. One date input in the toolbar.</summary>
    public sealed class ListDateFilterVm
    {
        /// <summary>The query-string / page-model property name, e.g. "FromDate".</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>For screen readers, and the small word drawn before the box ("From").</summary>
        public string Label { get; init; } = string.Empty;

        /// <summary>The current value as yyyy-MM-dd (invariant), or empty.</summary>
        public string Value { get; init; } = string.Empty;

        /// <summary>The ISO form an &lt;input type="date"&gt; accepts, whatever the server's culture.</summary>
        public static string Iso(DateTime? d)
            => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public sealed class ListToolbarVm
    {
        /// <summary>
        /// 076. The id of the toolbar's form, for inputs elsewhere on the
        /// page that should submit with it (form="…"). Null = no id.
        /// </summary>
        public string? FormId { get; init; }

        // ── Search ───────────────────────────────────────────────────
        public string SearchName { get; init; } = "SearchTerm";
        public string? SearchValue { get; init; }
        public string SearchPlaceholder { get; init; } = "Search…";
        public string SearchLabel { get; init; } = "Search";

        /// <summary>Width of the search box. Wide enough for the placeholder.</summary>
        public string SearchWidth { get; init; } = "300px";

        // ── Filters ──────────────────────────────────────────────────
        public List<ListFilterVm> Filters { get; init; } = new();

        /// <summary>096. Date inputs, drawn after the dropdowns.</summary>
        public List<ListDateFilterVm> Dates { get; init; } = new();

        /// <summary>
        /// Values the search form must carry through as hidden fields so a
        /// search does not throw them away — the page size, a selected tab.
        ///
        /// ⚠ MUST NOT CONTAIN THE PAGE NUMBER. See the header: carrying it
        /// lands a fresh search on page 7 of 2, which looks like "no
        /// matches". The toolbar does not add it and the page must not
        /// either.
        /// </summary>
        public Dictionary<string, string> Carry { get; init; } = new();

        // ── Clear ────────────────────────────────────────────────────

        /// <summary>True when anything is actually filtered, so Clear is worth drawing.</summary>
        public bool IsFiltered { get; init; }

        public string PageRoute { get; init; } = "./Index";

        /// <summary>
        /// What a Clear link KEEPS. Usually the page size and the tab —
        /// clearing a search should not also move you off the tab you were
        /// looking at.
        /// </summary>
        public Dictionary<string, string> ClearRoute { get; init; } = new();

        // ── The count, on the right ──────────────────────────────────
        public int TotalCount { get; init; }
        public int FirstRow { get; init; }
        public int LastRow { get; init; }

        /// <summary>"contact" / "contacts". Used only in the empty-ish count line.</summary>
        public string NounSingular { get; init; } = "result";
        public string NounPlural { get; init; } = "results";

        public string CountLabel => TotalCount == 0
            ? $"No {NounPlural}"
            : $"Showing {FirstRow}–{LastRow} of {TotalCount} {(TotalCount == 1 ? NounSingular : NounPlural)}";
    }

    public sealed class PagerVm
    {
        public int Page { get; init; } = 1;
        public int PageSize { get; init; } = 10;
        public int TotalCount { get; init; }
        public int TotalPages { get; init; }

        /// <summary>The page-model property the page number binds to.</summary>
        public string PageField { get; init; } = "PageNumber";
        public string PageSizeField { get; init; } = "PageSize";

        public string PageRoute { get; init; } = "./Index";

        /// <summary>
        /// Every filter currently applied, carried on EVERY page link.
        ///
        /// A dictionary rather than a list of asp-route-* attributes, and
        /// that is the whole point of this class: the old per-page pagers
        /// spelled each filter out three times — on Previous, on each
        /// number and on Next — so a filter added later had fifteen places
        /// to be remembered in and silently disappeared from page 2 if one
        /// was missed.
        /// </summary>
        public Dictionary<string, string> Route { get; init; } = new();

        public bool ShowPageSize { get; init; } = true;

        /// <summary>
        /// The sizes offered. 10 is the default everywhere: it fits a
        /// laptop screen without scrolling, which is what makes a pager
        /// worth having at all.
        /// </summary>
        public static readonly int[] Sizes = { 10, 25, 50, 100 };

        /// <summary>096. The one default page size, on every list.</summary>
        public const int DefaultSize = 10;

        /// <summary>
        /// 096. The id of the "Per page" select. A page that draws two
        /// pagers would otherwise put two elements with one id on the page
        /// (and a label pointing at the wrong one) — set a second id there.
        /// </summary>
        public string SizeSelectId { get; init; } = "lpPageSize";

        /// <summary>Route values for a given page — the filters plus the page and size.</summary>
        public Dictionary<string, string> RouteFor(int page)
        {
            var values = new Dictionary<string, string>(Route, StringComparer.OrdinalIgnoreCase)
            {
                [PageField] = page.ToString(CultureInfo.InvariantCulture),
                [PageSizeField] = PageSize.ToString(CultureInfo.InvariantCulture)
            };
            return values;
        }

        /// <summary>
        /// The page numbers to draw: always the first and last, plus two
        /// either side of the current one. 0 is the ELLIPSIS marker — a
        /// page is never 0, so it cannot be confused with a real one.
        ///
        /// Without this a workspace with 4,000 contacts at 10 a page draws
        /// four hundred links.
        /// </summary>
        public IEnumerable<int> Window()
        {
            if (TotalPages <= 0) yield break;

            var last = 0;
            for (var i = 1; i <= TotalPages; i++)
            {
                var near = i == 1 || i == TotalPages || (i >= Page - 2 && i <= Page + 2);
                if (!near) continue;

                if (last != 0 && i - last > 1) yield return 0;   // …
                yield return i;
                last = i;
            }
        }
    }

    // =================================================================
    // 096 — SORTING
    // =================================================================

    /// <summary>
    /// One column a list can be sorted by. DefaultDescending: numbers and
    /// dates start biggest / newest first, text starts A→Z. The two
    /// phrases are what the phone menu says for each direction.
    /// </summary>
    public sealed record SortOption(
        string Key, string Label, bool DefaultDescending = false,
        string? AscLabel = null, string? DescLabel = null)
    {
        public string AscText  => AscLabel  ?? (DefaultDescending ? "low → high" : "A → Z");
        public string DescText => DescLabel ?? (DefaultDescending ? "high → low" : "Z → A");
    }

    /// <summary>
    /// A list's current order and the links that change it. Built by the
    /// page; drawn by _SortHeader (one per column header) and _SortMenu
    /// (the phone menu, where there are no headers).
    /// </summary>
    public sealed class ListSortVm
    {
        public string SortField { get; init; } = "Sort";
        public string DirField  { get; init; } = "Dir";

        /// <summary>The columns this list offers, in menu order.</summary>
        public IReadOnlyList<SortOption> Options { get; init; } = Array.Empty<SortOption>();

        /// <summary>The bound ?Sort= value. Anything not in Options counts as "no sort".</summary>
        public string? Sort { get; init; }

        /// <summary>The bound ?Dir= value.</summary>
        public string? Dir { get; init; }

        /// <summary>The key the list is in when no sort is asked for, and its direction — so its header can show it.</summary>
        public string? DefaultKey { get; init; }
        public bool DefaultDescending { get; init; }

        /// <summary>
        /// Every filter and the page size — NOT the page number, NOT the sort.
        /// A sort link adds its own Sort/Dir and lands on page 1.
        /// </summary>
        public Dictionary<string, string> Route { get; init; } = new();

        public string PageRoute { get; init; } = "./Index";

        /// <summary>The option the list is sorted by right now (the default when none is asked for).</summary>
        public SortOption? Active
            => Options.FirstOrDefault(o => string.Equals(o.Key, Sort, StringComparison.OrdinalIgnoreCase))
               ?? Options.FirstOrDefault(o => string.Equals(o.Key, DefaultKey, StringComparison.OrdinalIgnoreCase));

        /// <summary>True when the URL asked for a sort this list offers.</summary>
        public bool IsExplicit
            => Options.Any(o => string.Equals(o.Key, Sort, StringComparison.OrdinalIgnoreCase));

        public bool ActiveDescending
        {
            get
            {
                var active = Active;
                if (active is null) return false;
                if (!IsExplicit) return DefaultDescending;
                if (string.Equals(Dir, "desc", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(Dir, "asc", StringComparison.OrdinalIgnoreCase)) return false;
                return active.DefaultDescending;
            }
        }

        public bool IsActive(string key) => string.Equals(Active?.Key, key, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The link for a header: the active column flips direction; any
        /// other column starts in its natural direction (A→Z for text,
        /// biggest / newest first for numbers and dates).
        /// </summary>
        public Dictionary<string, string> RouteFor(string key)
        {
            var option = Options.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));
            var desc = IsActive(key) ? !ActiveDescending : option?.DefaultDescending ?? false;
            return RouteFor(key, desc);
        }

        /// <summary>The link for an explicit column and direction (the phone menu).</summary>
        public Dictionary<string, string> RouteFor(string key, bool descending)
        {
            var values = new Dictionary<string, string>(Route, StringComparer.OrdinalIgnoreCase)
            {
                [SortField] = key,
                [DirField]  = descending ? "desc" : "asc"
            };
            return values;
        }

        /// <summary>The current Sort/Dir as route values, for links and forms that must keep them. Empty when unsorted.</summary>
        public Dictionary<string, string> Current()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (IsExplicit && Active is { } a)
            {
                values[SortField] = a.Key;
                values[DirField]  = ActiveDescending ? "desc" : "asc";
            }
            return values;
        }

        /// <summary>What to send the API: the checked key and direction, or null for "the list's own order".</summary>
        public string? ApiSort => IsExplicit ? Active!.Key : null;
        public string? ApiDir  => IsExplicit ? (ActiveDescending ? "desc" : "asc") : null;

        /// <summary>For aria-sort on a header cell.</summary>
        public string AriaSort(string key)
            => !IsActive(key) ? "none" : ActiveDescending ? "descending" : "ascending";
    }

    /// <summary>Model for _SortHeader: one clickable column title.</summary>
    public sealed record SortHeaderVm(ListSortVm List, string Key, string Label, string? CssClass = null);

    // =================================================================
    // 096 — THE REMEMBERED PAGE SIZE
    // =================================================================

    public static class ListPreferences
    {
        private const string CookiePrefix = "merkai.ps.";

        /// <summary>
        /// The page size to use for one list.
        ///
        ///   • The URL says a valid size → use it, and remember it.
        ///   • The URL says nothing      → the remembered size, if valid.
        ///   • Otherwise                 → PagerVm.DefaultSize (10).
        ///
        /// An invalid size in the URL (?PageSize=7) is treated as "said
        /// nothing", exactly as the pages' ClampPageSize did before.
        ///
        /// For GET handlers. A POST (delete, issue…) carries the size it
        /// came from as a form field and passes it straight back to the
        /// redirect; the GET that follows reads it from the URL.
        /// </summary>
        /// <param name="list">A short fixed name: "leads", "contacts"… never user input.</param>
        public static int PageSize(HttpContext http, string list, string queryKey = "PageSize")
        {
            var cookieName = CookiePrefix + list;

            if (http.Request.Query.TryGetValue(queryKey, out var raw) &&
                int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var asked) &&
                PagerVm.Sizes.Contains(asked))
            {
                Remember(http, cookieName, asked);
                return asked;
            }

            if (http.Request.Cookies.TryGetValue(cookieName, out var saved) &&
                int.TryParse(saved, NumberStyles.Integer, CultureInfo.InvariantCulture, out var remembered) &&
                PagerVm.Sizes.Contains(remembered))
                return remembered;

            return PagerVm.DefaultSize;
        }

        private static void Remember(HttpContext http, string cookieName, int size)
        {
            // Only written when it changes, so an ordinary page load does
            // not send a Set-Cookie header every time.
            if (http.Request.Cookies.TryGetValue(cookieName, out var current) &&
                current == size.ToString(CultureInfo.InvariantCulture))
                return;

            http.Response.Cookies.Append(cookieName, size.ToString(CultureInfo.InvariantCulture), new CookieOptions
            {
                Path        = "/",
                HttpOnly    = true,                 // nothing on the page needs to read it
                Secure      = http.Request.IsHttps,
                SameSite    = SameSiteMode.Lax,
                IsEssential = true,                 // a display preference, holds nothing about the person
                MaxAge      = TimeSpan.FromDays(365)
            });
        }
    }
}
