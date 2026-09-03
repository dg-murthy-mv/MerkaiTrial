using ClosedXML.Excel;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace MerkaiTrial.Application.Commands.Leads
{
    // ==================== EXCEL EXPORT ====================
    public class ExportLeadsHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;

        public ExportLeadsHandler(FlowDbContext db) => _db = db;

        public async Task<byte[]> Handle(ExportLeadsRequest request)
        {
            // Build query
            var query = from l in _db.Leads
                        join c in _db.Contacts on l.ContactId equals c.Id
                        where l.TenantId == request.TenantId && !l.IsDeleted
                        let deal = _db.Deals.FirstOrDefault(d => d.LeadId == l.Id && !d.IsDeleted)
                        let lastActivity = _db.LeadActivities
                            .Where(a => a.LeadId == l.Id && !a.IsDeleted)
                            .OrderByDescending(a => a.ActivityDate)
                            .Select(a => new { a.ActivityDate, a.ActivityType })
                            .FirstOrDefault()
                        select new
                        {
                            l.Id,
                            FullName = c.FirstName,
                            Email = c.Email ?? "",
                            Phone = c.Phone ?? "",
                            Channel = l.Channel.ToString(),
                            Source = l.Source,
                            Status = l.Status.ToString(),
                            Score = l.Score,
                            Owner = l.OwnerUserId ?? "",
                            CreatedDate = l.CreatedAtUtc,
                            LastActivityDate = lastActivity != null ? lastActivity.ActivityDate : (DateTime?)null,
                            LastActivityType = lastActivity != null ? lastActivity.ActivityType : "",
                            HasDeal = deal != null,
                            DealStage = deal != null ? deal.Stage.ToString() : "",
                            DealValue = deal != null ? deal.ExpectedValue : 0m
                            
                        };

            // Apply filters
            if (!string.IsNullOrWhiteSpace(request.SearchTerm))
            {
                var searchLower = request.SearchTerm.ToLower();
                query = query.Where(x =>
                    x.FullName.ToLower().Contains(searchLower) ||
                    x.Email.ToLower().Contains(searchLower) ||
                    x.Phone.ToLower().Contains(searchLower) ||
                    x.Source.ToLower().Contains(searchLower)
                );
            }

            if (!string.IsNullOrWhiteSpace(request.Status))
            {
                query = query.Where(x => x.Status == request.Status);
            }

            if (!string.IsNullOrWhiteSpace(request.AssignedTo))
            {
                query = query.Where(x => x.Owner == request.AssignedTo);
            }

            var leads = await query.OrderByDescending(x => x.CreatedDate).ToListAsync();

            // Create Excel file
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Leads");

            // Headers
            worksheet.Cell(1, 1).Value = "ID";
            worksheet.Cell(1, 2).Value = "Full Name";
            worksheet.Cell(1, 3).Value = "Email";
            worksheet.Cell(1, 4).Value = "Phone";
            worksheet.Cell(1, 5).Value = "Channel";
            worksheet.Cell(1, 6).Value = "Source";
            worksheet.Cell(1, 7).Value = "Status";
            worksheet.Cell(1, 8).Value = "Score";
            worksheet.Cell(1, 9).Value = "Assigned To";
            worksheet.Cell(1, 10).Value = "Created Date";
            worksheet.Cell(1, 11).Value = "Last Activity Date";
            worksheet.Cell(1, 12).Value = "Last Activity Type";
            worksheet.Cell(1, 13).Value = "Has Deal";
            worksheet.Cell(1, 14).Value = "Deal Stage";
            worksheet.Cell(1, 15).Value = "Deal Value";
            worksheet.Cell(1, 16).Value = "Currency";

            // Style header row
            var headerRow = worksheet.Row(1);
            headerRow.Style.Font.Bold = true;
            headerRow.Style.Fill.BackgroundColor = XLColor.LightBlue;

            // Data rows
            int row = 2;
            foreach (var lead in leads)
            {
                worksheet.Cell(row, 1).Value = lead.Id.ToString();
                worksheet.Cell(row, 2).Value = lead.FullName;
                worksheet.Cell(row, 3).Value = lead.Email;
                worksheet.Cell(row, 4).Value = lead.Phone;
                worksheet.Cell(row, 5).Value = lead.Channel;
                worksheet.Cell(row, 6).Value = lead.Source;
                worksheet.Cell(row, 7).Value = lead.Status;
                worksheet.Cell(row, 8).Value = lead.Score;
                worksheet.Cell(row, 9).Value = lead.Owner;
                worksheet.Cell(row, 10).Value = lead.CreatedDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                worksheet.Cell(row, 11).Value = lead.LastActivityDate?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
                worksheet.Cell(row, 12).Value = lead.LastActivityType;
                worksheet.Cell(row, 13).Value = lead.HasDeal ? "Yes" : "No";
                worksheet.Cell(row, 14).Value = lead.DealStage;
                worksheet.Cell(row, 15).Value = lead.DealValue;
                
                row++;
            }

            // Auto-fit columns
            worksheet.Columns().AdjustToContents();

            // Convert to byte array
            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            return stream.ToArray();
        }
    }

    // ==================== EXCEL/CSV IMPORT ====================
    public class ImportLeadsHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;

        public ImportLeadsHandler(FlowDbContext db) => _db = db;

        public async Task<ImportLeadsResult> Handle(ImportLeadsRequest request)
        {
            var errors = new List<string>();
            int successCount = 0;
            int failedCount = 0;

            foreach (var (row, index) in request.Rows.Select((r, i) => (r, i + 1)))
            {
                try
                {
                    // Validate required fields
                    if (string.IsNullOrWhiteSpace(row.FullName))
                    {
                        errors.Add($"Row {index}: Full name is required");
                        failedCount++;
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(row.Email))
                    {
                        errors.Add($"Row {index}: Email is required");
                        failedCount++;
                        continue;
                    }

                    // Check if email already exists
                    var existingContact = await _db.Contacts
                        .FirstOrDefaultAsync(c => c.Email == row.Email.Trim());

                    Contact contact;
                    if (existingContact != null)
                    {
                        // Update existing contact
                        contact = existingContact;
                        contact.FirstName = row.FullName.Trim();
                        contact.Phone = row.Phone?.Trim();
                        contact.UpdatedAtUtc = DateTime.UtcNow;
                    }
                    else
                    {
                        // Create new contact
                        contact = new Contact
                        {
                            Id = Guid.NewGuid(),
                            FirstName = row.FullName.Trim(),
                            Email = row.Email.Trim(),
                            Phone = row.Phone?.Trim(),
                            CreatedAtUtc = DateTime.UtcNow,
                            CreatedBy = request.ImportedBy
                        };
                        _db.Contacts.Add(contact);
                    }

                    // Parse channel
                    var channel = Channel.Web;
                    if (!string.IsNullOrWhiteSpace(row.Channel))
                    {
                        if (Enum.TryParse<Channel>(row.Channel, true, out var parsedChannel))
                            channel = parsedChannel;
                    }

                    // Parse status
                    var status = LeadStatus.New;
                    if (!string.IsNullOrWhiteSpace(row.Status))
                    {
                        if (Enum.TryParse<LeadStatus>(row.Status, true, out var parsedStatus))
                            status = parsedStatus;
                    }

                    // Check if lead already exists for this contact
                    var existingLead = await _db.Leads
                        .FirstOrDefaultAsync(l => 
                            l.ContactId == contact.Id && 
                            l.TenantId == request.TenantId && 
                            !l.IsDeleted);

                    if (existingLead != null)
                    {
                        // Update existing lead
                        existingLead.Channel = channel;
                        existingLead.Source = row.Source?.Trim() ?? "Import";
                        existingLead.Status = status;
                        existingLead.Score = row.Score ?? 0;
                        existingLead.OwnerUserId = row.OwnerUserId?.Trim();
                        existingLead.UpdatedAtUtc = DateTime.UtcNow;
                    }
                    else
                    {
                        // Create new lead
                        var lead = new Lead
                        {
                            Id = Guid.NewGuid(),
                            TenantId = request.TenantId,
                            ContactId = contact.Id,
                            Channel = channel,
                            Source = row.Source?.Trim() ?? "Import",
                            Status = status,
                            Score = row.Score ?? 0,
                            OwnerUserId = row.OwnerUserId?.Trim(),
                            CreatedAtUtc = DateTime.UtcNow,
                            CreatedBy = request.ImportedBy
                        };
                        _db.Leads.Add(lead);
                    }

                    await _db.SaveChangesAsync();
                    successCount++;
                }
                catch (Exception ex)
                {
                    errors.Add($"Row {index}: {ex.Message}");
                    failedCount++;
                }
            }

            return new ImportLeadsResult(
                request.Rows.Count,
                successCount,
                failedCount,
                errors
            );
        }
    }

    // ==================== CSV PARSING HELPER ====================
    public class ParseCsvHelper
    {
        public static List<ImportLeadRow> ParseCsv(string csvContent)
        {
            var rows = new List<ImportLeadRow>();
            var lines = csvContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length < 2)
                throw new InvalidOperationException("CSV file must have at least a header row and one data row");

            // Skip header
            for (int i = 1; i < lines.Length; i++)
            {
                var values = ParseCsvLine(lines[i]);
                if (values.Count < 2) continue; // Skip empty rows

                rows.Add(new ImportLeadRow(
                    FullName: values.ElementAtOrDefault(0) ?? "",
                    Email: values.ElementAtOrDefault(1) ?? "",
                    Phone: values.ElementAtOrDefault(2),
                    Channel: values.ElementAtOrDefault(3),
                    Source: values.ElementAtOrDefault(4),
                    Status: values.ElementAtOrDefault(5),
                    OwnerUserId: values.ElementAtOrDefault(6),
                    Score: int.TryParse(values.ElementAtOrDefault(7), out var score) ? score : null
                ));
            }

            return rows;
        }

        private static List<string> ParseCsvLine(string line)
        {
            var values = new List<string>();
            var currentValue = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    values.Add(currentValue.ToString().Trim());
                    currentValue.Clear();
                }
                else
                {
                    currentValue.Append(c);
                }
            }

            values.Add(currentValue.ToString().Trim());
            return values;
        }
    }

    // ==================== EXCEL PARSING HELPER ====================
    public class ParseExcelHelper
    {
        public static List<ImportLeadRow> ParseExcel(byte[] fileBytes)
        {
            var rows = new List<ImportLeadRow>();

            using var stream = new MemoryStream(fileBytes);
            using var workbook = new XLWorkbook(stream);
            var worksheet = workbook.Worksheet(1);

            // Skip header row
            var dataRows = worksheet.RowsUsed().Skip(1);

            foreach (var row in dataRows)
            {
                rows.Add(new ImportLeadRow(
                    FullName: row.Cell(1).GetString(),
                    Email: row.Cell(2).GetString(),
                    Phone: row.Cell(3).GetString(),
                    Channel: row.Cell(4).GetString(),
                    Source: row.Cell(5).GetString(),
                    Status: row.Cell(6).GetString(),
                    OwnerUserId: row.Cell(7).GetString(),
                    Score: int.TryParse(row.Cell(8).GetString(), out var score) ? score : null
                ));
            }

            return rows;
        }
    }
}
