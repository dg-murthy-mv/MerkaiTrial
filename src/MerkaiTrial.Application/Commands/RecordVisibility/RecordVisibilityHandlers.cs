// =====================================================================
// RecordVisibilityHandlers.cs
// Location: MerkaiTrial.Application/Commands/RecordVisibility/RecordVisibilityHandlers.cs
//
// Read/write side of Settings → Record visibility:
//   • the role × module scope matrix (Own / Team / All)
//   • teams, and which team each user is in
//   • ROUND 2: which teams each user MANAGES (many), and locked roles
//
// All handlers take the tenant id from the caller (controller reads it
// from the signed-in user) and re-check every id belongs to that tenant.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.RecordVisibility;

// ── DTOs ──────────────────────────────────────────────────────────────

public record RoleScopeRowDto(
    Guid RoleId,
    string Name,
    string DisplayName,
    bool IsSystemRole,
    int UserCount,
    bool IsLocked,                              // tenant_admin — always All, not editable
    Dictionary<string, RecordScope> Scopes,     // effective, per module
    Dictionary<string, RecordScope> Defaults);  // what "no override" means

public record RecordScopeMatrixDto(
    List<string> Modules,
    List<RoleScopeRowDto> Roles);

public record SetRecordScopeDto(
    Guid TenantId,
    Guid RoleId,
    string Module,
    RecordScope Scope,
    string? UpdatedBy = null);

public record TeamDto(Guid Id, string Name, string? Description, int MemberCount, int ManagerCount);

public record TeamUserDto(
    Guid Id,
    string FullName,
    string Email,
    Guid? TeamId,                 // the ONE team they belong to
    List<Guid> ManagedTeamIds,    // the teams they manage (any number)
    bool IsTenantAdmin,
    bool IsActive);

public record TeamsOverviewDto(List<TeamDto> Teams, List<TeamUserDto> Users);

public record CreateTeamDto(Guid TenantId, string Name, string? Description = null, string? CreatedBy = null);

public record UpdateTeamDto(Guid TenantId, Guid TeamId, string Name, string? Description = null, string? UpdatedBy = null);

public record SetUserTeamDto(Guid TenantId, Guid UserId, Guid? TeamId);

public record SetUserManagedTeamsDto(Guid TenantId, Guid UserId, List<Guid> TeamIds, string? UpdatedBy = null);

// ── SCOPE MATRIX ──────────────────────────────────────────────────────

public class GetRecordScopeMatrixHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    public GetRecordScopeMatrixHandler(FlowDbContext db) => _db = db;

    public async Task<RecordScopeMatrixDto> Handle(Guid tenantId, CancellationToken ct = default)
    {
        var modules = RecordModules.Enforced.ToList();

        // Roles filter: built-in (TenantId null) + this tenant's own.
        var roles = await _db.Roles.AsNoTracking()
            .Where(r => !r.IsDeleted)
            .OrderBy(r => r.IsSystemRole ? 0 : 1).ThenBy(r => r.DisplayName)
            .Select(r => new { r.Id, r.Name, r.DisplayName, r.IsSystemRole })
            .ToListAsync(ct);

        // How many of THIS tenant's users hold each role.
        var userCounts = await _db.Users.AsNoTracking()
            .Where(u => u.TenantId == tenantId && !u.IsDeleted)
            .SelectMany(u => u.UserRoles)
            .GroupBy(ur => ur.RoleId)
            .Select(g => new { RoleId = g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.RoleId, x => x.N, ct);

        var overrides = await _db.RoleRecordScopes.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .ToListAsync(ct);

        var rows = roles.Select(r =>
        {
            var locked = RecordScopeDefaults.IsLocked(r.Name);
            var defaults = modules.ToDictionary(m => m, m => RecordScopeDefaults.For(r.Name, m));
            var effective = modules.ToDictionary(m => m, m =>
                locked ? RecordScope.All
                : overrides.FirstOrDefault(o => o.RoleId == r.Id && o.Module == m)?.Scope ?? defaults[m]);

            return new RoleScopeRowDto(
                r.Id, r.Name, r.DisplayName, r.IsSystemRole,
                userCounts.GetValueOrDefault(r.Id),
                locked, effective, defaults);
        }).ToList();

        return new RecordScopeMatrixDto(modules, rows);
    }
}

public class SetRecordScopeHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ILogger<SetRecordScopeHandler> _logger;

    public SetRecordScopeHandler(FlowDbContext db, ILogger<SetRecordScopeHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Handle(SetRecordScopeDto dto, CancellationToken ct = default)
    {
        if (!RecordModules.Enforced.Contains(dto.Module))
            throw new InvalidOperationException($"'{dto.Module}' does not support record visibility yet.");

        if (!Enum.IsDefined(typeof(RecordScope), dto.Scope))
            throw new InvalidOperationException("Choose Own, Team or All.");

        // Role filter → built-in or this tenant's. Another tenant's custom
        // role is simply not found.
        var role = await _db.Roles.AsNoTracking()
            .Where(r => r.Id == dto.RoleId && !r.IsDeleted)
            .Select(r => new { r.Id, r.Name, r.DisplayName })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Role not found");

        if (RecordScopeDefaults.IsLocked(role.Name))
            throw new InvalidOperationException(
                $"{role.DisplayName} always sees everything — the workspace administrator must be able to fix any record.");

        var existing = await _db.RoleRecordScopes
            .FirstOrDefaultAsync(s => s.TenantId == dto.TenantId && s.RoleId == dto.RoleId && s.Module == dto.Module, ct);

        var fallback = RecordScopeDefaults.For(role.Name, dto.Module);

        if (dto.Scope == fallback)
        {
            // Back to the default: remove the override instead of storing a
            // copy of it, so a later change to the default still applies.
            if (existing != null) _db.RoleRecordScopes.Remove(existing);
        }
        else if (existing != null)
        {
            existing.Scope = dto.Scope;
            existing.UpdatedAtUtc = DateTime.UtcNow;
            existing.UpdatedBy = dto.UpdatedBy;
        }
        else
        {
            _db.RoleRecordScopes.Add(new RoleRecordScope
            {
                TenantId = dto.TenantId,
                RoleId = dto.RoleId,
                Module = dto.Module,
                Scope = dto.Scope,
                UpdatedAtUtc = DateTime.UtcNow,
                UpdatedBy = dto.UpdatedBy
            });
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Record scope for role {Role} / {Module} set to {Scope} in tenant {TenantId} by {User}",
            role.DisplayName, dto.Module, dto.Scope, dto.TenantId, dto.UpdatedBy);
    }
}

// ── TEAMS ─────────────────────────────────────────────────────────────

public class GetTeamsHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    public GetTeamsHandler(FlowDbContext db) => _db = db;

    public async Task<TeamsOverviewDto> Handle(Guid tenantId, CancellationToken ct = default)
    {
        // Users is not tenant-filtered — scope explicitly.
        var userRows = await _db.Users.AsNoTracking()
            .Where(u => u.TenantId == tenantId && !u.IsDeleted)
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
            .Select(u => new
            {
                u.Id,
                FullName = (u.FirstName + " " + u.LastName).Trim(),
                u.Email,
                u.TeamId,
                u.IsTenantAdmin,
                u.IsActive
            })
            .ToListAsync(ct);

        var managers = await _db.TeamManagers.AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .Select(m => new { m.UserId, m.TeamId })
            .ToListAsync(ct);

        var users = userRows.Select(u => new TeamUserDto(
            u.Id, u.FullName, u.Email, u.TeamId,
            managers.Where(m => m.UserId == u.Id).Select(m => m.TeamId).ToList(),
            u.IsTenantAdmin, u.IsActive)).ToList();

        var teams = await _db.Teams.AsNoTracking()
            .Where(t => t.TenantId == tenantId)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        var teamDtos = teams.Select(t => new TeamDto(
            t.Id, t.Name, t.Description,
            users.Count(u => u.TeamId == t.Id),
            managers.Count(m => m.TeamId == t.Id))).ToList();

        return new TeamsOverviewDto(teamDtos, users);
    }
}

public class CreateTeamHandler : ICommandHandler
{
    private const int MaxTeams = 50;
    private readonly FlowDbContext _db;
    public CreateTeamHandler(FlowDbContext db) => _db = db;

    public async Task<TeamDto> Handle(CreateTeamDto dto, CancellationToken ct = default)
    {
        var name = dto.Name?.Trim() ?? "";
        if (name.Length == 0) throw new InvalidOperationException("Give the team a name.");
        if (name.Length > 100) throw new InvalidOperationException("Team names can be at most 100 characters.");

        var existing = await _db.Teams.Where(t => t.TenantId == dto.TenantId).ToListAsync(ct);

        if (existing.Count >= MaxTeams)
            throw new InvalidOperationException($"A workspace can have at most {MaxTeams} teams.");

        if (existing.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"You already have a team called \"{name}\".");

        var team = new Team
        {
            TenantId = dto.TenantId,
            Name = name,
            Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim(),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = dto.CreatedBy
        };

        _db.Teams.Add(team);
        await _db.SaveChangesAsync(ct);

        return new TeamDto(team.Id, team.Name, team.Description, 0, 0);
    }
}

public class UpdateTeamHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    public UpdateTeamHandler(FlowDbContext db) => _db = db;

    public async Task Handle(UpdateTeamDto dto, CancellationToken ct = default)
    {
        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == dto.TeamId && t.TenantId == dto.TenantId, ct)
            ?? throw new KeyNotFoundException("Team not found");

        var name = dto.Name?.Trim() ?? "";
        if (name.Length == 0) throw new InvalidOperationException("Give the team a name.");

        var clash = await _db.Teams.AnyAsync(t =>
            t.TenantId == dto.TenantId && t.Id != dto.TeamId && t.Name == name, ct);
        if (clash) throw new InvalidOperationException($"You already have a team called \"{name}\".");

        team.Name = name;
        team.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
        team.UpdatedAtUtc = DateTime.UtcNow;
        team.UpdatedBy = dto.UpdatedBy;

        await _db.SaveChangesAsync(ct);
    }
}

public class DeleteTeamHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    public DeleteTeamHandler(FlowDbContext db) => _db = db;

    /// <summary>
    /// Members are released (TeamId = null), not deleted. A manager with
    /// Team scope and no team then sees their own + unassigned leads —
    /// narrower, never wider, so deleting a team can't leak anything.
    /// </summary>
    public async Task Handle(Guid tenantId, Guid teamId, CancellationToken ct = default)
    {
        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == teamId && t.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException("Team not found");

        var members = await _db.Users
            .Where(u => u.TenantId == tenantId && u.TeamId == teamId)
            .ToListAsync(ct);

        foreach (var u in members)
        {
            u.TeamId = null;
            u.UpdatedAtUtc = DateTime.UtcNow;
        }

        // Nobody manages a team that no longer exists.
        var managers = await _db.TeamManagers
            .Where(m => m.TenantId == tenantId && m.TeamId == teamId)
            .ToListAsync(ct);
        _db.TeamManagers.RemoveRange(managers);

        _db.Teams.Remove(team);
        await _db.SaveChangesAsync(ct);
    }
}

public class SetUserTeamHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    public SetUserTeamHandler(FlowDbContext db) => _db = db;

    public async Task Handle(SetUserTeamDto dto, CancellationToken ct = default)
    {
        // Users is not tenant-filtered — the TenantId check IS the isolation.
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == dto.UserId && u.TenantId == dto.TenantId && !u.IsDeleted, ct)
            ?? throw new KeyNotFoundException("User not found");

        if (dto.TeamId.HasValue)
        {
            var teamExists = await _db.Teams.AnyAsync(t => t.Id == dto.TeamId && t.TenantId == dto.TenantId, ct);
            if (!teamExists) throw new KeyNotFoundException("Team not found");
        }

        user.TeamId = dto.TeamId;
        user.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Replaces the full set of teams a user manages. Sending the whole set
/// (not add/remove one) means two admins editing at once can't leave a
/// half-applied state.
/// </summary>
public class SetUserManagedTeamsHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    public SetUserManagedTeamsHandler(FlowDbContext db) => _db = db;

    public async Task Handle(SetUserManagedTeamsDto dto, CancellationToken ct = default)
    {
        var userExists = await _db.Users.AnyAsync(
            u => u.Id == dto.UserId && u.TenantId == dto.TenantId && !u.IsDeleted, ct);
        if (!userExists) throw new KeyNotFoundException("User not found");

        var wanted = (dto.TeamIds ?? new()).Distinct().ToList();

        if (wanted.Count > 0)
        {
            var valid = await _db.Teams
                .Where(t => t.TenantId == dto.TenantId && wanted.Contains(t.Id))
                .Select(t => t.Id)
                .ToListAsync(ct);

            if (valid.Count != wanted.Count) throw new KeyNotFoundException("Team not found");
        }

        var current = await _db.TeamManagers
            .Where(m => m.TenantId == dto.TenantId && m.UserId == dto.UserId)
            .ToListAsync(ct);

        _db.TeamManagers.RemoveRange(current.Where(m => !wanted.Contains(m.TeamId)));

        foreach (var teamId in wanted.Where(t => current.All(m => m.TeamId != t)))
        {
            _db.TeamManagers.Add(new TeamManager
            {
                TenantId = dto.TenantId,
                TeamId = teamId,
                UserId = dto.UserId,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = dto.UpdatedBy
            });
        }

        await _db.SaveChangesAsync(ct);
    }
}
