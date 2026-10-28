// =====================================================================
// ExcelHandlers.cs
// Location: MerkaiTrial.Application/Commands/Leads/ExcelHandlers.cs
//
// COMPLETE FILE — replaces the existing one.
//
// FIXES (015, still true)
//   1. RECORD VISIBILITY: exports only the leads the user may see.
//   2. Reads the lead's own name, email, phone (no inner join on Contacts,
//      which left every unconverted lead out).
//   3. Status is the tenant's NAME, not the key.
//   4. Dates are UTC and labelled as such.
//
// 092 — AN EXPORT THAT MATCHES THE SCREEN AND COMES BACK IN
//
//   1. THE SAME FILTERS AS THE LIST. The search, tab, owner and custom
//      field filters come from LeadListFilter.Apply — the very code the
//      Leads page uses — so the file holds exactly the rows on screen.
//      Before, this handler kept its own copy, which ignored custom field
//      filters and did not search custom values.
//
//   2. ROUND-TRIPPABLE. The first fourteen columns are headed with the
//      import fields' own names (ImportFields.ExportHeading) and hold
//      values the importer reads back:
//        Owner     the owner's EMAIL (the importer matches owners by
//                  email); their name is in "Owner name" further along
//        Country, Lead source, Channel, Status, Industry   by name
//        Estimated value   a number, blank when there is none (it was 0,
//                  which a re-import would have stored as a real 0)
//      Then one column per ACTIVE Lead custom field, headed with its
//      label: numbers as numbers, dates as real Excel dates shown as
//      2026-10-16, a dropdown's choice NAME, a checkbox as Yes / No.
//      Then the columns that are for reading only — owner name, created,
//      last activity, deal — which the importer leaves unmatched.
//
//      So: export, change some cells, import with "Fill in the blanks",
//      and the auto-mapper has already matched every column.
//
//   3. A CEILING, SAID OUT LOUD. More than MaxRows rows is refused with a
//      sentence (filter, then export in parts) rather than building a
//      workbook that takes the web server's memory with it.
//
//   4. Handle returns the row count with the bytes, so the controller can
//      audit the export. An export is the easiest way to walk out of the
//      door with a customer list; it should leave a trace.
//
// 105 — VUL-15: TEXT THAT LOOKS LIKE A FORMULA STAYS TEXT
//   Before saving, every text cell starting with = + - @ (or a tab or line
//   break) is marked quote-prefix (Common/SpreadsheetText.cs), like typing
//   an apostrophe in front of it in Excel. Values are unchanged, so the
//   file still imports back exactly; Excel never runs them — not on open,
//   not after an edit. (Agreed limit: a copy someone re-saves as CSV
//   themselves loses the mark, as any CSV would.)
// =====================================================================

using ClosedXML.Excel;
using MerkaiTrial.Application.Common;
using MerkaiTrial.Application.Commands.CustomFields;
using MerkaiTrial.Application.Commands.Leads.Import;
using MerkaiTrial.Application.Commands.LeadStatuses;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.Leads
{
    /// <summary>092. The workbook, and how many leads are in it.</summary>
    public sealed record LeadExportFile(byte[] Content, int RowCount);

    // ==================== EXCEL EXPORT ====================
    public class ExportLeadsHandler : ICommandHandler
    {
        /// <summary>
        /// 092. The most rows one export holds. Ten times the import's
        /// 5,000-row limit; a list larger than this is narrowed first.
        /// </summary>
        public const int MaxRows = 50_000;

        private readonly FlowDbContext _db;
        private readonly IRecordScopeService _scope;
        private readonly ILeadStatusResolver _statuses;

        public ExportLeadsHandler(FlowDbContext db, IRecordScopeService scope, ILeadStatusResolver statuses)
        {
            _db = db;
            _scope = scope;
            _statuses = statuses;
        }

        public async Task<LeadExportFile> Handle(ExportLeadsRequest request, CancellationToken ct = default)
        {
            var tenantId = request.TenantId;
            var access   = await _scope.GetAsync(RecordModules.Leads, ct);
            var statuses = await _statuses.GetAsync(tenantId, ct);

            // The ACTIVE Lead custom fields: filters, search, and columns.
            var defs = await CustomFieldListQuery.ActiveDefinitionsAsync(
                _db, tenantId, CustomFieldEntityTypes.Lead, ct);

            var leads = _db.Leads.AsNoTracking()
                .Where(l => l.TenantId == tenantId && !l.IsDeleted)
                .VisibleTo(access);

            // Exactly the Leads list's filters (note 1).
            leads = LeadListFilter.Apply(
                _db, tenantId, leads, defs,
                request.SearchTerm, request.Status, request.AssignedTo, request.CustomFilters);

            var total = await leads.CountAsync(ct);
            if (total > MaxRows)
                throw new InvalidOperationException(
                    $"That list has {total:N0} leads, which is more than one export can hold ({MaxRows:N0}). " +
                    "Narrow it with a tab, the search or a filter, and export it in parts.");

            var rows = await leads
                .OrderByDescending(l => l.CreatedAtUtc)
                .ThenBy(l => l.Id)
                .Select(l => new
                {
                    l.Id,
                    l.FullName,
                    Email   = l.Email ?? "",
                    Phone   = l.Phone ?? "",
                    Company = l.CompanyName ?? "",
                    Address = l.Address ?? "",
                    Country = l.CountryId.HasValue
                        ? _db.Countries.Where(c => c.Id == l.CountryId).Select(c => c.Name).FirstOrDefault()
                        : null,
                    Source  = l.LeadSource != null ? l.LeadSource.Name : (l.Source ?? ""),
                    Channel = l.LeadChannel != null ? l.LeadChannel.Name : l.Channel.ToString(),
                    l.Status,
                    l.Score,
                    l.EstimatedValue,
                    Currency = l.Currency ?? "",
                    l.OwnerUserId,
                    Industry = l.VerticalId.HasValue
                        ? _db.CompanyVerticals.Where(v => v.Id == l.VerticalId.Value).Select(v => v.Name).FirstOrDefault()
                        : null,
                    l.CreatedAtUtc,
                    LastActivity = _db.Activities
                        .Where(a => a.EntityType == "Lead" && a.EntityId == l.Id &&
                                    !a.IsDeleted && (!a.IsTask || a.IsCompleted))
                        .OrderByDescending(a => a.ActivityDate)
                        .Select(a => new { a.ActivityDate, a.ActivityType })
                        .FirstOrDefault(),
                    Deal = _db.Deals
                        .Where(d => d.LeadId == l.Id && !d.IsDeleted)
                        .Select(d => new { d.Stage, d.ExpectedValue })
                        .FirstOrDefault()
                })
                .ToListAsync(ct);

            // Owners: id → (email, name). One query.
            var ownerIds = rows
                .Select(r => Guid.TryParse(r.OwnerUserId, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .Distinct()
                .ToList();

            var owners = (await _db.Users.AsNoTracking()
                    .Where(u => u.TenantId == tenantId && ownerIds.Contains(u.Id))
                    .Select(u => new { u.Id, u.Email, Name = (u.FirstName + " " + u.LastName).Trim() })
                    .ToListAsync(ct))
                .ToDictionary(u => u.Id.ToString(), u => (u.Email, u.Name), StringComparer.OrdinalIgnoreCase);

            // Custom values for exactly these leads: lead → field → wire. One query.
            var values = await CustomValuesAsync(leads, defs, tenantId, ct);

            // ── Workbook ──────────────────────────────────────────────────
            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Leads");

            // 1. The importable columns, headed as the importer names them.
            var headers = new List<string>
            {
                ImportFields.ExportHeading(ImportFields.FullName),
                ImportFields.ExportHeading(ImportFields.Email),
                ImportFields.ExportHeading(ImportFields.Phone),
                ImportFields.ExportHeading(ImportFields.CompanyName),
                ImportFields.ExportHeading(ImportFields.Address),
                ImportFields.ExportHeading(ImportFields.Country),
                ImportFields.ExportHeading(ImportFields.Source),
                ImportFields.ExportHeading(ImportFields.Channel),
                ImportFields.ExportHeading(ImportFields.Status),
                ImportFields.ExportHeading(ImportFields.Score),
                ImportFields.ExportHeading(ImportFields.EstimatedValue),
                ImportFields.ExportHeading(ImportFields.Currency),
                ImportFields.ExportHeading(ImportFields.Owner),
                ImportFields.ExportHeading(ImportFields.Vertical),
            };
            var firstCustom = headers.Count + 1;

            // 2. One column per active custom field.
            headers.AddRange(defs.Select(d => d.Label));
            var firstExtra = headers.Count + 1;

            // 3. For reading only — the importer leaves these unmatched.
            headers.AddRange(new[]
            {
                "Owner name", "Created (UTC)", "Last activity (UTC)", "Last activity type",
                "Has deal", "Deal stage", "Deal value", "Lead ID"
            });

            for (var c = 0; c < headers.Count; c++)
                ws.Cell(1, c + 1).Value = headers[c];

            var header = ws.Row(1);
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = XLColor.LightBlue;

            var optionLabels = defs
                .Where(d => d.FieldType == CustomFieldTypes.Dropdown)
                .ToDictionary(
                    d => d.Id,
                    d => CustomFieldOptionsJson.Parse(d.OptionsJson)
                            .Where(o => !string.IsNullOrEmpty(o.Key))
                            .GroupBy(o => o.Key, StringComparer.Ordinal)
                            .ToDictionary(g => g.Key, g => g.First().Label, StringComparer.Ordinal));

            var row = 2;
            foreach (var l in rows)
            {
                owners.TryGetValue(l.OwnerUserId ?? "", out var owner);

                ws.Cell(row, 1).Value  = l.FullName;
                ws.Cell(row, 2).Value  = l.Email;
                ws.Cell(row, 3).Value  = l.Phone;
                ws.Cell(row, 4).Value  = l.Company;
                ws.Cell(row, 5).Value  = l.Address;
                ws.Cell(row, 6).Value  = l.Country ?? "";
                ws.Cell(row, 7).Value  = l.Source;
                ws.Cell(row, 8).Value  = l.Channel ?? "";
                ws.Cell(row, 9).Value  = statuses.NameOf(l.Status);
                ws.Cell(row, 10).Value = l.Score;
                if (l.EstimatedValue.HasValue)
                    ws.Cell(row, 11).Value = l.EstimatedValue.Value;
                ws.Cell(row, 12).Value = l.Currency;
                ws.Cell(row, 13).Value = owner.Email ?? "";
                ws.Cell(row, 14).Value = l.Industry ?? "";

                // Custom fields
                values.TryGetValue(l.Id, out var mine);
                for (var i = 0; i < defs.Count; i++)
                {
                    var d = defs[i];
                    string? wire = null;
                    mine?.TryGetValue(d.Id, out wire);
                    WriteCustom(ws.Cell(row, firstCustom + i), d, wire, optionLabels);
                }

                // Read-only extras
                var e = firstExtra;
                ws.Cell(row, e++).Value = string.IsNullOrWhiteSpace(l.OwnerUserId)
                    ? "Unassigned"
                    : (owner.Name is { Length: > 0 } ? owner.Name : "");
                ws.Cell(row, e++).Value = l.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm");
                ws.Cell(row, e++).Value = l.LastActivity?.ActivityDate.ToString("yyyy-MM-dd HH:mm") ?? "";
                ws.Cell(row, e++).Value = l.LastActivity?.ActivityType ?? "";
                ws.Cell(row, e++).Value = l.Deal != null ? "Yes" : "No";
                ws.Cell(row, e++).Value = l.Deal?.Stage ?? "";
                if (l.Deal != null) ws.Cell(row, e).Value = l.Deal?.ExpectedValue ?? 0m;
                e++;
                ws.Cell(row, e).Value = l.Id.ToString();

                row++;
            }

            // Phone numbers stay text: "0812345678" must not become 812345678.
            ws.Column(3).Style.NumberFormat.Format = "@";

            ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents();

            // 105 — VUL-15. After AdjustToContents, which reads the styles.
            foreach (var cell in ws.CellsUsed(c => c.DataType == XLDataType.Text))
                if (SpreadsheetText.LooksLikeFormula(cell.GetString()))
                    cell.Style.IncludeQuotePrefix = true;

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            return new LeadExportFile(stream.ToArray(), rows.Count);
        }

        /// <summary>
        /// The ACTIVE custom field values of every lead in <paramref name="leads"/>.
        /// The lead query is used as a subquery, so this is one round trip
        /// however many leads there are.
        /// </summary>
        private async Task<Dictionary<Guid, Dictionary<Guid, string>>> CustomValuesAsync(
            IQueryable<Lead> leads, IReadOnlyList<CustomFieldDefinition> defs, Guid tenantId, CancellationToken ct)
        {
            var result = new Dictionary<Guid, Dictionary<Guid, string>>();
            if (defs.Count == 0) return result;

            var byId    = defs.ToDictionary(d => d.Id);
            var defIds  = byId.Keys.ToList();
            var leadIds = leads.Select(l => l.Id);

            var raw = await _db.CustomFieldValues.AsNoTracking()
                .Where(v => v.TenantId == tenantId && defIds.Contains(v.DefinitionId) && leadIds.Contains(v.EntityId))
                .Select(v => new { v.EntityId, v.DefinitionId, v.TextValue, v.NumberValue, v.DateValue, v.BoolValue })
                .ToListAsync(ct);

            foreach (var v in raw)
            {
                var wire = CustomFieldValueRules.ToWire(
                    byId[v.DefinitionId].FieldType, v.TextValue, v.NumberValue, v.DateValue, v.BoolValue);
                if (string.IsNullOrEmpty(wire)) continue;

                if (!result.TryGetValue(v.EntityId, out var map))
                    result[v.EntityId] = map = new Dictionary<Guid, string>();
                map[v.DefinitionId] = wire;
            }

            return result;
        }

        /// <summary>
        /// One custom value in the shape the importer reads back (note 2).
        /// A checkbox writes Yes or No — an empty cell would read as "not
        /// exported" to a person looking at the sheet.
        /// </summary>
        private static void WriteCustom(
            IXLCell cell, CustomFieldDefinition d, string? wire,
            IReadOnlyDictionary<Guid, Dictionary<string, string>> optionLabels)
        {
            if (d.FieldType == CustomFieldTypes.Checkbox)
            {
                cell.Value = wire == "true" ? "Yes" : "No";
                return;
            }

            if (string.IsNullOrEmpty(wire)) return;

            switch (d.FieldType)
            {
                case CustomFieldTypes.Number:
                    if (decimal.TryParse(wire, System.Globalization.NumberStyles.AllowLeadingSign |
                                               System.Globalization.NumberStyles.AllowDecimalPoint,
                                         System.Globalization.CultureInfo.InvariantCulture, out var n))
                        cell.Value = n;
                    else
                        cell.Value = wire;
                    break;

                case CustomFieldTypes.Date:
                    if (DateOnly.TryParseExact(wire, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                                               System.Globalization.DateTimeStyles.None, out var date))
                    {
                        cell.Value = date.ToDateTime(TimeOnly.MinValue);
                        cell.Style.NumberFormat.Format = "yyyy-mm-dd";
                    }
                    else
                    {
                        cell.Value = wire;
                    }
                    break;

                case CustomFieldTypes.Dropdown:
                    // The choice NAME; the key means nothing to a person,
                    // and the importer matches by name.
                    cell.Value = optionLabels.TryGetValue(d.Id, out var labels) && labels.TryGetValue(wire, out var label)
                        ? label
                        : wire;
                    break;

                default:
                    cell.Value = wire;
                    break;
            }
        }
    }
}
