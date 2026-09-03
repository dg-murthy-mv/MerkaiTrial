// =====================================================================
// ROLE QUERIES - Read-only operations
// Location: MerkaiTrial.Application/Queries/RoleQueries.cs
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Queries
{
    // ==================== GET PAGINATED ROLES ====================
    public class GetPaginatedRolesHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public GetPaginatedRolesHandler(FlowDbContext db) => _db = db;

        public async Task<PaginatedRolesResponse> Handle(int page = 1, int pageSize = 25, string? search = null, bool? isSystemRole = null)
        {
            var query = _db.Roles.Where(r => !r.IsDeleted);

            // Apply search filter
            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(r =>
                    r.Name.Contains(search) ||
                    r.DisplayName.Contains(search) ||
                    (r.Description != null && r.Description.Contains(search))
                );
            }

            // Apply system role filter
            if (isSystemRole.HasValue)
            {
                query = query.Where(r => r.IsSystemRole == isSystemRole.Value);
            }

            var totalCount = await query.CountAsync();

            var roles = await query
                .OrderBy(r => r.DisplayName)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(r => new
                {
                    Role = r,
                    UserCount = _db.UserRoles.Count(ur => ur.RoleId == r.Id)
                })
                .ToListAsync();

            var items = roles.Select(r => new RoleListItem(
                r.Role.Id,
                r.Role.Name,
                r.Role.DisplayName,
                r.Role.Description,
                r.Role.IsSystemRole,
                r.UserCount
            )).ToList();

            return new PaginatedRolesResponse(items, totalCount, page, pageSize);
        }
    }

    // ==================== GET ALL ROLES (NO PAGINATION) ====================
    public class GetAllRolesHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public GetAllRolesHandler(FlowDbContext db) => _db = db;

        public async Task<List<RoleListItem>> Handle()
        {
            var roles = await _db.Roles
                .Where(r => !r.IsDeleted)
                .Select(r => new
                {
                    Role = r,
                    UserCount = _db.UserRoles.Count(ur => ur.RoleId == r.Id)
                })
                .OrderBy(r => r.Role.DisplayName)
                .ToListAsync();

            return roles.Select(r => new RoleListItem(
                r.Role.Id,
                r.Role.Name,
                r.Role.DisplayName,
                r.Role.Description,
                r.Role.IsSystemRole,
                r.UserCount
            )).ToList();
        }
    }

    // ==================== GET ROLE DETAIL ====================
    public class GetRoleDetailHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public GetRoleDetailHandler(FlowDbContext db) => _db = db;

        public async Task<RoleDto> Handle(Guid roleId)
        {
            var role = await _db.Roles
                .FirstOrDefaultAsync(r => r.Id == roleId && !r.IsDeleted)
                ?? throw new KeyNotFoundException($"Role {roleId} not found");

            var userCount = await _db.UserRoles.CountAsync(ur => ur.RoleId == roleId);

            return new RoleDto(
                role.Id,
                role.Name,
                role.DisplayName,
                role.Description,
                role.IsSystemRole,
                role.Permissions,
                role.CreatedAtUtc,
                userCount
            );
        }
    }

    // ==================== GET ROLES LOOKUP ====================
    public class GetRolesLookupHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public GetRolesLookupHandler(FlowDbContext db) => _db = db;

        public async Task<List<RoleLookupDto>> Handle()
        {
            var roles = await _db.Roles
                .Where(r => !r.IsDeleted)
                .OrderBy(r => r.DisplayName)
                .Select(r => new RoleLookupDto(
                    r.Id,
                    r.Name,
                    r.DisplayName
                ))
                .ToListAsync();

            return roles;
        }
    }

    // ==================== GET ROLE USERS ====================
    public class GetRoleUsersHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public GetRoleUsersHandler(FlowDbContext db) => _db = db;

        public async Task<List<UserLookupDto>> Handle(Guid roleId)
        {
            var users = await _db.UserRoles
                .Where(ur => ur.RoleId == roleId)
                .Join(_db.Users, ur => ur.UserId, u => u.Id, (ur, u) => u)
                .Where(u => !u.IsDeleted)
                .OrderBy(u => u.FirstName)
                .ThenBy(u => u.LastName)
                .Select(u => new UserLookupDto(
                    u.Id,
                    $"{u.FirstName} {u.LastName}".Trim(),
                    u.Email
                ))
                .ToListAsync();

            return users;
        }
    }

    // ==================== GET ROLE STATS ====================
    public class GetRoleStatsHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public GetRoleStatsHandler(FlowDbContext db) => _db = db;

        public async Task<RoleStatsDto> Handle()
        {
            var totalRoles = await _db.Roles.CountAsync(r => !r.IsDeleted);
            var systemRoles = await _db.Roles.CountAsync(r => !r.IsDeleted && r.IsSystemRole);
            var customRoles = await _db.Roles.CountAsync(r => !r.IsDeleted && !r.IsSystemRole);
            
            var totalUsers = await _db.UserRoles
                .Select(ur => ur.UserId)
                .Distinct()
                .CountAsync();

            return new RoleStatsDto(
                totalRoles,
                systemRoles,
                customRoles,
                totalUsers
            );
        }
    }
}
