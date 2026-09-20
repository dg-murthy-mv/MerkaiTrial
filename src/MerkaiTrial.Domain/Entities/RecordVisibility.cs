// =====================================================================
// RecordVisibility.cs
// Location: MerkaiTrial.Domain/Entities/RecordVisibility.cs
//
// NEW FILE. Who can see WHICH records — separate from what they can DO.
//
//   Permissions (Role.Permissions JSON) answer "may this person read
//   leads at all?". Record scope answers "WHICH leads?".
//
// WHY A SEPARATE TABLE, NOT A FIELD ON ROLE
//   Built-in roles (Sales Rep, Sales Manager) are ONE shared row for every
//   tenant and are read-only to tenants. A scope stored on the role would
//   let one tenant change what every other tenant's reps can see — the
//   exact bug the role split removed. RoleRecordScopes is keyed by
//   (TenantId, RoleId, Module), so each tenant owns its own setting, for
//   built-in and custom roles alike.
//
//   No row = the code default (RecordScopeDefaults). Rows exist only
//   where a tenant admin changed something.
//
// ROUND 2 (migration 015)
//   • Unknown / new roles now default to OWN (least privilege). Roles that
//     existed before 015 were given an explicit "All" row by the migration,
//     so nobody lost access they had.
//   • tenant_admin is LOCKED to All — shown, never editable.
//   • viewer defaults to All (read-only users own nothing, so Own would
//     show them an empty CRM).
//   • TeamManager: a user can MANAGE several teams (sees all their members'
//     records) while still BELONGING to one (User.TeamId).
// =====================================================================

namespace MerkaiTrial.Domain.Entities;

/// <summary>
/// How much of a module a role can see. Ordered narrow → wide: when a user
/// holds several roles, the WIDEST wins.
/// </summary>
public enum RecordScope
{
    /// <summary>Records assigned to me.</summary>
    Own = 0,

    /// <summary>Records assigned to me or anyone in my team, plus unassigned ones.</summary>
    Team = 1,

    /// <summary>Every record in the workspace.</summary>
    All = 2
}

/// <summary>Module names used as RoleRecordScope.Module. Match Modules.* casing.</summary>
public static class RecordModules
{
    public const string Leads = "Leads";
    public const string Deals = "Deals";

    /// <summary>
    /// Modules whose scope is actually ENFORCED today. The settings page
    /// only offers these — a setting that silently does nothing is worse
    /// than no setting. Deals added in round 3 (016).
    /// </summary>
    public static readonly IReadOnlyList<string> Enforced = new[] { Leads, Deals };

    public static bool IsKnown(string module) =>
        module == Leads || module == Deals;
}

/// <summary>
/// Defaults when a tenant has not set anything. Matched on the role's
/// Name (not DisplayName), normalised so "sales-rep", "sales_rep" and
/// "Sales Rep" all match.
///
/// Anything not listed — including every role created from now on —
/// defaults to OWN. An admin widens access on purpose; nobody gets every
/// lead in the workspace because a setting was forgotten.
/// </summary>
public static class RecordScopeDefaults
{
    private static readonly HashSet<string> LockedAllRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "tenant_admin", "tenantadmin"
    };

    private static readonly HashSet<string> AllRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "viewer"
    };

    private static readonly HashSet<string> OwnRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "sales_rep", "sales_representative", "salesrep"
    };

    private static readonly HashSet<string> TeamRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "sales_manager", "salesmanager"
    };

    public static RecordScope For(string? roleName, string module)
    {
        var n = Normalise(roleName);
        if (LockedAllRoles.Contains(n)) return RecordScope.All;
        if (AllRoles.Contains(n))       return RecordScope.All;
        if (TeamRoles.Contains(n))      return RecordScope.Team;
        if (OwnRoles.Contains(n))       return RecordScope.Own;
        return RecordScope.Own;          // new / unknown roles: least privilege
    }

    /// <summary>
    /// Roles whose scope is fixed at All and cannot be overridden. The
    /// workspace administrator must always be able to see everything, or
    /// nobody can fix a mis-assigned lead.
    /// </summary>
    public static bool IsLocked(string? roleName) => LockedAllRoles.Contains(Normalise(roleName));

    public static string Normalise(string? roleName) =>
        (roleName ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
}

/// <summary>A tenant's override of one role's scope for one module.</summary>
public class RoleRecordScope
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid RoleId { get; set; }

    /// <summary>RecordModules.Leads / RecordModules.Deals</summary>
    public string Module { get; set; } = string.Empty;

    public RecordScope Scope { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// "This user manages that team." A manager sees records owned by members
/// of every team they manage, in addition to their own team. Separate from
/// membership: a sales manager usually belongs to one team but oversees
/// two or three.
/// </summary>
public class TeamManager
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid TeamId { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
}

/// <summary>
/// A group of users. "Team" scope = records owned by anyone in my team.
/// One team per user (User.TeamId) — enough for a sales floor; nested
/// teams / hierarchies can come later without changing this table.
/// </summary>
public class Team
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}
