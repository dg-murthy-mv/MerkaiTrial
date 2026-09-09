// =====================================================================
// USER QUERIES - Read-only operations
// Location: MerkaiTrial.Application/Queries/UserQueries.cs
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.Users
{
    // ==================== GET PAGINATED USERS ====================
    public class GetUsersPaginatedHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public GetUsersPaginatedHandler(FlowDbContext db) => _db = db;

        public async Task<PaginatedUsersResponse> Handle(Guid tenantId, int page, int pageSize, string? searchTerm)
        {
            var query = _db.Users
                 .IgnoreQueryFilters()
                 .Where(u => u.TenantId == tenantId && !u.IsDeleted);

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                var search = searchTerm.ToLower();
                query = query.Where(u =>
                    u.FirstName.ToLower().Contains(search) ||
                    u.LastName.ToLower().Contains(search) ||
                    u.Email.ToLower().Contains(search));
            }

            var totalCount = await query.CountAsync();
            var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
            var skip = (page - 1) * pageSize;

            var users = await query
                .Include(u => u.UserRoles)
                    .ThenInclude(ur => ur.Role)
                .OrderByDescending(u => u.CreatedAtUtc)
                .Skip(skip)
                .Take(pageSize)
                .ToListAsync();

            var items = users.Select(u => new UserListItem(
                u.Id,
                u.FullName,
                u.Email,
                u.Phone,
                u.Department,
                u.JobTitle,
                u.IsActive,
                u.IsTenantAdmin,
                u.LastLoginUtc,
                u.CreatedAtUtc,
                u.UserRoles
                    .Where(ur => ur.Role != null && !ur.Role.IsDeleted)
                    .Select(ur => ur.Role!.DisplayName)
                    .ToList()
            )).ToList();

            return new PaginatedUsersResponse(items, totalCount, page, pageSize, totalPages);
        }
    }

    // ==================== GET USER DETAIL ====================
    public class GetUserDetailHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public GetUserDetailHandler(FlowDbContext db) => _db = db;

        public async Task<UserDto> Handle(Guid tenantId, Guid userId)
        {
            // IgnoreQueryFilters: the Roles filter resolves to the CALLER's
            // tenant, so a super admin reading another tenant's user gets null
            // for every tenant-scoped role and the Where below strips them —
            // the Roles tab showed 0 for a user who clearly had one. The user
            // is still scoped explicitly by tenantId, so nothing is widened.
            var user = await _db.Users
                .IgnoreQueryFilters()
                .Include(u => u.UserRoles)
                    .ThenInclude(ur => ur.Role)
                .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId && !u.IsDeleted)
                ?? throw new KeyNotFoundException($"User {userId} not found");

            return new UserDto(
                user.Id,
                user.TenantId,
                user.FirstName,
                user.LastName,
                user.Email,
                user.Phone,
                user.Department,
                user.JobTitle,
                user.IsActive,
                user.IsTenantAdmin,
                user.LastLoginUtc,
                user.CreatedAtUtc,
                // !IsDeleted is now explicit: IgnoreQueryFilters also switched off
                // the soft-delete filter on Roles, so a deleted role would reappear.
                user.UserRoles
                    .Where(ur => ur.Role != null && !ur.Role.IsDeleted)
                    .Select(ur => ur.Role!.DisplayName)
                    .ToList()
            );
        }
    }


    // ==================== GET USER ROLES ====================
    public class GetUserRolesHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public GetUserRolesHandler(FlowDbContext db) => _db = db;

        public async Task<List<UserRoleDto>> Handle(Guid tenantId, Guid userId)
        {
            var userRoles = await _db.UserRoles
                .IgnoreQueryFilters()
                .Include(ur => ur.Role)
                .Where(ur => ur.UserId == userId
                          && ur.Role != null
                          && !ur.Role.IsDeleted)
                .Join(_db.Users.IgnoreQueryFilters(),
                      ur => ur.UserId, u => u.Id, (ur, u) => new { ur, u })
                .Where(x => x.u.TenantId == tenantId && !x.u.IsDeleted)
                .Select(x => new UserRoleDto(
                    x.ur.UserId,
                    x.ur.RoleId,
                    x.ur.Role!.Name,
                    x.ur.Role!.DisplayName,
                    x.ur.AssignedAtUtc
                ))
                .ToListAsync();

            return userRoles;
        }
    }

    // ==================== GET USERS LOOKUP ====================
    public class GetUsersLookupHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public GetUsersLookupHandler(FlowDbContext db) => _db = db;

        public async Task<List<UserLookupDto>> Handle(Guid tenantId)
        {
            var users = await _db.Users
                .Where(u => u.TenantId == tenantId && !u.IsDeleted && u.IsActive)
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

    // ==================== GET USER STATS ====================
    public class GetUserStatsHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public GetUserStatsHandler(FlowDbContext db) => _db = db;

        public async Task<UserStatsDto> Handle(Guid tenantId)
        {
            var sevenDaysAgo = DateTime.UtcNow.AddDays(-7);

            var users = await _db.Users
                .Where(u => u.TenantId == tenantId && !u.IsDeleted)
                .ToListAsync();

            return new UserStatsDto(
                TotalUsers: users.Count,
                ActiveUsers: users.Count(u => u.IsActive),
                InactiveUsers: users.Count(u => !u.IsActive),
                AdminUsers: users.Count(u => u.IsTenantAdmin),
                RecentLogins: users.Count(u => u.LastLoginUtc.HasValue && u.LastLoginUtc.Value >= sevenDaysAgo)
            );
        }
    }

    // ==================== GET SALES TEAM ====================
    //public class GetSalesTeamHandler : ICommandHandler
    //{
    //    private readonly FlowDbContext _db;
    //    public GetSalesTeamHandler(FlowDbContext db) => _db = db;

    //    public async Task<List<SalesTeamMemberDto>> Handle(Guid tenantId)
    //    {
    //        var salesUsers = await _db.Users
    //            .Where(u => u.TenantId == tenantId && !u.IsDeleted && u.IsActive)
    //            .Where(u => u.Department == "Sales" || u.JobTitle!.Contains("Sales"))
    //            .OrderBy(u => u.FirstName)
    //            .Select(u => new SalesTeamMemberDto(
    //                u.Id,
    //                $"{u.FirstName} {u.LastName}".Trim(),
    //                u.Email,
    //                u.JobTitle
    //            ))
    //            .ToListAsync();

    //        return salesUsers;
    //    }
    //}
}
