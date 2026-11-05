// =====================================================================
// FILE: MerkaiTrial.WebApi/Authorization/PlatformAdminOnlyAttribute.cs
//
// NEW (119). [PlatformAdminOnly] on an action (or a controller): only a
// super admin who is NOT viewing as someone else may call it. Anyone else
// gets 403 { error = "This is a platform administration endpoint." } and a
// log line, before any of the action's code runs.
//
// The same rule RoleTemplatesController checks by hand (117). Written as
// an attribute so a platform endpoint states it where it is declared.
// A permission policy can't say this: PermissionHandler lets every
// WORKSPACE admin through every permission.
//
// Used on: the write endpoints of Countries (119) and Company Verticals
// (119), which until now any signed-in person could call.
// =====================================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MerkaiTrial.WebApi.Authorization;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class PlatformAdminOnlyAttribute : Attribute, IAsyncAuthorizationFilter
{
    public static bool IsPlatformAdmin(System.Security.Claims.ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
        && user.HasClaim("IsSuperAdmin", "true")
        && !user.HasClaim("ViewingAs", "true");

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;
        if (IsPlatformAdmin(user)) return Task.CompletedTask;

        if (user.Identity?.IsAuthenticated != true)
        {
            context.Result = new UnauthorizedResult();
            return Task.CompletedTask;
        }

        var logger = context.HttpContext.RequestServices.GetService<ILogger<PlatformAdminOnlyAttribute>>();
        logger?.LogWarning("Refused platform endpoint {Method} {Path} for non-super-admin {User}",
            context.HttpContext.Request.Method, context.HttpContext.Request.Path,
            user.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? user.Identity?.Name);

        context.Result = new ObjectResult(new { error = "This is a platform administration endpoint." })
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
        return Task.CompletedTask;
    }
}
