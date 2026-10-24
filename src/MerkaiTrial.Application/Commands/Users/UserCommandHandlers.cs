// =====================================================================
// USER COMMAND HANDLERS - Write operations
// Location: MerkaiTrial.Application/Commands/UserCommandHandlers.cs
// =====================================================================

using MerkaiTrial.Application.Common;   // 103 — PlanQuota
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.Users
{
    // ==================== CREATE USER ====================
    public class CreateUserHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public CreateUserHandler(FlowDbContext db) => _db = db;

        public async Task<UserDto> Handle(CreateUserCommand cmd)
        {
            // Email is unique GLOBALLY among live users (UX_Users_Email_Live),
            // not per tenant — one person belongs to one workspace. The old
            // check was scoped to the tenant, so a clash with another tenant's
            // user passed here and failed at SaveChanges with a raw index
            // violation the user could not act on.
            var emailTaken = await _db.Users.IgnoreQueryFilters()
                .AnyAsync(u => u.Email == cmd.Email && !u.IsDeleted);

            if (emailTaken)
                throw new InvalidOperationException(
                    $"'{cmd.Email}' already belongs to an account. Each person can be in one workspace.");

            // ── QUOTA ────────────────────────────────────────────────────
            // The limit that actually binds during a trial. Checked here in
            // the handler rather than in the page: the page is a courtesy, the
            // handler is the rule.
            //
            // 103 — PlanQuota: the limit read live and held until the person
            // is saved, so two invitations sent at once can't both take the
            // last place. Everyone in the workspace counts, switched on or
            // off (a switched-off person keeps their place and can be
            // switched back on). The old advice "deactivate someone" did not
            // free a place, so the Add person page now says "remove someone".
            // No settings row → no limit to apply (MadeeVision has none).
            await using var quota = await PlanQuota.HoldAsync(_db, cmd.TenantId, PlanQuota.Users);

            // ── ROLES: validate BEFORE inserting anything ────────────────
            // Resolved up front so a bad role id fails the whole operation
            // rather than half-creating a user with no access.
            var roleIds = cmd.RoleIds?.Distinct().ToList() ?? new List<Guid>();
            var rolesToAssign = new List<Guid>();

            if (roleIds.Count > 0)
            {
                // IgnoreQueryFilters, then an EXPLICIT ownership check. The
                // filter would hide the tenant's own roles from a super admin;
                // without the check, any tenant's role could be assigned.
                var valid = await _db.Roles.AsNoTracking().IgnoreQueryFilters()
                    .Where(r => roleIds.Contains(r.Id)
                             && !r.IsDeleted
                             && (r.TenantId == cmd.TenantId || r.TenantId == null))
                    .Select(r => r.Id)
                    .ToListAsync();

                var rejected = roleIds.Except(valid).ToList();
                if (rejected.Count > 0)
                    throw new InvalidOperationException(
                        "One or more selected roles do not belong to this workspace.");

                rolesToAssign = valid;
            }

            var now = DateTime.UtcNow;

            var user = new User
            {
                Id = Guid.NewGuid(),
                TenantId = cmd.TenantId,
                FirstName = cmd.FirstName.Trim(),
                LastName = cmd.LastName.Trim(),
                Email = cmd.Email.Trim(),
                Phone = cmd.Phone?.Trim(),
                Department = cmd.Department?.Trim(),
                JobTitle = cmd.JobTitle?.Trim(),
                IsActive = true,
                IsTenantAdmin = cmd.IsTenantAdmin,
                IsSuperAdmin = false,

                // No password. They set one from the invite link, which also
                // proves the address. Same as provisioning's admin user.
                PasswordHash = null,

                // REQUIRED. OnValidatePrincipal compares this on every request;
                // a null or empty stamp signs the user out on their first page
                // load, which looks like a broken login rather than a bad insert.
                SecurityStamp = Guid.NewGuid().ToString("N"),

                MustChangePassword = false,
                CreatedAtUtc = now,
                CreatedBy = cmd.CreatedBy ?? "System",
            };

            _db.Users.Add(user);

            foreach (var roleId in rolesToAssign)
            {
                _db.UserRoles.Add(new UserRole
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    RoleId = roleId,
                    AssignedAtUtc = now,
                    AssignedBy = cmd.CreatedBy ?? "System",
                });
            }

            await _db.SaveChangesAsync();
            await quota.CommitAsync();   // 103

            var roleNames = await _db.UserRoles.IgnoreQueryFilters()
                .Where(ur => ur.UserId == user.Id && ur.Role != null)
                .Select(ur => ur.Role!.DisplayName)
                .ToListAsync();

            return new UserDto(
                user.Id, user.TenantId, user.FirstName, user.LastName, user.Email,
                user.Phone, user.Department, user.JobTitle, user.IsActive,
                user.IsTenantAdmin, user.LastLoginUtc, user.CreatedAtUtc, roleNames);
        }
    }

    // ==================== UPDATE USER ====================
    public class UpdateUserHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public UpdateUserHandler(FlowDbContext db) => _db = db;

        public async Task Handle(UpdateUserCommand cmd)
        {
            var user = await _db.Users
                .FirstOrDefaultAsync(u => u.Id == cmd.UserId && u.TenantId == cmd.TenantId && !u.IsDeleted)
                ?? throw new KeyNotFoundException($"User {cmd.UserId} not found");

            // Check if email is being changed and if it's already taken
            if (user.Email != cmd.Email)
            {
                var emailExists = await _db.Users.AnyAsync(u =>
                    u.TenantId == cmd.TenantId &&
                    u.Email == cmd.Email &&
                    u.Id != cmd.UserId &&
                    !u.IsDeleted);

                if (emailExists)
                    throw new InvalidOperationException($"Email '{cmd.Email}' is already in use");
            }

            user.FirstName = cmd.FirstName.Trim();
            user.LastName = cmd.LastName.Trim();
            user.Email = cmd.Email.Trim();
            user.Phone = cmd.Phone?.Trim();
            user.Department = cmd.Department?.Trim();
            user.JobTitle = cmd.JobTitle?.Trim();
            user.IsActive = cmd.IsActive;
            user.IsTenantAdmin = cmd.IsTenantAdmin;
            user.UpdatedAtUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync();
        }
    }

    // ==================== UPDATE USER STATUS ====================
    public class UpdateUserStatusHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public UpdateUserStatusHandler(FlowDbContext db) => _db = db;

        public async Task Handle(UpdateUserStatusCommand cmd)
        {
            var user = await _db.Users
                .FirstOrDefaultAsync(u => u.Id == cmd.UserId && u.TenantId == cmd.TenantId && !u.IsDeleted)
                ?? throw new KeyNotFoundException($"User {cmd.UserId} not found");

            user.IsActive = cmd.IsActive;
            user.UpdatedAtUtc = DateTime.UtcNow;

            // 102 — switching someone OFF ends their sessions for good. Their
            // cookie and API tokens were refused while they were off, but the
            // stamp stayed the same, so switching them back ON brought every
            // old session and token back to life. A new stamp means they
            // sign in again, fresh, if they are switched back on.
            if (!cmd.IsActive)
                user.SecurityStamp = Guid.NewGuid().ToString("N");

            await _db.SaveChangesAsync();
        }
    }

    // ==================== DELETE USER ====================
    public class DeleteUserHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public DeleteUserHandler(FlowDbContext db) => _db = db;

        public async Task Handle(Guid tenantId, Guid userId)
        {
            var user = await _db.Users
                .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId && !u.IsDeleted)
                ?? throw new KeyNotFoundException($"User {userId} not found");

            user.IsDeleted = true;
            user.UpdatedAtUtc = DateTime.UtcNow;
            user.SecurityStamp = Guid.NewGuid().ToString("N");   // 102 — see UpdateUserStatusHandler

            await _db.SaveChangesAsync();
        }
    }

    // ==================== ASSIGN USER ROLES ====================
    public class AssignUserRolesHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public AssignUserRolesHandler(FlowDbContext db) => _db = db;

        public async Task Handle(AssignUserRolesCommand cmd)
        {
            var user = await _db.Users.IgnoreQueryFilters()
                .Include(u => u.UserRoles)
                .FirstOrDefaultAsync(u => u.Id == cmd.UserId
                                       && u.TenantId == cmd.TenantId
                                       && !u.IsDeleted)
                ?? throw new KeyNotFoundException($"User {cmd.UserId} not found");

            var roleIds = cmd.RoleIds?.Distinct().ToList() ?? new List<Guid>();
            var valid = new List<Guid>();

            if (roleIds.Count > 0)
            {
                valid = await _db.Roles.AsNoTracking().IgnoreQueryFilters()
                    .Where(r => roleIds.Contains(r.Id)
                             && !r.IsDeleted
                             && (r.TenantId == cmd.TenantId || r.TenantId == null))
                    .Select(r => r.Id)
                    .ToListAsync();

                if (roleIds.Except(valid).Any())
                    throw new InvalidOperationException(
                        "One or more selected roles do not belong to this workspace.");
            }

            _db.UserRoles.RemoveRange(user.UserRoles);

            foreach (var roleId in valid)
            {
                _db.UserRoles.Add(new UserRole
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    RoleId = roleId,
                    AssignedAtUtc = DateTime.UtcNow,
                });
            }

            // Permissions live in the auth cookie from sign-in, so without a new
            // stamp the change appears to do nothing until the cookie expires.
            // Rotating it signs them out; they pick up the new roles next login.
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            user.UpdatedAtUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync();
        }
    }

    // ==================== BULK UPDATE USER STATUS ====================
    public class BulkUpdateUserStatusHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public BulkUpdateUserStatusHandler(FlowDbContext db) => _db = db;

        public async Task<BulkOperationResult> Handle(BulkUpdateUserStatusCommand cmd)
        {
            var errors = new List<string>();
            int successCount = 0;

            foreach (var userId in cmd.UserIds)
            {
                try
                {
                    var user = await _db.Users
                        .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == cmd.TenantId && !u.IsDeleted);

                    if (user == null)
                    {
                        errors.Add($"User {userId} not found");
                        continue;
                    }

                    user.IsActive = cmd.IsActive;
                    user.UpdatedAtUtc = DateTime.UtcNow;
                    if (!cmd.IsActive)
                        user.SecurityStamp = Guid.NewGuid().ToString("N");   // 102 — see UpdateUserStatusHandler
                    successCount++;
                }
                catch (Exception ex)
                {
                    errors.Add($"User {userId}: {ex.Message}");
                }
            }

            await _db.SaveChangesAsync();

            return new BulkOperationResult(
                cmd.UserIds.Count,
                successCount,
                errors.Count,
                errors
            );
        }
    }
}
