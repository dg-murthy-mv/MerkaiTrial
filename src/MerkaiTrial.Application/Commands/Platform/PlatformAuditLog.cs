// =====================================================================
// FILE: MerkaiTrial.Application/Commands/Platform/PlatformAuditLog.cs
//
// NEW (119). Admin → Audit Log: everything MadeeVision's own people did.
//
// WHICH ROWS. Every audit row whose ACTOR is a super admin (ActorUserId),
// wherever it was written:
//   • console changes (PlatformChange, written by SuperAdminPageFilter)
//   • support in a customer's workspace (unlock, reset, admin changes …)
//   • View As started / stopped, and everything done while viewing as
//     (AuditService records the REAL person as the actor)
//   • their own sign-ins
// plus every row written into MadeeVision's own workspace (staff added or
// switched off), whoever wrote it.
//
// Rows are add-only (106), so this is a record nobody can quietly edit.
//
// FAST ENOUGH: the time window is always applied, the actor list is a
// handful of ids, and Sql/119_AuditLogs_ActorIndex.sql gives SQL Server an
// index on (ActorUserId, CreatedAtUtc). 121-update1: the rows are found
// by id first (two index seeks), and only then filtered and searched.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.Platform;

public sealed class PlatformAuditQuery
{
    /// <summary>1, 7, 30, 90 or 365.</summary>
    public int Days { get; set; } = 30;
    /// <summary>One member of staff.</summary>
    public Guid? Who { get; set; }
    /// <summary>One customer workspace.</summary>
    public Guid? Workspace { get; set; }
    /// <summary>all | changes | problems | support | viewas | signin.</summary>
    public string? Kind { get; set; }
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public sealed class PlatformAuditRow
{
    public Guid Id { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string By { get; set; } = string.Empty;
    public Guid? ActorUserId { get; set; }
    public Guid? TenantId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string? Data { get; set; }
    public string? IpAddress { get; set; }

    // Filled in after the query.
    public string WorkspaceName { get; set; } = string.Empty;
    public bool InOwnWorkspace { get; set; }
}

public sealed class PlatformAuditResult
{
    public List<PlatformAuditRow> Rows { get; set; } = new();
    public int TotalCount { get; set; }
    public List<(Guid Id, string Name)> Staff { get; set; } = new();
    public List<(Guid Id, string Name)> Workspaces { get; set; } = new();
}

public sealed class PlatformAuditLogHandler
{
    public static readonly int[] DayChoices = { 1, 7, 30, 90, 365 };

    private static readonly string[] SupportActions =
    {
        AuditAction.AccountUnlocked, AuditAction.PermissionChanged, AuditAction.InviteIssued,
        AuditAction.ResetIssued, AuditAction.StaffAdded, AuditAction.StaffSwitchedOn, AuditAction.StaffSwitchedOff,
    };

    private readonly FlowDbContext _db;
    public PlatformAuditLogHandler(FlowDbContext db) => _db = db;

    public async Task<PlatformAuditResult> HandleAsync(PlatformAuditQuery q, CancellationToken ct = default)
    {
        var days = DayChoices.Contains(q.Days) ? q.Days : 30;
        var since = DateTime.UtcNow.AddDays(-days);
        var size = q.PageSize is 10 or 25 or 50 or 100 ? q.PageSize : 25;
        var page = Math.Max(1, q.Page);

        // MadeeVision's people (switched off and deleted ones too: what they did still counts).
        var staff = await _db.Users.AsNoTracking().IgnoreQueryFilters()
            .Where(u => u.IsSuperAdmin)
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
            .Select(u => new { u.Id, u.TenantId, Name = (u.FirstName + " " + u.LastName).Trim(), u.Email })
            .ToListAsync(ct);
        var staffIds = staff.Select(s => s.Id).ToList();
        var ownTenants = staff.Select(s => s.TenantId).Distinct().ToList();

        // 121-update1 — FIRST the ids of the rows this page is about, by two
        // index seeks (staff → IX_AuditLogs_Actor_CreatedAt, MadeeVision's
        // workspace → IX_AuditLogs_Tenant_CreatedAt), THEN everything else
        // on just those rows. Asked as one "staff OR own workspace" query,
        // SQL Server read the whole audit table — 203,000 rows in the
        // performance workspace — and opened every row's Data for a search:
        // 5 s for a year searched, 1.9 s for one workspace.
        var byStaff = _db.AuditLogs.AsNoTracking()
            .Where(a => a.ActorUserId != null && staffIds.Contains(a.ActorUserId.Value) && a.CreatedAtUtc >= since)
            .Select(a => a.Id);
        var inOwnWorkspace = _db.AuditLogs.AsNoTracking()
            .Where(a => a.TenantId != null && ownTenants.Contains(a.TenantId.Value) && a.CreatedAtUtc >= since)
            .Select(a => a.Id);
        var candidateIds = await byStaff.Union(inOwnWorkspace).ToListAsync(ct);

        var rows = _db.AuditLogs.AsNoTracking().Where(a => candidateIds.Contains(a.Id));

        if (q.Who is { } who)
            rows = rows.Where(a => a.ActorUserId == who);

        if (q.Workspace is { } w)
            rows = rows.Where(a => a.TenantId == w ||
                                   (a.Action == AuditAction.PlatformChange && a.EntityType == "Workspace" && a.EntityId == w));

        switch ((q.Kind ?? "all").ToLowerInvariant())
        {
            case "changes":
                rows = rows.Where(a => a.Action == AuditAction.PlatformChange);
                break;
            case "problems":
                rows = rows.Where(a => a.Action == AuditAction.PlatformChange && a.Data != null &&
                                       (a.Data.Contains("\"outcome\":\"refused\"") || a.Data.Contains("\"outcome\":\"failed\"")));
                break;
            case "support":
                rows = rows.Where(a => SupportActions.Contains(a.Action));
                break;
            case "viewas":
                rows = rows.Where(a => a.Action == AuditAction.ViewAsStart || a.Action == AuditAction.ViewAsEnd ||
                                       (a.TenantId != null && !ownTenants.Contains(a.TenantId.Value) &&
                                        a.Action != AuditAction.PlatformChange && !SupportActions.Contains(a.Action)));
                break;
            case "signin":
                rows = rows.Where(a => a.Action == AuditAction.SignIn || a.Action == AuditAction.SignInFailed);
                break;
        }

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            if (s.Length > 100) s = s[..100];
            rows = rows.Where(a => a.By.Contains(s) || a.Action.Contains(s) || a.EntityType.Contains(s) ||
                                   (a.Data != null && a.Data.Contains(s)));
        }

        var total = await rows.CountAsync(ct);
        var list = await rows
            .OrderByDescending(a => a.CreatedAtUtc).ThenByDescending(a => a.Id)
            .Skip((page - 1) * size).Take(size)
            .Select(a => new PlatformAuditRow
            {
                Id = a.Id, CreatedAtUtc = a.CreatedAtUtc, By = a.By, ActorUserId = a.ActorUserId,
                TenantId = a.TenantId, Action = a.Action, EntityType = a.EntityType, EntityId = a.EntityId,
                Data = a.Data, IpAddress = a.IpAddress,
            })
            .ToListAsync(ct);

        // Names for the workspaces on this page (the row's own, or the workspace a console change was about).
        var ids = list.Select(r => r.TenantId).Concat(list.Where(r => r.EntityType == "Workspace").Select(r => r.EntityId))
            .Where(g => g.HasValue).Select(g => g!.Value).Distinct().ToList();
        var names = await _db.Tenants.AsNoTracking().IgnoreQueryFilters()
            .Where(t => ids.Contains(t.Id)).Select(t => new { t.Id, t.Name }).ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        foreach (var r in list)
        {
            var about = r.Action == AuditAction.PlatformChange && r.EntityType == "Workspace" ? r.EntityId : r.TenantId;
            r.InOwnWorkspace = r.TenantId is { } tid && ownTenants.Contains(tid) && about == r.TenantId;
            r.WorkspaceName = about is { } a && names.TryGetValue(a, out var n) ? n : "";
        }

        var workspaces = await _db.Tenants.AsNoTracking().IgnoreQueryFilters()
            .Where(t => !t.IsDeleted)
            .OrderBy(t => t.Name)
            .Select(t => new { t.Id, t.Name })
            .Take(1000)
            .ToListAsync(ct);

        return new PlatformAuditResult
        {
            Rows = list,
            TotalCount = total,
            Staff = staff.Select(s => (s.Id, string.IsNullOrWhiteSpace(s.Name) ? s.Email : s.Name)).ToList(),
            Workspaces = workspaces.Select(t => (t.Id, t.Name)).ToList(),
        };
    }
}
