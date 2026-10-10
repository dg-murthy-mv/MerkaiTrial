// =====================================================================
// FILE: MerkaiTrial.Application/Commands/CustomFields/CustomFieldHandlers.cs
//
// NEW FILE (075 — custom fields, Round A). Read, create, edit, delete and
// reorder custom field DEFINITIONS. The values themselves are written by
// CustomFieldValueWriter, from inside the record's own handler.
//
// 076 — ShowInList (a column on the list page), capped at
//   CustomFieldLimits.MaxListColumns ACTIVE list columns per entity type.
//   A switched-off field keeps its flag but does not count and is not
//   shown, so switching it back on can be refused if the slots have been
//   used meanwhile — with a sentence saying which.
//
// 077 — LiveEntityIds knows Deals, so "filled in on N deals" and the
//   delete guard ignore values left on soft-deleted deals.
//
// 079 — LEADS, AND THE LEAD → CONTACT / DEAL MAPPING
//   • LiveEntityIds knows Leads.
//   • A Lead field may name a Contact field and a Deal field that receive
//     its value when the lead is converted (MapToContactFieldId /
//     MapToDealFieldId). ValidateMappingAsync checks, on every save:
//       – the target is a live field of THIS workspace, on contacts
//         (or deals), of the SAME type — a date cannot land in a number;
//       – no other Lead field already feeds that target, because two
//         lead values racing for one contact field would make the result
//         depend on the order they happened to be copied in.
//     On any other kind of field the mapping is forced to null.
//   • Deleting a Contact or Deal field CLEARS every Lead field's pointer
//     to it, in the same save — the columns are not foreign keys, so a
//     stale pointer would otherwise linger and confuse the Settings page.
//     Switching a target OFF leaves the pointer: conversion simply skips
//     a switched-off target, and switching it back on restores the copy.
//
// Registered automatically: the Scrutor scan in ApiServiceRegistration
// picks up every ICommandHandler in this assembly.
//
// ─────────────────────────────────────────────────────────────────────
// THE RULES THAT MAKE THIS FILE LONGER THAN IT LOOKS
//
// 1. A FIELD'S TYPE IS FIXED ONCE IT EXISTS. Turning "number" into
//    "date" would leave every stored NumberValue unreadable. Update
//    ignores FieldType; the Settings page shows it read-only.
//
// 2. A FIELD WITH VALUES IS RETIRED, NOT DELETED. Delete is refused while
//    any live record carries a value, with a sentence pointing at the
//    switch. Values on soft-deleted records do not count and are removed
//    with the field — otherwise one deleted contact would block the
//    delete for ever, with "used on 0 contacts" on screen.
//
// 3. DROPDOWN OPTIONS HAVE STABLE KEYS. The value stores the key, so a
//    rename is free. An option that disappears from the submitted list is
//    dropped — unless a live record uses it, in which case it is kept and
//    marked retired, so nobody's saved choice turns into "(unknown)".
//    Keys the client sends that the field never had are NOT trusted;
//    they get a fresh key, the same as a brand-new option.
//
// 4. OWNERSHIP IS CHECKED HERE, explicitly, on every write — not left to
//    the query filter. These tables are strict, so the filter would also
//    refuse another workspace's row, but the handlers take tenantId as a
//    parameter and say so in their own predicates, the way
//    ProductCategoryHandlers do.
// ─────────────────────────────────────────────────────────────────────
// =====================================================================

using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.CustomFields
{
    // =================================================================
    // SHARED PLUMBING
    // =================================================================

    internal static class CustomFieldOps
    {
        /// <summary>
        /// Ids of the LIVE records of one entity type in this workspace.
        /// Used so "used on N contacts" and the delete guard ignore values
        /// sitting on soft-deleted contacts. Round C adds a line per type.
        /// </summary>
        public static IQueryable<Guid> LiveEntityIds(FlowDbContext db, Guid tenantId, string entityType)
            => entityType switch
            {
                CustomFieldEntityTypes.Contact =>
                    db.Contacts.Where(c => c.TenantId == tenantId && !c.IsDeleted).Select(c => c.Id),
                CustomFieldEntityTypes.Deal =>
                    db.Deals.Where(d => d.TenantId == tenantId && !d.IsDeleted).Select(d => d.Id),
                CustomFieldEntityTypes.Company =>
                    db.Companies.Where(c => c.TenantId == tenantId && !c.IsDeleted).Select(c => c.Id),
                CustomFieldEntityTypes.Lead =>                                               // 079
                    db.Leads.Where(l => l.TenantId == tenantId && !l.IsDeleted).Select(l => l.Id),
                _ => throw new ArgumentOutOfRangeException(nameof(entityType), entityType, "Unknown entity type")
            };

        /// <summary>Live value counts per definition, in one grouped query.</summary>
        public static async Task<Dictionary<Guid, int>> ValueCountsAsync(
            FlowDbContext db, Guid tenantId, string entityType, CancellationToken ct)
        {
            var live = LiveEntityIds(db, tenantId, entityType);

            var rows = await db.CustomFieldValues
                .AsNoTracking()
                .Where(v => v.TenantId == tenantId && live.Contains(v.EntityId))
                .GroupBy(v => v.DefinitionId)
                .Select(g => new { DefinitionId = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            return rows.ToDictionary(r => r.DefinitionId, r => r.Count);
        }

        /// <summary>Dropdown option keys in use by live records of this field.</summary>
        public static async Task<HashSet<string>> KeysInUseAsync(
            FlowDbContext db, Guid tenantId, CustomFieldDefinition def, CancellationToken ct)
        {
            var live = LiveEntityIds(db, tenantId, def.EntityType);

            var keys = await db.CustomFieldValues
                .AsNoTracking()
                .Where(v => v.TenantId == tenantId && v.DefinitionId == def.Id &&
                            v.TextValue != null && live.Contains(v.EntityId))
                .Select(v => v.TextValue!)
                .Distinct()
                .ToListAsync(ct);

            return new HashSet<string>(keys, StringComparer.Ordinal);
        }

        public static CustomFieldDefinitionDto ToDto(CustomFieldDefinition d, int valueCount) => new()
        {
            Id            = d.Id,
            EntityType    = d.EntityType,
            Label         = d.Label,
            FieldType     = d.FieldType,
            HelpText      = d.HelpText,
            IsRequired    = d.IsRequired,
            IsActive      = d.IsActive,
            SortOrder     = d.SortOrder,
            ShowInList    = d.ShowInList,
            DecimalPlaces = d.DecimalPlaces,
            MapToContactFieldId = d.MapToContactFieldId,     // 079
            MapToDealFieldId    = d.MapToDealFieldId,        // 079
            Options       = d.FieldType == CustomFieldTypes.Dropdown
                                ? CustomFieldOptionsJson.Parse(d.OptionsJson)
                                : new List<CustomFieldOptionDto>(),
            ValueCount    = valueCount
        };

        /// <summary>Label and help text, trimmed and checked. Throws InvalidOperationException.</summary>
        public static (string Label, string? HelpText) ValidateText(string? label, string? helpText)
        {
            var cleanLabel = label?.Trim() ?? string.Empty;
            if (cleanLabel.Length == 0)
                throw new InvalidOperationException("A field needs a name.");
            if (cleanLabel.Length > CustomFieldLimits.LabelMaxLength)
                throw new InvalidOperationException(
                    $"A field name cannot be longer than {CustomFieldLimits.LabelMaxLength} characters.");

            var cleanHelp = string.IsNullOrWhiteSpace(helpText) ? null : helpText.Trim();
            if (cleanHelp is not null && cleanHelp.Length > CustomFieldLimits.HelpTextMaxLength)
                throw new InvalidOperationException(
                    $"The hint cannot be longer than {CustomFieldLimits.HelpTextMaxLength} characters.");

            return (cleanLabel, cleanHelp);
        }

        /// <summary>
        /// The final option list for a dropdown, given what was submitted and
        /// what the field had before. See note 3 in the file header.
        /// </summary>
        public static List<CustomFieldOptionDto> MergeOptions(
            IEnumerable<CustomFieldOptionDto>? submitted,
            IReadOnlyList<CustomFieldOptionDto> previous,
            ISet<string> keysInUse)
        {
            var previousByKey = previous
                .Where(o => !string.IsNullOrEmpty(o.Key))
                .GroupBy(o => o.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var result = new List<CustomFieldOptionDto>();
            var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenKeys   = new HashSet<string>(StringComparer.Ordinal);

            foreach (var o in submitted ?? Enumerable.Empty<CustomFieldOptionDto>())
            {
                var label = o.Label?.Trim() ?? string.Empty;

                // A blank row from the editor's "Add option" that was never
                // filled in. Not an error — just nothing.
                if (label.Length == 0 && string.IsNullOrEmpty(o.Key)) continue;

                if (label.Length == 0)
                    throw new InvalidOperationException("Every option needs a name.");
                if (label.Length > CustomFieldLimits.OptionLabelMaxLength)
                    throw new InvalidOperationException(
                        $"An option cannot be longer than {CustomFieldLimits.OptionLabelMaxLength} characters.");
                if (!seenLabels.Add(label))
                    throw new InvalidOperationException($"\"{label}\" appears twice in the options.");

                // Only a key this field really had is kept. Anything else —
                // empty, made up, or duplicated — gets a fresh one.
                var key = !string.IsNullOrEmpty(o.Key) && previousByKey.ContainsKey(o.Key) && !seenKeys.Contains(o.Key)
                    ? o.Key
                    : CustomFieldOptionsJson.NewKey();

                seenKeys.Add(key);
                result.Add(new CustomFieldOptionDto { Key = key, Label = label, IsActive = o.IsActive });
            }

            // Options that were removed from the list but are still in use
            // on a live record: kept, retired, at the end.
            foreach (var old in previous)
            {
                if (string.IsNullOrEmpty(old.Key) || seenKeys.Contains(old.Key)) continue;
                if (!keysInUse.Contains(old.Key)) continue;

                if (!seenLabels.Add(old.Label))
                    throw new InvalidOperationException(
                        $"\"{old.Label}\" is still chosen on some records, so it cannot be removed while another " +
                        "option has the same name. Rename one of them.");

                seenKeys.Add(old.Key);
                result.Add(new CustomFieldOptionDto { Key = old.Key, Label = old.Label, IsActive = false });
            }

            if (result.Count > CustomFieldLimits.MaxOptions)
                throw new InvalidOperationException(
                    $"A dropdown can have at most {CustomFieldLimits.MaxOptions} options.");

            if (!result.Any(o => o.IsActive))
                throw new InvalidOperationException("A dropdown needs at least one option that is switched on.");

            return result;
        }

        /// <summary>Who did it, as stored in CreatedBy/UpdatedBy (64 characters).</summary>
        /// <summary>
        /// 076. Refuse a save that would put more than MaxListColumns ACTIVE
        /// fields on the list. Only checked when the field will itself be an
        /// active list column after the save.
        /// </summary>
        public static async Task EnsureListColumnSlotAsync(
            FlowDbContext db, Guid tenantId, string entityType, Guid? exceptId,
            bool willBeActive, bool willShowInList, CancellationToken ct)
        {
            if (!willBeActive || !willShowInList) return;

            var used = await db.CustomFieldDefinitions
                .Where(d => d.TenantId == tenantId && d.EntityType == entityType && !d.IsDeleted &&
                            d.IsActive && d.ShowInList && (exceptId == null || d.Id != exceptId))
                .Select(d => d.Label)
                .ToListAsync(ct);

            if (used.Count >= CustomFieldLimits.MaxListColumns)
                throw new InvalidOperationException(
                    $"The list already shows {CustomFieldLimits.MaxListColumns} custom columns " +
                    $"({string.Join(", ", used.OrderBy(l => l))}). Take one of those off the list first.");
        }

        /// <summary>
        /// 079. The lead → contact / deal mapping for a field about to be
        /// saved, checked. Returns (null, null) for anything that is not a
        /// Lead field. Throws InvalidOperationException with a sentence for
        /// the person when a target is not acceptable. See the header.
        /// </summary>
        public static async Task<(Guid? ToContact, Guid? ToDeal)> ValidateMappingAsync(
            FlowDbContext db, Guid tenantId, string entityType, string fieldType, Guid? selfId,
            Guid? toContact, Guid? toDeal, CancellationToken ct)
        {
            if (entityType != CustomFieldEntityTypes.Lead) return (null, null);

            var contact = await CheckTargetAsync(db, tenantId, fieldType, selfId,
                NullIfEmpty(toContact), CustomFieldEntityTypes.Contact, isContact: true, ct);
            var deal    = await CheckTargetAsync(db, tenantId, fieldType, selfId,
                NullIfEmpty(toDeal), CustomFieldEntityTypes.Deal, isContact: false, ct);

            return (contact, deal);
        }

        private static Guid? NullIfEmpty(Guid? id) => id is { } g && g != Guid.Empty ? g : null;

        private static async Task<Guid?> CheckTargetAsync(
            FlowDbContext db, Guid tenantId, string fieldType, Guid? selfId,
            Guid? targetId, string targetEntity, bool isContact, CancellationToken ct)
        {
            if (targetId is null) return null;

            var noun = CustomFieldEntityTypes.SingularNoun(targetEntity);

            var target = await db.CustomFieldDefinitions
                .AsNoTracking()
                .Where(d => d.Id == targetId.Value && d.TenantId == tenantId && !d.IsDeleted)
                .Select(d => new { d.Label, d.EntityType, d.FieldType })
                .FirstOrDefaultAsync(ct);

            if (target is null || target.EntityType != targetEntity)
                throw new InvalidOperationException(
                    $"The {noun} field chosen to receive this value no longer exists. Pick another, or none.");

            if (target.FieldType != fieldType)
                throw new InvalidOperationException(
                    $"\"{target.Label}\" on {noun}s is a {CustomFieldTypes.DisplayName(target.FieldType).ToLowerInvariant()} field, " +
                    $"so it cannot receive a {CustomFieldTypes.DisplayName(fieldType).ToLowerInvariant()} value. " +
                    "Pick a field of the same type.");

            var taken = await db.CustomFieldDefinitions
                .AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.EntityType == CustomFieldEntityTypes.Lead && !d.IsDeleted &&
                            (selfId == null || d.Id != selfId) &&
                            (isContact ? d.MapToContactFieldId == targetId : d.MapToDealFieldId == targetId))
                .Select(d => d.Label)
                .FirstOrDefaultAsync(ct);

            if (taken is not null)
                throw new InvalidOperationException(
                    $"\"{target.Label}\" on {noun}s already receives the lead field \"{taken}\". " +
                    "One lead field per target — change that one first.");

            return targetId;
        }

        public static string Actor(string? fullName)
            => string.IsNullOrWhiteSpace(fullName)
                ? "System"
                : (fullName.Length > 64 ? fullName[..64] : fullName);
    }

    // =================================================================
    // READ
    // =================================================================

    public class GetCustomFieldDefinitionsHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;

        public GetCustomFieldDefinitionsHandler(FlowDbContext db) => _db = db;

        /// <summary>
        /// The fields for one entity type, in display order.
        ///
        /// includeInactive: the Settings page and the record DETAIL page
        /// want switched-off fields too (the detail page shows a retired
        /// field's value, marked as retired). Create/Edit forms use only
        /// the active ones, and filter for themselves.
        /// </summary>
        public async Task<List<CustomFieldDefinitionDto>> Handle(
            Guid tenantId, string entityType, bool includeInactive, CancellationToken ct = default)
        {
            var type = CustomFieldEntityTypes.Normalise(entityType)
                       ?? throw new InvalidOperationException("Custom fields are not available for that kind of record.");

            var defs = await _db.CustomFieldDefinitions
                .AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.EntityType == type && !d.IsDeleted &&
                            (includeInactive || d.IsActive))
                .OrderBy(d => d.SortOrder)
                .ThenBy(d => d.Label)
                .ToListAsync(ct);

            if (defs.Count == 0) return new List<CustomFieldDefinitionDto>();

            var counts = await CustomFieldOps.ValueCountsAsync(_db, tenantId, type, ct);

            return defs
                .Select(d => CustomFieldOps.ToDto(d, counts.TryGetValue(d.Id, out var n) ? n : 0))
                .ToList();
        }
    }

    // =================================================================
    // CREATE
    // =================================================================

    public class CreateCustomFieldHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;
        private readonly IAuditService _audit;
        private readonly ILogger<CreateCustomFieldHandler> _logger;

        public CreateCustomFieldHandler(
            FlowDbContext db,
            ICurrentUserService currentUser,
            IAuditService audit,
            ILogger<CreateCustomFieldHandler> logger)
        {
            _db          = db;
            _currentUser = currentUser;
            _audit       = audit;
            _logger      = logger;
        }

        public async Task<CustomFieldDefinitionDto> Handle(
            Guid tenantId, SaveCustomFieldDefinitionDto dto, CancellationToken ct = default)
        {
            var entityType = CustomFieldEntityTypes.Normalise(dto.EntityType)
                             ?? throw new InvalidOperationException("Custom fields are not available for that kind of record yet.");

            var fieldType = CustomFieldTypes.Normalise(dto.FieldType)
                            ?? throw new InvalidOperationException("Pick a field type.");

            var (label, help) = CustomFieldOps.ValidateText(dto.Label, dto.HelpText);

            var existingCount = await _db.CustomFieldDefinitions
                .CountAsync(d => d.TenantId == tenantId && d.EntityType == entityType && !d.IsDeleted, ct);

            if (existingCount >= CustomFieldLimits.MaxFieldsPerEntity)
                throw new InvalidOperationException(
                    $"You already have {CustomFieldLimits.MaxFieldsPerEntity} fields on " +
                    $"{CustomFieldEntityTypes.PluralLabel(entityType).ToLowerInvariant()}, which is the most allowed. " +
                    "Delete one you no longer use first.");

            if (await _db.CustomFieldDefinitions.AnyAsync(d => d.TenantId == tenantId && d.EntityType == entityType &&
                                                               !d.IsDeleted && d.Label == label, ct))
                throw new InvalidOperationException($"There is already a field called \"{label}\".");

            await CustomFieldOps.EnsureListColumnSlotAsync(
                _db, tenantId, entityType, null, dto.IsActive, dto.ShowInList, ct);

            // 079 — Lead fields only; (null, null) for anything else.
            var (mapContact, mapDeal) = await CustomFieldOps.ValidateMappingAsync(
                _db, tenantId, entityType, fieldType, null, dto.MapToContactFieldId, dto.MapToDealFieldId, ct);

            var options = fieldType == CustomFieldTypes.Dropdown
                ? CustomFieldOps.MergeOptions(dto.Options, Array.Empty<CustomFieldOptionDto>(), new HashSet<string>())
                : new List<CustomFieldOptionDto>();

            var maxOrder = await _db.CustomFieldDefinitions
                .Where(d => d.TenantId == tenantId && d.EntityType == entityType && !d.IsDeleted)
                .Select(d => (int?)d.SortOrder)
                .MaxAsync(ct) ?? 0;

            var actor = CustomFieldOps.Actor((await _currentUser.GetCurrentUserAsync())?.FullName);

            var row = new CustomFieldDefinition
            {
                Id            = Guid.NewGuid(),
                TenantId      = tenantId,
                EntityType    = entityType,
                Label         = label,
                FieldType     = fieldType,
                HelpText      = help,
                IsRequired    = CustomFieldTypes.SupportsRequired(fieldType) && dto.IsRequired,
                IsActive      = dto.IsActive,
                SortOrder     = maxOrder + 10,
                ShowInList    = dto.ShowInList,
                DecimalPlaces = fieldType == CustomFieldTypes.Number
                                    ? Math.Clamp(dto.DecimalPlaces, 0, CustomFieldLimits.MaxDecimalPlaces)
                                    : 0,
                OptionsJson   = CustomFieldOptionsJson.Serialize(options),
                MapToContactFieldId = mapContact,     // 079
                MapToDealFieldId    = mapDeal,        // 079
                CreatedAtUtc  = DateTime.UtcNow,
                CreatedBy     = actor,
                IsDeleted     = false
            };

            _db.CustomFieldDefinitions.Add(row);
            await _db.SaveChangesAsync(ct);

            await _audit.WriteAsync(
                AuditAction.CustomFieldCreated, AuditEntityType.CustomField, row.Id, tenantId,
                new
                {
                    entityType, label = row.Label, fieldType, isRequired = row.IsRequired, isActive = row.IsActive,
                    mapsToContact = row.MapToContactFieldId, mapsToDeal = row.MapToDealFieldId    // 079
                },
                ct);

            _logger.LogInformation("Custom field {Label} ({Type}) created on {Entity} for tenant {TenantId}",
                row.Label, fieldType, entityType, tenantId);

            return CustomFieldOps.ToDto(row, 0);
        }
    }

    // =================================================================
    // UPDATE
    // =================================================================

    public class UpdateCustomFieldHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;
        private readonly IAuditService _audit;
        private readonly ILogger<UpdateCustomFieldHandler> _logger;

        public UpdateCustomFieldHandler(
            FlowDbContext db,
            ICurrentUserService currentUser,
            IAuditService audit,
            ILogger<UpdateCustomFieldHandler> logger)
        {
            _db          = db;
            _currentUser = currentUser;
            _audit       = audit;
            _logger      = logger;
        }

        /// <summary>
        /// Rename, re-hint, switch on/off, change required, change decimal
        /// places, edit dropdown options. NOT the type and NOT the entity
        /// type — see note 1 in the header.
        /// </summary>
        public async Task<CustomFieldDefinitionDto> Handle(
            Guid tenantId, Guid id, SaveCustomFieldDefinitionDto dto, CancellationToken ct = default)
        {
            var row = await _db.CustomFieldDefinitions
                .FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId && !d.IsDeleted, ct)
                ?? throw new KeyNotFoundException("That field no longer exists.");

            var (label, help) = CustomFieldOps.ValidateText(dto.Label, dto.HelpText);

            if (await _db.CustomFieldDefinitions.AnyAsync(d => d.TenantId == tenantId && d.EntityType == row.EntityType &&
                                                               !d.IsDeleted && d.Id != row.Id && d.Label == label, ct))
                throw new InvalidOperationException($"There is already a field called \"{label}\".");

            if (row.FieldType == CustomFieldTypes.Dropdown)
            {
                var keysInUse = await CustomFieldOps.KeysInUseAsync(_db, tenantId, row, ct);
                var previous  = CustomFieldOptionsJson.Parse(row.OptionsJson);
                var merged    = CustomFieldOps.MergeOptions(dto.Options, previous, keysInUse);
                row.OptionsJson = CustomFieldOptionsJson.Serialize(merged);
            }

            if (row.FieldType == CustomFieldTypes.Number)
            {
                var places = Math.Clamp(dto.DecimalPlaces, 0, CustomFieldLimits.MaxDecimalPlaces);

                // Fewer places than values already stored would make those
                // values invalid on the next edit of each record, with a
                // message the person could not understand. Refuse here,
                // where the cause is visible.
                if (places < row.DecimalPlaces)
                {
                    var live   = CustomFieldOps.LiveEntityIds(_db, tenantId, row.EntityType);

                    var stored = await _db.CustomFieldValues
                        .AsNoTracking()
                        .Where(v => v.TenantId == tenantId && v.DefinitionId == row.Id &&
                                    v.NumberValue != null && live.Contains(v.EntityId))
                        .Select(v => v.NumberValue!.Value)
                        .ToListAsync(ct);

                    if (stored.Any(n => decimal.Round(n, places) != n))
                        throw new InvalidOperationException(
                            $"Some records hold \"{row.Label}\" with more than {places} decimal place" +
                            $"{(places == 1 ? "" : "s")}. Keep the current setting, or correct those values first.");
                }

                row.DecimalPlaces = places;
            }

            await CustomFieldOps.EnsureListColumnSlotAsync(
                _db, tenantId, row.EntityType, row.Id, dto.IsActive, dto.ShowInList, ct);

            // 079 — the type is the STORED one; the DTO's is ignored on update.
            var (mapContact, mapDeal) = await CustomFieldOps.ValidateMappingAsync(
                _db, tenantId, row.EntityType, row.FieldType, row.Id,
                dto.MapToContactFieldId, dto.MapToDealFieldId, ct);

            var before = new { row.Label, row.IsRequired, row.IsActive };

            row.Label        = label;
            row.HelpText     = help;
            row.IsRequired   = CustomFieldTypes.SupportsRequired(row.FieldType) && dto.IsRequired;
            row.IsActive     = dto.IsActive;
            row.ShowInList   = dto.ShowInList;
            row.MapToContactFieldId = mapContact;    // 079
            row.MapToDealFieldId    = mapDeal;       // 079
            row.UpdatedAtUtc = DateTime.UtcNow;
            row.UpdatedBy    = CustomFieldOps.Actor((await _currentUser.GetCurrentUserAsync())?.FullName);

            await _db.SaveChangesAsync(ct);

            await _audit.WriteAsync(
                AuditAction.CustomFieldUpdated, AuditEntityType.CustomField, row.Id, tenantId,
                new
                {
                    entityType    = row.EntityType,
                    label         = row.Label,
                    previousLabel = before.Label == row.Label ? null : before.Label,
                    isRequired    = row.IsRequired,
                    isActive      = row.IsActive,
                    mapsToContact = row.MapToContactFieldId,     // 079
                    mapsToDeal    = row.MapToDealFieldId
                },
                ct);

            _logger.LogInformation("Custom field {Id} updated for tenant {TenantId}", row.Id, tenantId);

            var counts = await CustomFieldOps.ValueCountsAsync(_db, tenantId, row.EntityType, ct);
            return CustomFieldOps.ToDto(row, counts.TryGetValue(row.Id, out var n) ? n : 0);
        }
    }

    // =================================================================
    // DELETE
    // =================================================================

    public class DeleteCustomFieldHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;
        private readonly IAuditService _audit;
        private readonly ILogger<DeleteCustomFieldHandler> _logger;

        public DeleteCustomFieldHandler(
            FlowDbContext db,
            ICurrentUserService currentUser,
            IAuditService audit,
            ILogger<DeleteCustomFieldHandler> logger)
        {
            _db          = db;
            _currentUser = currentUser;
            _audit       = audit;
            _logger      = logger;
        }

        public async Task Handle(Guid tenantId, Guid id, CancellationToken ct = default)
        {
            var row = await _db.CustomFieldDefinitions
                .FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId && !d.IsDeleted, ct)
                ?? throw new KeyNotFoundException("That field no longer exists.");

            var live = CustomFieldOps.LiveEntityIds(_db, tenantId, row.EntityType);

            var liveCount = await _db.CustomFieldValues
                .CountAsync(v => v.TenantId == tenantId && v.DefinitionId == row.Id && live.Contains(v.EntityId), ct);

            if (liveCount > 0)
            {
                var noun = CustomFieldEntityTypes.SingularNoun(row.EntityType);
                throw new InvalidOperationException(
                    $"\"{row.Label}\" is filled in on {liveCount} {noun}{(liveCount == 1 ? "" : "s")}, so it cannot be deleted. " +
                    "Switch it off instead — it disappears from forms and the values stay on record.");
            }

            // Values left on soft-deleted records only. They go with the
            // field; nothing can display them and they would hold the
            // foreign key.
            var orphans = await _db.CustomFieldValues
                .Where(v => v.TenantId == tenantId && v.DefinitionId == row.Id)
                .ToListAsync(ct);
            _db.CustomFieldValues.RemoveRange(orphans);

            // 079 — Lead fields that copy INTO this one lose the pointer,
            // in this same save. The columns are not foreign keys, so a
            // stale id would otherwise stay behind (see the header).
            var pointing = 0;
            if (row.EntityType == CustomFieldEntityTypes.Contact || row.EntityType == CustomFieldEntityTypes.Deal)
            {
                var sources = await _db.CustomFieldDefinitions
                    .Where(d => d.TenantId == tenantId && d.EntityType == CustomFieldEntityTypes.Lead &&
                                (d.MapToContactFieldId == row.Id || d.MapToDealFieldId == row.Id))
                    .ToListAsync(ct);

                foreach (var s in sources)
                {
                    if (s.MapToContactFieldId == row.Id) s.MapToContactFieldId = null;
                    if (s.MapToDealFieldId    == row.Id) s.MapToDealFieldId    = null;
                    s.UpdatedAtUtc = DateTime.UtcNow;
                }
                pointing = sources.Count;
            }

            row.IsDeleted    = true;
            row.IsActive     = false;
            row.UpdatedAtUtc = DateTime.UtcNow;
            row.UpdatedBy    = CustomFieldOps.Actor((await _currentUser.GetCurrentUserAsync())?.FullName);

            await _db.SaveChangesAsync(ct);

            await _audit.WriteAsync(
                AuditAction.CustomFieldDeleted, AuditEntityType.CustomField, row.Id, tenantId,
                new { entityType = row.EntityType, label = row.Label, fieldType = row.FieldType, leadMappingsCleared = pointing },
                ct);

            _logger.LogInformation("Custom field {Label} deleted for tenant {TenantId}", row.Label, tenantId);
        }
    }

    // =================================================================
    // REORDER
    // =================================================================

    public class ReorderCustomFieldsHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;

        public ReorderCustomFieldsHandler(FlowDbContext db, ICurrentUserService currentUser)
        {
            _db          = db;
            _currentUser = currentUser;
        }

        /// <summary>
        /// The whole order for one entity type. Ids that are not this
        /// workspace's fields of this type are ignored (a stale page);
        /// fields the page did not send keep their place after the rest.
        /// Not audited — an order is a display preference.
        /// </summary>
        public async Task<int> Handle(Guid tenantId, ReorderCustomFieldsDto dto, CancellationToken ct = default)
        {
            var entityType = CustomFieldEntityTypes.Normalise(dto.EntityType)
                             ?? throw new InvalidOperationException("Custom fields are not available for that kind of record.");

            if (dto.OrderedIds is null || dto.OrderedIds.Count == 0) return 0;

            var rows = await _db.CustomFieldDefinitions
                .Where(d => d.TenantId == tenantId && d.EntityType == entityType && !d.IsDeleted)
                .ToListAsync(ct);

            var byId  = rows.ToDictionary(r => r.Id);
            var actor = CustomFieldOps.Actor((await _currentUser.GetCurrentUserAsync())?.FullName);
            var now   = DateTime.UtcNow;
            var moved = 0;
            var order = 10;
            var placed = new HashSet<Guid>();

            foreach (var id in dto.OrderedIds)
            {
                if (!byId.TryGetValue(id, out var row) || !placed.Add(id)) continue;
                if (row.SortOrder != order)
                {
                    row.SortOrder = order;
                    row.UpdatedAtUtc = now;
                    row.UpdatedBy = actor;
                    moved++;
                }
                order += 10;
            }

            foreach (var row in rows.Where(r => !placed.Contains(r.Id)).OrderBy(r => r.SortOrder).ThenBy(r => r.Label))
            {
                if (row.SortOrder != order)
                {
                    row.SortOrder = order;
                    moved++;
                }
                order += 10;
            }

            if (moved > 0) await _db.SaveChangesAsync(ct);
            return moved;
        }
    }
}
