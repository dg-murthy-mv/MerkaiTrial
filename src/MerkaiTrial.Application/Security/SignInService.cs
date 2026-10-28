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
//
// 087b — SIGNING IN WITH AN UNKNOWN EMAIL CRASHED (found by the
// end-to-end tests). To make a missing account take as long as a wrong
// password, the code checked the password against a made-up hash — but
// that hash was not valid Base-64, so PasswordHasher threw FormatException
// and the person got a 500 error page. Worse than ugly: a real email said
// "Email or password is incorrect" and an unknown one crashed, which is
// exactly the leak the timing trick was there to prevent.
//
//   • The stand-in hash is now a REAL hash, made once by the same
//     PasswordService that makes every other hash, so it verifies (and
//     fails) in the same time a real one does.
//   • "No password set yet" (an invite not accepted) does the same
//     stand-in check, so it is not measurably faster either.
//   • A stored hash that cannot be read (corrupt row, hand-edited
//     database) now counts as a wrong password and is logged, instead of
//     taking the sign-in page down for that person.
//
// 105 — A SESSION ON THE SERVER (VUL-11)
//   Signing out used to only ask the browser to forget its cookie. A copy
//   of the cookie kept working for up to 8 hours (2 weeks with "keep me
//   signed in"), because nothing on the server knew the session had ended.
//
//   • Every sign-in now writes a UserSessions row and the cookie carries
//     its id ("sid"). The cookie check in Admin.Web refuses a cookie
//     whose row is ended, past its limit, or missing.
//   • SignOutAsync ends THIS row only — the person's other browsers stay
//     signed in.
//   • A session lasts at most 24 hours from sign-in (MaxSessionLifetime),
//     however busy it is; the 8-hour idle limit is unchanged. "Keep me
//     signed in" now means "survives closing the browser", still within
//     the 24 hours — it used to mean two weeks.
//   • View As keeps the super admin's session (the sid is carried into
//     the impersonated cookie and back out), so Exit and Sign out work on
//     the session the super admin actually started.
//   • Old sessions of the person (over a week past their limit) are
//     deleted at each sign-in, so the table never grows without bound.
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

    /// <summary>105. The UserSessions row this cookie belongs to.</summary>
    public const string ClaimSessionId    = "sid";

    /// <summary>105. However busy it is, a session ends this long after sign-in.</summary>
    public static readonly TimeSpan MaxSessionLifetime = TimeSpan.FromHours(24);

    /// <summary>105. Without activity, the cookie ends after this (unchanged: 8 hours).</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromHours(8);

    /// <summary>
    /// 105. The person whose session a principal belongs to: the super admin
    /// during View As (RealUserId), otherwise the signed-in person.
    /// </summary>
    public static Guid? SessionOwnerOf(ClaimsPrincipal? principal)
    {
        var raw = principal?.FindFirst(ClaimRealUserId)?.Value ?? principal?.FindFirst(ClaimUserId)?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    /// <summary>105. The session id a principal carries, or null.</summary>
    public static Guid? SessionIdOf(ClaimsPrincipal? principal)
        => Guid.TryParse(principal?.FindFirst(ClaimSessionId)?.Value, out var id) ? id : null;

    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// 087b. A genuine hash of a random value nobody knows, made on first
    /// use and kept for the life of the process. Checked against when
    /// there is no real hash to check, so every "no" costs the same time.
    /// </summary>
    private static string? _standInHash;

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
            SpendTheSameTime(password);
            return new SignInResult(SignInOutcome.InvalidCredentials, null, generic);
        }

        if (user.IsLockedOut())
            return new SignInResult(SignInOutcome.LockedOut, null,
                "Account temporarily locked after repeated failed attempts. Try again in 15 minutes.");

        if (string.IsNullOrEmpty(user.PasswordHash))
        {
            SpendTheSameTime(password);   // 087b — as slow as a real "no"
            return new SignInResult(SignInOutcome.NoPasswordSet, null, generic);
        }

        if (!user.IsActive)
            return new SignInResult(SignInOutcome.Inactive, null, generic);

        // 087b: an unreadable stored hash is a failed sign-in, not a crash.
        Microsoft.AspNetCore.Identity.PasswordVerificationResult verify;
        try
        {
            verify = _passwords.Verify(user.PasswordHash, password);
        }
        catch (FormatException ex)
        {
            _logger.LogError(ex,
                "User {UserId} has a stored password hash that cannot be read. " +
                "Sign-in refused; send them a password reset.", user.Id);
            verify = Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed;
        }
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

        // 105 — the session this sign-in starts, saved with the user row.
        var now = DateTime.UtcNow;
        var session = new UserSession
        {
            Id           = Guid.NewGuid(),
            UserId       = user.Id,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(MaxSessionLifetime),
            IpAddress    = Cut(_http.HttpContext?.Connection.RemoteIpAddress?.ToString(), 64),
            UserAgent    = Cut(_http.HttpContext?.Request.Headers.UserAgent.ToString(), 256),
        };
        _db.UserSessions.Add(session);
        await _db.SaveChangesAsync(ct);

        await ForgetOldSessionsAsync(user.Id, now, ct);

        var principal = await BuildPrincipalAsync(user, tenant, realUser: null, session.Id, ct);

        // The cookie never outlives the session. Without "keep me signed in"
        // it is a browser-session cookie that ends after 8 idle hours; with
        // it, it survives closing the browser — until the 24-hour limit.
        var cookieEnds = rememberMe ? session.ExpiresAtUtc : Min(now.Add(IdleLimit), session.ExpiresAtUtc);

        await _http.HttpContext!.SignInAsync(Scheme, principal, new AuthenticationProperties
        {
            IsPersistent = rememberMe,
            ExpiresUtc   = new DateTimeOffset(cookieEnds, TimeSpan.Zero),
            AllowRefresh = true
        });

        _logger.LogInformation("Sign-in succeeded for user {UserId} in tenant {TenantId}", user.Id, user.TenantId);
        return new SignInResult(SignInOutcome.Success, user);
    }

    /// <summary>
    /// 087b. Runs one real password check against a hash nobody has the
    /// password for, so "no such account" and "no password set yet" take
    /// as long as "wrong password". The result is ignored.
    /// </summary>
    private void SpendTheSameTime(string password)
    {
        // A benign race: two first requests may both make one; either is
        // a valid hash, and the last one written wins.
        _standInHash ??= _passwords.Hash(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));

        try { _passwords.Verify(_standInHash, password); }
        catch (FormatException) { /* cannot happen with our own hash; never let it reach the user */ }
    }

    /// <summary>
    /// 105. Ends THIS session on the server, then clears the cookie. A copy
    /// of the cookie is refused from the next request on, wherever it is.
    /// The person's other sessions are left alone.
    /// </summary>
    public async Task SignOutAsync()
    {
        var http = _http.HttpContext!;
        var sid = SessionIdOf(http.User);

        if (sid is { } id)
        {
            try
            {
                var now = DateTime.UtcNow;
                await _db.UserSessions
                    .Where(s => s.Id == id && s.EndedAtUtc == null)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(s => s.EndedAtUtc, (DateTime?)now)
                        .SetProperty(s => s.EndReason, SessionEndReason.SignedOut));
            }
            catch (Exception ex)
            {
                // The cookie is still cleared below. Logged at Error: a
                // session that could not be ended stays usable from a copy
                // until its 24-hour limit, and that is worth knowing about.
                _logger.LogError(ex, "Could not end session {SessionId} on sign-out", id);
            }
        }

        await http.SignOutAsync(Scheme);
    }

    /// <summary>105. A person's sessions more than a week past their limit — nobody needs them.</summary>
    private async Task ForgetOldSessionsAsync(Guid userId, DateTime now, CancellationToken ct)
    {
        try
        {
            var cutoff = now.AddDays(-7);
            await _db.UserSessions
                .Where(s => s.UserId == userId && s.ExpiresAtUtc < cutoff)
                .ExecuteDeleteAsync(ct);
        }
        catch (Exception ex)
        {
            // Housekeeping only; never a reason to fail a sign-in.
            _logger.LogWarning(ex, "Could not clear old sessions for user {UserId}", userId);
        }
    }

    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;

    private static string? Cut(string? value, int max)
        => string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];

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

        // 105 — still the super admin's own session.
        var principal = await BuildPrincipalAsync(target, tenant, realUser, SessionIdOf(current), ct);
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

        // 105 — the same session they started View As from.
        var principal = await BuildPrincipalAsync(realUser, tenant, realUser: null, SessionIdOf(current), ct);
        await _http.HttpContext!.SignInAsync(Scheme, principal);
    }

    // ── PRINCIPAL CONSTRUCTION ────────────────────────────────────────
    private async Task<ClaimsPrincipal> BuildPrincipalAsync(
        User user, Tenant tenant, User? realUser, Guid? sessionId, CancellationToken ct)
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

        // 105 — no session id, no cookie that passes the check. Every path
        // that builds a principal has one; this is the belt to that brace.
        if (sessionId is { } sid)
            claims.Add(new Claim(ClaimSessionId, sid.ToString()));

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
