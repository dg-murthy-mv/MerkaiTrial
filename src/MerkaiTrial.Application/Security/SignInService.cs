// =====================================================================
// FILE: MerkaiTrial.Application/Security/SignInService.cs
//
// REPLACES DemoAuthenticationHandler as the source of identity.
//
// THE VULNERABILITY THIS CLOSES
// -----------------------------
// DemoAuthenticationHandler derived identity from request state:
//
//     var cookieTenantId = Context.Request.Cookies["ViewAs_TenantId"];
//     ...
//     var effectiveIsSuperAdmin = isViewingAs ? true : Options.IsSuperAdmin;
//
// Presenting two cookies put you in ViewAs mode, and being in ViewAs mode
// granted IsSuperAdmin=true. HttpOnly stops JavaScript from READING a
// cookie; it does not stop a person CREATING one in DevTools. Any trial
// client could therefore mint themselves super-admin and reach /Admin,
// every tenant's data, and the ViewAs picker. The same held for the
// X-Tenant-Id / X-User-Id headers on the API, settable by any curl.
//
// THE RULE NOW: claims are built ONCE, server-side, from a verified
// password (or from a verified super-admin's impersonation request) and
// sealed into an encrypted auth cookie. Nothing the browser sends is ever
// a source of identity.
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

    /// <summary>Enter ViewAs. Throws unless the CURRENT principal is a real
    /// super-admin who is not already impersonating.</summary>
    Task StartViewAsAsync(Guid targetUserId, Guid targetTenantId, CancellationToken ct = default);

    Task ExitViewAsAsync(CancellationToken ct = default);
}

public class SignInService : ISignInService
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    // Claim names kept identical to the old handler so PermissionHandler,
    // ClaimsTransformer, CurrentUserService and CurrentTenantService keep
    // working unchanged.
    public const string ClaimTenantId      = "TenantId";
    public const string ClaimUserId        = "UserId";
    public const string ClaimIsTenantAdmin = "IsTenantAdmin";
    public const string ClaimIsSuperAdmin  = "IsSuperAdmin";
    public const string ClaimPermission    = "permission";
    public const string ClaimPlan          = "Plan";
    public const string ClaimSecurityStamp = "SecurityStamp";

    // ViewAs bookkeeping — DERIVED from a verified session, never read from
    // the request. RealUserId is what ExitViewAs restores.
    public const string ClaimViewingAs     = "ViewingAs";
    public const string ClaimRealUserId    = "RealUserId";
    public const string ClaimRealTenantId  = "RealTenantId";

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
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Email == email && !u.IsDeleted, ct);

        // Uniform failure message throughout: never reveal whether an account
        // exists, is locked, or simply has the wrong password. An attacker
        // learning "that email exists" is a free enumeration oracle.
        const string generic = "Email or password is incorrect.";

        if (user is null)
        {
            // Equalise timing so a missing user isn't measurably faster.
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

        // Tenant-level gate: a suspended or hard-expired tenant cannot sign in
        // at all. (Soft trial expiry is read-only and handled in middleware —
        // the client must still be able to log in to export their data.)
        var tenant = await _db.Tenants.AsNoTracking()
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

        // THE CHECK THAT WAS MISSING. Super-admin status must come from the
        // already-authenticated session, never from the act of impersonating.
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
        var realUser     = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == realUserId, ct);

        var target = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == targetUserId && u.TenantId == targetTenantId && !u.IsDeleted, ct)
            ?? throw new InvalidOperationException("Target user not found.");

        var tenant = await _db.Tenants.AsNoTracking()
            .FirstAsync(t => t.Id == targetTenantId && !t.IsDeleted, ct);

        // Impersonation is an audit event. Log actor, target and time — with
        // real client data in these tenants, this is the record you will want.
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

        var realUser = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == realUserId, ct);
        var tenant   = await _db.Tenants.AsNoTracking().FirstAsync(t => t.Id == realUser.TenantId, ct);

        _logger.LogWarning("VIEW-AS EXIT: superadmin {RealUserId} returned to own identity", realUserId);

        var principal = await BuildPrincipalAsync(realUser, tenant, realUser: null, ct);
        await _http.HttpContext!.SignInAsync(Scheme, principal);
    }

    // ── PRINCIPAL CONSTRUCTION ────────────────────────────────────────
    /// <param name="realUser">Non-null only while impersonating. The
    /// impersonated user's OWN permissions govern what is visible inside the
    /// tenant; the real user's super-admin status governs access back to
    /// /Admin and the Exit control.</param>
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
        };

        // Super-admin comes from the ROW, and while impersonating it is the
        // REAL user's row that counts — never inferred from ViewAs itself.
        var effectiveSuperAdmin = realUser?.IsSuperAdmin ?? user.IsSuperAdmin;
        claims.Add(new Claim(ClaimIsSuperAdmin, effectiveSuperAdmin.ToString().ToLowerInvariant()));

        if (realUser is not null)
        {
            claims.Add(new Claim(ClaimViewingAs, "true"));
            claims.Add(new Claim(ClaimRealUserId, realUser.Id.ToString()));
            claims.Add(new Claim(ClaimRealTenantId, realUser.TenantId.ToString()));
        }

        // Roles + permissions, scoped to this tenant's own roles plus system roles.
        var roleIds = await _db.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == user.Id)
            .Select(ur => ur.RoleId)
            .ToListAsync(ct);

        if (roleIds.Count > 0)
        {
            var roles = await _db.Roles.AsNoTracking()
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
                    // Fail CLOSED. The old code granted a tenant admin every
                    // permission when their role JSON was corrupt — convenient
                    // in a demo, wrong when the row belongs to a client.
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
