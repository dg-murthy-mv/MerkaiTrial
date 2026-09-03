// FILE: MerkaiTrial.WebApi/Middleware/PermissionAuthorizationMiddleware.cs

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using MerkaiTrial.WebApi.Authorization;

namespace MerkaiTrial.WebApi.Middleware
{
    public class PermissionAuthorizationMiddleware
    {
        private readonly RequestDelegate _next;

        public PermissionAuthorizationMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            await _next(context);

            // If authorization failed (403) and it's an API request
            if (context.Response.StatusCode == 403 && 
                context.Request.Path.StartsWithSegments("/api"))
            {
                var endpoint = context.GetEndpoint();
                var authorizeData = endpoint?.Metadata.GetMetadata<IAuthorizeData>();
                
                if (authorizeData != null)
                {
                    var userEmail = context.User.FindFirst("Email")?.Value ?? "Unknown User";
                    var policy = authorizeData.Policy ?? "Unknown";
                    
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "Forbidden",
                        message = $"You don't have permission to access this resource.",
                        details = $"Required policy: {policy}",
                        user = userEmail,
                        statusCode = 403
                    });
                }
            }
        }
    }

    public static class PermissionAuthorizationMiddlewareExtensions
    {
        public static IApplicationBuilder UsePermissionAuthorizationMessages(
            this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<PermissionAuthorizationMiddleware>();
        }
    }
}
