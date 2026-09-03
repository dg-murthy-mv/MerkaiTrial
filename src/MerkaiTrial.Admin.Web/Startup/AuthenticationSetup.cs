// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Startup/AuthenticationSetup.cs
//
// Drop-in replacement for the AddAuthentication("Demo") block in
// Program.cs. Call from Program.cs as:
//
//     builder.AddMerkaiAuthentication();
//     ...
//     app.UseMerkaiAuthPipeline();
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
        // ── GUARD: demo auth must never reach a public environment ──────
        // Config drift on a shared host is exactly how a demo handler ends
        // up live. Refuse to boot rather than start insecurely.
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
                options.Cookie.Name        = "MerkaiTrial.Auth";
                options.Cookie.HttpOnly    = true;
                options.Cookie.SameSite    = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.IsEssential = true;

                options.LoginPath        = "/Account/Login";
                options.LogoutPath       = "/Account/Logout";
                options.AccessDeniedPath = "/Account/Denied";

                options.ExpireTimeSpan = TimeSpan.FromHours(8);
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
                    var row = await db.Users.AsNoTracking()
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

            // ── FALLBACK POLICY ────────────────────────────────────────
            // Every endpoint requires authentication unless it explicitly
            // opts out with [AllowAnonymous]. Without this, a new page ships
            // unprotected whenever someone forgets an attribute — and "someone
            // forgot the attribute" is the most common way tenant data leaks.
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        return builder;
    }

    public static WebApplication UseMerkaiAuthPipeline(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }
        app.UseHttpsRedirection();

        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }
}

/* =====================================================================
   PROGRAM.CS CHANGES REQUIRED
   =====================================================================

   1. DELETE the whole AddAuthentication("Demo") block.

   2. Replace with:            builder.AddMerkaiAuthentication();

   3. Static files for Uploads MUST GO. This currently runs before
      authentication and performs no authorization at all, so every
      attachment is a public download:

          // DELETE THIS ENTIRELY
          app.UseStaticFiles(new StaticFileOptions {
              FileProvider = new PhysicalFileProvider(
                  Path.Combine(builder.Environment.ContentRootPath, "Uploads")),
              RequestPath = "/Uploads"
          });

      Replace with an authenticated controller action that loads the
      Attachment row, compares its TenantId to the caller's, returns 404
      (not 403) on mismatch, and only then streams the file.

      Note also the casing mismatch: LocalFileStorageService writes to
      "uploads" while this served "Uploads". Windows tolerates it; Linux
      App Service does not — every attachment would 404 in production.

   4. Order the pipeline exactly:

          app.UseStaticFiles();              // wwwroot only
          app.UseSerilogRequestLogging();
          app.UseMerkaiAuthPipeline();       // routing → authn → authz
          app.MapRazorPages();

   5. Anonymous pages need [AllowAnonymous]: Login, SetPassword,
      ForgotPassword, Error, and the public quote-acceptance endpoint.
      Everything else is now protected by default.

   6. WebApi Program.cs, separately and equally urgently:
        - REMOVE the Demo scheme and the X-Tenant-Id / X-User-Id trust path.
        - Gate Swagger behind IsDevelopment().
        - Validate a signed JWT issued at login instead.
        - Restrict the API App Service to Admin.Web's outbound IPs so it is
          not internet-reachable at all.
   ===================================================================== */
