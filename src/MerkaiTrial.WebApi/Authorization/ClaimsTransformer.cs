// FILE: MerkaiTrial.WebApi/Authorization/ClaimsTransformer.cs

using Microsoft.AspNetCore.Authentication;
using MerkaiTrial.Application.Services;
using System.Security.Claims;

namespace MerkaiTrial.WebApi.Authorization
{
    public class ClaimsTransformer : IClaimsTransformation
    {
        private readonly IServiceProvider _serviceProvider;

        public ClaimsTransformer(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            // Create a clone of the principal
            var clone = principal.Clone();
            var identity = (ClaimsIdentity?)clone.Identity;

            if (identity == null || !identity.IsAuthenticated)
                return principal;

            // Check if claims already added (avoid duplicate transformation)
            if (identity.HasClaim(c => c.Type == "permissions_loaded"))
                return clone;

            using var scope = _serviceProvider.CreateScope();
            var currentUserService = scope.ServiceProvider.GetRequiredService<ICurrentUserService>();

            try
            {
                var user = await currentUserService.GetCurrentUserAsync();

                // Add user info claims
                identity.AddClaim(new Claim("UserId", user.UserId.ToString()));
                identity.AddClaim(new Claim("TenantId", user.TenantId.ToString()));
                identity.AddClaim(new Claim("FullName", user.FullName));
                identity.AddClaim(new Claim("Email", user.Email));
                identity.AddClaim(new Claim("IsTenantAdmin", user.IsTenantAdmin.ToString()));

                // Add permission claims
                foreach (var module in user.Permissions.Keys)
                {
                    foreach (var action in user.Permissions[module])
                    {
                        identity.AddClaim(new Claim("permission", $"{module}.{action}"));
                    }
                }

                // Mark as loaded
                identity.AddClaim(new Claim("permissions_loaded", "true"));
            }
            catch
            {
                // If error loading user, return original principal
                return principal;
            }

            return clone;
        }
    }
}
