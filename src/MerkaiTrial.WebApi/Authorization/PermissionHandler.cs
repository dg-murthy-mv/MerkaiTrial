// FILE: MerkaiTrial.WebApi/Authorization/PermissionHandler.cs

using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace MerkaiTrial.WebApi.Authorization
{
    public class PermissionHandler : AuthorizationHandler<PermissionRequirement>
    {
        protected override Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            PermissionRequirement requirement)
        {
            // Get user claims
            var user = context.User;

            // Check if user is SystemAdmin or TenantAdmin (bypass)
            if (user.HasClaim(c => c.Type == "IsTenantAdmin" && c.Value == "true"))
            {
                context.Succeed(requirement);
                return Task.CompletedTask;
            }

            // Check specific permission claim
            var permissionClaim = $"{requirement.Module}.{requirement.Action}";
            if (user.HasClaim("permission", permissionClaim))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }
}
