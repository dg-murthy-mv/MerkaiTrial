// =====================================================================
// FILE: MerkaiTrial.Application/Queries/RoleQueries.cs  (TENANT-SCOPED)
//
// WHAT WAS WRONG
// --------------
// Not one query filtered by tenant. With roles shared across tenants that
// was invisible; the moment two clients share the database it means:
//
//   * GetRoleUsers returned the names and emails of users in EVERY tenant
//     assigned to that role. That is the "4 users" on each role in your
//     Roles screen — one per demo tenant. A live cross-tenant PII leak.
//   * GetRoleStats counted every user in the database as "totalUsers".
//   * The role list showed other clients' custom roles.
//   * GetRoleDetail loaded any role by id, regardless of owner.
//
// THE SCOPE RULE
// --------------
//   TenantId == null  -> system role, visible to everyone, read-only
//   TenantId == mine  -> my tenant's own custom role
//   anything else     -> does not exist (404, never 403)
//
// 403 confirms the row exists. 404 says nothing. Always 404.
//
// SUPER-ADMIN NUANCE
// ------------------
// A super admin legitimately sees every tenant's roles at /Admin/Roles.
// But while VIEWING AS a tenant user they must NOT — they are standing in
// a tenant's shoes and should see exactly what that user sees. So the
// unrestricted branch requires IsSuperAdmin AND NOT ViewingAs.
// =====================================================================

using System.Security.Claims;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Queries;

/// <summary>
/// Resolves who is asking and what they may see. One place, so the rule
/// cannot drift between handlers.
/// </summary>
public interface IRoleScope
{
    Guid TenantId { get; }
    bool SeesAllTenants { get; }
    IQueryable<Role> Visible(FlowDbContext db);
}

public class RoleScope : IRoleScope
{
    private readonly ICurrentUserService _currentUser;
    private readonly IHttpContextAccessor _http;

    public RoleScope(ICurrentUserService currentUser, IHttpContextAccessor http)
    {
        _currentUser = currentUser;
        _http = http;
    }

    public Guid TenantId => _currentUser.GetCurrentTenantId();

    public bool SeesAllTenants
    {
        get
        {
            var user = _http.HttpContext?.User;
            if (user is null) return false;

            var isSuperAdmin = user.HasClaim(SignInService.ClaimIsSuperAdmin, "true");
            var isViewingAs = user.HasClaim(SignInService.ClaimViewingAs, "true");

            // While impersonating, a super admin sees what the impersonated
            // user sees — otherwise ViewAs would show a tenant admin every
            // other client's roles.
            return isSuperAdmin && !isViewingAs;
        }
    }

    public IQueryable<Role> Visible(FlowDbContext db)
    {
        // IgnoreQueryFilters is REQUIRED here. Without it the global
        // filter in FlowDbContext is applied underneath whatever this
        // method returns, and a super admin's "all tenants" branch is
        // silently narrowed back to their own tenant — 200 OK, one row,
        // no error anywhere.
        //
        // Turning the automatic filter off does not widen anything: the
        // scope is re-applied explicitly below, and it is the same rule.
        var q = db.Roles.IgnoreQueryFilters().Where(r => !r.IsDeleted);

        if (SeesAllTenants) return q;

        var tenantId = TenantId;
        return q.Where(r => r.TenantId == null || r.TenantId == tenantId);
    }
}

// ==================== GET PAGINATED ROLES ====================
public class GetPaginatedRolesHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly IRoleScope _scope;

    public GetPaginatedRolesHandler(FlowDbContext db, IRoleScope scope)
    {
        _db = db; _scope = scope;
    }

    public async Task<PaginatedRolesResponse> Handle(
        int page = 1, int pageSize = 25, string? search = null, bool? isSystemRole = null)
    {
        // Bound the page size. An unbounded pageSize from the query string
        // lets a caller pull the whole table in one request.
        pageSize = Math.Clamp(pageSize, 1, 200);
        page     = Math.Max(page, 1);

        var query = _scope.Visible(_db);

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(r =>
                r.Name.Contains(search) ||
                r.DisplayName.Contains(search) ||
                (r.Description != null && r.Description.Contains(search)));
        }

        if (isSystemRole.HasValue)
            query = query.Where(r => r.IsSystemRole == isSystemRole.Value);

        var totalCount = await query.CountAsync();

        var tenantId = _scope.TenantId;
        var seesAll  = _scope.SeesAllTenants;

        var roles = await query
            .OrderBy(r => r.DisplayName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new
            {
                Role = r,
                // User counts must be scoped too, or a tenant admin learns
                // how many users other clients have on a shared system role.
                UserCount = _db.UserRoles.Count(ur =>
                    ur.RoleId == r.Id &&
                    _db.Users.Any(u => u.Id == ur.UserId
                                    && !u.IsDeleted
                                    && (seesAll || u.TenantId == tenantId)))
            })
            .ToListAsync();

        var items = roles.Select(r => new RoleListItem(
            r.Role.Id, r.Role.Name, r.Role.DisplayName,
            r.Role.Description, r.Role.IsSystemRole, r.UserCount)).ToList();

        return new PaginatedRolesResponse(items, totalCount, page, pageSize);
    }
}

// ==================== GET ALL ROLES ====================
public class GetAllRolesHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly IRoleScope _scope;

    public GetAllRolesHandler(FlowDbContext db, IRoleScope scope)
    {
        _db = db; _scope = scope;
    }

    public async Task<List<RoleListItem>> Handle()
    {
        var tenantId = _scope.TenantId;
        var seesAll  = _scope.SeesAllTenants;

        var roles = await _scope.Visible(_db)
            .Select(r => new
            {
                Role = r,
                UserCount = _db.UserRoles.Count(ur =>
                    ur.RoleId == r.Id &&
                    _db.Users.Any(u => u.Id == ur.UserId
                                    && !u.IsDeleted
                                    && (seesAll || u.TenantId == tenantId)))
            })
            .OrderBy(r => r.Role.DisplayName)
            .ToListAsync();

        return roles.Select(r => new RoleListItem(
            r.Role.Id, r.Role.Name, r.Role.DisplayName,
            r.Role.Description, r.Role.IsSystemRole, r.UserCount)).ToList();
    }
}

// ==================== GET ROLE DETAIL ====================
public class GetRoleDetailHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly IRoleScope _scope;

    public GetRoleDetailHandler(FlowDbContext db, IRoleScope scope)
    {
        _db = db; _scope = scope;
    }

    public async Task<RoleDto> Handle(Guid roleId)
    {
        // Another tenant's role is NOT FOUND, not forbidden. KeyNotFound maps
        // to 404 in the controller, which reveals nothing about existence.
        var role = await _scope.Visible(_db)
            .FirstOrDefaultAsync(r => r.Id == roleId)
            ?? throw new KeyNotFoundException($"Role {roleId} not found");

        var tenantId = _scope.TenantId;
        var seesAll  = _scope.SeesAllTenants;

        var userCount = await _db.UserRoles.CountAsync(ur =>
            ur.RoleId == roleId &&
            _db.Users.Any(u => u.Id == ur.UserId
                            && !u.IsDeleted
                            && (seesAll || u.TenantId == tenantId)));

        return new RoleDto(
            role.Id, role.Name, role.DisplayName, role.Description,
            role.IsSystemRole, role.Permissions, role.CreatedAtUtc, userCount);
    }
}

// ==================== GET ROLES LOOKUP ====================
public class GetRolesLookupHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly IRoleScope _scope;

    public GetRolesLookupHandler(FlowDbContext db, IRoleScope scope)
    {
        _db = db; _scope = scope;
    }

    public async Task<List<RoleLookupDto>> Handle()
        => await _scope.Visible(_db)
            .OrderBy(r => r.DisplayName)
            .Select(r => new RoleLookupDto(r.Id, r.Name, r.DisplayName))
            .ToListAsync();
}

// ==================== GET ROLE USERS ====================
public class GetRoleUsersHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly IRoleScope _scope;

    public GetRoleUsersHandler(FlowDbContext db, IRoleScope scope)
    {
        _db = db; _scope = scope;
    }

    public async Task<List<UserLookupDto>> Handle(Guid roleId)
    {
        // The role itself must be visible first — otherwise passing any GUID
        // enumerates users through a role you cannot see.
        var roleVisible = await _scope.Visible(_db).AnyAsync(r => r.Id == roleId);
        if (!roleVisible)
            throw new KeyNotFoundException($"Role {roleId} not found");

        var tenantId = _scope.TenantId;
        var seesAll  = _scope.SeesAllTenants;

        // THE LEAK THIS FIXES: previously every user in every tenant holding
        // this role was returned, with name and email. On a shared system
        // role like tenant_admin that is every client's administrator.
        return await _db.UserRoles
            .Where(ur => ur.RoleId == roleId)
            .Join(_db.Users, ur => ur.UserId, u => u.Id, (ur, u) => u)
            .Where(u => !u.IsDeleted && (seesAll || u.TenantId == tenantId))
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
            .Select(u => new UserLookupDto(
                u.Id, (u.FirstName + " " + u.LastName).Trim(), u.Email))
            .ToListAsync();
    }
}

// ==================== GET ROLE STATS ====================
public class GetRoleStatsHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly IRoleScope _scope;

    public GetRoleStatsHandler(FlowDbContext db, IRoleScope scope)
    {
        _db = db; _scope = scope;
    }

    public async Task<RoleStatsDto> Handle()
    {
        var visible  = _scope.Visible(_db);
        var tenantId = _scope.TenantId;
        var seesAll  = _scope.SeesAllTenants;

        var totalRoles  = await visible.CountAsync();
        var systemRoles = await visible.CountAsync(r => r.IsSystemRole);
        var customRoles = await visible.CountAsync(r => !r.IsSystemRole);

        // Was: every distinct user in the database, shown to every tenant.
        var totalUsers = await _db.Users
            .Where(u => !u.IsDeleted && (seesAll || u.TenantId == tenantId))
            .Where(u => _db.UserRoles.Any(ur => ur.UserId == u.Id))
            .CountAsync();

        return new RoleStatsDto(totalRoles, systemRoles, customRoles, totalUsers);
    }
}
