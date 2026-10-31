// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Shared/RecordSearchComponents.cs
//
// NEW (108). The server side of wwwroot/js/record-search.js — the search
// box above a long drop-down (contacts, companies, deals).
//
// WHY
//   These drop-downs listed records straight from the database: the New
//   Deal form showed the first 10 contacts (only 10 — the paged list's
//   default) and up to 5,000 companies; the contact forms up to 5,000
//   companies; New Quote the newest 500 deals. In a workspace with 30,000
//   contacts and 10,000 companies most records could not be chosen at all,
//   and the pages carried thousands of <option>s.
//
//   Now each drop-down starts with a short list (the newest 20, plus what
//   is already chosen), and the box above it searches the whole workspace
//   through a small JSON handler on the same page — so the page's own
//   permission check covers the search too.
// =====================================================================

namespace MerkaiTrial.Admin.Web.Pages.Shared
{
    /// <summary>One match, as record-search.js reads it: id, text, and data-* values for the option.</summary>
    public sealed record RecordSearchItem(string Id, string Text, IReadOnlyDictionary<string, string?>? Data = null);

    public static class RecordSearch
    {
        /// <summary>How many a drop-down starts with, and the most one search returns.</summary>
        public const int Take = 20;

        /// <summary>record-search.js asks only from 2 characters; the server agrees.</summary>
        public const int MinLength = 2;

        /// <summary>The search text, trimmed and kept short — or null when there is nothing to search for.</summary>
        public static string? Clean(string? q)
        {
            if (string.IsNullOrWhiteSpace(q)) return null;
            var t = q.Trim();
            if (t.Length < MinLength) return null;
            return t.Length > 100 ? t[..100] : t;
        }

        /// <summary>"Ravi Kumar (ravi@acme.in)", or just the name when there is no email.</summary>
        public static string ContactText(string fullName, string? email) =>
            string.IsNullOrWhiteSpace(email) ? fullName : $"{fullName} ({email})";
    }
}
