// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Startup/AuthenticationSetup.cs
//
// UPDATED: the security-stamp revalidation query now calls
// IgnoreQueryFilters(). See the long comment on OnValidatePrincipal —
// this is the one that produces a redirect loop with nothing in the logs.
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
                options.AccessDeniedPath = "/Account/Denied";

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
                    var row = await db.Users.AsNoTracking().IgnoreQueryFilters()
                        .Where(u => u.Id == userId && !u.IsDeleted && u.IsActive)
                        .Select(u => new { u.SecurityStamp })
                        .FirstOrDefaultAsync();

                    if (row is null || row.SecurityStamp != stampClaim)
                    {
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
