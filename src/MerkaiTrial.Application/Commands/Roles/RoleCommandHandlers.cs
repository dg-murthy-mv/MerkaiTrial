// =====================================================================
// ROLE COMMAND HANDLERS - Write operations
// Location: MerkaiTrial.Application/Commands/RoleCommandHandlers.cs
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands
{
    // ==================== CREATE ROLE ====================
    public class CreateRoleHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public CreateRoleHandler(FlowDbContext db) => _db = db;

        public async Task<RoleDto> Handle(CreateRoleCommand cmd)
        {
            // Check if role with same name exists
            var exists = await _db.Roles.AnyAsync(r => r.Name == cmd.Name && !r.IsDeleted);
            if (exists)
                throw new InvalidOperationException($"Role with name '{cmd.Name}' already exists");

            var role = new Role
            {
                Id = Guid.NewGuid(),
                Name = cmd.Name.Trim(),
                DisplayName = cmd.DisplayName.Trim(),
                Description = cmd.Description?.Trim(),
                IsSystemRole = false, // Custom roles are never system roles
                Permissions = cmd.Permissions,
                CreatedAtUtc = DateTime.UtcNow
            };

            _db.Roles.Add(role);
            await _db.SaveChangesAsync();

            return new RoleDto(
                role.Id,
                role.Name,
                role.DisplayName,
                role.Description,
                role.IsSystemRole,
                role.Permissions,
                role.CreatedAtUtc,
                0 // No users yet
            );
        }
    }

    // ==================== UPDATE ROLE ====================
    public class UpdateRoleHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public UpdateRoleHandler(FlowDbContext db) => _db = db;

        public async Task Handle(UpdateRoleCommand cmd)
        {
            var role = await _db.Roles
                .FirstOrDefaultAsync(r => r.Id == cmd.RoleId && !r.IsDeleted)
                ?? throw new KeyNotFoundException($"Role {cmd.RoleId} not found");

            // Cannot modify system roles
            if (role.IsSystemRole)
                throw new InvalidOperationException("Cannot modify system roles");

            // Check if name is being changed and if it's already taken
            if (role.Name != cmd.Name)
            {
                var nameExists = await _db.Roles.AnyAsync(r =>
                    r.Name == cmd.Name && r.Id != cmd.RoleId && !r.IsDeleted);
                if (nameExists)
                    throw new InvalidOperationException($"Role name '{cmd.Name}' is already in use");
            }

            role.Name = cmd.Name.Trim();
            role.DisplayName = cmd.DisplayName.Trim();
            role.Description = cmd.Description?.Trim();
            role.Permissions = cmd.Permissions;
            role.UpdatedAtUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync();
        }
    }

    // ==================== DELETE ROLE ====================
    public class DeleteRoleHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public DeleteRoleHandler(FlowDbContext db) => _db = db;

        public async Task Handle(Guid roleId)
        {
            var role = await _db.Roles
                .FirstOrDefaultAsync(r => r.Id == roleId && !r.IsDeleted)
                ?? throw new KeyNotFoundException($"Role {roleId} not found");

            // Cannot delete system roles
            if (role.IsSystemRole)
                throw new InvalidOperationException("Cannot delete system roles");

            // Check if role is assigned to any users
            var hasUsers = await _db.UserRoles.AnyAsync(ur => ur.RoleId == roleId);
            if (hasUsers)
                throw new InvalidOperationException("Cannot delete role that is assigned to users");

            role.IsDeleted = true;
            role.UpdatedAtUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync();
        }
    }
}
