using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MerkaiTrial.Infrastructure.Persistence;

namespace MerkaiTrial.WebApi.Controllers;

[ApiController]
[Route("api/leads")]
public class LeadsWorkflowController : ControllerBase
{
    private readonly FlowDbContext _db;
    private readonly ILogger<LeadsWorkflowController> _logger;

    public LeadsWorkflowController(FlowDbContext db, ILogger<LeadsWorkflowController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpPost("{leadId:guid}/convert")]
    public async Task<IActionResult> ConvertLead(Guid leadId, CancellationToken ct)
    {
        // --- Validate tenant header ---
        var tenantHeader = Request.Headers["X-Tenant-Id"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(tenantHeader))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Missing X-Tenant-Id header.");

        if (!Guid.TryParse(tenantHeader, out var tenantId))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid X-Tenant-Id format.");

        try
        {
            await using var conn = _db.Database.GetDbConnection();
            await conn.OpenAsync(ct);

            // 1) If a deal already exists for this lead, return it
            await using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT TOP (1) Id FROM dbo.Deals WHERE LeadId=@lead";
                Add(check, "@lead", leadId);
                var existing = await check.ExecuteScalarAsync(ct);
                if (existing is Guid g)
                {
                    _logger.LogInformation("Lead {LeadId} already converted to Deal {DealId}", leadId, g);
                    return Ok(new { dealId = g });
                }
            }

            // 2) Validate the lead exists and fetch ContactId/CompanyId (we also grab a display name for Title)
            Guid contactId;
            Guid? companyId = null;
            string? contactName = null;
            await using (var leadCmd = conn.CreateCommand())
            {
                leadCmd.CommandText = @"
SELECT l.ContactId, l.CompanyId, COALESCE(NULLIF(LTRIM(RTRIM(c.FirstName + ' ' + ISNULL(c.LastName,''))), ''), c.FirstName)
FROM dbo.Leads l
JOIN dbo.Contacts c ON c.Id = l.ContactId
WHERE l.Id = @id AND l.TenantId = @tid";
                Add(leadCmd, "@id", leadId);
                Add(leadCmd, "@tid", tenantId);
                await using var r = await leadCmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                    return NotFound("Lead not found for this tenant.");

                contactId = r.GetGuid(0);
                if (!await r.IsDBNullAsync(1, ct)) companyId = r.GetGuid(1);
                if (!await r.IsDBNullAsync(2, ct)) contactName = r.GetString(2);
            }

            // 3) Insert Deal (fixed: Stage='New', column name CreatedUtc, set TenantId & Title)
            var dealId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var title = string.IsNullOrWhiteSpace(contactName)
                ? $"Deal {now:yyyyMMddHHmm}"
                : $"{contactName} – New Deal";

            await using (var ins = conn.CreateCommand())
            {
                ins.CommandText = @"
INSERT INTO dbo.Deals
    (Id, LeadId, ContactId, CompanyId, Stage, Currency, ExpectedValue,
     CreatedUtc, CreatedBy, TenantId, Title)
VALUES
    (@deal, @lead, @contact, @company, N'New', N'THB', 0,
     SYSUTCDATETIME(), @createdBy, @tenantId, @title);";
                Add(ins, "@deal", dealId);
                Add(ins, "@lead", leadId);
                Add(ins, "@contact", contactId);
                Add(ins, "@company", (object?)companyId ?? DBNull.Value);
                Add(ins, "@createdBy", tenantHeader); // keep string for CreatedBy trace
                Add(ins, "@tenantId", tenantId);      // actual GUID for FK/queries
                Add(ins, "@title", title);
                await ins.ExecuteNonQueryAsync(ct);
            }

            _logger.LogInformation("Converted Lead {LeadId} to Deal {DealId} (Tenant {TenantId})", leadId, dealId, tenantId);
            return Ok(new { dealId });
        }
        catch (OperationCanceledException oce)
        {
            _logger.LogWarning(oce, "POST /api/leads/{LeadId}/convert canceled (Tenant {TenantId})", leadId, tenantId);
            return Problem(statusCode: StatusCodes.Status408RequestTimeout, title: "Request canceled or timed out.");
        }
        catch (SqlException sqlex)
        {
            _logger.LogError(sqlex, "SQL error converting lead {LeadId} (Tenant {TenantId})", leadId, tenantId);
            // FK/constraint violations -> 409
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Database constraint error.", detail: sqlex.GetBaseException().Message);
        }
        catch (DbException dbex)
        {
            _logger.LogError(dbex, "DB error converting lead {LeadId} (Tenant {TenantId})", leadId, tenantId);
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Database error.", detail: dbex.GetBaseException().Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error converting lead {LeadId} (Tenant {TenantId})", leadId, tenantId);
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Failed to convert lead.");
        }
    }

    private static void Add(DbCommand c, string name, object value)
    {
        var p = c.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        c.Parameters.Add(p);
    }
}
