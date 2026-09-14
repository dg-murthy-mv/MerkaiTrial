// =====================================================================
// AuditQueries.cs
// Location: MerkaiTrial.Application/Queries/AuditQueries.cs
//
// NEW FILE. Read side of the audit trail.
//
// TENANT SCOPING: AuditLogs has NO global query filter — it is in
// FlowDbContext's TenantFilterExemptions, because SuperAdmin reads are
// legitimately cross-tenant. That makes the explicit TenantId condition
// below the ONLY thing keeping one client's history out of another's.
// Do not remove it, and do not add a code path that omits it.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Queries;

// ── DTOs ──────────────────────────────────────────────────────────────

public record AuditLogListItem(
    Guid Id,
    string Action,
    string EntityType,
    Guid? EntityId,
    string By,
    Guid? ActorUserId,
    /// <summary>Set when ActorUserId differs from the acting identity —
    /// i.e. a SuperAdmin was impersonating. This is the whole point of
    /// recording both.</summary>
    string? RealActorName,
    string? IpAddress,
    string? Data,
    DateTime CreatedAtUtc);

public record AuditLogPage(
    List<AuditLogListItem> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages);

public record AuditLogFilter(
    Guid TenantId,
    string? Search = null,
    string? EntityType = null,
    string? Action = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Page = 1,
    int PageSize = 25);

// ── Handler ───────────────────────────────────────────────────────────

public class GetAuditLogsHandler : ICommandHandler
{
    private const int MaxPageSize = 100;

    private readonly FlowDbContext _db;

    public GetAuditLogsHandler(FlowDbContext db) => _db = db;

    public async Task<AuditLogPage> Handle(AuditLogFilter filter, CancellationToken ct = default)
    {
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 5, MaxPageSize);

        // The tenant condition is the only scoping — see the file header.
        var q = _db.Set<AuditLog>().AsNoTracking()
            .Where(a => a.TenantId == filter.TenantId);

        if (!string.IsNullOrWhiteSpace(filter.EntityType))
            q = q.Where(a => a.EntityType == filter.EntityType);

        if (!string.IsNullOrWhiteSpace(filter.Action))
            q = q.Where(a => a.Action == filter.Action);

        if (filter.FromUtc.HasValue)
            q = q.Where(a => a.CreatedAtUtc >= filter.FromUtc.Value);

        if (filter.ToUtc.HasValue)
            q = q.Where(a => a.CreatedAtUtc <= filter.ToUtc.Value);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            // Data is searched too: it holds the record name, which is
            // usually what someone is actually looking for ("what happened
            // to Pimchanok"), and after a delete it is the ONLY place that
            // name still exists.
            q = q.Where(a => a.By.Contains(term)
                          || a.Action.Contains(term)
                          || (a.Data != null && a.Data.Contains(term)));
        }

        var totalCount = await q.CountAsync(ct);

        var rows = await q
            .OrderByDescending(a => a.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new
            {
                a.Id, a.Action, a.EntityType, a.EntityId,
                a.By, a.ActorUserId, a.IpAddress, a.Data, a.CreatedAtUtc
            })
            .ToListAsync(ct);

        // Resolve the REAL actor's name where it differs from the acting
        // identity. During ViewAs, By is the impersonated user and
        // ActorUserId is the super admin — showing only one of the two
        // would hide exactly what the audit trail exists to reveal.
        var actorIds = rows
            .Where(r => r.ActorUserId.HasValue)
            .Select(r => r.ActorUserId!.Value)
            .Distinct()
            .ToList();

        var actorNames = new Dictionary<Guid, string>();
        if (actorIds.Count > 0)
        {
            // IgnoreQueryFilters is not needed: Users has no global filter.
            // But a super admin belongs to ANOTHER tenant, so this lookup
            // deliberately does not filter by tenant — otherwise the
            // impersonator's name would never resolve.
            var users = await _db.Users.AsNoTracking()
                .Where(u => actorIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email })
                .ToListAsync(ct);

            foreach (var u in users)
            {
                var name = $"{u.FirstName} {u.LastName}".Trim();
                actorNames[u.Id] = string.IsNullOrWhiteSpace(name) ? u.Email : name;
            }
        }

        var items = rows.Select(r =>
        {
            string? realActor = null;
            if (r.ActorUserId.HasValue && actorNames.TryGetValue(r.ActorUserId.Value, out var n))
            {
                // Only surface it when it is genuinely someone else —
                // otherwise every row would show the same name twice.
                if (!string.Equals(n, r.By, StringComparison.OrdinalIgnoreCase))
                    realActor = n;
            }

            return new AuditLogListItem(
                r.Id, r.Action, r.EntityType, r.EntityId,
                r.By, r.ActorUserId, realActor, r.IpAddress, r.Data, r.CreatedAtUtc);
        }).ToList();

        return new AuditLogPage(
            Items: items,
            TotalCount: totalCount,
            Page: page,
            PageSize: pageSize,
            TotalPages: (int)Math.Ceiling(totalCount / (double)pageSize));
    }
}

/// <summary>
/// The distinct entity types and actions actually present for this
/// tenant, so the filter dropdowns offer only what exists rather than
/// every constant in AuditAction.
/// </summary>
public class GetAuditFiltersHandler : ICommandHandler
{
    private readonly FlowDbContext _db;

    public GetAuditFiltersHandler(FlowDbContext db) => _db = db;

    public async Task<(List<string> EntityTypes, List<string> Actions)> Handle(
        Guid tenantId, CancellationToken ct = default)
    {
        var entityTypes = await _db.Set<AuditLog>().AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .Select(a => a.EntityType)
            .Distinct().OrderBy(x => x)
            .ToListAsync(ct);

        var actions = await _db.Set<AuditLog>().AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .Select(a => a.Action)
            .Distinct().OrderBy(x => x)
            .ToListAsync(ct);

        return (entityTypes, actions);
    }
}
