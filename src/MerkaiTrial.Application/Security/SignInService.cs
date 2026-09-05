// =====================================================================
// FILE: MerkaiTrial.Application/Security/SignInService.cs
//
// UPDATED for global query filters. Three queries here run BEFORE the
// user is signed in, so the tenant claim does not exist yet and the
// filter would compare TenantId against Guid.Empty and match nothing.
// Each is marked below.
//
// (Unchanged: this class replaces DemoAuthenticationHandler as the source
// of identity. Claims are built once, server-side, from a verified
// password — never from a cookie or header the browser can set.)
// =====================================================================

using System.Security.Claims;
using System.Text.Json;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Security;

public enum SignInOutcome { Success, InvalidCredentials, LockedOut, NoPasswordSet, Inactive, TenantSuspended }

public record SignInResult(SignInOutcome Outcome, User? User = null, string? Message = null);

public interface ISignInService
{
    Task<SignInResult> PasswordSignInAsync(string email, string password, bool rememberMe, CancellationToken ct = default);
    Task SignOutAsync();
    Task StartViewAsAsync(Guid targetUserId, Guid targetTenantId, CancellationToken ct = default);
    Task ExitViewAsAsync(CancellationToken ct = default);
}

public class SignInService : ISignInService
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    public const string ClaimTenantId           = "TenantId";
    public const string ClaimUserId             = "UserId";
    public const string ClaimIsTenantAdmin      = "IsTenantAdmin";
    public const string ClaimIsSuperAdmin       = "IsSuperAdmin";
    public const string ClaimPermission         = "permission";
    public const string ClaimPlan               = "Plan";
    public const string ClaimSecurityStamp      = "SecurityStamp";
    public const string ClaimMustChangePassword = "MustChangePassword";

    public const string ClaimViewingAs    = "ViewingAs";
    public const string ClaimRealUserId   = "RealUserId";
    public const string ClaimRealTenantId = "RealTenantId";

    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly FlowDbContext _db;
    private readonly IPasswordService _passwords;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<SignInService> _logger;

    public SignInService(FlowDbContext db, IPasswordService passwords,
                         IHttpContextAccessor http, ILogger<SignInService> logger)
    {
        _db = db; _passwords = passwords; _http = http; _logger = logger;
    }

    // ── PASSWORD SIGN-IN ──────────────────────────────────────────────
    public async Task<SignInResult> PasswordSignInAsync(string email, string password,
        bool rememberMe, CancellationToken ct = default)
    {
        // ── IgnoreQueryFilters #1 ────────────────────────────────────
        // The user has typed an email and nothing else. There is no
        // tenant claim yet — working out which tenant they belong to is
        // the whole job of this method. With the filter active, EF would
        // add "WHERE TenantId = '00000000-...'" and find nobody, so
        // EVERY login would fail with "email or password is incorrect".
        //
        // Users is not filtered today, but this makes the intent explicit
        // and keeps working if anyone filters it later.
        var user = await _db.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email == email && !u.IsDeleted, ct);

        const string generic = "Email or password is incorrect.";

        if (user is null)
        {
            // Equalise timing so a missing account is not measurably faster.
            _passwords.Verify("AQAAAAIAAYagAAAAEK5t0000000000000000000000000000000000000000000000000000==", password);
            return new SignInResult(SignInOutcome.InvalidCredentials, null, generic);
        }

        if (user.IsLockedOut())
            return new SignInResult(SignInOutcome.LockedOut, null,
                "Account temporarily locked after repeated failed attempts. Try again in 15 minutes.");

        if (string.IsNullOrEmpty(user.PasswordHash))
            return new SignInResult(SignInOutcome.NoPasswordSet, null, generic);

        if (!user.IsActive)
            return new SignInResult(SignInOutcome.Inactive, null, generic);

        var verify = _passwords.Verify(user.PasswordHash, password);
        if (verify == Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailedAttempts)
            {
                user.LockoutEndUtc = DateTime.UtcNow.Add(LockoutDuration);
                user.FailedLoginCount = 0;
                _logger.LogWarning("Lockout applied to user {UserId} after {Max} failed attempts",
                    user.Id, MaxFailedAttempts);
            }
            await _db.SaveChangesAsync(ct);
            return new SignInResult(SignInOutcome.InvalidCredentials, null, generic);
        }

        // ── IgnoreQueryFilters #1b ───────────────────────────────────
        // Tenants has no TenantId column so it is not filtered, but this
        // lookup also happens pre-authentication. Left unfiltered and
        // called out so nobody adds a filter to Tenants without seeing
        // this line.
        var tenant = await _db.Tenants.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == user.TenantId && !t.IsDeleted, ct);

        if (tenant is null || !tenant.IsActive)
            return new SignInResult(SignInOutcome.TenantSuspended, null,
                "This workspace is not currently available. Please contact MadeeVision.");

        if (verify == Microsoft.AspNetCore.Identity.PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = _passwords.Hash(password);

        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        user.LastLoginUtc = DateTime.UtcNow;
        user.SecurityStamp ??= Guid.NewGuid().ToString("N");
        await _db.SaveChangesAsync(ct);

        var principal = await BuildPrincipalAsync(user, tenant, realUser: null, ct);

        await _http.HttpContext!.SignInAsync(Scheme, principal, new AuthenticationProperties
        {
            IsPersistent = rememberMe,
            ExpiresUtc   = DateTimeOffset.UtcNow.AddHours(rememberMe ? 24 * 14 : 8),
            AllowRefresh = true
        });

        _logger.LogInformation("Sign-in succeeded for user {UserId} in tenant {TenantId}", user.Id, user.TenantId);
        return new SignInResult(SignInOutcome.Success, user);
    }

    public async Task SignOutAsync() => await _http.HttpContext!.SignOutAsync(Scheme);

    // ── VIEW AS ───────────────────────────────────────────────────────
    public async Task StartViewAsAsync(Guid targetUserId, Guid targetTenantId, CancellationToken ct = default)
    {
        var current = _http.HttpContext!.User;

        var isSuperAdmin = current.HasClaim(ClaimIsSuperAdmin, "true");
        var alreadyViewing = current.HasClaim(ClaimViewingAs, "true");

        if (!isSuperAdmin || alreadyViewing)
        {
            _logger.LogWarning("Rejected ViewAs attempt by user {UserId} (superadmin={Super}, alreadyViewing={Viewing})",
                current.FindFirst(ClaimUserId)?.Value, isSuperAdmin, alreadyViewing);
            throw new UnauthorizedAccessException("Only a signed-in super admin may use View As.");
        }

        var realUserId   = Guid.Parse(current.FindFirst(ClaimUserId)!.Value);
        var realTenantId = Guid.Parse(current.FindFirst(ClaimTenantId)!.Value);

        // ── IgnoreQueryFilters #2 ────────────────────────────────────
        // Cross-tenant BY DEFINITION. The current claim says the super
        // admin's own tenant; the target user belongs to a different one,
        // so the filter would hide exactly the row being looked up.
        // Safe because the super-admin check above has already passed.
        var realUser = await _db.Users.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(u => u.Id == realUserId, ct);

        var target = await _db.Users.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == targetUserId && u.TenantId == targetTenantId && !u.IsDeleted, ct)
            ?? throw new InvalidOperationException("Target user not found.");

        var tenant = await _db.Tenants.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(t => t.Id == targetTenantId && !t.IsDeleted, ct);

        _logger.LogWarning("VIEW-AS START: superadmin {RealUserId} impersonating {TargetUserId} in tenant {TenantId}",
            realUserId, targetUserId, targetTenantId);

        var principal = await BuildPrincipalAsync(target, tenant, realUser, ct);
        await _http.HttpContext!.SignInAsync(Scheme, principal);
    }

    public async Task ExitViewAsAsync(CancellationToken ct = default)
    {
        var current = _http.HttpContext!.User;
        var realIdRaw = current.FindFirst(ClaimRealUserId)?.Value;

        if (!Guid.TryParse(realIdRaw, out var realUserId))
        {
            await SignOutAsync();
            return;
        }

        // Cross-tenant for the same reason as above: the active claim is
        // the impersonated tenant, the row wanted is the super admin's.
        var realUser = await _db.Users.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(u => u.Id == realUserId, ct);

        var tenant = await _db.Tenants.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(t => t.Id == realUser.TenantId, ct);

        _logger.LogWarning("VIEW-AS EXIT: superadmin {RealUserId} returned to own identity", realUserId);

        var principal = await BuildPrincipalAsync(realUser, tenant, realUser: null, ct);
        await _http.HttpContext!.SignInAsync(Scheme, principal);
    }

    // ── PRINCIPAL CONSTRUCTION ────────────────────────────────────────
    private async Task<ClaimsPrincipal> BuildPrincipalAsync(
        User user, Tenant tenant, User? realUser, CancellationToken ct)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimUserId,   user.Id.ToString()),
            new(ClaimTenantId, user.TenantId.ToString()),
            new(ClaimTypes.Name,  user.FullName),
            new(ClaimTypes.Email, user.Email),
            new(ClaimIsTenantAdmin, user.IsTenantAdmin.ToString().ToLowerInvariant()),
            new(ClaimSecurityStamp, user.SecurityStamp ?? string.Empty),
            new(ClaimPlan, tenant.Plan ?? "starter"),
            new(ClaimMustChangePassword, user.MustChangePassword.ToString().ToLowerInvariant()),
        };

        var effectiveSuperAdmin = realUser?.IsSuperAdmin ?? user.IsSuperAdmin;
        claims.Add(new Claim(ClaimIsSuperAdmin, effectiveSuperAdmin.ToString().ToLowerInvariant()));

        if (realUser is not null)
        {
            claims.Add(new Claim(ClaimViewingAs, "true"));
            claims.Add(new Claim(ClaimRealUserId, realUser.Id.ToString()));
            claims.Add(new Claim(ClaimRealTenantId, realUser.TenantId.ToString()));
        }

        // ── IgnoreQueryFilters #3 — THE ONE THAT MATTERS MOST ────────
        // This runs DURING sign-in. The tenant claim is being built right
        // now and is not on HttpContext.User yet, so the filter would
        // compare against Guid.Empty, return no roles, and the user would
        // sign in successfully with ZERO permissions — every page denied,
        // no error anywhere, and it would look like the permission system
        // was broken rather than the query.
        //
        // Safe because the Where clause below already restricts to this
        // user's own tenant plus system roles (TenantId == null). Ignoring
        // the automatic filter does not widen what is returned.
        var roleIds = await _db.UserRoles.AsNoTracking().IgnoreQueryFilters()
            .Where(ur => ur.UserId == user.Id)
            .Select(ur => ur.RoleId)
            .ToListAsync(ct);

        if (roleIds.Count > 0)
        {
            var roles = await _db.Roles.AsNoTracking().IgnoreQueryFilters()
                .Where(r => roleIds.Contains(r.Id)
                         && !r.IsDeleted
                         && (r.TenantId == null || r.TenantId == user.TenantId))
                .ToListAsync(ct);

            foreach (var role in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role.Name));
                if (string.IsNullOrWhiteSpace(role.Permissions)) continue;

                try
                {
                    var perms = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                        role.Permissions, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (perms is null) continue;
                    foreach (var (module, actions) in perms)
                        foreach (var action in actions)
                            claims.Add(new Claim(ClaimPermission, $"{module}.{action}"));
                }
                catch (JsonException ex)
                {
                    // Fail CLOSED. Granting a tenant admin everything when
                    // their role JSON is corrupt was fine in a demo and is
                    // an accidental privilege grant with client data.
                    _logger.LogError(ex,
                        "Role {RoleId} ({RoleName}) has invalid Permissions JSON — granting NO permissions from it.",
                        role.Id, role.Name);
                }
            }
        }

        claims.Add(new Claim("permissions_loaded", "true"));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
    }
}
