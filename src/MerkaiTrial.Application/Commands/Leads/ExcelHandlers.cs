// =====================================================================
// ExcelHandlers.cs
// Location: MerkaiTrial.Application/Commands/Leads/ExcelHandlers.cs
//
// COMPLETE FILE — replaces the existing one.
//
// FIXES
//   1. RECORD VISIBILITY (015): exports only the leads the user may see.
//      Before, a rep with "Own" visibility could export every lead in the
//      workspace — the export was the one list with no filter at all.
//   2. The query INNER JOINED Contacts on l.ContactId. A lead only gets a
//      contact when it is converted, so every open lead was silently left
//      out of the export. It now reads the lead's own name, email, phone.
//   3. "Full Name" was the contact's FIRST name only.
//   4. Status exported the KEY ("SiteVisit"); now the tenant's name.
//   5. Owner exported a user GUID; now the user's name.
//   6. Status filter accepts several keys (Active tab), like the list.
//   7. Dates used the SERVER's time zone (ToLocalTime); now UTC, labelled.
//   8. Currency column had a header but no value.
//
// The commented-out import helpers at the bottom of the old file are gone
// — import lives in Commands/Leads/Import/ now.
// =====================================================================

using ClosedXML.Excel;
using MerkaiTrial.Application.Commands.LeadStatuses;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.Leads
{
    // ==================== EXCEL EXPORT ====================
    public class ExportLeadsHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly IRecordScopeService _scope;
        private readonly ILeadStatusResolver _statuses;

        public ExportLeadsHandler(FlowDbContext db, IRecordScopeService scope, ILeadStatusResolver statuses)
        {
            _db = db;
            _scope = scope;
            _statuses = statuses;
        }

        public async Task<byte[]> Handle(ExportLeadsRequest request)
        {
            var access = await _scope.GetAsync(RecordModules.Leads);
            var statuses = await _statuses.GetAsync(request.TenantId);

            var leads = _db.Leads.AsNoTracking()
                .Where(l => l.TenantId == request.TenantId && !l.IsDeleted)
                .VisibleTo(access);

            // ── Filters (same meaning as the Leads list) ─────────────────
            if (!string.IsNullOrWhiteSpace(request.SearchTerm))
            {
                var s = request.SearchTerm.ToLower();
                leads = leads.Where(l =>
                    l.FullName.ToLower().Contains(s) ||
                    (l.Email != null && l.Email.ToLower().Contains(s)) ||
                    (l.Phone != null && l.Phone.ToLower().Contains(s)) ||
                    (l.CompanyName != null && l.CompanyName.ToLower().Contains(s)));
            }

            if (!string.IsNullOrWhiteSpace(request.Status))
            {
                var keys = request.Status.Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                leads = leads.Where(l => keys.Contains(l.Status));
            }

            if (!string.IsNullOrWhiteSpace(request.AssignedTo))
                leads = leads.Where(l => l.OwnerUserId == request.AssignedTo);

            var rows = await leads
                .OrderByDescending(l => l.CreatedAtUtc)
                .Select(l => new
                {
                    l.Id,
                    l.FullName,
                    Email = l.Email ?? "",
                    Phone = l.Phone ?? "",
                    Company = l.CompanyName ?? "",
                    Channel = l.LeadChannel != null ? l.LeadChannel.Name : l.Channel.ToString(),
                    Source = l.LeadSource != null ? l.LeadSource.Name : (l.Source ?? ""),
                    l.Status,
                    l.Score,
                    l.OwnerUserId,
                    l.CreatedAtUtc,
                    l.EstimatedValue,
                    Currency = l.Currency ?? "",
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
                .ToListAsync();

            // Owner GUID → name, one query.
            var ownerIds = rows
                .Select(r => Guid.TryParse(r.OwnerUserId, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .Distinct()
                .ToList();

            var owners = await _db.Users.AsNoTracking()
                .Where(u => u.TenantId == request.TenantId && ownerIds.Contains(u.Id))
                .Select(u => new { u.Id, Name = (u.FirstName + " " + u.LastName).Trim() })
                .ToDictionaryAsync(u => u.Id.ToString(), u => u.Name, StringComparer.OrdinalIgnoreCase);

            string OwnerName(string? id) =>
                string.IsNullOrWhiteSpace(id) ? "Unassigned"
                : owners.TryGetValue(id, out var n) ? n : id;

            // ── Workbook ──────────────────────────────────────────────────
            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Leads");

            string[] headers =
            {
                "ID", "Full Name", "Email", "Phone", "Company", "Channel", "Source",
                "Status", "Score", "Assigned To", "Created (UTC)",
                "Last Activity (UTC)", "Last Activity Type",
                "Has Deal", "Deal Stage", "Deal Value", "Estimated Value", "Currency"
            };

            for (var c = 0; c < headers.Length; c++)
                ws.Cell(1, c + 1).Value = headers[c];

            var header = ws.Row(1);
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = XLColor.LightBlue;

            var row = 2;
            foreach (var l in rows)
            {
                ws.Cell(row, 1).Value  = l.Id.ToString();
                ws.Cell(row, 2).Value  = l.FullName;
                ws.Cell(row, 3).Value  = l.Email;
                ws.Cell(row, 4).Value  = l.Phone;
                ws.Cell(row, 5).Value  = l.Company;
                ws.Cell(row, 6).Value  = l.Channel;
                ws.Cell(row, 7).Value  = l.Source;
                ws.Cell(row, 8).Value  = statuses.NameOf(l.Status);
                ws.Cell(row, 9).Value  = l.Score;
                ws.Cell(row, 10).Value = OwnerName(l.OwnerUserId);
                ws.Cell(row, 11).Value = l.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm");
                ws.Cell(row, 12).Value = l.LastActivity?.ActivityDate.ToString("yyyy-MM-dd HH:mm") ?? "";
                ws.Cell(row, 13).Value = l.LastActivity?.ActivityType ?? "";
                ws.Cell(row, 14).Value = l.Deal != null ? "Yes" : "No";
                ws.Cell(row, 15).Value = l.Deal?.Stage ?? "";
                ws.Cell(row, 16).Value = l.Deal?.ExpectedValue ?? 0m;
                ws.Cell(row, 17).Value = l.EstimatedValue ?? 0m;
                ws.Cell(row, 18).Value = l.Currency;
                row++;
            }

            ws.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            return stream.ToArray();
        }
    }
}
