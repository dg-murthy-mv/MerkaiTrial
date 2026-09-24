// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Startup/AuthenticationSetup.cs
//
// UPDATED: the security-stamp revalidation query now calls
// IgnoreQueryFilters(). See the long comment on OnValidatePrincipal —
// this is the one that produces a redirect loop with nothing in the logs.
//
// CHANGES (025)
//   ✅ AccessDeniedPath points at /AccessDenied. There were two
//      access-denied pages; now there is one.
//   ✅ The security-stamp query is wrapped in try/catch, so a database
//      that cannot be reached logs the person out instead of throwing a
//      500 out of the authentication middleware.
//
// WHAT IS DELIBERATELY *NOT* HERE: EnableRetryOnFailure
//   I said I would add it. I am not, and the reason matters.
//
//   SqlServerRetryingExecutionStrategy treats a command timeout (error
//   -2) as transient and retries it — which would have made the timeout
//   above invisible. But it also THROWS on any explicit transaction that
//   is not wrapped in an execution strategy, and this solution has at
//   least two:
//
//       TenantProvisioningService.ProvisionAsync
//           await using var tx = await _db.Database.BeginTransactionAsync(ct);
//
//       ConvertLeadHandler.Handle
//           using var transaction = await _db.Database.BeginTransactionAsync(ct);
//
//   Switching retries on without first wrapping every one of those in
//   db.Database.CreateExecutionStrategy().ExecuteAsync(...) trades a rare
//   500 on a settings page for a guaranteed failure of tenant
//   provisioning and lead conversion. That is a worse bug, and it would
//   only show up the next time someone created a workspace.
//
//   It needs a search for BeginTransaction across the solution and a pass
//   over each call site. Its own round, not a line slipped into this one.
// =====================================================================

using MerkaiTrial.Application.Security;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Admin.Web.Startup;

public static class AuthenticationSetup
{
    public static WebApplicationBuilder AddMerkaiAuthentication(this WebApplicationBuilder builder)
    {
        // Demo auth must never reach a public environment. Config drift on
        // a shared host is exactly how a demo handler ends up live, so
        // refuse to boot rather than start insecurely.
        var demoEnabled = builder.Configuration.GetValue<bool>("DevelopmentTenantContext:Enabled");
        if (demoEnabled && !builder.Environment.IsEnvironment("Demo") && !builder.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "FATAL: DevelopmentTenantContext:Enabled is true outside Development/Demo. " +
                "Demo authentication grants identity from configuration and request headers — " +
                "it must never run where real clients can reach it. Set it to false.");
        }

        builder.Services.AddScoped<IPasswordService, PasswordService>();
        builder.Services.AddScoped<IUserTokenService, UserTokenService>();
        builder.Services.AddScoped<ISignInService, SignInService>();

        builder.Services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name         = "MerkaiTrial.Auth";
                options.Cookie.HttpOnly     = true;
                options.Cookie.SameSite     = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.IsEssential  = true;

                options.LoginPath        = "/Account/Login";
                options.LogoutPath       = "/Account/Logout";

                // 025: one access-denied page, not two. This used to point
                // at /Account/Denied while AuthorizedPageModel redirected to
                // /AccessDenied — two screens for the same event, with two
                // designs, two wordings, and a broken link out of each.
                // /Account/Denied now redirects here.
                options.AccessDeniedPath = "/AccessDenied";

                options.ExpireTimeSpan    = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;

                // Revalidate the security stamp so a password change or a
                // forced sign-out invalidates existing cookies rather than
                // leaving them valid for the full 8 hours.
                options.Events.OnValidatePrincipal = async ctx =>
                {
                    var stampClaim = ctx.Principal?.FindFirst(SignInService.ClaimSecurityStamp)?.Value;
                    var userIdRaw  = ctx.Principal?.FindFirst(SignInService.ClaimUserId)?.Value;

                    if (!Guid.TryParse(userIdRaw, out var userId)) { ctx.RejectPrincipal(); return; }

                    var db = ctx.HttpContext.RequestServices.GetRequiredService<FlowDbContext>();

                    // ── IgnoreQueryFilters — THE DANGEROUS ONE ────────
                    // This runs on EVERY request, inside the authentication
                    // middleware, BEFORE HttpContext.User is populated. The
                    // tenant provider reads HttpContext.User, so at this
                    // moment there is no tenant: the filter would compare
                    // against Guid.Empty and find no row.
                    //
                    // No row means the stamp check fails, the cookie is
                    // rejected, and the user is redirected to /Account/Login
                    // — where they sign in successfully, get a fresh cookie,
                    // and are rejected again on the very next request.
                    //
                    // The symptom is an endless bounce between /Dashboard
                    // and /Account/Login with NOTHING in the logs, because
                    // nothing threw. It looks like broken authentication.
                    // It is a missing IgnoreQueryFilters.
                    // ── 025: A DATABASE BLIP MUST NOT 500 THE WHOLE APP ──
                    //
                    // This runs inside the authentication middleware on
                    // EVERY request. An exception here escapes before any
                    // page code runs, so the person gets a bare 500 on
                    // whatever they clicked, and the stack trace points at
                    // this file rather than at anything they did.
                    //
                    // That happened: a lock somewhere else in the database
                    // held this query past its 30-second command timeout,
                    // and /Settings/PipelineRules returned a 500 that looked
                    // like a bug in the settings page. It was not. It was
                    // this line, on a bad day.
                    //
                    // Rejecting the principal is the honest outcome. We
                    // could not verify the cookie, so we do not trust it —
                    // the person is sent to sign in again, which works the
                    // moment the database is reachable. One re-login beats
                    // an error page, and it beats the alternative of
                    // accepting an unverified cookie, which would turn a
                    // transient outage into a way of keeping a revoked
                    // session alive.
                    try
                    {
                        var row = await db.Users.AsNoTracking().IgnoreQueryFilters()
                            .Where(u => u.Id == userId && !u.IsDeleted && u.IsActive)
                            .Select(u => new { u.SecurityStamp })
                            .FirstOrDefaultAsync();

                        if (row is null || row.SecurityStamp != stampClaim)
                        {
                            ctx.RejectPrincipal();
                            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Logged at Error, not Warning: this is never normal,
                        // and it is the line that tells you the database was
                        // unreachable rather than the page being broken.
                        ctx.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("SecurityStamp")
                            .LogError(ex,
                                "Could not validate the security stamp for user {UserId}; " +
                                "rejecting the cookie and sending them to sign in again.",
                                userId);

                        // RejectPrincipal affects THIS request only. Without
                        // the sign-out the browser keeps the cookie and
                        // re-runs the same failing query on every subsequent
                        // request — a silent bounce on every click, which is
                        // the exact failure mode the comment above is about.
                        // The success path signs out; so does this one.
                        ctx.RejectPrincipal();
                        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                };
            });

        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("TenantAccess", p => p.RequireAuthenticatedUser());
            options.AddPolicy("SuperAdmin",   p => p.RequireClaim(SignInService.ClaimIsSuperAdmin, "true"));

            // Every endpoint requires authentication unless it explicitly
            // opts out with [AllowAnonymous]. Without this, a new page ships
            // unprotected whenever someone forgets an attribute — and that
            // is the most common way tenant data leaks.
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        return builder;
    }

    public static WebApplication UseMerkaiAuthPipeline(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
            app.UseHsts();

        app.UseHttpsRedirection();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }
}
