// =====================================================================
// FILE: MerkaiTrial.Application/Commands/CustomFields/CustomFieldValueWriter.cs
//
// NEW FILE (075 — custom fields, Round A).
//
// Reading and writing the VALUES of custom fields on one record. Called
// from inside the Contact handlers (and, from round C, the Company ones)
// so the values are saved in the SAME SaveChangesAsync as the record:
// a contact that saved while its fields failed, or the reverse, is not
// a state anybody can reason about.
//
// A static helper rather than an injected service, for the same reason
// ProductCategoryOps is one: it is a few operations on a DbContext the
// caller already holds, and an interface would only be a registration
// line to forget.
//
// ─────────────────────────────────────────────────────────────────────
// THE NULL-VS-EMPTY RULE (067, 069, 070, 071) APPLIES HERE
//
//   submitted == null   the caller said nothing about custom fields.
//                       Touch nothing, check nothing. This is what every
//                       existing caller of CreateContactHandler /
//                       UpdateContactHandler sends — lead conversion,
//                       imports, anything written before 075 — and it
//                       must keep working exactly as before.
//
//   submitted == {...}  the caller is stating values. A key that is
//                       present with an empty value CLEARS that field.
//                       A key that is ABSENT is left alone: the forms
//                       only render active fields, so a retired field's
//                       value must survive an ordinary edit.
//
//   Required fields are enforced only when a map is sent, and only on
//   ACTIVE fields: the stored value counts, so a required field already
//   filled in does not have to be re-sent.
// ─────────────────────────────────────────────────────────────────────
// =====================================================================

using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.CustomFields
{
    /// <summary>
    /// One or more custom field values were refused. An
    /// InvalidOperationException, so the controllers' existing
    /// "400 with { error }" path carries the sentence to the page, and
    /// IApiService turns it back into an InvalidOperationException there.
    /// </summary>
    public class CustomFieldValidationException : InvalidOperationException
    {
        public IReadOnlyList<string> Errors { get; }

        public CustomFieldValidationException(IReadOnlyList<string> errors)
            : base(string.Join(" ", errors))
        {
            Errors = errors;
        }
    }

    public static class CustomFieldValueWriter
    {
        /// <summary>
        /// Stage the custom field values for one record. ADDS, CHANGES AND
        /// REMOVES ROWS; DOES NOT SAVE — the caller's SaveChangesAsync
        /// commits them together with the record.
        ///
        /// Throws CustomFieldValidationException, listing every problem at
        /// once rather than the first, so a form with three mistakes needs
        /// one correction round and not three.
        /// </summary>
        public static async Task ApplyAsync(
            FlowDbContext db,
            Guid tenantId,
            string entityType,
            Guid entityId,
            IReadOnlyDictionary<Guid, string?>? submitted,
            string? actor,
            bool isNew,
            CancellationToken ct)
        {
            // Null: the caller said nothing. See the header.
            if (submitted is null) return;

            // Every definition, active or not, for this workspace and this
            // kind of record. The tenant predicate is explicit as well as
            // in the query filter, for the reason ProductCategoryOps gives:
            // the parameter is the tenant the caller meant.
            var defs = await db.CustomFieldDefinitions
                .AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.EntityType == entityType && !d.IsDeleted)
                .ToListAsync(ct);

            var byId = defs.ToDictionary(d => d.Id);

            // ── OWNERSHIP ────────────────────────────────────────────
            // A key that is not one of THIS workspace's fields for THIS
            // entity type is refused, not ignored. It is either a stale form
            // (a field deleted in another tab) or a hand-made request
            // carrying another workspace's field id — and silently ignoring
            // the second would hide a probe.
            if (submitted.Keys.Any(k => !byId.ContainsKey(k)))
                throw new CustomFieldValidationException(new[]
                {
                    "One of the custom fields on this form no longer exists. Reload the page and try again."
                });

            var existing = isNew
                ? new List<CustomFieldValue>()
                : await db.CustomFieldValues
                    .Where(v => v.TenantId == tenantId && v.EntityId == entityId)
                    .ToListAsync(ct);

            var existingByDef = existing
                .Where(v => byId.ContainsKey(v.DefinitionId))
                .ToDictionary(v => v.DefinitionId);

            var errors = new List<string>();
            var now    = DateTime.UtcNow;

            // What each ACTIVE field will hold after this save, for the
            // required check below: true = has a value.
            var finalHasValue = new Dictionary<Guid, bool>();
            foreach (var d in defs)
                finalHasValue[d.Id] = existingByDef.ContainsKey(d.Id);

            foreach (var (defId, raw) in submitted)
            {
                var def = byId[defId];

                // A field switched off after the form was opened: leave its
                // stored value exactly as it is. Writing to it would let a
                // stale form overwrite data the Settings page has retired.
                if (!def.IsActive) continue;

                existingByDef.TryGetValue(defId, out var row);

                var options = def.FieldType == CustomFieldTypes.Dropdown
                    ? CustomFieldOptionsJson.Parse(def.OptionsJson)
                    : new List<CustomFieldOptionDto>();

                var parsed = CustomFieldValueRules.Parse(
                    def.FieldType, def.Label, def.DecimalPlaces, options, raw,
                    currentKey: row?.TextValue);

                if (parsed.Error is not null)
                {
                    errors.Add(parsed.Error);
                    continue;
                }

                if (parsed.IsEmpty)
                {
                    if (row is not null) db.CustomFieldValues.Remove(row);
                    finalHasValue[defId] = false;
                    continue;
                }

                if (row is null)
                {
                    row = new CustomFieldValue
                    {
                        Id           = Guid.NewGuid(),
                        TenantId     = tenantId,
                        DefinitionId = defId,
                        EntityId     = entityId,
                        CreatedAtUtc = now
                    };
                    db.CustomFieldValues.Add(row);
                }
                else
                {
                    row.UpdatedAtUtc = now;
                }

                // Exactly one typed column holds the value. Clearing the
                // other three means a row can never carry two answers.
                row.TextValue   = parsed.Text;
                row.NumberValue = parsed.Number;
                row.DateValue   = parsed.Date;
                row.BoolValue   = parsed.Bool;
                row.UpdatedBy   = Truncate(actor, 64);

                finalHasValue[defId] = true;
            }

            // ── REQUIRED ─────────────────────────────────────────────
            // Active, required, not a checkbox, and empty after this save.
            foreach (var d in defs.OrderBy(d => d.SortOrder))
            {
                if (!d.IsActive || !d.IsRequired) continue;
                if (!CustomFieldTypes.SupportsRequired(d.FieldType)) continue;
                if (!finalHasValue[d.Id]) errors.Add($"{d.Label} is required.");
            }

            if (errors.Count > 0)
                throw new CustomFieldValidationException(errors.Distinct().ToList());
        }

        /// <summary>
        /// Every stored value on one record, in the wire shape, keyed by
        /// field id. Includes values on retired (switched-off) fields — the
        /// detail page shows them, marked as retired — but not on deleted
        /// ones, which by construction have none.
        /// </summary>
        public static async Task<Dictionary<Guid, string>> ReadAsync(
            FlowDbContext db, Guid tenantId, string entityType, Guid entityId, CancellationToken ct)
        {
            var rows = await db.CustomFieldValues
                .AsNoTracking()
                .Where(v => v.TenantId == tenantId && v.EntityId == entityId)
                .Join(db.CustomFieldDefinitions.Where(d => d.TenantId == tenantId &&
                                                           d.EntityType == entityType &&
                                                           !d.IsDeleted),
                      v => v.DefinitionId,
                      d => d.Id,
                      (v, d) => new
                      {
                          v.DefinitionId,
                          d.FieldType,
                          v.TextValue,
                          v.NumberValue,
                          v.DateValue,
                          v.BoolValue
                      })
                .ToListAsync(ct);

            var map = new Dictionary<Guid, string>();
            foreach (var r in rows)
            {
                var wire = CustomFieldValueRules.ToWire(r.FieldType, r.TextValue, r.NumberValue, r.DateValue, r.BoolValue);
                if (!string.IsNullOrEmpty(wire)) map[r.DefinitionId] = wire;
            }
            return map;
        }

        private static string? Truncate(string? value, int max)
            => value is null || value.Length <= max ? value : value[..max];
    }
}
