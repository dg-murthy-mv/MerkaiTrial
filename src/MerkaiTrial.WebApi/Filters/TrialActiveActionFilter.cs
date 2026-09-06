
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MerkaiTrial.WebApi.Filters;

public class TrialActiveActionFilter : IAsyncActionFilter
{
    private readonly ILogger<TrialActiveActionFilter> _logger;

    public TrialActiveActionFilter(ILogger<TrialActiveActionFilter> logger) => _logger = logger;

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;

        // Reads always pass. So do anonymous endpoints — the public quote
        // link is opened by the CLIENT'S CUSTOMER, not by the tenant, and
        // they should not be punished for the tenant's expiry.
        var allowsAnonymous = context.ActionDescriptor.EndpointMetadata
            .OfType<IAllowAnonymous>().Any();

        if (allowsAnonymous
            || HttpMethods.IsGet(http.Request.Method)
            || HttpMethods.IsHead(http.Request.Method)
            || HttpMethods.IsOptions(http.Request.Method))
        {
            await next();
            return;
        }

        // No tenant claim means no tenant state to check. Authorization has
        // already run by this point, so an unauthenticated caller never
        // reaches here — but resolving tenant context would throw, so guard.
        var tenantClaim = http.User?.FindFirst("TenantId")?.Value;
        if (string.IsNullOrEmpty(tenantClaim))
        {
            await next();
            return;
        }

        var tenantCtx = http.RequestServices.GetRequiredService<ICurrentTenantService>();

        bool isActive;
        try
        {
            isActive = tenantCtx.IsAccountActive();
        }
        catch (UnauthorizedAccessException)
        {
            // Tenant row missing or unresolvable. Let the action run and fail
            // on its own terms rather than reporting a trial problem that may
            // not be the real one.
            await next();
            return;
        }

        if (isActive)
        {
            await next();
            return;
        }

        // 403, not 401. 401 means "authenticate"; the caller IS authenticated
        // and correct — the WORKSPACE is what is refused. A 401 would send
        // Admin.Web into a re-authentication loop.
        _logger.LogInformation(
            "Write refused on {Method} {Path}: tenant {TenantId} is not active (trial expired or suspended).",
            http.Request.Method, http.Request.Path, tenantClaim);

        context.Result = new ObjectResult(new ProblemDetails
        {
            Title = "Trial ended",
            Detail = "This workspace is read-only because its trial has ended. "
                   + "Existing data can still be viewed and exported.",
            Status = StatusCodes.Status403Forbidden,
            Type = "https://merkai/errors/trial-expired",
        })
        {
            StatusCode = StatusCodes.Status403Forbidden
        };
    }
}