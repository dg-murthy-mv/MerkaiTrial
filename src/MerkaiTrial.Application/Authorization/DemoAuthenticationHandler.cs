// =====================================================================
// FILE: MerkaiTrial.Application/Authorization/DemoAuthenticationHandler.cs
//
// SESSION 8 PERFORMANCE FIX — identity result caching
//
// WHAT CHANGED AND WHY:
// This handler runs on EVERY authenticated request, in BOTH hosts
// (Admin.Web and WebApi both register it — WebApi/Authorization has its
// own unused copy; Program.cs imports MerkaiTrial.Application.Authorization,
// so THIS file is the live one in both processes).
//
// It issued four DB round trips per request:
//     1. Users        (IsTenantAdmin, FullName, Email)
//     2. UserRoles    (role ids)
//     3. Roles        (role names + permission JSON)
//     4. Tenants      (plan)
//
// /Dashboard renders one Admin.Web request that fans out to five parallel
// WebApi calls. Six requests x four queries = 24 identity lookups for a
// single page view, all returning byte-identical data that cannot change
// within the page load.
//
// FIX: the DB-derived facts are now cached in IMemoryCache, keyed by
// (tenantId, userId), for CacheSeconds. On a cache hit the handler does
// ZERO database work. The queries themselves are UNCHANGED — deliberately.
// Rewriting them into a single projection was considered and rejected:
// User.FullName may be a computed/[NotMapped] property (ViewAsIndex and the
// old code here both read it in memory, never inside a Select), so folding
// it into a server-side projection risks a translation exception. Caching
// removes ~95% of the queries without touching a single LINQ expression.
//
// WHAT IS *NOT* CACHED (recomputed every request, all free):
//   - NameIdentifier / UserId / TenantId claims
//   - the isViewingAs decision and the ViewAs* claims
//   - IsSuperAdmin (depends on isViewingAs, not on the DB)
//   - the displayName/displayEmail choice between Options.* and the DB row
// Only pure database facts live in the cache. That keeps ViewAs switching
// instant and correct: a different user is a different cache key, so
// starting or exiting ViewAs never serves a stale identity.
//
// STAMPEDE PROTECTION (session 8, second pass): concurrent misses for the
// same identity are serialised behind a per-key SemaphoreSlim. See
// GetIdentityFactsAsync. Without it a ViewAs switch cost 20 identity queries
// instead of 4, because all five parallel Dashboard API calls missed at once.
//
// STALENESS WINDOW: editing a role's permission JSON in /Admin/Roles takes
// up to CacheSeconds to be reflected. Call Evict(tenantId, userId) — or just
// wait — after a permission change. For a demo, 60s is the right trade.
// Set CacheSeconds to 0 to disable caching entirely and restore the old
// behaviour without editing anything else.
// =====================================================================

using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Collections.Concurrent;
using System.Text.Json;

namespace MerkaiTrial.Application.Authorization
{
    /// <summary>Cookie names used by the /Admin/ViewAs picker (browser-facing,
    /// used when the browser talks directly to Admin.Web). Shared constant
    /// so the picker page and this handler can never drift out of sync.</summary>
    public static class ViewAsCookies
    {
        public const string TenantId = "ViewAs_TenantId";
        public const string UserId   = "ViewAs_UserId";

        public static readonly CookieOptions Options = new()
        {
            HttpOnly = true,        // never needs to be read by JS
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            // No Expires set -> session cookie, cleared when the browser closes.
            // That's intentional: a "view as" override shouldn't silently
            // persist across days if someone forgets to click Exit.
        };
    }

    /// <summary>Header names used to forward the effective tenant/user identity
    /// from Admin.Web's server-side HttpClient calls to WebApi. Cookies don't
    /// travel on server-to-server HTTP calls, so ViewAs needs a second
    /// propagation path for anything that goes through IApiService/ApiClient
    /// to reach WebApi — this is that path. See TenantContextForwardingHandler
    /// in Admin.Web, which sets these on every outgoing request.</summary>
    public static class TenantContextHeaders
    {
        public const string TenantId = "X-Tenant-Id";
        public const string UserId   = "X-User-Id";
    }

    public class DemoAuthenticationHandler : AuthenticationHandler<DemoAuthenticationOptions>
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IMemoryCache _cache;

        /// <summary>
        /// One gate per (tenant, user). Prevents the cache stampede described in
        /// the header: without it, five simultaneous requests all miss the cache
        /// and all five run the four identity queries.
        /// Bounded by the number of distinct users seen since process start —
        /// a handful in demo, and each entry is a single SemaphoreSlim.
        /// </summary>
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _loadGates = new();

        /// <summary>How long a resolved identity stays cached. Set to 0 to
        /// disable caching and hit the DB on every request (old behaviour).</summary>
        public const int CacheSeconds = 60;

        private const string CacheKeyPrefix = "demoauth::identity::";

        public DemoAuthenticationHandler(
            IOptionsMonitor<DemoAuthenticationOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            IServiceScopeFactory scopeFactory,
            IMemoryCache cache)
            : base(options, logger, encoder)
        {
            _scopeFactory = scopeFactory;
            _cache = cache;
        }

        /// <summary>Cache key for one resolved identity. Public so callers that
        /// mutate roles/permissions (e.g. /Admin/Roles Edit) can invalidate
        /// immediately instead of waiting out CacheSeconds.</summary>
        public static string CacheKey(Guid tenantId, Guid userId)
            => $"{CacheKeyPrefix}{tenantId}::{userId}";

        /// <summary>Drop a cached identity so the next request re-reads the DB.</summary>
        public static void Evict(IMemoryCache cache, Guid tenantId, Guid userId)
            => cache.Remove(CacheKey(tenantId, userId));

        /// <summary>Pure database facts for one user. Everything derived from
        /// request state (ViewAs, IsSuperAdmin, display-name fallbacks) is
        /// computed OUTSIDE this record and is therefore never cached.</summary>
        private sealed record IdentityFacts(
            bool UserExists,
            bool IsTenantAdmin,
            string? FullName,
            string? Email,
            IReadOnlyList<string> RoleNames,
            IReadOnlyList<string> Permissions,
            string? Plan);

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            // ── ViewAs override, in priority order ──────────────────────────
            // 1. Cookie — set by /Admin/ViewAs, present when the BROWSER talks
            //    directly to this host (i.e. this is Admin.Web itself).
            // 2. Header — set by TenantContextForwardingHandler, present when
            //    ANOTHER SERVER (Admin.Web's HttpClient) is calling this host
            //    (i.e. this is WebApi receiving a call from Admin.Web). Cookies
            //    never reach this path — server-to-server calls don't carry
            //    the original browser's cookies unless explicitly forwarded,
            //    which is exactly what the header does instead.
            // 3. Neither present → appsettings.json DevelopmentTenantContext,
            //    exactly as before.
            var cookieTenantId = Context.Request.Cookies[ViewAsCookies.TenantId];
            var cookieUserId   = Context.Request.Cookies[ViewAsCookies.UserId];
            var headerTenantId = Context.Request.Headers[TenantContextHeaders.TenantId].FirstOrDefault();
            var headerUserId   = Context.Request.Headers[TenantContextHeaders.UserId].FirstOrDefault();

            var isViewingAs = false;
            string? rawUserId = null;
            string? rawTenantId = null;

            if (!string.IsNullOrWhiteSpace(cookieTenantId) && !string.IsNullOrWhiteSpace(cookieUserId))
            {
                isViewingAs = true;
                rawTenantId = cookieTenantId;
                rawUserId   = cookieUserId;
            }
            else if (!string.IsNullOrWhiteSpace(headerTenantId) && !string.IsNullOrWhiteSpace(headerUserId))
            {
                isViewingAs = true;
                rawTenantId = headerTenantId;
                rawUserId   = headerUserId;
            }

            rawUserId   ??= Options.UserId;
            rawTenantId ??= Options.TenantId;

            if (!Guid.TryParse(rawUserId, out var userId))
                return AuthenticateResult.Fail("Invalid UserId (ViewAs override or DemoAuthenticationOptions)");

            if (!Guid.TryParse(rawTenantId, out var tenantId))
                return AuthenticateResult.Fail("Invalid TenantId (ViewAs override or DemoAuthenticationOptions)");

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, rawUserId),
                new Claim("UserId",   rawUserId),
                new Claim("TenantId", rawTenantId),
            };

            try
            {
                // ── The only DB work in this handler, and now usually skipped ──
                var facts = await GetIdentityFactsAsync(userId, tenantId);

                claims.Add(new Claim("IsTenantAdmin", facts.IsTenantAdmin.ToString().ToLower()));

                // During ViewAs, use the real user's name/email from the DB
                // (this is the fix — previously fell through to a hardcoded
                // "Demo User" fallback because isViewingAs deliberately
                // skipped Options.UserName but nothing filled in the real
                // value in its place).
                var displayName = isViewingAs
                    ? (facts.UserExists ? facts.FullName : null)
                    : Options.UserName;
                var displayEmail = isViewingAs
                    ? facts.Email
                    : Options.Email;

                claims.Add(new Claim(ClaimTypes.Name, displayName ?? "Demo User"));
                claims.Add(new Claim(ClaimTypes.Email, displayEmail ?? string.Empty));

                foreach (var roleName in facts.RoleNames)
                    claims.Add(new Claim(ClaimTypes.Role, roleName));

                foreach (var permission in facts.Permissions)
                    claims.Add(new Claim("permission", permission));

                if (facts.Plan != null)
                    claims.Add(new Claim("Plan", facts.Plan));

                // ── IsSuperAdmin: while viewing-as, the *person driving the
                // browser* is still the SuperAdmin who launched the preview —
                // that's what gates access back to /Admin/* and the Exit
                // control. It does NOT affect what the previewed user can see
                // inside the tenant CRM (that's governed entirely by
                // IsTenantAdmin + permission claims above, which correctly
                // reflect the impersonated user, not the SuperAdmin). ─────────
                var effectiveIsSuperAdmin = isViewingAs ? true : Options.IsSuperAdmin;
                claims.Add(new Claim("IsSuperAdmin", effectiveIsSuperAdmin.ToString().ToLower()));

                if (isViewingAs)
                {
                    claims.Add(new Claim("ViewingAs", "true"));
                    claims.Add(new Claim("ViewAsTenantId", rawTenantId));
                    claims.Add(new Claim("ViewAsUserId", rawUserId));
                }

                // Demoted from Information to Debug: this fired on every request
                // in both hosts. Session 7 established that log writes, not SQL,
                // dominated request time — 24 of these per Dashboard load is
                // exactly the pattern that caused it. Raise it back to
                // LogInformation temporarily if you need to trace ViewAs.
                Logger.LogDebug(
                    "DemoAuth: user {UserId} resolved (ViewingAs={ViewingAs}) — IsTenantAdmin={Admin} IsSuperAdmin={SuperAdmin} Roles={RoleCount} Plan={Plan}",
                    rawUserId, isViewingAs, facts.IsTenantAdmin, effectiveIsSuperAdmin,
                    facts.RoleNames.Count, facts.Plan ?? "starter");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex,
                    "DemoAuth: could not load user/role data from DB for user {UserId}. " +
                    "App will run with no permissions.", rawUserId);
            }

            var identity  = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket    = new AuthenticationTicket(principal, Scheme.Name);

            return AuthenticateResult.Success(ticket);
        }

        /// <summary>
        /// Returns the cached identity facts, loading them from the database only
        /// on a miss. A failed load is NOT cached — it throws, the caller logs it,
        /// and the next request retries rather than serving an empty identity for
        /// the whole CacheSeconds window.
        /// </summary>
        private async Task<IdentityFacts> GetIdentityFactsAsync(Guid userId, Guid tenantId)
        {
            if (CacheSeconds <= 0)
                return await LoadIdentityFactsAsync(userId, tenantId);

            var key = CacheKey(tenantId, userId);

            // Fast path — no lock, no allocation. This is what almost every
            // request takes once the identity is warm.
            if (_cache.TryGetValue(key, out IdentityFacts? cached) && cached != null)
                return cached;

            // ── Slow path: serialise concurrent misses for THIS identity ──────
            // /Dashboard fires five parallel WebApi calls. Measured 2026-08-04,
            // first request after a ViewAs switch, all five in the same
            // millisecond:
            //     15:44:46.643  Executed DbCommand (139ms)  @__userId_0, @__tenantId_1
            //     15:44:46.643  Executed DbCommand (176ms)  @__userId_0, @__tenantId_1
            //     15:44:46.643  Executed DbCommand (149ms)  @__userId_0, @__tenantId_1
            //     15:44:46.643  Executed DbCommand (164ms)  @__userId_0, @__tenantId_1
            //     15:44:46.643  Executed DbCommand (209ms)  @__userId_0, @__tenantId_1
            // Five identical queries for the same user, x4 (Users, UserRoles,
            // Roles, Tenants) = 20 lookups where 4 would do. The gate below lets
            // the first request load while the other four wait, then all five
            // read the same cached result.
            //
            // The gate is per-KEY, so a request for a different user is never
            // blocked by one for this user.
            var gate = _loadGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

            await gate.WaitAsync();
            try
            {
                // Double-check: whoever held the gate before us has just
                // populated the cache, which is the entire point.
                if (_cache.TryGetValue(key, out cached) && cached != null)
                    return cached;

                var facts = await LoadIdentityFactsAsync(userId, tenantId);

                _cache.Set(key, facts, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(CacheSeconds)
                });

                return facts;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// The original four queries, unchanged. Kept verbatim on purpose — the
        /// performance win comes from calling this rarely, not from rewriting it.
        /// </summary>
        private async Task<IdentityFacts> LoadIdentityFactsAsync(Guid userId, Guid tenantId)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FlowDbContext>();

            // 1. Load user — get IsTenantAdmin flag (+ display fields if present)
            var user = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId
                                       && u.TenantId == tenantId
                                       && !u.IsDeleted);

            var roleNames   = new List<string>();
            var permissions = new List<string>();

            // 2. Load roles for this user
            var roleIds = await db.UserRoles.AsNoTracking()
                .Where(ur => ur.UserId == userId)
                .Select(ur => ur.RoleId)
                .ToListAsync();

            if (roleIds.Any())
            {
                var roles = await db.Roles.AsNoTracking()
                    .Where(r => roleIds.Contains(r.Id) && !r.IsDeleted)
                    .ToListAsync();

                foreach (var role in roles)
                {
                    roleNames.Add(role.Name);

                    if (string.IsNullOrWhiteSpace(role.Permissions)) continue;

                    try
                    {
                        var perms = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                            role.Permissions,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (perms == null) continue;

                        foreach (var (module, actions) in perms)
                            foreach (var action in actions)
                                permissions.Add($"{module}.{action}");
                    }
                    catch (JsonException ex)
                    {
                        Logger.LogWarning(ex, "Could not parse Permissions JSON for role {Role}", role.Name);
                    }
                }
            }

            // 3. Load Plan from Tenants.Plan
            var tenant = await db.Tenants.AsNoTracking()
                .Where(t => t.Id == tenantId && !t.IsDeleted)
                .Select(t => new { t.Plan })
                .FirstOrDefaultAsync();

            return new IdentityFacts(
                UserExists:    user != null,
                IsTenantAdmin: user?.IsTenantAdmin ?? false,
                FullName:      user?.FullName,
                Email:         user?.Email,
                RoleNames:     roleNames,
                Permissions:   permissions,
                Plan:          tenant != null ? (tenant.Plan ?? "starter") : null);
        }
    }
}
