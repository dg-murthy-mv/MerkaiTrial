// =====================================================================
// FILE: MerkaiTrial.Application/Security/AccessChangeGuard.cs
//
// NEW (104). Nobody can give anyone more access than they have themselves.
//
// WHAT WAS WRONG
//   A workspace admin (IsTenantAdmin) passes every permission check. The
//   people and roles endpoints only asked "may you edit people / roles?",
//   so anyone holding users.update or roles.update — a manager, say —
//   could:
//     • tick "workspace admin" on themselves or a friend (PUT /api/users),
//       or add a person as one (POST /api/users) — and with it, everything;
//     • give themselves a role with permissions they don't hold, or add
//       permissions to their own role (roles.update);
//     • change an admin's email, then reset the password on the new
//       address — the admin's account was theirs.
//
// THE RULE, for anyone who is not a workspace admin
//   (admins and the platform's super admin are not limited here):
//     1. They can't make anyone a workspace admin, or stop anyone being one.
//     2. They can't change, switch off, delete or re-role a person who has
//        access they don't (a workspace admin, or roles with permissions
//        beyond their own).
//     3. They can't give roles — to a person, or to a role's own list —
//        that carry permissions they don't hold, and can't edit or delete a
//        role that does.
//
// A refusal is a PrivilegeEscalationException: 403, with the sentence.
// =====================================================================

using System.Text.Json;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Security;

/// <summary>A change refused because it would give more access than the caller has. Shown to the person (403).</summary>
public sealed class PrivilegeEscalationException : UnauthorizedAccessException
{
    public PrivilegeEscalationException(string message) : base(message) { }
}

public sealed class AccessChangeGuard : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ICurrentUserService _me;

    public AccessChangeGuard(FlowDbContext db, ICurrentUserService me)
    {
        _db = db;
        _me = me;
    }

    /// <summary>Adding a person: as an admin, or with these roles.</summary>
    public async Task CheckNewPersonAsync(Guid tenantId, bool makesAdmin, IEnumerable<Guid>? roleIds, CancellationToken ct = default)
    {
        var me = await _me.GetCurrentUserAsync();
        if (me.IsTenantAdmin) return;

        if (makesAdmin)
            throw new PrivilegeEscalationException("Only a workspace admin can add another workspace admin.");

        await EnsureRolesWithinAsync(me, tenantId, roleIds, "give that role", ct);
    }

    /// <summary>
    /// Changing a person — their details (and admin flag), switching them on
    /// or off, deleting them, or replacing their roles.
    /// </summary>
    public async Task CheckChangePersonAsync(Guid tenantId, Guid userId, bool? newIsTenantAdmin, IEnumerable<Guid>? newRoleIds,
        CancellationToken ct = default)
    {
        var me = await _me.GetCurrentUserAsync();
        if (me.IsTenantAdmin) return;

        var target = await _db.Users.AsNoTracking().IgnoreQueryFilters()
            .Where(u => u.Id == userId && u.TenantId == tenantId && !u.IsDeleted)
            .Select(u => new { u.IsTenantAdmin })
            .FirstOrDefaultAsync(ct);
        if (target is null) return;   // the handler answers "not found"

        if (target.IsTenantAdmin)
            throw new PrivilegeEscalationException("Only a workspace admin can change a workspace admin's account.");

        if (newIsTenantAdmin == true)
            throw new PrivilegeEscalationException("Only a workspace admin can make someone a workspace admin.");

        var targetRoleIds = await _db.UserRoles.AsNoTracking().IgnoreQueryFilters()
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.RoleId)
            .ToListAsync(ct);
        await EnsureRolesWithinAsync(me, tenantId, targetRoleIds,
            "change this person — they have access you don't", ct);

        if (newRoleIds is not null)
            await EnsureRolesWithinAsync(me, tenantId, newRoleIds, "give that role", ct);
    }

    /// <summary>Creating or editing a role (<paramref name="existingRoleId"/> null for a new one) with these permissions.</summary>
    public async Task CheckRoleAsync(Guid? existingRoleId, string? newPermissionsJson, CancellationToken ct = default)
    {
        var me = await _me.GetCurrentUserAsync();
        if (me.IsTenantAdmin) return;
        var mine = Flatten(me.Permissions);

        if (existingRoleId is Guid id)
        {
            var current = await _db.Roles.AsNoTracking()
                .Where(r => r.Id == id && !r.IsDeleted)
                .Select(r => r.Permissions)
                .FirstOrDefaultAsync(ct);
            var beyond = Parse(current).Except(mine).ToList();
            if (beyond.Count > 0)
                throw new PrivilegeEscalationException(
                    $"You can't change or delete this role: it has access you don't ({Describe(beyond)}).");
        }

        if (newPermissionsJson is not null)
        {
            var beyond = Parse(newPermissionsJson).Except(mine).ToList();
            if (beyond.Count > 0)
                throw new PrivilegeEscalationException(
                    $"You can't give a role access you don't have yourself ({Describe(beyond)}).");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────

    private async Task EnsureRolesWithinAsync(CurrentUserContext me, Guid tenantId, IEnumerable<Guid>? roleIds, string what,
        CancellationToken ct)
    {
        var ids = roleIds?.Distinct().ToList() ?? new List<Guid>();
        if (ids.Count == 0) return;

        var perms = await _db.Roles.AsNoTracking().IgnoreQueryFilters()
            .Where(r => ids.Contains(r.Id) && !r.IsDeleted && (r.TenantId == tenantId || r.TenantId == null))
            .Select(r => r.Permissions)
            .ToListAsync(ct);

        var beyond = perms.SelectMany(Parse).Distinct().Except(Flatten(me.Permissions)).ToList();
        if (beyond.Count > 0)
            throw new PrivilegeEscalationException($"You can't {what} ({Describe(beyond)}).");
    }

    /// <summary>"module.action" for every permission in a role's JSON ({"leads":["read","create"]}). Unreadable JSON grants nothing, as at sign-in.</summary>
    public static HashSet<string> Parse(string? json)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return set;
        try
        {
            var perms = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (perms is null) return set;
            foreach (var (module, actions) in perms)
                foreach (var action in actions ?? new List<string>())
                    set.Add($"{module}.{action}");
        }
        catch (JsonException) { }
        return set;
    }

    private static HashSet<string> Flatten(Dictionary<string, List<string>>? perms)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (perms is null) return set;
        foreach (var (module, actions) in perms)
            foreach (var action in actions ?? new List<string>())
                set.Add($"{module}.{action}");
        return set;
    }

    private static string Describe(List<string> perms) =>
        string.Join(", ", perms.OrderBy(p => p).Take(5)) + (perms.Count > 5 ? $" and {perms.Count - 5} more" : "");
}
