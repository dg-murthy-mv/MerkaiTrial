// =====================================================================
// FILE: MerkaiTrial.Application/Commands/CustomFields/LeadFieldMapper.cs
//
// NEW FILE (079). Copies a lead's custom field values onto the contact
// and the deal its conversion creates, following the mapping set on each
// Lead field in Settings → Custom Fields → Leads.
//
// CALLED FROM BOTH CONVERSION PATHS, inside their transaction and BEFORE
// their final SaveChangesAsync, so the copied values commit with the
// contact and the deal or not at all:
//
//   ConvertLeadToDealHandler   the page's "Convert to Deal" (contact + deal)
//   ConvertLeadHandler         POST /api/leads/{id}/convert (contact only)
//
// ─────────────────────────────────────────────────────────────────────
// THE RULES
//
// 1. ONLY A MAPPED LEAD FIELD THAT HAS A VALUE is copied. A field with no
//    mapping, or no value on this lead, does nothing.
//
// 2. THE TARGET MUST BE LIVE AND SWITCHED ON. A Contact or Deal field
//    that has been switched off is skipped: a value the forms cannot show
//    or edit is a value nobody knows is there. Switching it back on
//    applies to the NEXT conversion — nothing is back-filled.
//
// 3. SAME TYPE ONLY (the Settings page and CustomFieldHandlers refuse
//    anything else, and this file checks again in case a type was changed
//    by hand in the database). Specifically:
//      text / date / checkbox   copied as they are
//      number                   rounded to the TARGET field's decimal
//                               places, so the contact's own form never
//                               refuses the value on its next edit
//      dropdown                 matched by CHOICE NAME (case-insensitive),
//                               because the two fields' option keys are
//                               different. A choice the target does not
//                               have — or has only retired — is skipped.
//
// 4. AN EXISTING CONTACT IS FILLED IN, NEVER OVERWRITTEN. Conversion can
//    reuse a contact that already exists (same email). Whatever that
//    contact already holds was typed by somebody on purpose; only its
//    EMPTY fields are filled from the lead. A brand-new contact and the
//    new deal have nothing to protect, so every mapped value lands.
//
// 5. REQUIRED-FIELD RULES ARE NOT ENFORCED HERE. This is a copy, not a
//    form. A required Deal field the lead could not supply is asked for
//    the next time somebody edits the deal — the same as for a deal that
//    existed before the field was made required.
//
// Values are added straight to CustomFieldValues rather than through
// CustomFieldValueWriter.ApplyAsync, deliberately: ApplyAsync validates
// a whole FORM (required fields, the full submitted map), and refusing a
// conversion because a deal field nobody mapped is required would be
// the wrong answer to the wrong question.
// =====================================================================

using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.CustomFields
{
    /// <summary>How many values a conversion carried across.</summary>
    public sealed record LeadFieldCopyResult(int ToContact, int ToDeal)
    {
        public static readonly LeadFieldCopyResult None = new(0, 0);
        public int Total => ToContact + ToDeal;
    }

    public static class LeadFieldMapper
    {
        /// <summary>
        /// Stages the copied values on <paramref name="db"/>. Does NOT save —
        /// the caller's SaveChangesAsync commits them with everything else.
        /// Pass null for <paramref name="contactId"/> or
        /// <paramref name="dealId"/> to skip that side.
        /// </summary>
        public static async Task<LeadFieldCopyResult> CopyAsync(
            FlowDbContext db,
            Guid tenantId,
            Guid leadId,
            Guid? contactId,
            bool contactIsNew,
            Guid? dealId,
            string? actor,
            CancellationToken ct = default)
        {
            // ── 1. Mapped Lead fields ────────────────────────────────────
            var leadDefs = await db.CustomFieldDefinitions
                .AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.EntityType == CustomFieldEntityTypes.Lead && !d.IsDeleted &&
                            (d.MapToContactFieldId != null || d.MapToDealFieldId != null))
                .ToListAsync(ct);

            if (leadDefs.Count == 0) return LeadFieldCopyResult.None;

            var leadDefIds = leadDefs.Select(d => d.Id).ToList();

            // ── 2. This lead's values for them ───────────────────────────
            var leadValues = await db.CustomFieldValues
                .AsNoTracking()
                .Where(v => v.TenantId == tenantId && v.EntityId == leadId && leadDefIds.Contains(v.DefinitionId))
                .ToListAsync(ct);

            if (leadValues.Count == 0) return LeadFieldCopyResult.None;

            var valueByDef = leadValues
                .GroupBy(v => v.DefinitionId)
                .ToDictionary(g => g.Key, g => g.First());

            // ── 3. The targets: live and switched on (rule 2) ────────────
            var targetIds = leadDefs
                .SelectMany(d => new[] { d.MapToContactFieldId, d.MapToDealFieldId })
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();

            var targets = await db.CustomFieldDefinitions
                .AsNoTracking()
                .Where(d => d.TenantId == tenantId && targetIds.Contains(d.Id) && !d.IsDeleted && d.IsActive)
                .ToDictionaryAsync(d => d.Id, ct);

            // ── 4. What an EXISTING contact already holds (rule 4) ───────
            var contactHas = new HashSet<Guid>();
            if (contactId.HasValue && !contactIsNew)
            {
                var contactTargetIds = targets.Values
                    .Where(t => t.EntityType == CustomFieldEntityTypes.Contact)
                    .Select(t => t.Id)
                    .ToList();

                if (contactTargetIds.Count > 0)
                {
                    var held = await db.CustomFieldValues
                        .AsNoTracking()
                        .Where(v => v.TenantId == tenantId && v.EntityId == contactId.Value &&
                                    contactTargetIds.Contains(v.DefinitionId))
                        .Select(v => v.DefinitionId)
                        .ToListAsync(ct);
                    contactHas = new HashSet<Guid>(held);
                }
            }

            // ── 5. Copy ──────────────────────────────────────────────────
            var now       = DateTime.UtcNow;
            var who       = string.IsNullOrWhiteSpace(actor) ? null : (actor.Length > 64 ? actor[..64] : actor);
            var written   = new HashSet<(Guid Def, Guid Entity)>();
            var toContact = 0;
            var toDeal    = 0;

            foreach (var source in leadDefs)
            {
                if (!valueByDef.TryGetValue(source.Id, out var value)) continue;

                if (contactId.HasValue &&
                    source.MapToContactFieldId is { } cId &&
                    targets.TryGetValue(cId, out var cTarget) &&
                    cTarget.EntityType == CustomFieldEntityTypes.Contact &&
                    !contactHas.Contains(cTarget.Id) &&
                    written.Add((cTarget.Id, contactId.Value)) &&
                    TryConvert(source, value, cTarget, out var cRow))
                {
                    cRow.Id = Guid.NewGuid();
                    cRow.TenantId = tenantId;
                    cRow.DefinitionId = cTarget.Id;
                    cRow.EntityId = contactId.Value;
                    cRow.CreatedAtUtc = now;
                    cRow.UpdatedBy = who;
                    db.CustomFieldValues.Add(cRow);
                    toContact++;
                }

                if (dealId.HasValue &&
                    source.MapToDealFieldId is { } dId &&
                    targets.TryGetValue(dId, out var dTarget) &&
                    dTarget.EntityType == CustomFieldEntityTypes.Deal &&
                    written.Add((dTarget.Id, dealId.Value)) &&
                    TryConvert(source, value, dTarget, out var dRow))
                {
                    dRow.Id = Guid.NewGuid();
                    dRow.TenantId = tenantId;
                    dRow.DefinitionId = dTarget.Id;
                    dRow.EntityId = dealId.Value;
                    dRow.CreatedAtUtc = now;
                    dRow.UpdatedBy = who;
                    db.CustomFieldValues.Add(dRow);
                    toDeal++;
                }
            }

            return new LeadFieldCopyResult(toContact, toDeal);
        }

        /// <summary>
        /// The target field's value, built from the lead's. False when it
        /// cannot be carried across (rule 3) — the caller then skips it.
        /// </summary>
        private static bool TryConvert(
            CustomFieldDefinition source, CustomFieldValue value, CustomFieldDefinition target,
            out CustomFieldValue row)
        {
            row = new CustomFieldValue();

            if (source.FieldType != target.FieldType) return false;

            switch (target.FieldType)
            {
                case CustomFieldTypes.Text:
                    if (string.IsNullOrWhiteSpace(value.TextValue)) return false;
                    row.TextValue = value.TextValue.Length > CustomFieldLimits.TextValueMaxLength
                        ? value.TextValue[..CustomFieldLimits.TextValueMaxLength]
                        : value.TextValue;
                    return true;

                case CustomFieldTypes.Number:
                    if (value.NumberValue is not { } n) return false;
                    row.NumberValue = decimal.Round(n, Math.Clamp(target.DecimalPlaces, 0, CustomFieldLimits.MaxDecimalPlaces),
                                                    MidpointRounding.AwayFromZero);
                    return true;

                case CustomFieldTypes.Date:
                    if (value.DateValue is not { } d) return false;
                    row.DateValue = d;
                    return true;

                case CustomFieldTypes.Checkbox:
                    if (value.BoolValue != true) return false;
                    row.BoolValue = true;
                    return true;

                case CustomFieldTypes.Dropdown:
                {
                    if (string.IsNullOrEmpty(value.TextValue)) return false;

                    var label = CustomFieldOptionsJson.Parse(source.OptionsJson)
                        .FirstOrDefault(o => o.Key == value.TextValue)?.Label;
                    if (string.IsNullOrWhiteSpace(label)) return false;

                    var match = CustomFieldOptionsJson.Parse(target.OptionsJson)
                        .FirstOrDefault(o => o.IsActive &&
                                             string.Equals(o.Label?.Trim(), label.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (match is null || string.IsNullOrEmpty(match.Key)) return false;

                    row.TextValue = match.Key;
                    return true;
                }

                default:
                    return false;
            }
        }
    }
}
