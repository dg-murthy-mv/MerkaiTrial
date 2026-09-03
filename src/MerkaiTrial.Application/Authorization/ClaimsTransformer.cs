// =====================================================================
// FILE: MerkaiTrial.Application/Authorization/ClaimsTransformer.cs
//
// SESSION 8 PERFORMANCE FIX — hot-path logging demoted to Debug.
//
// FUNCTIONAL BEHAVIOUR IS UNCHANGED. The session-5 fix (skip the redundant
// ICurrentUserService re-fetch when DemoAuthenticationHandler has already
// populated TenantId/UserId/IsTenantAdmin) is preserved exactly.
//
// WHY THIS FILE MATTERS LESS THAN IT LOOKS:
// DemoAuthenticationHandler ALWAYS adds TenantId, UserId and IsTenantAdmin
// on every successful authentication. That means the early-exit branch below
// is taken on EVERY request and the ICurrentUserService path is effectively
// dead code — retained only as a safety net if the auth handler ever stops
// setting one of those three claims.
//
// So the only thing this class actually did per request was emit two
// Information-level log lines and add one claim. Both hosts run it, and
// /Dashboard is six requests, so that was 12 log writes per page view doing
// no work — precisely the cost profile session 7 identified as dominating
// request time. Those two lines are now Debug.
//
// The fallback path also no longer calls _serviceProvider.CreateScope().
// ICurrentUserService is scoped and has a per-request _cachedUser field;
// resolving it from a NEW scope guaranteed a cache miss and a duplicate
// Users/UserRoles/Roles query even when the request had already loaded the
// same user. It now resolves from the request's own scope via
// IHttpContextAccessor.HttpContext.RequestServices, falling back to a new
// scope only when there is no HttpContext (which should never happen inside
// IClaimsTransformation, but the guard is free).
//
// CONSIDER DELETING THIS CLASS ENTIRELY once the demo is done — see the note
// above about the fallback being unreachable. Not done now: removing a layer
// of the auth pipeline is not a pre-demo change.
// =====================================================================

using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace MerkaiTrial.Application.Authorization
{
    public class ClaimsTransformer : IClaimsTransformation
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<ClaimsTransformer> _logger;

        public ClaimsTransformer(
            IServiceProvider serviceProvider,
            IHttpContextAccessor httpContextAccessor,
            ILogger<ClaimsTransformer> logger)
        {
            _serviceProvider = serviceProvider;
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
        }

        public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            var clone = principal.Clone();
            var identity = (ClaimsIdentity?)clone.Identity;

            if (identity == null || !identity.IsAuthenticated)
            {
                _logger.LogWarning("ClaimsTransformer: identity is null or not authenticated");
                return principal;
            }

            // Avoid duplicate transformation
            if (identity.HasClaim(c => c.Type == "permissions_loaded"))
            {
                _logger.LogDebug("ClaimsTransformer: permissions already loaded, skipping");
                return clone;
            }

            // DemoAuthenticationHandler already builds the full, correct claim
            // set for this identity — including IsTenantAdmin and permission claims
            // that correctly reflect the ViewAs'd user, not the real superadmin.
            // Re-querying ICurrentUserService here and layering a second
            // "IsTenantAdmin" claim on top (via AddClaim, which appends rather
            // than replaces) caused PermissionHandler's HasClaim(predicate) to
            // match the stale "true" claim from ICurrentUserService regardless
            // of which claim DemoAuthenticationHandler had already set — meaning
            // every role bypassed every permission check. If the identity already
            // carries TenantId/UserId/IsTenantAdmin from DemoAuthenticationHandler,
            // trust it and skip this redundant (and ViewAs-unaware) re-fetch entirely.
            //
            // ── THIS BRANCH IS TAKEN ON EVERY REQUEST. See file header. ──
            if (identity.HasClaim(c => c.Type == "TenantId") &&
                identity.HasClaim(c => c.Type == "UserId") &&
                identity.HasClaim(c => c.Type == "IsTenantAdmin"))
            {
                _logger.LogDebug(
                    "ClaimsTransformer: claims already populated by DemoAuthenticationHandler, skipping ICurrentUserService lookup");
                identity.AddClaim(new Claim("permissions_loaded", "true"));
                return clone;
            }

            // ── FALLBACK PATH — effectively unreachable in current wiring ──
            _logger.LogWarning(
                "ClaimsTransformer: DemoAuthenticationHandler did not supply TenantId/UserId/IsTenantAdmin — " +
                "falling back to ICurrentUserService. This is unexpected; check the auth handler.");

            // Resolve from the REQUEST's scope, not a new one. ICurrentUserService
            // is scoped and caches the resolved user in a private field, so a new
            // scope here meant a guaranteed cache miss and a duplicate
            // Users/UserRoles/Roles query for a user the request may already hold.
            IServiceScope? ownedScope = null;
            var services = _httpContextAccessor.HttpContext?.RequestServices;

            if (services == null)
            {
                ownedScope = _serviceProvider.CreateScope();
                services = ownedScope.ServiceProvider;
            }

            try
            {
                var currentUserService = services.GetRequiredService<ICurrentUserService>();
                var user = await currentUserService.GetCurrentUserAsync();

                identity.AddClaim(new Claim("UserId",   user.UserId.ToString()));
                identity.AddClaim(new Claim("TenantId", user.TenantId.ToString()));
                identity.AddClaim(new Claim("FullName", user.FullName));
                identity.AddClaim(new Claim("Email",    user.Email));

                // Lowercase for consistent casing — PermissionHandler compares
                // case-insensitively now, but the claim values should still match
                // what DemoAuthenticationHandler writes.
                identity.AddClaim(new Claim("IsTenantAdmin", user.IsTenantAdmin.ToString().ToLower()));

                // IsSuperAdmin is already in the clone from DemoAuthenticationHandler.
                // We do NOT override it here.

                int permissionCount = 0;
                foreach (var module in user.Permissions.Keys)
                {
                    foreach (var action in user.Permissions[module])
                    {
                        identity.AddClaim(new Claim("permission", $"{module}.{action}"));
                        permissionCount++;
                    }
                }

                identity.AddClaim(new Claim("permissions_loaded", "true"));

                _logger.LogDebug(
                    "ClaimsTransformer: fallback loaded {FullName} ({UserId}) with {Count} permission claims",
                    user.FullName, user.UserId, permissionCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ClaimsTransformer: error loading user permissions — {Message}", ex.Message);
                return principal;
            }
            finally
            {
                ownedScope?.Dispose();
            }

            return clone;
        }
    }
}
