// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Startup/TenantContextWarmup.cs
//
// NEW FILE (060).
//
// Fills ICurrentTenantService's per-request cache ASYNCHRONOUSLY, once,
// before any page runs — so that the ~28 sync getters on that service
// become field reads instead of blocking database calls.
//
// WHY THIS EXISTS. CurrentTenantService.Load() is
// LoadAsync().GetAwaiter().GetResult(). There is no deadlock risk —
// ASP.NET Core has no SynchronizationContext — and the per-request cache
// means it blocks once across four queries rather than 28 times. But
// that one block parks a REQUEST THREAD, and the thread pool only adds
// threads at roughly one or two per second once its ready threads are
// used up. Under a burst, or on a slow database, requests then queue
// before they even start executing and latency climbs much further than
// the database latency alone would explain.
//
// WHY MIDDLEWARE RATHER THAN MAKING THE SERVICE ASYNC. _Layout.cshtml
// calls @Ui.GetTenantName(), @Ui.GetUserEmail(), @Ui.GetPlanName(),
// @Ui.GetPlanDisplayName(), @Ui.GetCurrencyCode() and @Ui.HasFeature()
// — a Razor view, which cannot await. Making the interface async end to
// end would mean rewriting every call site including those, for the same
// result this achieves in one line. This way nothing else in the app
// changes at all.
//
// WHY IT IS SAFE IN FRONT OF EVERYTHING. TryWarmUpAsync never throws,
// and returns false without touching the database for any request that
// has no tenant — anonymous ones (the public quote page, Login,
// SetPassword, ForgotPassword, Error) and signed-in users with no
// TenantId claim. If it fails for any other reason the request carries
// on and Load() blocks exactly as it does today. This is an
// optimisation, never a dependency.
//
// WHY ADMIN.WEB AND NOT THE API. Here the load is GUARANTEED: _Layout
// renders on every authenticated page and calls six of those getters, so
// warming the cache costs the same four queries the request was going to
// run regardless — it just awaits them instead of blocking on them.
// MerkaiTrial.WebApi has no layout, so many endpoints never touch tenant
// context, and warming it unconditionally there would ADD four queries
// to requests that do not need them. SETUP.md has the three lines to
// enable it on that host if its thread pool ever becomes the problem.
//
// PLACEMENT. After UseMerkaiAuthPipeline() — which calls
// UseAuthentication() and UseAuthorization() — because the TenantId
// claim does not exist before authentication runs. Before
// MapRazorPages(), because that is where the pages that read it are.
// =====================================================================

// Explicit rather than relying on ImplicitUsings — this file is an
// IApplicationBuilder extension and is cheap to make self-sufficient.
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MerkaiTrial.Admin.Web.Startup;

public static class TenantContextWarmup
{
    /// <summary>
    /// Warms ICurrentTenantService for this request. Must be called
    /// AFTER UseAuthentication/UseAuthorization and BEFORE the endpoint
    /// middleware. Adds nothing to anonymous requests.
    /// </summary>
    public static IApplicationBuilder UseTenantContextWarmup(this IApplicationBuilder app)
    {
        // The explicit (HttpContext, RequestDelegate) signature is
        // deliberate: app.Use has two overloads and an untyped lambda
        // can bind to the wrong one.
        return app.Use(async (HttpContext ctx, RequestDelegate next) =>
        {
            // RequestServices, not constructor injection — this resolves
            // the SCOPED instance belonging to THIS request, which is
            // the same object the page will be handed. Injecting it into
            // a middleware constructor would capture one instance for
            // the lifetime of the app and warm the wrong cache, which is
            // also why middleware constructors cannot take scoped
            // dependencies.
            var tenant = ctx.RequestServices.GetRequiredService<ICurrentTenantService>();

            // Return value ignored on purpose. False means "nothing to
            // warm" or "could not warm", and both are fine — the sync
            // getters still work. There is no failure to handle here.
            await tenant.TryWarmUpAsync(ctx.RequestAborted);

            await next(ctx);
        });
    }
}
