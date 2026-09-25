// =====================================================================
// RecordVisibilityHandlers.cs
// Location: MerkaiTrial.Application/Commands/RecordVisibility/RecordVisibilityHandlers.cs
//
// COMPLETE FILE — replaces the previous version.
//
// Read/write side of Settings → Record visibility:
//   • the role × module scope matrix (Own / Team / All)
//   • teams, and which team each user is in
//   • which teams each user MANAGES (many), and locked roles
//
// All handlers take the tenant id from the caller (controller reads it
// from the signed-in user) and re-check every id belongs to that tenant.
//
// CHANGES (026)
//   ✅ EVERY WRITE IS NOW AUDITED. Changing who can see whose records,
//      and who manages a team, is a privilege change: it widens what
//      people see and it decides who signs off on quotes
//      (QuoteApprovalEngine reads TeamManagers). Until now only
//      SetRecordScopeHandler wrote to the application log and nothing
//      reached AuditLogs, so there was no record of it afterwards.
//      Audited with the string overload, so no new AuditAction /
//      AuditEntityType members are needed.
//
//   ✅ NULL-SAFE NAME READS. FirstName / LastName / Email are declared
//      non-nullable but the columns allow NULL. EF emits an unguarded
//      GetFieldValue<string> for such a projection, so a single row with
//      a NULL name takes the whole page down with
//      SqlNullValueException — the same crash we hit on
//      DealStageHistory in 023a. The fix has to be INSIDE the
//      projection, where it becomes COALESCE in the SQL:
//          First = u.FirstName ?? string.Empty
//      Doing it after the read (in C#) never runs, because the reader
//      throws first.
//
//      SCOPE OF THAT FIX: it covers every projection this page READS, so
//      one bad row can no longer take the whole page down. The two write
//      handlers (SetUserTeam, DeleteTeam) still load tracked User
//      entities, which materialise every column and would still throw —
//      exactly as they always have. Changing that means replacing the
//      tracked write with ExecuteUpdateAsync, which needs the real
//      nullability of User.UpdatedAtUtc, so it is left as-is rather than
//      guessed at. If a NULL name ever does surface there, that is the
//      fix.
//
//   ✅ ACTIVE MANAGERS ARE COUNTED SEPARATELY.
//      QuoteApprovalEngine.ApproversForDealAsync only ever picks ACTIVE
//      users, so a team whose only manager can no longer sign in has, in
//      practice, no manager: its quotes all fall to workspace admins.
//      TeamDto now carries ActiveManagerCount so the page can say so
//      instead of showing a reassuring green "1 manager".
//
//   ✅ Names travel with the data, so the page can explain itself:
//        TeamDto.ManagerNames      — who manages this team
//        RoleScopeRowDto.Holders   — who holds this role
//        TeamUserDto.RoleIds/Names — so the page can work out what each
//                                    person actually sees today
//      All added as OPTIONAL trailing parameters, so any existing call
//      site (controller, tests) still compiles unchanged.
//
//   ✅ UpdateTeamHandler's duplicate-name check now matches
//      CreateTeamHandler's — case-insensitive either way, rather than
//      relying on the database collation to be CI.
//
//   ✅ DeleteTeamHandler still releases EVERY row pointing at the team
//      (including soft-deleted users, so no orphan TeamId is left
//      behind) but counts only live people in its audit entry.
//
// Handlers are ICommandHandler, so the Scrutor scan registers them.
// Nothing to add to Program.cs.
// =====================================================================

using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
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
    Dictionary<string, RecordScope> Defaults,   // what "no override" means
    // Up to a handful of the people holding this role, so the page can say
    // WHO a change affects rather than only how many. Optional — older
    // callers get null.
    List<string>? Holders = null);

public record RecordScopeMatrixDto(
    List<string> Modules,
    List<RoleScopeRowDto> Roles);

public record SetRecordScopeDto(
    Guid TenantId,
    Guid RoleId,
    string Module,
    RecordScope Scope,
    string? UpdatedBy = null);

public record TeamDto(
    Guid Id,
    string Name,
    string? Description,
    int MemberCount,
    int ManagerCount,
    // Who manages this team, inactive people marked as such.
    List<string>? ManagerNames = null,
    // Managers who can actually sign in. This is the number that decides
    // whether the team has an approver — QuoteApprovalEngine filters on
    // IsActive. Null means an older caller didn't supply it; treat it as
    // unknown and fall back to ManagerCount.
    int? ActiveManagerCount = null);

public record TeamUserDto(
    Guid Id,
    string FullName,
    string Email,
    Guid? TeamId,                 // the ONE team they belong to
    List<Guid> ManagedTeamIds,    // the teams they manage (any number)
    bool IsTenantAdmin,
    bool IsActive,
    // Roles this person holds — so the page can work out the widest scope
    // they actually get (the widest wins across roles).
    List<Guid>? RoleIds = null,
    List<string>? RoleNames = null);

public record TeamsOverviewDto(List<TeamDto> Teams, List<TeamUserDto> Users);

public record CreateTeamDto(Guid TenantId, string Name, string? Description = null, string? CreatedBy = null);

public record UpdateTeamDto(Guid TenantId, Guid TeamId, string Name, string? Description = null, string? UpdatedBy = null);

public record SetUserTeamDto(Guid TenantId, Guid UserId, Guid? TeamId);

public record SetUserManagedTeamsDto(Guid TenantId, Guid UserId, List<Guid> TeamIds, string? UpdatedBy = null);

// ── Shared helpers ────────────────────────────────────────────────────

internal static class PersonNames
{
    /// <summary>
    /// Joins name parts that have ALREADY been read safely — each part
    /// must be coalesced inside the EF projection (see the header note),
    /// because a NULL column throws in the reader before any C# here runs.
    /// </summary>
    public static string Full(string? first, string? last, string? emailFallback = null)
    {
        var name = $"{first ?? string.Empty} {last ?? string.Empty}".Trim();
        if (name.Length > 0) return name;
        return string.IsNullOrWhiteSpace(emailFallback) ? "(no name)" : emailFallback!;
    }
}

// ── SCOPE MATRIX ──────────────────────────────────────────────────────

public class GetRecordScopeMatrixHandler : ICommandHandler
{
    /// <summary>How many holders to name before falling back to "and N more".</summary>
    private const int MaxHoldersNamed = 8;

    private readonly FlowDbContext _db;
    public GetRecordScopeMatrixHandler(FlowDbContext db) => _db = db;

    public async Task<RecordScopeMatrixDto> Handle(Guid tenantId, CancellationToken ct = default)
    {
        var modules = RecordModules.Enforced.ToList();

        // NO EXPLICIT TENANT FILTER HERE, ON PURPOSE. Role.TenantId is
        // nullable: null means a built-in role shared by every tenant, and
        // those must appear in this grid. FlowDbContext's global query
        // filter for Role is what admits "this tenant's own, plus the
        // shared ones" — see AssertEveryTenantEntityIsCovered. Adding
        // `r.TenantId == tenantId` here would hide every built-in role.
        // Every string is coalesced in the projection: a NULL column
        // throws SqlNullValueException in the reader otherwise.
        var roles = await _db.Roles.AsNoTracking()
            .Where(r => !r.IsDeleted)
            .OrderBy(r => r.IsSystemRole ? 0 : 1).ThenBy(r => r.DisplayName)
            .Select(r => new
            {
                r.Id,
                Name = r.Name ?? string.Empty,
                DisplayName = r.DisplayName ?? string.Empty,
                r.IsSystemRole
            })
            .ToListAsync(ct);

        // This tenant's users and the roles they hold — one query, so the
        // count and the names come from the same read.
        // Users is NOT tenant-filtered; the TenantId predicate IS the
        // isolation.
        var holders = await _db.Users.AsNoTracking()
            .Where(u => u.TenantId == tenantId && !u.IsDeleted)
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
            .Select(u => new
            {
                First = u.FirstName ?? string.Empty,
                Last = u.LastName ?? string.Empty,
                Email = u.Email ?? string.Empty,
                u.IsActive,
                RoleIds = u.UserRoles.Select(ur => ur.RoleId).ToList()
            })
            .ToListAsync(ct);

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

            var mine = holders.Where(h => h.RoleIds.Contains(r.Id)).ToList();

            var named = mine.Take(MaxHoldersNamed)
                .Select(h => PersonNames.Full(h.First, h.Last, h.Email) + (h.IsActive ? "" : " (can't sign in)"))
                .ToList();

            if (mine.Count > MaxHoldersNamed)
                named.Add($"and {mine.Count - MaxHoldersNamed} more");

            return new RoleScopeRowDto(
                r.Id, r.Name, r.DisplayName, r.IsSystemRole,
                mine.Count,
                locked, effective, defaults,
                named);
        }).ToList();

        return new RecordScopeMatrixDto(modules, rows);
    }
}

public class SetRecordScopeHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly IAuditService _audit;
    private readonly ILogger<SetRecordScopeHandler> _logger;

    public SetRecordScopeHandler(FlowDbContext db, IAuditService audit, ILogger<SetRecordScopeHandler> logger)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
    }

    public async Task Handle(SetRecordScopeDto dto, CancellationToken ct = default)
    {
        if (!RecordModules.Enforced.Contains(dto.Module))
            throw new InvalidOperationException($"'{dto.Module}' does not support record visibility yet.");

        if (!Enum.IsDefined(typeof(RecordScope), dto.Scope))
            throw new InvalidOperationException("Choose Own, Team or All.");

        // Built-in roles (TenantId null) plus this tenant's own — admitted
        // by FlowDbContext's global filter for Role. Another tenant's
        // custom role is simply not found.
        var role = await _db.Roles.AsNoTracking()
            .Where(r => r.Id == dto.RoleId && !r.IsDeleted)
            .Select(r => new
            {
                r.Id,
                Name = r.Name ?? string.Empty,
                DisplayName = r.DisplayName ?? string.Empty
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Role not found");

        if (RecordScopeDefaults.IsLocked(role.Name))
            throw new InvalidOperationException(
                $"{role.DisplayName} always sees everything — the workspace administrator must be able to fix any record.");

        var existing = await _db.RoleRecordScopes
            .FirstOrDefaultAsync(s => s.TenantId == dto.TenantId && s.RoleId == dto.RoleId && s.Module == dto.Module, ct);

        var fallback = RecordScopeDefaults.For(role.Name, dto.Module);

        // Captured before anything is mutated, so the audit entry can say
        // what it was as well as what it became.
        var before = existing?.Scope ?? fallback;

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

        // After SaveChanges, and with nothing left tracked that the audit
        // service's own SaveChanges could write back.
        await _audit.WriteAsync(
            "RecordScopeChanged", "Role", dto.RoleId, dto.TenantId,
            new
            {
                role = role.DisplayName,
                roleKey = role.Name,
                module = dto.Module,
                from = before.ToString(),
                to = dto.Scope.ToString(),
                backToDefault = dto.Scope == fallback,
                by = dto.UpdatedBy
            },
            ct);

        _logger.LogInformation(
            "Record scope for role {Role} / {Module} changed from {From} to {To} in tenant {TenantId} by {User}",
            role.DisplayName, dto.Module, before, dto.Scope, dto.TenantId, dto.UpdatedBy);
    }
}

// ── TEAMS ─────────────────────────────────────────────────────────────

public class GetTeamsHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    public GetTeamsHandler(FlowDbContext db) => _db = db;

    public async Task<TeamsOverviewDto> Handle(Guid tenantId, CancellationToken ct = default)
    {
        // Users is NOT tenant-filtered — the TenantId predicate IS the
        // isolation. Every string is coalesced in the projection so a NULL
        // name can't crash the reader.
        var userRows = await _db.Users.AsNoTracking()
            .Where(u => u.TenantId == tenantId && !u.IsDeleted)
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
            .Select(u => new
            {
                u.Id,
                First = u.FirstName ?? string.Empty,
                Last = u.LastName ?? string.Empty,
                Email = u.Email ?? string.Empty,
                u.TeamId,
                u.IsTenantAdmin,
                u.IsActive,
                RoleIds = u.UserRoles.Select(ur => ur.RoleId).ToList()
            })
            .ToListAsync(ct);

        var managers = await _db.TeamManagers.AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .Select(m => new { m.UserId, m.TeamId })
            .ToListAsync(ct);

        // Display names for the roles those users hold. Built-in roles have
        // TenantId null, so this read leans on the same global Role filter
        // as the matrix rather than filtering by tenant here.
        var roleIds = userRows.SelectMany(u => u.RoleIds).Distinct().ToList();

        var roleNames = roleIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Roles.AsNoTracking()
                .Where(r => roleIds.Contains(r.Id) && !r.IsDeleted)
                .Select(r => new { r.Id, DisplayName = r.DisplayName ?? string.Empty })
                .ToDictionaryAsync(r => r.Id, r => r.DisplayName, ct);

        var users = userRows.Select(u => new TeamUserDto(
            u.Id,
            PersonNames.Full(u.First, u.Last, u.Email),
            u.Email,
            u.TeamId,
            managers.Where(m => m.UserId == u.Id).Select(m => m.TeamId).ToList(),
            u.IsTenantAdmin,
            u.IsActive,
            u.RoleIds,
            u.RoleIds.Where(roleNames.ContainsKey).Select(id => roleNames[id]).OrderBy(n => n).ToList()))
            .ToList();

        var teams = await _db.Teams.AsNoTracking()
            .Where(t => t.TenantId == tenantId)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        var byId = users.ToDictionary(u => u.Id);

        var teamDtos = teams.Select(t =>
        {
            var mgrs = managers
                .Where(m => m.TeamId == t.Id && byId.ContainsKey(m.UserId))
                .Select(m => byId[m.UserId])
                .OrderBy(u => u.FullName)
                .ToList();

            return new TeamDto(
                t.Id,
                t.Name,
                t.Description,
                users.Count(u => u.TeamId == t.Id),
                mgrs.Count,
                mgrs.Select(u => u.IsActive ? u.FullName : $"{u.FullName} (can't sign in)").ToList(),
                mgrs.Count(u => u.IsActive));
        }).ToList();

        return new TeamsOverviewDto(teamDtos, users);
    }
}

public class CreateTeamHandler : ICommandHandler
{
    private const int MaxTeams = 50;

    private readonly FlowDbContext _db;
    private readonly IAuditService _audit;

    public CreateTeamHandler(FlowDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<TeamDto> Handle(CreateTeamDto dto, CancellationToken ct = default)
    {
        var name = dto.Name?.Trim() ?? "";
        if (name.Length == 0) throw new InvalidOperationException("Give the team a name.");
        if (name.Length > 100) throw new InvalidOperationException("Team names can be at most 100 characters.");

        var existing = await _db.Teams.AsNoTracking()
            .Where(t => t.TenantId == dto.TenantId)
            .Select(t => t.Name)
            .ToListAsync(ct);

        if (existing.Count >= MaxTeams)
            throw new InvalidOperationException($"A workspace can have at most {MaxTeams} teams.");

        if (existing.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
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

        await _audit.WriteAsync(
            "TeamCreated", "Team", team.Id, dto.TenantId,
            new { name = team.Name, description = team.Description, by = dto.CreatedBy },
            ct);

        return new TeamDto(team.Id, team.Name, team.Description, 0, 0, new List<string>(), 0);
    }
}

public class UpdateTeamHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly IAuditService _audit;

    public UpdateTeamHandler(FlowDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task Handle(UpdateTeamDto dto, CancellationToken ct = default)
    {
        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == dto.TeamId && t.TenantId == dto.TenantId, ct)
            ?? throw new KeyNotFoundException("Team not found");

        var name = dto.Name?.Trim() ?? "";
        if (name.Length == 0) throw new InvalidOperationException("Give the team a name.");
        if (name.Length > 100) throw new InvalidOperationException("Team names can be at most 100 characters.");

        // Compared in memory, case-insensitively, so this agrees with
        // CreateTeamHandler instead of depending on the column collation.
        // Teams are capped at 50 per workspace, so the read is cheap.
        var others = await _db.Teams.AsNoTracking()
            .Where(t => t.TenantId == dto.TenantId && t.Id != dto.TeamId)
            .Select(t => t.Name)
            .ToListAsync(ct);

        if (others.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"You already have a team called \"{name}\".");

        var beforeName = team.Name;
        var beforeDescription = team.Description;

        team.Name = name;
        team.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
        team.UpdatedAtUtc = DateTime.UtcNow;
        team.UpdatedBy = dto.UpdatedBy;

        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(
            "TeamUpdated", "Team", team.Id, dto.TenantId,
            new
            {
                fromName = beforeName,
                toName = team.Name,
                fromDescription = beforeDescription,
                toDescription = team.Description,
                by = dto.UpdatedBy
            },
            ct);
    }
}

public class DeleteTeamHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly IAuditService _audit;

    public DeleteTeamHandler(FlowDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    /// <summary>
    /// Members are released (TeamId = null), not deleted. A manager with
    /// Team scope and no team then sees their own + unassigned leads —
    /// narrower, never wider, so deleting a team can't leak anything.
    ///
    /// Soft-deleted users are released too. They are not counted in the
    /// audit entry (nobody lost anything), but leaving their TeamId
    /// pointing at a row that no longer exists would create exactly the
    /// orphan the 026 check script looks for.
    /// </summary>
    public async Task Handle(Guid tenantId, Guid teamId, CancellationToken ct = default)
    {
        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == teamId && t.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException("Team not found");

        var teamName = team.Name;

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

        var releasedMembers = members.Count(u => !u.IsDeleted);
        var releasedManagers = managers.Count;

        _db.Teams.Remove(team);
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(
            "TeamDeleted", "Team", teamId, tenantId,
            new { name = teamName, membersReleased = releasedMembers, managersReleased = releasedManagers },
            ct);
    }
}

public class SetUserTeamHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly IAuditService _audit;

    public SetUserTeamHandler(FlowDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task Handle(SetUserTeamDto dto, CancellationToken ct = default)
    {
        // Users is not tenant-filtered — the TenantId check IS the isolation.
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == dto.UserId && u.TenantId == dto.TenantId && !u.IsDeleted, ct)
            ?? throw new KeyNotFoundException("User not found");

        string? teamName = null;

        if (dto.TeamId.HasValue)
        {
            teamName = await _db.Teams.AsNoTracking()
                .Where(t => t.Id == dto.TeamId && t.TenantId == dto.TenantId)
                .Select(t => t.Name)
                .FirstOrDefaultAsync(ct);

            if (teamName == null) throw new KeyNotFoundException("Team not found");
        }

        var before = user.TeamId;

        // Nothing to do — and nothing worth an audit entry either. Nothing
        // else in this handler has a side effect, so returning here skips
        // only an UpdatedAtUtc bump and a no-op SaveChanges.
        if (before == dto.TeamId) return;

        var beforeName = before.HasValue
            ? await _db.Teams.AsNoTracking()
                .Where(t => t.Id == before.Value && t.TenantId == dto.TenantId)
                .Select(t => t.Name)
                .FirstOrDefaultAsync(ct)
            : null;

        user.TeamId = dto.TeamId;
        user.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(
            "UserTeamChanged", "User", dto.UserId, dto.TenantId,
            new
            {
                person = PersonNames.Full(user.FirstName, user.LastName, user.Email),
                from = beforeName ?? "(no team)",
                to = teamName ?? "(no team)"
            },
            ct);
    }
}

/// <summary>
/// Replaces the full set of teams a user manages.
///
/// Sending the whole set (rather than add-one / remove-one) keeps two
/// admins from leaving a half-applied state — but it also means the
/// LATER writer silently wins. The caller is therefore responsible for
/// only calling this when the set really changed, and for checking that
/// what it believes the current set to be still matches. The Admin.Web
/// page does both (see OnPostPersonAsync).
///
/// This is a privilege change twice over: with Team scope a manager sees
/// every record belonging to the teams they manage, and
/// QuoteApprovalEngine treats them as an approver for those teams'
/// deals. Hence the audit entry naming exactly what was added and
/// removed.
/// </summary>
public class SetUserManagedTeamsHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly IAuditService _audit;

    public SetUserManagedTeamsHandler(FlowDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task Handle(SetUserManagedTeamsDto dto, CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking()
            .Where(u => u.Id == dto.UserId && u.TenantId == dto.TenantId && !u.IsDeleted)
            .Select(u => new
            {
                First = u.FirstName ?? string.Empty,
                Last = u.LastName ?? string.Empty,
                Email = u.Email ?? string.Empty
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("User not found");

        var wanted = (dto.TeamIds ?? new()).Distinct().ToList();

        // Names for the audit entry, and the tenant check, in one read.
        var validTeams = wanted.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Teams.AsNoTracking()
                .Where(t => t.TenantId == dto.TenantId && wanted.Contains(t.Id))
                .Select(t => new { t.Id, t.Name })
                .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        if (validTeams.Count != wanted.Count) throw new KeyNotFoundException("Team not found");

        var current = await _db.TeamManagers
            .Where(m => m.TenantId == dto.TenantId && m.UserId == dto.UserId)
            .ToListAsync(ct);

        var removed = current.Where(m => !wanted.Contains(m.TeamId)).ToList();
        var addedIds = wanted.Where(t => current.All(m => m.TeamId != t)).ToList();

        // No change, no audit noise. Nothing else in this handler has a
        // side effect, so there is nothing else to skip.
        if (removed.Count == 0 && addedIds.Count == 0) return;

        // Names for the teams being taken away — fetched before the rows go.
        var removedTeamIds = removed.Select(m => m.TeamId).ToList();

        var removedNames = removedTeamIds.Count == 0
            ? new List<string>()
            : await _db.Teams.AsNoTracking()
                .Where(t => t.TenantId == dto.TenantId && removedTeamIds.Contains(t.Id))
                .Select(t => t.Name)
                .ToListAsync(ct);

        _db.TeamManagers.RemoveRange(removed);

        foreach (var teamId in addedIds)
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

        await _audit.WriteAsync(
            "UserManagedTeamsChanged", "User", dto.UserId, dto.TenantId,
            new
            {
                person = PersonNames.Full(user.First, user.Last, user.Email),
                nowManages = wanted.Select(id => validTeams[id]).OrderBy(n => n).ToList(),
                added = addedIds.Select(id => validTeams[id]).OrderBy(n => n).ToList(),
                removed = removedNames.OrderBy(n => n).ToList(),
                by = dto.UpdatedBy
            },
            ct);
    }
}
