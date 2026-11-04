// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Filters/PlanFeaturePageFilter.cs
//
// NEW (114). A page whose model carries [RequiresPlanFeature(...)] opens
// only when the workspace's plan includes that feature. Otherwise the
// person lands on /Upgrade?feature=…, which says what the feature is,
// which plans include it, and who to ask. Nothing on the page runs.
//
// The API refuses the same things on its own (WebApi PlanFeatureFilter),
// so this is about a clear page, not the only lock.
//
// Registered globally in AdminWebServiceRegistration, after
// AccountStatePageFilter (a read-only workspace is explained first).
// =====================================================================

using System.Reflection;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MerkaiTrial.Admin.Web.Filters;

public class PlanFeaturePageFilter : IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var rules = context.HandlerInstance?.GetType()
            .GetCustomAttributes<RequiresPlanFeatureAttribute>(inherit: true)
            .ToList();

        if (rules is null || rules.Count == 0 || context.HttpContext.User.Identity?.IsAuthenticated != true)
        {
            await next();
            return;
        }

        var tenant = context.HttpContext.RequestServices.GetRequiredService<ICurrentTenantService>();
        foreach (var rule in rules)
        {
            if (rule.AnyOf.Any(tenant.HasFeature)) continue;

            context.Result = new RedirectToPageResult("/Upgrade", new { feature = rule.AnyOf.FirstOrDefault() });
            return;
        }

        await next();
    }
}
