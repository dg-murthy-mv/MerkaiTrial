// =====================================================================
// FILE: MerkaiTrial.Application/Authorization/PermissionHandler.cs
//
// (Header corrected — this file's namespace is MerkaiTrial.Application.Authorization
// and it lives in the Application project. The old header claimed
// MerkaiTrial.Admin.Web/Authorization/, which is the same stale-path pattern that
// hid _ValidationScriptsPartial.cshtml from a grep for an entire session.)
//
// SESSION 8 PERFORMANCE FIX — success-path logging demoted to Debug.
//
// AUTHORIZATION BEHAVIOUR IS COMPLETELY UNCHANGED. Only log levels and the
// point at which DescribeUser() is evaluated have moved.
//
// WHY: this handler runs on every permission-gated call. One Dashboard load
// produced ten Information lines in WebApi alone:
//     🔐 Checking permission: Quotes.Read for user manager@sathornprestige.co.th
//     ✅ Access granted (has permission: Quotes.Read) for manager@...
// ...x5, all confirming that nothing went wrong. Session 7 established that
// log writes, not SQL, dominate request time in this application; emitting
// two lines per check to say "this worked" is exactly that cost with none of
// the diagnostic value.
//
// WHAT STAYS AT WARNING: the DENIAL path. "❌ Access denied" is a real audit
// signal, it fires rarely, and it is the line you actually want in the file
// when a role misbehaves during testing. Untouched.
//
// DescribeUser() was previously called unconditionally at the top of every
// check, purely to populate a log line — several FindFirst() walks over the
// claim collection per authorization decision, discarded when the log level
// filtered the line out. It is now evaluated only when a line will actually
// be written.
//
// TO DEBUG A PERMISSION PROBLEM: set MinimumLevel to Debug (or add an
// override for MerkaiTrial.Application.Authorization) in appsettings.
// Development.json and the full trace comes back verbatim.
// =====================================================================

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Security.Claims;

namespace MerkaiTrial.Application.Authorization
{
    public class PermissionHandler : AuthorizationHandler<PermissionRequirement>
    {
        private readonly ILogger<PermissionHandler> _logger;

        public PermissionHandler(ILogger<PermissionHandler> logger)
        {
            _logger = logger;
        }

        protected override Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            PermissionRequirement requirement)
        {
            var user = context.User;

            // Evaluated lazily — see header. DescribeUser walks the claim
            // collection up to four times and was previously run on every
            // authorization decision just to fill in a log line.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Checking permission: {Module}.{Action} for user {Email}",
                    requirement.Module, requirement.Action, DescribeUser(user));
            }

            // Check if user is SystemAdmin or TenantAdmin (bypass)
            if (user.HasClaim(c => c.Type == "IsTenantAdmin" &&
                            string.Equals(c.Value, "true", StringComparison.OrdinalIgnoreCase)))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Access granted (TenantAdmin bypass) for {Email}", DescribeUser(user));

                context.Succeed(requirement);
                return Task.CompletedTask;
            }

            // Check specific permission claim
            // ✅ FIX (retained): case-insensitive comparison. DemoAuthenticationHandler
            // builds this claim's value directly from the Role.Permissions JSON's module
            // key casing (e.g. "Leads.read" — PascalCase module, lowercase action).
            // Policy strings and Policies.* constants elsewhere in the codebase
            // use inconsistent casing ("Deals.Read", "products.read", etc).
            // HasClaim(type, value) compares the value with ordinal (case-sensitive)
            // comparison, so any casing mismatch here silently denies access for
            // every non-tenant-admin role. Comparing case-insensitively removes
            // that whole class of bug rather than relying on every call site to
            // use exactly the right casing forever.
            var permissionClaim = $"{requirement.Module}.{requirement.Action}";
            var hasPermission = user.Claims.Any(c =>
                c.Type == "permission" &&
                string.Equals(c.Value, permissionClaim, StringComparison.OrdinalIgnoreCase));

            if (hasPermission)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Access granted (has permission: {Permission}) for {Email}",
                        permissionClaim, DescribeUser(user));
                }

                context.Succeed(requirement);
            }
            else
            {
                // ── DENIAL PATH — deliberately still Warning. This is an audit
                // signal, it is rare, and it is the line you want in the file
                // when role-by-role testing turns up a surprise. ──────────────
                var userEmail = DescribeUser(user);
                var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                             ?? user.FindFirst("UserId")?.Value
                             ?? "Unknown";

                _logger.LogWarning("❌ Access denied (missing permission: {Permission}) for user {Email} (ID: {UserId})",
                    permissionClaim, userEmail, userId);

                // Add failure reason to context
                context.Fail(new AuthorizationFailureReason(
                    this,
                    $"User '{userEmail}' does not have '{requirement.Action}' permission for '{requirement.Module}' module"));
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Best-effort identity for audit log lines. Mirrors
        /// AuthorizedPageModel.DescribeUser so both permission paths — the
        /// page-entry gate and the policy handler — render the same user.
        ///
        /// The old code read FindFirst("Email") — a literal claim TYPE of
        /// "Email". DemoAuthenticationHandler stores the address under
        /// ClaimTypes.Email, i.e. the WS-Federation URI
        /// "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress",
        /// so the lookup never matched and every line logged "Unknown".
        /// The fallback chain below tries both conventions plus Identity.Name
        /// so it keeps working when DemoAuthenticationHandler is replaced by
        /// real authentication, whichever convention that ends up using.
        /// </summary>
        private static string DescribeUser(ClaimsPrincipal user)
        {
            var email = user.FindFirst(ClaimTypes.Email)?.Value;
            if (!string.IsNullOrWhiteSpace(email)) return email;

            email = user.FindFirst("Email")?.Value;
            if (!string.IsNullOrWhiteSpace(email)) return email;

            var name = user.Identity?.Name;
            if (!string.IsNullOrWhiteSpace(name)) return name;

            var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? user.FindFirst("UserId")?.Value;
            return string.IsNullOrWhiteSpace(id) ? "Unknown" : id;
        }
    }
}
