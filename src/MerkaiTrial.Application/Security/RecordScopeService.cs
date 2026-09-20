// =====================================================================
// RecordScopeService.cs
// Location: MerkaiTrial.Application/Security/RecordScopeService.cs
//
// NEW FILE. The ONE place that decides which records the current user may
// see. Every lead handler asks this, then applies .VisibleTo(access).
//
// RULES
//   • Tenant admin           → All, always. Not configurable.
//   • Otherwise, for each of the user's roles:
//       tenant override (RoleRecordScopes) ?? RecordScopeDefaults
//     and the WIDEST wins (a user who is both rep and manager sees Team).
//   • User not found / no roles → Own. Fail closed.
//
//   Own  : OwnerUserId == me
//   Team : OwnerUserId in (members of my team + members of every team I
//          MANAGE, incl. me) OR unassigned
//   All  : no filter
//
//   A role that is locked (tenant_admin) is All whatever any override row
//   says — the override can't be created through the UI, but a row put in
//   by hand must not be able to blind the administrator.
//
// WHY READ FROM THE DB, NOT FROM CLAIMS
//   Claims are fixed at sign-in. If an admin moves a rep to Own, the rep
//   should lose the other leads on their next click, not after their
//   cookie expires. It costs two small indexed queries per request, and
//   the result is cached for the rest of the request (scoped service).
//
// WHY "NOT FOUND" RATHER THAN "FORBIDDEN"
//   A lead outside your scope is treated exactly like another tenant's
//   lead: KeyNotFoundException → 404. Returning 403 would confirm that the
//   id exists, which is itself information.
// =====================================================================

using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Security;

/// <summary>What the current user may see in one module.</summary>
public sealed class RecordAccess
{
    public RecordScope Scope { get; }

    /// <summary>Current user id, in the same format as OwnerUserId (Guid "D", lower case).</summary>
    public string UserId { get; }

    /// <summary>For Team scope: every user id in my team and the teams I manage, including mine.</summary>
    public IReadOnlyList<string> TeamUserIds { get; }

    public bool SeesAll => Scope == RecordScope.All;

    public RecordAccess(RecordScope scope, string userId, IReadOnlyList<string>? teamUserIds = null)
    {
        Scope = scope;
        UserId = userId;
        TeamUserIds = teamUserIds ?? new[] { userId };
    }

    public static RecordAccess Everything(string userId) => new(RecordScope.All, userId);
}

public interface IRecordScopeService
{
    /// <summary>What the current user may see in <paramref name="module"/> (RecordModules.*).</summary>
    Task<RecordAccess> GetAsync(string module, CancellationToken ct = default);
}

public class RecordScopeService : IRecordScopeService
{
    private readonly FlowDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<RecordScopeService> _logger;

    // Per-request cache — this service is registered Scoped.
    private readonly Dictionary<string, RecordAccess> _cache = new(StringComparer.OrdinalIgnoreCase);

    public RecordScopeService(
        FlowDbContext db,
        ICurrentUserService currentUser,
        ILogger<RecordScopeService> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<RecordAccess> GetAsync(string module, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(module, out var cached)) return cached;

        var userId = _currentUser.GetCurrentUserId();
        var tenantId = _currentUser.GetCurrentTenantId();
        var me = userId.ToString();

        // Users is NOT tenant-filtered (see FlowDbContext) — scope explicitly.
        var user = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId && u.TenantId == tenantId && !u.IsDeleted)
            .Select(u => new
            {
                u.IsTenantAdmin,
                u.TeamId,
                RoleIds = u.UserRoles.Select(r => r.RoleId).ToList()
            })
            .FirstOrDefaultAsync(ct);

        RecordAccess access;

        if (user is null)
        {
            // Fail closed. Should not happen for an authenticated request.
            _logger.LogWarning("RecordScope: user {UserId} not found in tenant {TenantId} — Own scope", userId, tenantId);
            access = new RecordAccess(RecordScope.Own, me);
        }
        else if (user.IsTenantAdmin)
        {
            access = RecordAccess.Everything(me);
        }
        else
        {
            var scope = await ResolveScopeAsync(user.RoleIds, module, ct);

            if (scope == RecordScope.Team)
            {
                // The teams whose records I see: the one I belong to, plus
                // every team I manage.
                var teams = await _db.TeamManagers.AsNoTracking()
                    .Where(m => m.UserId == userId)
                    .Select(m => m.TeamId)
                    .ToListAsync(ct);

                if (user.TeamId.HasValue) teams.Add(user.TeamId.Value);

                var teamIds = new List<string> { me };

                if (teams.Count > 0)
                {
                    var members = await _db.Users.AsNoTracking()
                        .Where(u => u.TenantId == tenantId && !u.IsDeleted &&
                                    u.TeamId.HasValue && teams.Contains(u.TeamId.Value))
                        .Select(u => u.Id)
                        .ToListAsync(ct);

                    teamIds.AddRange(members.Select(id => id.ToString()).Where(id => id != me).Distinct());
                }

                access = new RecordAccess(RecordScope.Team, me, teamIds);
            }
            else
            {
                access = new RecordAccess(scope, me);
            }
        }

        _logger.LogDebug("RecordScope: {Module} = {Scope} for {UserId}", module, access.Scope, userId);

        _cache[module] = access;
        return access;
    }

    private async Task<RecordScope> ResolveScopeAsync(List<Guid> roleIds, string module, CancellationToken ct)
    {
        if (roleIds.Count == 0) return RecordScope.Own;

        // Roles filter: TenantId null (built-in) or this tenant.
        var roles = await _db.Roles.AsNoTracking()
            .Where(r => roleIds.Contains(r.Id) && !r.IsDeleted)
            .Select(r => new { r.Id, r.Name })
            .ToListAsync(ct);

        if (roles.Count == 0) return RecordScope.Own;

        // Tenant filter applies — only THIS tenant's overrides.
        var overrides = await _db.RoleRecordScopes.AsNoTracking()
            .Where(s => s.Module == module && roleIds.Contains(s.RoleId))
            .ToDictionaryAsync(s => s.RoleId, s => s.Scope, ct);

        // Widest wins. A locked role (tenant_admin) is All, full stop.
        return roles
            .Select(r =>
                RecordScopeDefaults.IsLocked(r.Name) ? RecordScope.All
                : overrides.TryGetValue(r.Id, out var o) ? o
                : RecordScopeDefaults.For(r.Name, module))
            .Max();
    }
}

/// <summary>
/// The filter itself. One method per entity so the SQL stays a plain
/// WHERE clause EF can translate — no reflection, no expression tricks.
/// Quotes and invoices have no owner of their own — they follow the deal
/// they belong to (WithVisibleDeal).
/// </summary>
public static class RecordScopeQueryExtensions
{
    public static IQueryable<Deal> VisibleTo(this IQueryable<Deal> deals, RecordAccess access)
    {
        switch (access.Scope)
        {
            case RecordScope.All:
                return deals;

            case RecordScope.Own:
            {
                var me = access.UserId;
                return deals.Where(d => d.OwnerUserId == me);
            }

            default: // Team
            {
                var ids = access.TeamUserIds.ToList();
                return deals.Where(d =>
                    d.OwnerUserId == null ||
                    d.OwnerUserId == "" ||
                    ids.Contains(d.OwnerUserId));
            }
        }
    }

    /// <summary>Quotes on deals the user can see (pass the DEALS access).</summary>
    public static IQueryable<Quote> WithVisibleDeal(this IQueryable<Quote> quotes, FlowDbContext db, RecordAccess dealAccess)
    {
        if (dealAccess.SeesAll) return quotes;
        var dealIds = db.Deals.VisibleTo(dealAccess).Select(d => d.Id);
        return quotes.Where(q => dealIds.Contains(q.DealId));
    }

    /// <summary>
    /// Invoices on deals the user can see — linked directly, or through the
    /// quote they were raised from. An invoice linked to neither is visible
    /// only to All.
    /// </summary>
    public static IQueryable<Invoice> WithVisibleDeal(this IQueryable<Invoice> invoices, FlowDbContext db, RecordAccess dealAccess)
    {
        if (dealAccess.SeesAll) return invoices;
        var dealIds = db.Deals.VisibleTo(dealAccess).Select(d => d.Id);
        return invoices.Where(i =>
            (i.DealId != null && dealIds.Contains(i.DealId.Value)) ||
            (i.QuoteId != null && i.Quote != null && dealIds.Contains(i.Quote.DealId)));
    }

    public static IQueryable<Lead> VisibleTo(this IQueryable<Lead> leads, RecordAccess access)
    {
        switch (access.Scope)
        {
            case RecordScope.All:
                return leads;

            case RecordScope.Own:
            {
                var me = access.UserId;
                return leads.Where(l => l.OwnerUserId == me);
            }

            default: // Team
            {
                var ids = access.TeamUserIds.ToList();
                return leads.Where(l =>
                    l.OwnerUserId == null ||
                    l.OwnerUserId == "" ||
                    ids.Contains(l.OwnerUserId));
            }
        }
    }
}

/// <summary>
/// "Can the current user see lead / deal X?" — for handlers that act on
/// something hanging off a record (notes, attachments, tasks, timeline)
/// and only have its id.
///
///   EnsureLeadVisibleAsync — for writes: throws KeyNotFoundException,
///                            which controllers already turn into 404.
///   CanSeeLeadAsync        — for lists: the handler returns an empty list,
///                            exactly as it already does for a lead that
///                            doesn't exist.
/// </summary>
public static class RecordScopeGuards
{
    public static async Task<bool> CanSeeLeadAsync(
        this IRecordScopeService scope, FlowDbContext db,
        Guid tenantId, Guid leadId, CancellationToken ct = default)
    {
        var access = await scope.GetAsync(RecordModules.Leads, ct);

        return await db.Leads.AsNoTracking()
            .Where(l => l.Id == leadId && l.TenantId == tenantId && !l.IsDeleted)
            .VisibleTo(access)
            .AnyAsync(ct);
    }

    public static async Task<bool> CanSeeDealAsync(
        this IRecordScopeService scope, FlowDbContext db,
        Guid tenantId, Guid dealId, CancellationToken ct = default)
    {
        var access = await scope.GetAsync(RecordModules.Deals, ct);

        return await db.Deals.AsNoTracking()
            .Where(d => d.Id == dealId && d.TenantId == tenantId && !d.IsDeleted)
            .VisibleTo(access)
            .AnyAsync(ct);
    }

    public static async Task EnsureDealVisibleAsync(
        this IRecordScopeService scope, FlowDbContext db,
        Guid tenantId, Guid dealId, CancellationToken ct = default)
    {
        if (!await scope.CanSeeDealAsync(db, tenantId, dealId, ct))
            throw new KeyNotFoundException($"Deal {dealId} not found");
    }

    public static async Task EnsureLeadVisibleAsync(
        this IRecordScopeService scope, FlowDbContext db,
        Guid tenantId, Guid leadId, CancellationToken ct = default)
    {
        if (!await scope.CanSeeLeadAsync(db, tenantId, leadId, ct))
            throw new KeyNotFoundException($"Lead {leadId} not found");
    }
}
