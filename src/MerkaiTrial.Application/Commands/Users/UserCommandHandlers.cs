// =====================================================================
// USER COMMAND HANDLERS - Write operations
// Location: MerkaiTrial.Application/Commands/UserCommandHandlers.cs
// =====================================================================

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
            // Check if user with same email exists in this tenant
            var exists = await _db.Users.AnyAsync(u =>
                u.TenantId == cmd.TenantId &&
                u.Email == cmd.Email &&
                !u.IsDeleted);

            if (exists)
                throw new InvalidOperationException($"User with email '{cmd.Email}' already exists in this tenant");

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
                CreatedAtUtc = DateTime.UtcNow
            };

            _db.Users.Add(user);

            // Assign roles if provided
            if (cmd.RoleIds != null && cmd.RoleIds.Any())
            {
                foreach (var roleId in cmd.RoleIds)
                {
                    var roleExists = await _db.Roles.AnyAsync(r => r.Id == roleId && !r.IsDeleted);
                    if (roleExists)
                    {
                        _db.UserRoles.Add(new UserRole
                        {
                            UserId = user.Id,
                            RoleId = roleId,
                            AssignedAtUtc = DateTime.UtcNow
                        });
                    }
                }
            }

            await _db.SaveChangesAsync();

            // Reload to get roles
            var roles = await _db.UserRoles
                .Include(ur => ur.Role)
                .Where(ur => ur.UserId == user.Id)
                .Select(ur => ur.Role!.DisplayName)
                .ToListAsync();

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
                roles
            );
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
            var user = await _db.Users
                .Include(u => u.UserRoles)
                .FirstOrDefaultAsync(u => u.Id == cmd.UserId && u.TenantId == cmd.TenantId && !u.IsDeleted)
                ?? throw new KeyNotFoundException($"User {cmd.UserId} not found");

            // Remove existing roles
            _db.UserRoles.RemoveRange(user.UserRoles);

            // Add new roles
            foreach (var roleId in cmd.RoleIds)
            {
                var roleExists = await _db.Roles.AnyAsync(r => r.Id == roleId && !r.IsDeleted);
                if (roleExists)
                {
                    _db.UserRoles.Add(new UserRole
                    {
                        UserId = user.Id,
                        RoleId = roleId,
                        AssignedAtUtc = DateTime.UtcNow
                    });
                }
            }

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
