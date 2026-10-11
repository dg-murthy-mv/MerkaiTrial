// =====================================================================
// FILE: MerkaiTrial.Application/Commands/CustomFields/DocumentFields.cs
//
// NEW FILE (081). Custom fields on QUOTES and INVOICES as documents:
// what gets printed, when it is frozen, and how a quote's values reach
// the invoice raised from it.
//
// 082 — PRODUCT FIELDS UNDER EACH LINE
//   Product fields switched to "Print on quotes & invoices" print under
//   the line of every quote and invoice that sells the product. The copy
//   sits on the LINE (QuoteItems / InvoiceLines.CustomFieldsSnapshot):
//     quote line    frozen when the QUOTE is sent, with the quote's own
//                   fields; a draft shows the product as it is now
//     invoice line  raised from a quote → takes the quote line's copy, so
//                   the invoice says what the customer accepted; any line
//                   still without a copy is frozen when the invoice is
//                   ISSUED. A copy on an invoice line always counts.
//   BuildManyAsync reads any number of products in two queries;
//   ResolveLinesAsync / FreezeProductsAsync are the line-level helpers.
//
// ─────────────────────────────────────────────────────────────────────
// WHAT IS PRINTED
//   Fields switched to "Print on quotes & invoices" (PrintOnDocuments),
//   switched on, and filled in on this record — in the Settings order.
//   An empty field prints nothing at all, not "Label: —": a customer
//   reading a quote should not see the gaps in somebody's CRM.
//
// WHEN IT FREEZES
//   Quote:   when it is SENT (UpdateQuoteStatusHandler, → Sent).
//   Invoice: when it is ISSUED (IssueInvoiceHandler, in the same UPDATE
//            that claims the invoice number).
//   The frozen copy (Quotes/Invoices.CustomFieldsSnapshot) holds label,
//   type and value together — see DocumentFieldEntry — so the PDF, the
//   customer's link and the detail page keep saying what was sent even if
//   the field is renamed, a choice relabelled, or the field deleted.
//
//   ResolveAsync decides which to show:
//     frozen and a snapshot exists   → the snapshot
//     otherwise                      → a live build (a draft's preview;
//                                      or a quote/invoice sent before 081,
//                                      which has no snapshot)
//
//   A quote that is REVISED goes back to editable; the next send takes a
//   new snapshot, replacing the old one. That is right: a revised quote
//   is a new offer and carries its own details.
//
// QUOTE → INVOICE
//   A Quote field may name an Invoice field (MapToInvoiceFieldId).
//   CopyQuoteToInvoiceAsync carries the values across when an invoice is
//   raised from a quote WITHOUT a form (the API path, milestone billing).
//   The invoice form pre-fills the same values through the same
//   CustomFieldWireMapper, so what the person sees is what is saved.
//   Like the 079 lead copy: same type only, the target must be live and
//   switched on, dropdowns are matched by choice name, and required-field
//   rules are NOT enforced (this is a copy, not a form).
//
// IgnoreQueryFilters IN BuildAsync — ON PURPOSE
//   The customer's /q/{token} page is anonymous, so the current tenant is
//   Guid.Empty and the STRICT filters on both custom field tables would
//   return nothing: a quote sent before 081 (no snapshot) would print with
//   its details silently missing. Every query here names tenantId
//   explicitly, which is the tenancy; the filter is only a second lock,
//   and GetQuoteByTokenHandler bypasses it for the same reason three
//   times already.
// ─────────────────────────────────────────────────────────────────────
// =====================================================================

using System.Text.Json;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.CustomFields
{
    public static class DocumentFields
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// The printed fields of one record, as they are NOW. Two small
        /// queries; empty when no field of this type prints.
        /// </summary>
        public static async Task<List<DocumentFieldEntry>> BuildAsync(
            FlowDbContext db, Guid tenantId, string entityType, Guid entityId, CancellationToken ct = default)
        {
            var many = await BuildManyAsync(db, tenantId, entityType, new[] { entityId }, ct);
            return many.TryGetValue(entityId, out var list) ? list : new List<DocumentFieldEntry>();
        }

        /// <summary>
        /// 082. The printed fields of MANY records of one type, as they are
        /// NOW — record id → entries, in the Settings order. Two queries
        /// whatever the count, so a quote with forty product lines costs the
        /// same as one with four. A record with nothing to print maps to an
        /// empty list (every requested id is present in the result).
        /// </summary>
        public static async Task<Dictionary<Guid, List<DocumentFieldEntry>>> BuildManyAsync(
            FlowDbContext db, Guid tenantId, string entityType, IEnumerable<Guid> entityIds, CancellationToken ct = default)
        {
            var ids = entityIds.Distinct().ToList();
            var result = ids.ToDictionary(id => id, _ => new List<DocumentFieldEntry>());
            if (ids.Count == 0) return result;

            var defs = await db.CustomFieldDefinitions
                .AsNoTracking()
                .IgnoreQueryFilters()      // see the header — tenantId is explicit
                .Where(d => d.TenantId == tenantId && d.EntityType == entityType &&
                            !d.IsDeleted && d.IsActive && d.PrintOnDocuments)
                .OrderBy(d => d.SortOrder)
                .ThenBy(d => d.Label)
                .ToListAsync(ct);

            if (defs.Count == 0) return result;

            var defIds = defs.Select(d => d.Id).ToList();

            var values = await db.CustomFieldValues
                .AsNoTracking()
                .IgnoreQueryFilters()      // see the header — tenantId is explicit
                .Where(v => v.TenantId == tenantId && ids.Contains(v.EntityId) && defIds.Contains(v.DefinitionId))
                .ToListAsync(ct);

            var byEntityDef = values
                .GroupBy(v => (v.EntityId, v.DefinitionId))
                .ToDictionary(g => g.Key, g => g.First());

            // Dropdown options parsed once per field, not once per record.
            var optionsByDef = defs
                .Where(d => d.FieldType == CustomFieldTypes.Dropdown)
                .ToDictionary(d => d.Id, d => CustomFieldOptionsJson.Parse(d.OptionsJson));

            foreach (var id in ids)
            {
                var list = result[id];
                foreach (var d in defs)
                {
                    if (!byEntityDef.TryGetValue((id, d.Id), out var v)) continue;

                    var wire = CustomFieldValueRules.ToWire(d.FieldType, v.TextValue, v.NumberValue, v.DateValue, v.BoolValue);
                    if (string.IsNullOrEmpty(wire)) continue;

                    string? optionLabel = null;
                    if (d.FieldType == CustomFieldTypes.Dropdown)
                    {
                        optionLabel = optionsByDef[d.Id].FirstOrDefault(o => o.Key == wire)?.Label;
                        // A key the field no longer knows has nothing printable.
                        if (string.IsNullOrWhiteSpace(optionLabel)) continue;
                    }

                    list.Add(new DocumentFieldEntry
                    {
                        FieldId       = d.Id,
                        Label         = d.Label,
                        Type          = d.FieldType,
                        DecimalPlaces = d.DecimalPlaces,
                        Value         = wire,
                        OptionLabel   = optionLabel
                    });
                }
            }

            return result;
        }

        // ── 082: PRODUCT FIELDS ON DOCUMENT LINES ─────────────────────

        /// <summary>
        /// 082. One quote or invoice line, as far as its printed product
        /// fields are concerned. <paramref name="UseSnapshot"/> is the
        /// caller's rule for whether this line's frozen copy counts (see
        /// QuoteDocument / InvoiceDocument).
        /// </summary>
        public sealed record LineFieldSource(Guid LineId, Guid? ProductId, string? Snapshot, bool UseSnapshot);

        /// <summary>
        /// 082. Line id → the product fields that line prints: its frozen
        /// copy when <c>UseSnapshot</c> and the copy is readable, otherwise
        /// the product's values as they are now (one batched read for all
        /// such lines). A custom line with no product prints nothing.
        /// </summary>
        public static async Task<Dictionary<Guid, List<DocumentFieldEntry>>> ResolveLinesAsync(
            FlowDbContext db, Guid tenantId, IEnumerable<LineFieldSource> lines, CancellationToken ct = default)
        {
            var list   = lines.ToList();
            var result = new Dictionary<Guid, List<DocumentFieldEntry>>();
            var needLive = new List<LineFieldSource>();

            foreach (var line in list)
            {
                if (line.UseSnapshot && line.Snapshot is not null && Deserialize(line.Snapshot) is { } frozen)
                    result[line.LineId] = frozen;
                else if (line.ProductId.HasValue)
                    needLive.Add(line);
                else
                    result[line.LineId] = new List<DocumentFieldEntry>();
            }

            if (needLive.Count > 0)
            {
                var live = await BuildManyAsync(db, tenantId, CustomFieldEntityTypes.Product,
                    needLive.Select(l => l.ProductId!.Value), ct);
                foreach (var line in needLive)
                    result[line.LineId] = live.TryGetValue(line.ProductId!.Value, out var e) ? e : new List<DocumentFieldEntry>();
            }

            return result;
        }

        /// <summary>
        /// 082. The snapshot JSON for each product line, as the products are
        /// NOW: product id → serialized copy ("[]" when nothing prints — still
        /// a copy, so the line stays fixed). Lines without a product get no
        /// entry and are left NULL by the caller.
        /// </summary>
        public static async Task<Dictionary<Guid, string>> FreezeProductsAsync(
            FlowDbContext db, Guid tenantId, IEnumerable<Guid?> productIds, CancellationToken ct = default)
        {
            var ids = productIds.Where(p => p.HasValue).Select(p => p!.Value).Distinct().ToList();
            if (ids.Count == 0) return new Dictionary<Guid, string>();

            var live = await BuildManyAsync(db, tenantId, CustomFieldEntityTypes.Product, ids, ct);
            return live.ToDictionary(kv => kv.Key, kv => Serialize(kv.Value));
        }

        /// <summary>
        /// What a document prints: the frozen copy when the document is
        /// frozen and has one, otherwise the live values. Never throws on a
        /// damaged snapshot — it falls back to the live values instead, so a
        /// bad row cannot stop an invoice from opening.
        /// </summary>
        public static async Task<List<DocumentFieldEntry>> ResolveAsync(
            FlowDbContext db, Guid tenantId, string entityType, Guid entityId,
            bool frozen, string? snapshotJson, CancellationToken ct = default)
        {
            if (frozen && snapshotJson is not null)
            {
                var parsed = Deserialize(snapshotJson);
                if (parsed is not null) return parsed;
            }

            return await BuildAsync(db, tenantId, entityType, entityId, ct);
        }

        /// <summary>The snapshot column's content. "[]" when nothing prints — still a snapshot.</summary>
        public static string Serialize(IReadOnlyCollection<DocumentFieldEntry> entries)
            => JsonSerializer.Serialize(entries, Json);

        /// <summary>Null when the JSON cannot be read.</summary>
        public static List<DocumentFieldEntry>? Deserialize(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return JsonSerializer.Deserialize<List<DocumentFieldEntry>>(json, Json)?
                    .Where(e => !string.IsNullOrWhiteSpace(e.Label) && !string.IsNullOrEmpty(e.Value))
                    .ToList();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Stages, on <paramref name="db"/>, the invoice's copies of the
        /// quote's mapped values. Does NOT save — the caller's save commits
        /// them with the invoice. Returns how many were copied.
        /// </summary>
        public static async Task<int> CopyQuoteToInvoiceAsync(
            FlowDbContext db, Guid tenantId, Guid quoteId, Guid invoiceId, string? actor, CancellationToken ct = default)
        {
            var sources = await db.CustomFieldDefinitions
                .AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.EntityType == CustomFieldEntityTypes.Quote &&
                            !d.IsDeleted && d.MapToInvoiceFieldId != null)
                .ToListAsync(ct);

            if (sources.Count == 0) return 0;

            var targetIds = sources.Select(s => s.MapToInvoiceFieldId!.Value).Distinct().ToList();

            var targets = await db.CustomFieldDefinitions
                .AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.EntityType == CustomFieldEntityTypes.Invoice &&
                            targetIds.Contains(d.Id) && !d.IsDeleted && d.IsActive)
                .ToDictionaryAsync(d => d.Id, ct);

            if (targets.Count == 0) return 0;

            var sourceIds = sources.Select(s => s.Id).ToList();
            var values = await db.CustomFieldValues
                .AsNoTracking()
                .Where(v => v.TenantId == tenantId && v.EntityId == quoteId && sourceIds.Contains(v.DefinitionId))
                .ToListAsync(ct);

            var valueByDef = values.GroupBy(v => v.DefinitionId).ToDictionary(g => g.Key, g => g.First());

            // The invoice may already hold values (CreateInvoiceHandler is
            // the only caller, on a brand-new invoice, so normally none) —
            // never add a second row for the same field.
            var held = new HashSet<Guid>(db.CustomFieldValues.Local
                .Where(v => v.EntityId == invoiceId)
                .Select(v => v.DefinitionId));

            var now    = DateTime.UtcNow;
            var who    = string.IsNullOrWhiteSpace(actor) ? null : (actor.Length > 64 ? actor[..64] : actor);
            var copied = 0;

            foreach (var source in sources)
            {
                if (!valueByDef.TryGetValue(source.Id, out var value)) continue;
                if (!targets.TryGetValue(source.MapToInvoiceFieldId!.Value, out var target)) continue;
                if (!held.Add(target.Id)) continue;

                var sourceWire = CustomFieldValueRules.ToWire(
                    source.FieldType, value.TextValue, value.NumberValue, value.DateValue, value.BoolValue);

                var targetOptions = target.FieldType == CustomFieldTypes.Dropdown
                    ? CustomFieldOptionsJson.Parse(target.OptionsJson)
                    : new List<CustomFieldOptionDto>();

                var targetWire = CustomFieldWireMapper.Convert(
                    source.FieldType,
                    source.FieldType == CustomFieldTypes.Dropdown
                        ? CustomFieldOptionsJson.Parse(source.OptionsJson)
                        : new List<CustomFieldOptionDto>(),
                    sourceWire,
                    target.FieldType,
                    target.DecimalPlaces,
                    targetOptions);

                if (targetWire is null) continue;

                var parsed = CustomFieldValueRules.Parse(
                    target.FieldType, target.Label, target.DecimalPlaces, targetOptions, targetWire);
                if (parsed.Error is not null || parsed.IsEmpty) continue;

                db.CustomFieldValues.Add(new CustomFieldValue
                {
                    Id           = Guid.NewGuid(),
                    TenantId     = tenantId,
                    DefinitionId = target.Id,
                    EntityId     = invoiceId,
                    TextValue    = parsed.Text,
                    NumberValue  = parsed.Number,
                    DateValue    = parsed.Date,
                    BoolValue    = parsed.Bool,
                    CreatedAtUtc = now,
                    UpdatedBy    = who
                });
                copied++;
            }

            return copied;
        }
    }
}
