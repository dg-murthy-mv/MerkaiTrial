// =====================================================================
// FILE: MerkaiTrial.Application/Commands/RoleCommandHandlers.cs
//       (TENANT-SCOPED)
//
// WHAT WAS WRONG
// --------------
// CREATE:
//   * TenantId was never set, so every role a client created landed with
//     TenantId NULL — a new GLOBAL role, shared with every other tenant.
//     That reintroduces the exact problem migration 001 removes, on the
//     first role your first trial client creates.
//   * The name uniqueness check was global:
//         r.Name == cmd.Name && !r.IsDeleted
//     So Client A creating "Sales Manager" permanently blocks Client B
//     from creating one, and the error confirms the existence of a name
//     they never chose. Uniqueness must be per tenant.
//   * CreatedBy was never populated.
//
// UPDATE / DELETE:
//   * The role was loaded by id alone. A tenant admin who knows (or
//     guesses) another tenant's role GUID could edit or delete it.
//   * System roles were correctly blocked — that part was right, and is
//     kept, because tenant_admin is a shared row your clients must not
//     be able to touch.
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Queries;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands;

// ==================== CREATE ROLE ====================
public class CreateRoleHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly IRoleScope _scope;
    private readonly ICurrentUserService _currentUser;

    public CreateRoleHandler(FlowDbContext db, IRoleScope scope, ICurrentUserService currentUser)
    {
        _db = db; _scope = scope; _currentUser = currentUser;
    }

    public async Task<RoleDto> Handle(CreateRoleCommand cmd)
    {
        var tenantId = _scope.TenantId;
        var name     = cmd.Name.Trim();

        // Uniqueness is PER TENANT, and must also exclude the system-role
        // names — a tenant creating its own "tenant_admin" would be
        // confusing at best and a privilege-escalation attempt at worst.
        var clashesInTenant = await _db.Roles.AnyAsync(r =>
            r.Name == name && !r.IsDeleted &&
            (r.TenantId == tenantId || r.TenantId == null));

        if (clashesInTenant)
            throw new InvalidOperationException($"Role with name '{name}' already exists");

        var actor = await _currentUser.GetCurrentUserAsync();

        var role = new Role
        {
            Id           = Guid.NewGuid(),
            TenantId     = tenantId,   // ← was missing; NULL made it global
            Name         = name,
            DisplayName  = cmd.DisplayName.Trim(),
            Description  = cmd.Description?.Trim(),
            IsSystemRole = false,      // tenants never create system roles
            Permissions  = cmd.Permissions,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy    = actor.Email
        };

        _db.Roles.Add(role);
        await _db.SaveChangesAsync();

        return new RoleDto(
            role.Id, role.Name, role.DisplayName, role.Description,
            role.IsSystemRole, role.Permissions, role.CreatedAtUtc, 0);
    }
}

// ==================== UPDATE ROLE ====================
public class UpdateRoleHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly IRoleScope _scope;
    private readonly ICurrentUserService _currentUser;

    public UpdateRoleHandler(FlowDbContext db, IRoleScope scope, ICurrentUserService currentUser)
    {
        _db = db; _scope = scope; _currentUser = currentUser;
    }

    public async Task Handle(UpdateRoleCommand cmd)
    {
        // Visible() restricts to this tenant's roles plus system roles, so
        // another tenant's role is NOT FOUND rather than forbidden.
        var role = await _scope.Visible(_db)
            .FirstOrDefaultAsync(r => r.Id == cmd.RoleId)
            ?? throw new KeyNotFoundException($"Role {cmd.RoleId} not found");

        if (role.IsSystemRole)
            throw new InvalidOperationException("Cannot modify system roles");

        // A system role reaches Visible() with TenantId == null. Belt and
        // braces in case IsSystemRole is ever wrong in the data: a role the
        // caller does not own cannot be edited even if it slipped through.
        var tenantId = _scope.TenantId;
        if (role.TenantId != tenantId)
            throw new KeyNotFoundException($"Role {cmd.RoleId} not found");

        var name = cmd.Name.Trim();

        if (role.Name != name)
        {
            var nameExists = await _db.Roles.AnyAsync(r =>
                r.Name == name && r.Id != cmd.RoleId && !r.IsDeleted &&
                (r.TenantId == tenantId || r.TenantId == null));

            if (nameExists)
                throw new InvalidOperationException($"Role name '{name}' is already in use");
        }

        var actor = await _currentUser.GetCurrentUserAsync();

        role.Name        = name;
        role.DisplayName = cmd.DisplayName.Trim();
        role.Description = cmd.Description?.Trim();
        role.Permissions = cmd.Permissions;
        role.UpdatedAtUtc = DateTime.UtcNow;
        role.UpdatedBy    = actor.Email;

        await _db.SaveChangesAsync();

        // Permission claims are baked into the auth cookie at sign-in, so
        // users holding this role keep their OLD permissions until they sign
        // in again. Rotating their SecurityStamp forces revalidation to fail
        // and signs them out — the change then takes effect immediately.
        // Without this, revoking a permission appears to do nothing.
        var affectedUserIds = await _db.UserRoles
            .Where(ur => ur.RoleId == role.Id)
            .Select(ur => ur.UserId)
            .ToListAsync();

        if (affectedUserIds.Count > 0)
        {
            await _db.Users
                .Where(u => affectedUserIds.Contains(u.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(
                    u => u.SecurityStamp, _ => Guid.NewGuid().ToString("N")));
        }
    }
}

// ==================== DELETE ROLE ====================
public class DeleteRoleHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly IRoleScope _scope;

    public DeleteRoleHandler(FlowDbContext db, IRoleScope scope)
    {
        _db = db; _scope = scope;
    }

    public async Task Handle(Guid roleId)
    {
        var role = await _scope.Visible(_db)
            .FirstOrDefaultAsync(r => r.Id == roleId)
            ?? throw new KeyNotFoundException($"Role {roleId} not found");

        if (role.IsSystemRole)
            throw new InvalidOperationException("Cannot delete system roles");

        var tenantId = _scope.TenantId;
        if (role.TenantId != tenantId)
            throw new KeyNotFoundException($"Role {roleId} not found");

        // Assignment check is scoped: another tenant's user holding this role
        // should not block (nor reveal itself), though after migration 001
        // that combination should no longer be possible.
        var hasUsers = await _db.UserRoles.AnyAsync(ur =>
            ur.RoleId == roleId &&
            _db.Users.Any(u => u.Id == ur.UserId && !u.IsDeleted && u.TenantId == tenantId));

        if (hasUsers)
            throw new InvalidOperationException("Cannot delete role that is assigned to users");

        role.IsDeleted    = true;
        role.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }
}
