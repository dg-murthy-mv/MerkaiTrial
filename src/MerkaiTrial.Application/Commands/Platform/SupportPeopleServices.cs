// =====================================================================
// FILE: MerkaiTrial.Application/Commands/Platform/SupportPeopleServices.cs
//
// NEW (118). What MadeeVision support may do for the PEOPLE in a customer's
// workspace, and for its own staff. Replaces Admin → Users, which let a
// super admin create, edit and delete anyone in any workspace — the
// workspace's own admin does that now (Settings → Users).
//
// WorkspacePeopleService — one workspace (Admin → Tenants → People):
//   • list the people (never the super admins)
//   • unlock someone locked out by wrong passwords
//   • make someone the workspace admin, or take it away — never from the
//     last active admin
//   • switch someone off or on — never the last active admin
//   • add a NEW workspace admin by invite, when the old one has left and
//     nobody else can take over
//   (sending an invite or a reset is IAccountEmails; the page calls
//   RecordLinkIssuedAsync afterwards so it is written down too)
//
// PlatformTeamService — MadeeVision's own staff (Admin → MadeeVision Team):
//   • list the super admins
//   • add one by invite (in the caller's own workspace, as admin there)
//   • switch one off or on — never yourself, never the last active one
//
// EVERY CHANGE IS RECORDED in the workspace's own Activity Log
// (AuditLogs, with that workspace's TenantId), so a customer's admin can
// see what MadeeVision support did in their workspace, and when. The log
// row is saved in the SAME SaveChanges as the change (WriteCriticalAsync
// saves everything tracked), so there is never a change without its row.
//
// Switching someone off, or changing whether they are an admin, gives
// them a new SecurityStamp: their open sessions end and they sign in
// again with their new access (the 102 / 104 rule).
//
// Not ICommandHandler on purpose: these need IAccountEmails, which only
// Admin.Web registers. Admin.Web registers them by hand.
// =====================================================================

using MerkaiTrial.Application.Commands.Users;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.Platform;

// ── What the pages show ───────────────────────────────────────────────

public sealed class SupportPersonDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool IsTenantAdmin { get; set; }
    public bool IsSuperAdmin { get; set; }
    /// <summary>No password yet: they have never used their invite.</summary>
    public bool HasPassword { get; set; }
    public DateTime? LastLoginUtc { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public int FailedLoginCount { get; set; }
    public List<string> Roles { get; set; } = new();
    public DateTime CreatedAtUtc { get; set; }

    public bool IsLocked => LockedUntilUtc.HasValue && LockedUntilUtc.Value > DateTime.UtcNow;
    public bool NeverSignedIn => LastLoginUtc is null;
}

public sealed class WorkspacePeopleDto
{
    public Guid TenantId { get; set; }
    public string WorkspaceName { get; set; } = string.Empty;
    public bool WorkspaceIsActive { get; set; }
    public int TotalPeople { get; set; }
    public int ActiveAdmins { get; set; }
    public List<SupportPersonDto> People { get; set; } = new();
}

public static class SupportPeopleRules
{
    /// <summary>How many rows a list shows at once; the search narrows it.</summary>
    public const int ListLimit = 100;

    public static string? NewPersonProblem(string? first, string? last, string? email)
    {
        if (string.IsNullOrWhiteSpace(first)) return "Enter their first name.";
        if (first.Trim().Length > 100) return "The first name can be up to 100 characters.";
        if ((last ?? "").Trim().Length > 100) return "The last name can be up to 100 characters.";
        if (string.IsNullOrWhiteSpace(email)) return "Enter their email address.";
        var e = email.Trim();
        if (e.Length > 256) return "The email address can be up to 256 characters.";
        if (!System.Net.Mail.MailAddress.TryCreate(e, out var parsed) || !string.Equals(parsed.Address, e, StringComparison.OrdinalIgnoreCase)
            || !e.Contains('.', StringComparison.Ordinal) || e.Contains(' '))
            return "That doesn't look like an email address.";
        return null;
    }

    internal static string Short(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "MadeeVision support" : (s.Length <= max ? s : s[..max]);

    /// <summary>The text after the action in the workspace's Activity Log.</summary>
    internal const string BySupport = "by MadeeVision support";
}

// =====================================================================
// One workspace's people
// =====================================================================

public sealed class WorkspacePeopleService
{
    private readonly FlowDbContext _db;
    private readonly IAuditService _audit;
    private readonly CreateUserHandler _createUser;

    public WorkspacePeopleService(FlowDbContext db, IAuditService audit, CreateUserHandler createUser)
    {
        _db = db; _audit = audit; _createUser = createUser;
    }

    /// <exception cref="KeyNotFoundException">No such workspace.</exception>
    public async Task<WorkspacePeopleDto> ListAsync(Guid tenantId, string? search, CancellationToken ct = default)
    {
        var tenant = await _db.Tenants.AsNoTracking().IgnoreQueryFilters()
            .Where(t => t.Id == tenantId && !t.IsDeleted)
            .Select(t => new { t.Id, t.Name, t.IsActive })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("That workspace does not exist.");

        var all = _db.Users.AsNoTracking().IgnoreQueryFilters()
            .Where(u => u.TenantId == tenantId && !u.IsDeleted && !u.IsSuperAdmin);

        var total = await all.CountAsync(ct);
        var activeAdmins = await all.CountAsync(u => u.IsActive && u.IsTenantAdmin, ct);

        var q = all;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(u => u.Email.Contains(s) || u.FirstName.Contains(s) || u.LastName.Contains(s));
        }

        var people = await Project(q
                .OrderByDescending(u => u.IsTenantAdmin)
                .ThenByDescending(u => u.IsActive)
                .ThenBy(u => u.FirstName).ThenBy(u => u.LastName)
                .Take(SupportPeopleRules.ListLimit))
            .ToListAsync(ct);

        return new WorkspacePeopleDto
        {
            TenantId = tenant.Id,
            WorkspaceName = tenant.Name,
            WorkspaceIsActive = tenant.IsActive,
            TotalPeople = total,
            ActiveAdmins = activeAdmins,
            People = people,
        };
    }

    /// <summary>One person, and only if they are in this workspace (never a super admin).</summary>
    /// <exception cref="KeyNotFoundException">Not in this workspace.</exception>
    public async Task<SupportPersonDto> GetAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
        => await Project(_db.Users.AsNoTracking().IgnoreQueryFilters()
                .Where(u => u.Id == userId && u.TenantId == tenantId && !u.IsDeleted && !u.IsSuperAdmin))
            .FirstOrDefaultAsync(ct)
           ?? throw new KeyNotFoundException("That person is not in this workspace.");

    /// <summary>Wrong-password lockout cleared; they can try again straight away.</summary>
    public async Task<string> UnlockAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        var u = await LoadAsync(tenantId, userId, ct);
        if (!(u.LockoutEndUtc.HasValue && u.LockoutEndUtc.Value > DateTime.UtcNow) && u.FailedLoginCount == 0)
            throw new InvalidOperationException($"{u.FullName} is not locked out.");

        u.LockoutEndUtc = null;
        u.FailedLoginCount = 0;
        u.UpdatedAtUtc = DateTime.UtcNow;
        u.UpdatedBy = "MadeeVision support";

        await _audit.WriteCriticalAsync(AuditAction.AccountUnlocked, "User", u.Id, tenantId,
            new { person = u.FullName, summary = SupportPeopleRules.BySupport }, ct);
        return $"{u.FullName} is unlocked and can sign in again.";
    }

    /// <summary>Make them the workspace admin (they keep their roles).</summary>
    public async Task<string> MakeAdminAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        var u = await LoadAsync(tenantId, userId, ct);
        if (u.IsTenantAdmin) throw new InvalidOperationException($"{u.FullName} is already a workspace admin.");
        if (!u.IsActive) throw new InvalidOperationException($"{u.FullName} is switched off. Switch them on first.");

        u.IsTenantAdmin = true;
        Touch(u);
        await _audit.WriteCriticalAsync(AuditAction.PermissionChanged, "User", u.Id, tenantId,
            new { person = u.FullName, change = "made workspace admin", summary = $"{u.FullName} made workspace admin {SupportPeopleRules.BySupport}" }, ct);
        return $"{u.FullName} is now a workspace admin. Their next page load signs them in again with the new access.";
    }

    /// <summary>Take workspace admin away — never from the last active admin.</summary>
    public async Task<string> RemoveAdminAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        var u = await LoadAsync(tenantId, userId, ct);
        if (!u.IsTenantAdmin) throw new InvalidOperationException($"{u.FullName} is not a workspace admin.");
        await EnsureAnotherActiveAdminAsync(tenantId, u, "take admin away from", ct);

        u.IsTenantAdmin = false;
        Touch(u);
        await _audit.WriteCriticalAsync(AuditAction.PermissionChanged, "User", u.Id, tenantId,
            new { person = u.FullName, change = "no longer workspace admin", summary = $"{u.FullName} is no longer workspace admin ({SupportPeopleRules.BySupport})" }, ct);
        return $"{u.FullName} is no longer a workspace admin. They keep their other roles.";
    }

    /// <summary>Switch someone off (signs them out everywhere) or back on.</summary>
    public async Task<string> SetActiveAsync(Guid tenantId, Guid userId, bool active, CancellationToken ct = default)
    {
        var u = await LoadAsync(tenantId, userId, ct);
        if (u.IsActive == active)
            throw new InvalidOperationException($"{u.FullName} is already switched {(active ? "on" : "off")}.");
        if (!active && u.IsTenantAdmin)
            await EnsureAnotherActiveAdminAsync(tenantId, u, "switch off", ct);

        u.IsActive = active;
        if (active) u.UpdatedAtUtc = DateTime.UtcNow; else Touch(u);
        u.UpdatedBy = "MadeeVision support";

        await _audit.WriteCriticalAsync(AuditAction.PermissionChanged, "User", u.Id, tenantId,
            new { person = u.FullName, change = active ? "switched on" : "switched off",
                  summary = $"{u.FullName} switched {(active ? "on" : "off")} {SupportPeopleRules.BySupport}" }, ct);
        return active
            ? $"{u.FullName} is switched on. They can sign in again."
            : $"{u.FullName} is switched off and signed out everywhere. They keep their place in the workspace.";
    }

    /// <summary>
    /// A NEW workspace admin, by invite — for when the old admin has left and
    /// nobody in the workspace can take over. Counts against the plan's
    /// user limit like anyone else (CreateUserHandler holds the place).
    /// Returns the new person's id; the page then sends the invite.
    /// </summary>
    public async Task<Guid> AddAdminAsync(Guid tenantId, string first, string? last, string email, string by, CancellationToken ct = default)
    {
        var problem = SupportPeopleRules.NewPersonProblem(first, last, email);
        if (problem is not null) throw new InvalidOperationException(problem);

        var tenantOk = await _db.Tenants.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(t => t.Id == tenantId && !t.IsDeleted, ct);
        if (!tenantOk) throw new KeyNotFoundException("That workspace does not exist.");

        var created = await _createUser.Handle(new CreateUserCommand(
            tenantId, first.Trim(), (last ?? "").Trim(), email.Trim(),
            null, null, null, IsTenantAdmin: true, RoleIds: new List<Guid>(),
            CreatedBy: SupportPeopleRules.Short(by, 100)));

        await _audit.WriteCriticalAsync(AuditAction.PermissionChanged, "User", created.Id, tenantId,
            new { person = $"{created.FirstName} {created.LastName}".Trim(), change = "added as workspace admin",
                  summary = $"{created.Email} added as workspace admin {SupportPeopleRules.BySupport}" }, ct);
        return created.Id;
    }

    /// <summary>After IAccountEmails issued an invite or a reset for someone in this workspace.</summary>
    public Task RecordLinkIssuedAsync(Guid tenantId, Guid userId, string personName, bool invite, bool emailed, CancellationToken ct = default)
        => _audit.WriteAsync(invite ? AuditAction.InviteIssued : AuditAction.ResetIssued, "User", userId, tenantId,
            new { person = personName, emailed,
                  summary = $"for {personName}, {(emailed ? "emailed" : "link given by hand")}, {SupportPeopleRules.BySupport}" }, ct);

    // ── plumbing ────────────────────────────────────────────────────

    private async Task<User> LoadAsync(Guid tenantId, Guid userId, CancellationToken ct)
        => await _db.Users.IgnoreQueryFilters()
               .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId && !u.IsDeleted && !u.IsSuperAdmin, ct)
           ?? throw new KeyNotFoundException("That person is not in this workspace.");

    private async Task EnsureAnotherActiveAdminAsync(Guid tenantId, User u, string verb, CancellationToken ct)
    {
        if (!u.IsActive) return;   // an admin who is already off isn't holding the workspace up
        var others = await _db.Users.IgnoreQueryFilters().CountAsync(x =>
            x.TenantId == tenantId && x.Id != u.Id && !x.IsDeleted && !x.IsSuperAdmin && x.IsActive && x.IsTenantAdmin, ct);
        if (others == 0)
            throw new InvalidOperationException(
                $"{u.FullName} is the only active workspace admin, so you can't {verb} them. " +
                "Make someone else the admin first, or add a new admin by invite.");
    }

    private static void Touch(User u)
    {
        u.SecurityStamp = Guid.NewGuid().ToString("N");   // their sessions end; they sign in with the new access
        u.UpdatedAtUtc = DateTime.UtcNow;
        u.UpdatedBy = "MadeeVision support";
    }

    internal static IQueryable<SupportPersonDto> Project(IQueryable<User> q) => q.Select(u => new SupportPersonDto
    {
        Id = u.Id,
        Name = (u.FirstName + " " + u.LastName).Trim(),
        Email = u.Email,
        IsActive = u.IsActive,
        IsTenantAdmin = u.IsTenantAdmin,
        IsSuperAdmin = u.IsSuperAdmin,
        HasPassword = u.PasswordHash != null,
        LastLoginUtc = u.LastLoginUtc,
        LockedUntilUtc = u.LockoutEndUtc,
        FailedLoginCount = u.FailedLoginCount,
        CreatedAtUtc = u.CreatedAtUtc,
        Roles = u.UserRoles.Where(ur => ur.Role != null && !ur.Role.IsDeleted)
                           .Select(ur => ur.Role!.DisplayName).ToList(),
    });
}

// =====================================================================
// MadeeVision's own staff
// =====================================================================

public sealed class PlatformTeamService
{
    private readonly FlowDbContext _db;
    private readonly IAuditService _audit;

    public PlatformTeamService(FlowDbContext db, IAuditService audit)
    {
        _db = db; _audit = audit;
    }

    public async Task<List<SupportPersonDto>> ListAsync(CancellationToken ct = default)
        => await WorkspacePeopleService.Project(_db.Users.AsNoTracking().IgnoreQueryFilters()
                .Where(u => u.IsSuperAdmin && !u.IsDeleted)
                .OrderByDescending(u => u.IsActive).ThenBy(u => u.FirstName).ThenBy(u => u.LastName))
            .ToListAsync(ct);

    /// <exception cref="KeyNotFoundException">Not a member of the team.</exception>
    public async Task<SupportPersonDto> GetAsync(Guid userId, CancellationToken ct = default)
        => await WorkspacePeopleService.Project(_db.Users.AsNoTracking().IgnoreQueryFilters()
                .Where(u => u.Id == userId && u.IsSuperAdmin && !u.IsDeleted))
            .FirstOrDefaultAsync(ct)
           ?? throw new KeyNotFoundException("That person is not on the MadeeVision team.");

    /// <summary>
    /// A new member of staff: a super admin in the caller's own workspace
    /// (MadeeVision's), admin there, no password until they use the invite.
    /// Returns their id; the page sends the invite.
    /// </summary>
    public async Task<Guid> AddAsync(Guid platformTenantId, string first, string? last, string email, string by, CancellationToken ct = default)
    {
        var problem = SupportPeopleRules.NewPersonProblem(first, last, email);
        if (problem is not null) throw new InvalidOperationException(problem);
        var address = email.Trim();

        if (platformTenantId == Guid.Empty || !await _db.Tenants.AsNoTracking().IgnoreQueryFilters()
                .AnyAsync(t => t.Id == platformTenantId && !t.IsDeleted, ct))
            throw new InvalidOperationException("Your own workspace could not be found, so nobody can be added to it. Sign out and in again.");

        if (await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == address && !u.IsDeleted, ct))
            throw new InvalidOperationException($"'{address}' already belongs to an account. Each person can be in one workspace.");

        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = platformTenantId,
            FirstName = first.Trim(),
            LastName = (last ?? "").Trim(),
            Email = address,
            IsActive = true,
            IsTenantAdmin = true,
            IsSuperAdmin = true,
            PasswordHash = null,                              // set from the invite link
            SecurityStamp = Guid.NewGuid().ToString("N"),     // required — see CreateUserHandler
            MustChangePassword = false,
            CreatedAtUtc = now,
            CreatedBy = SupportPeopleRules.Short(by, 100),
        };
        _db.Users.Add(user);

        await _audit.WriteCriticalAsync(AuditAction.StaffAdded, "User", user.Id, platformTenantId,
            new { person = user.FullName, email = address, summary = $"{user.FullName} added to the MadeeVision team" }, ct);
        return user.Id;
    }

    /// <summary>Switch a member of staff off (signed out everywhere) or back on.</summary>
    public async Task<string> SetActiveAsync(Guid userId, bool active, Guid callerUserId, CancellationToken ct = default)
    {
        var u = await _db.Users.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == userId && x.IsSuperAdmin && !x.IsDeleted, ct)
                ?? throw new KeyNotFoundException("That person is not on the MadeeVision team.");

        if (u.IsActive == active)
            throw new InvalidOperationException($"{u.FullName} is already switched {(active ? "on" : "off")}.");

        if (!active)
        {
            if (u.Id == callerUserId)
                throw new InvalidOperationException("You can't switch yourself off. Ask another member of the team.");
            var others = await _db.Users.IgnoreQueryFilters()
                .CountAsync(x => x.IsSuperAdmin && x.IsActive && !x.IsDeleted && x.Id != u.Id, ct);
            if (others == 0)
                throw new InvalidOperationException($"{u.FullName} is the last active super admin. Nobody could run the platform without them.");
            u.SecurityStamp = Guid.NewGuid().ToString("N");
        }

        u.IsActive = active;
        u.UpdatedAtUtc = DateTime.UtcNow;

        await _audit.WriteCriticalAsync(active ? AuditAction.StaffSwitchedOn : AuditAction.StaffSwitchedOff,
            "User", u.Id, u.TenantId,
            new { person = u.FullName, summary = $"{u.FullName} switched {(active ? "on" : "off")}" }, ct);

        return active
            ? $"{u.FullName} is switched on and can sign in again."
            : $"{u.FullName} is switched off and signed out everywhere.";
    }

    /// <summary>After IAccountEmails issued an invite or a reset for a member of staff.</summary>
    public async Task RecordLinkIssuedAsync(Guid userId, bool invite, bool emailed, CancellationToken ct = default)
    {
        var u = await GetAsync(userId, ct);
        var tenantId = await _db.Users.AsNoTracking().IgnoreQueryFilters()
            .Where(x => x.Id == userId).Select(x => x.TenantId).FirstAsync(ct);
        await _audit.WriteAsync(invite ? AuditAction.InviteIssued : AuditAction.ResetIssued, "User", userId, tenantId,
            new { person = u.Name, emailed, summary = $"for {u.Name}, {(emailed ? "emailed" : "link given by hand")}" }, ct);
    }
}
