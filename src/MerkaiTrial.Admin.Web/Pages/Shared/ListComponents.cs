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
}
