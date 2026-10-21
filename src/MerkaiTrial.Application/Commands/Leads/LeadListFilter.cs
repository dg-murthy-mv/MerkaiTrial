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
                var s = search.Trim().ToLower();

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
                        l.FullName.ToLower().Contains(s) ||
                        (l.Email != null && l.Email.ToLower().Contains(s)) ||
                        (l.Phone != null && l.Phone.ToLower().Contains(s)) ||
                        (digits != "" && l.Phone != null &&
                         l.Phone.Replace(" ", "").Replace("-", "").Replace(".", "")
                                .Replace("(", "").Replace(")", "").Replace("+", "").Contains(digits)) ||
                        (l.CompanyName != null && l.CompanyName.ToLower().Contains(s)));
                }
                else
                {
                    leads = leads.Where(l =>
                        l.FullName.ToLower().Contains(s) ||
                        (l.Email != null && l.Email.ToLower().Contains(s)) ||
                        (l.Phone != null && l.Phone.ToLower().Contains(s)) ||
                        (digits != "" && l.Phone != null &&
                         l.Phone.Replace(" ", "").Replace("-", "").Replace(".", "")
                                .Replace("(", "").Replace(")", "").Replace("+", "").Contains(digits)) ||
                        (l.CompanyName != null && l.CompanyName.ToLower().Contains(s)) ||
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
