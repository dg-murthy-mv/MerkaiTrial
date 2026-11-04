// =====================================================================
// LeadListFilter.cs
// Location: MerkaiTrial.Application/Commands/Leads/LeadListFilter.cs
//
// NEW FILE (092).
//
// The Leads list's filters — search, status, owner and custom fields —
// written ONCE and used by both:
//
//   GetLeadsPaginatedHandler   the Leads page
//   ExportLeadsHandler         the Export button on that page
//
// WHY ITS OWN FILE
//   The export had its own copy of the filters. The copy had already
//   drifted: it did not search custom field values, ignored the custom
//   field filters altogether, and did not trim the owner id. So "Export"
//   on a filtered list could hand back rows that were not on the screen —
//   the one thing an export must never do. One copy cannot drift.
//
// 096 — PHONE SEARCH IGNORES FORMATTING. "9876543210" did not find a
//   lead stored as "98765 43210", because the phone was compared exactly
//   as typed. A search with four or more digits now also matches the
//   stored phone with spaces, dashes, dots, brackets and "+" removed.
//   Everything else the search matched, it still matches.
//
// 116 — FASTER: no LOWER() per row (the database ignores case) and the
//   phone's digits come from the stored column Leads.PhoneDigits
//   (Sql/116_Indexes_And_Search.sql) instead of six REPLACE()s per row.
//   Same results.
//
// RECORD VISIBILITY is NOT applied here. Each caller applies
// .VisibleTo(access) to its own base query first, where it is plainly
// visible in the handler — a filter helper that quietly also decided who
// may see what would be one more place to forget it.
// =====================================================================

using MerkaiTrial.Application.Commands.CustomFields;
using MerkaiTrial.Application.Configuration;   // 096 — ListSort.PhoneDigits
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;

namespace MerkaiTrial.Application.Commands.Leads
{
    public static class LeadListFilter
    {
        /// <summary>
        /// Narrow <paramref name="leads"/> exactly as the Leads list does.
        ///
        ///   search      name, email, phone, company — and custom text
        ///               values and dropdown choice names
        ///   status      one key, or several comma-separated (Active tab)
        ///   assignedTo  an owner's user id
        ///   filters     custom field conditions (?cf=), ANDed
        ///
        /// <paramref name="defs"/> are the ACTIVE Lead custom fields
        /// (CustomFieldListQuery.ActiveDefinitionsAsync).
        /// </summary>
        public static IQueryable<Lead> Apply(
            FlowDbContext db,
            Guid tenantId,
            IQueryable<Lead> leads,
            IReadOnlyList<CustomFieldDefinition> defs,
            string? search,
            string? status,
            string? assignedTo,
            IReadOnlyList<CustomFieldFilter>? filters)
        {
            if (!string.IsNullOrWhiteSpace(search))
            {
                // 116 — compared as typed: the database ignores upper and
                // lower case (Sql/116 checks its collation), so LOWER() on
                // every lead was work for nothing; and the phone's digits
                // are a stored column (Leads.PhoneDigits) instead of six
                // REPLACE()s on every lead, every search.
                var term = search.Trim();
                var s    = term.ToLower();   // for custom-field and dropdown matching

                // 096 — the search's digits, matched against the phone with
                // its separators stripped. "" (never null inside the query)
                // when the search has fewer than four digits, and then the
                // `digits != ""` guard switches the comparison off.
                var digits = ListSort.PhoneDigits(s) ?? string.Empty;

                // Null = no custom field could match; keep the plain search.
                var cfMatches = CustomFieldListQuery.SearchMatches(db, tenantId, defs, s);

                if (cfMatches is null)
                {
                    leads = leads.Where(l =>
                        l.FullName.Contains(term) ||
                        (l.Email != null && l.Email.Contains(term)) ||
                        (l.Phone != null && l.Phone.Contains(term)) ||
                        (digits != "" && l.PhoneDigits != null && l.PhoneDigits.Contains(digits)) ||
                        (l.CompanyName != null && l.CompanyName.Contains(term)));
                }
                else
                {
                    leads = leads.Where(l =>
                        l.FullName.Contains(term) ||
                        (l.Email != null && l.Email.Contains(term)) ||
                        (l.Phone != null && l.Phone.Contains(term)) ||
                        (digits != "" && l.PhoneDigits != null && l.PhoneDigits.Contains(digits)) ||
                        (l.CompanyName != null && l.CompanyName.Contains(term)) ||
                        cfMatches.Contains(l.Id));
                }
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                var keys = status.Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (keys.Length == 1)
                {
                    var key = keys[0];
                    leads = leads.Where(l => l.Status == key);
                }
                else if (keys.Length > 1)
                {
                    leads = leads.Where(l => keys.Contains(l.Status));
                }
            }

            if (!string.IsNullOrWhiteSpace(assignedTo))
            {
                var owner = assignedTo.Trim();
                leads = leads.Where(l => l.OwnerUserId == owner);
            }

            foreach (var clause in CustomFieldListQuery.FilterClauses(db, tenantId, defs, filters))
            {
                var ids = clause.EntityIds;   // a local, so EF inlines the subquery
                leads = clause.Exclude
                    ? leads.Where(l => !ids.Contains(l.Id))
                    : leads.Where(l => ids.Contains(l.Id));
            }

            return leads;
        }
    }
}
