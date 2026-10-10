// =====================================================================
// FILE: MerkaiTrial.Application/Commands/CustomFields/CustomFieldListQuery.cs
//
// NEW FILE (077). Custom fields on a LIST query — search, filters and the
// column values — written ONCE and used by every list handler:
//
//   GetContactsHandler (Contacts list)        moved here from 076
//   GetDealsHandler    (Pipeline board/table) new in 077
//   … and Companies / Leads in later rounds.
//
// Round 076 wrote this logic inside GetContactsHandler. A second copy in
// GetDealsHandler would be the 029 quote-editor lesson again: two copies
// of "what does a checkbox 'No' filter mean" that drift until one is
// wrong. So it lives here, and both handlers call it.
//
// ─────────────────────────────────────────────────────────────────────
// HOW IT PLUGS INTO ANY LIST
//
//   Each method returns an IQueryable<Guid> of matching ENTITY ids (a
//   subquery against CustomFieldValues), never a filtered list of rows.
//   The caller writes
//
//       var ids = clause.EntityIds;
//       query = query.Where(c => ids.Contains(c.Id));
//
//   and EF turns it into `c.Id IN (SELECT EntityId FROM CustomFieldValues
//   WHERE …)`. That works whatever shape the caller's query has — a plain
//   Contacts query, or the Deals query that joins Contacts and Companies
//   into an anonymous type — which is why this is not an extension method
//   on IQueryable<Contact>.
//
//   The id is copied into a LOCAL before the lambda on purpose. EF inlines
//   a captured IQueryable as a subquery; a local is the documented way.
//
// TENANT SCOPE is on every subquery (v.TenantId == tenantId) as well as in
// the global filter. ENTITY TYPE is enforced by the definition ids: only
// this entity type's fields are ever looked up, so a Deal filter can never
// match a Contact's values, although both live in one table.
//
// WHAT IS DROPPED rather than refused (unchanged from 076): a filter on an
// unknown, deleted or switched-off field, or with an operator that does
// not fit the field's type, or a value that cannot be read. It is a
// filter, not a write — a bookmark mentioning a deleted field should show
// the list, not an error.
// ─────────────────────────────────────────────────────────────────────
// =====================================================================

using System.Globalization;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.CustomFields
{
    /// <summary>One filter, as a set of entity ids to keep — or to exclude.</summary>
    public sealed record CustomFieldFilterClause(IQueryable<Guid> EntityIds, bool Exclude);

    public static class CustomFieldListQuery
    {
        /// <summary>The ACTIVE fields of one entity type, in display order. One small query.</summary>
        public static Task<List<CustomFieldDefinition>> ActiveDefinitionsAsync(
            FlowDbContext db, Guid tenantId, string entityType, CancellationToken ct = default)
            => db.CustomFieldDefinitions
                .AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.EntityType == entityType &&
                            !d.IsDeleted && d.IsActive)
                .OrderBy(d => d.SortOrder)
                .ToListAsync(ct);

        /// <summary>
        /// Records whose custom TEXT values contain the search, or whose
        /// DROPDOWN choice's label contains it ("Gold" finds every record
        /// whose tier is Gold, although the row stores a key).
        ///
        /// Null when no field could possibly match — then the caller keeps
        /// its plain search and adds no subquery at all.
        ///
        /// <paramref name="search"/> must already be trimmed and lower-cased,
        /// the same value the caller compares its own columns against.
        /// </summary>
        public static IQueryable<Guid>? SearchMatches(
            FlowDbContext db, Guid tenantId, IReadOnlyList<CustomFieldDefinition> defs, string search)
        {
            if (string.IsNullOrEmpty(search)) return null;

            var textDefIds = defs
                .Where(d => d.FieldType == CustomFieldTypes.Text)
                .Select(d => d.Id)
                .ToList();

            var dropdownDefIds = new List<Guid>();
            var dropdownKeys   = new List<string>();
            foreach (var d in defs.Where(d => d.FieldType == CustomFieldTypes.Dropdown))
            {
                var keys = CustomFieldOptionsJson.Parse(d.OptionsJson)
                    .Where(o => o.Label.ToLower().Contains(search))
                    .Select(o => o.Key)
                    .ToList();
                if (keys.Count == 0) continue;
                dropdownDefIds.Add(d.Id);
                dropdownKeys.AddRange(keys);
            }

            if (textDefIds.Count == 0 && dropdownKeys.Count == 0) return null;

            return db.CustomFieldValues
                .Where(v => v.TenantId == tenantId && v.TextValue != null &&
                            ((textDefIds.Contains(v.DefinitionId) && v.TextValue.ToLower().Contains(search)) ||
                             (dropdownDefIds.Contains(v.DefinitionId) && dropdownKeys.Contains(v.TextValue))))
                .Select(v => v.EntityId);
        }

        /// <summary>
        /// One clause per usable filter, ANDed by the caller. A checkbox "No"
        /// is an EXCLUDE clause: an unticked box has NO ROW (see
        /// CustomFieldValueWriter), so "No" means "has no ticked row" — which
        /// also counts records saved before the field existed, correctly.
        /// </summary>
        public static List<CustomFieldFilterClause> FilterClauses(
            FlowDbContext db, Guid tenantId,
            IReadOnlyList<CustomFieldDefinition> defs,
            IEnumerable<CustomFieldFilter>? filters)
        {
            var clauses = new List<CustomFieldFilterClause>();
            if (filters is null) return clauses;

            var byId   = defs.ToDictionary(d => d.Id);
            var values = db.CustomFieldValues.Where(v => v.TenantId == tenantId);

            foreach (var f in filters.Take(CustomFieldLimits.MaxFiltersPerQuery))
            {
                if (!byId.TryGetValue(f.FieldId, out var def)) continue;

                var op = f.Op?.Trim().ToLowerInvariant() ?? string.Empty;
                if (!CustomFieldFilterOps.Applies(def.FieldType, op)) continue;

                var defId = def.Id;
                var raw   = f.Value?.Trim() ?? string.Empty;
                if (raw.Length == 0) continue;

                switch (def.FieldType)
                {
                    case CustomFieldTypes.Text:
                    {
                        var needle = raw.ToLower();
                        clauses.Add(new(values
                            .Where(v => v.DefinitionId == defId && v.TextValue != null &&
                                        v.TextValue.ToLower().Contains(needle))
                            .Select(v => v.EntityId), false));
                        break;
                    }

                    case CustomFieldTypes.Dropdown:
                    {
                        var key = raw;
                        clauses.Add(new(values
                            .Where(v => v.DefinitionId == defId && v.TextValue == key)
                            .Select(v => v.EntityId), false));
                        break;
                    }

                    case CustomFieldTypes.Number:
                    {
                        if (!decimal.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                                CultureInfo.InvariantCulture, out var n)) break;

                        clauses.Add(new(op == CustomFieldFilterOps.AtLeast
                            ? values.Where(v => v.DefinitionId == defId && v.NumberValue >= n).Select(v => v.EntityId)
                            : values.Where(v => v.DefinitionId == defId && v.NumberValue <= n).Select(v => v.EntityId),
                            false));
                        break;
                    }

                    case CustomFieldTypes.Date:
                    {
                        if (!DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out var d)) break;

                        clauses.Add(new(op == CustomFieldFilterOps.AtLeast
                            ? values.Where(v => v.DefinitionId == defId && v.DateValue >= d).Select(v => v.EntityId)
                            : values.Where(v => v.DefinitionId == defId && v.DateValue <= d).Select(v => v.EntityId),
                            false));
                        break;
                    }

                    case CustomFieldTypes.Checkbox:
                    {
                        var ticked = values
                            .Where(v => v.DefinitionId == defId && v.BoolValue == true)
                            .Select(v => v.EntityId);

                        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
                            clauses.Add(new(ticked, false));
                        else if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase))
                            clauses.Add(new(ticked, true));
                        break;
                    }
                }
            }

            return clauses;
        }

        /// <summary>
        /// The values of the list-column fields (ShowInList) for the records
        /// on THIS page only, in the wire shape: entity id → field id → value.
        /// One query. Empty when there are no columns or no rows.
        /// </summary>
        public static async Task<Dictionary<Guid, Dictionary<Guid, string>>> ListValuesAsync(
            FlowDbContext db, Guid tenantId,
            IReadOnlyList<CustomFieldDefinition> defs,
            IReadOnlyCollection<Guid> entityIds,
            CancellationToken ct = default)
        {
            var result = new Dictionary<Guid, Dictionary<Guid, string>>();

            var columns = defs.Where(d => d.ShowInList).ToDictionary(d => d.Id);
            if (columns.Count == 0 || entityIds.Count == 0) return result;

            var ids    = entityIds.ToList();
            var defIds = columns.Keys.ToList();

            var rows = await db.CustomFieldValues
                .AsNoTracking()
                .Where(v => v.TenantId == tenantId && ids.Contains(v.EntityId) && defIds.Contains(v.DefinitionId))
                .Select(v => new { v.EntityId, v.DefinitionId, v.TextValue, v.NumberValue, v.DateValue, v.BoolValue })
                .ToListAsync(ct);

            foreach (var r in rows)
            {
                var wire = CustomFieldValueRules.ToWire(
                    columns[r.DefinitionId].FieldType, r.TextValue, r.NumberValue, r.DateValue, r.BoolValue);
                if (string.IsNullOrEmpty(wire)) continue;

                if (!result.TryGetValue(r.EntityId, out var map))
                    result[r.EntityId] = map = new Dictionary<Guid, string>();
                map[r.DefinitionId] = wire;
            }

            return result;
        }
    }
}
