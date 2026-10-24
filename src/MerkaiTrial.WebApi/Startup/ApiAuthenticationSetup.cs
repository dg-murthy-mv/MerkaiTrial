// =====================================================================
// FILE: MerkaiTrial.WebApi/Startup/ApiAuthenticationSetup.cs
//
// Create the folder: MerkaiTrial.WebApi/Startup/
//
// NUGET REQUIRED (WebApi project):
//     Microsoft.AspNetCore.Authentication.JwtBearer
//
// Install with:
//     dotnet add MerkaiTrial.WebApi package Microsoft.AspNetCore.Authentication.JwtBearer
// Match the major version to your .NET version (8.x for net8.0).
//
// WHY THIS FILE MUST EXIST
// Removing the Demo scheme left the API with NO authentication at all.
// app.UseAuthentication() requires IAuthenticationSchemeProvider, which
// only AddAuthentication registers — so the app compiles and then throws
// on startup. It also means ApiCurrentUserService finds no claims and
// every tenant-scoped request fails.
// =====================================================================

using System.Text;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace MerkaiTrial.WebApi.Startup;

public static class ApiAuthenticationSetup
{
    public static WebApplicationBuilder AddMerkaiApiAuthentication(this WebApplicationBuilder builder)
    {
        // Same guard as Admin.Web: demo identity must never run where
        // clients can reach it. Refuse to boot rather than start insecurely.
        var demoEnabled = builder.Configuration.GetValue<bool>("DevelopmentTenantContext:Enabled");
        if (demoEnabled && !builder.Environment.IsEnvironment("Demo") && !builder.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "FATAL: DevelopmentTenantContext:Enabled is true outside Development/Demo.");
        }

        var secret = builder.Configuration["Jwt:SigningKey"]
            ?? throw new InvalidOperationException(
                "Jwt:SigningKey is not configured. The API cannot validate tokens without it. " +
                "It must be the SAME value configured in Admin.Web — if they differ, every call " +
                "returns 401 and the dashboard goes blank with no obvious cause.");

        if (Encoding.UTF8.GetByteCount(secret) < 32)
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes for HMAC-SHA256.");

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();

                // The token carries "UserId" and "TenantId" as literal claim
                // types. Leaving the inbound claim-type map on would rewrite
                // them to WS-Federation URIs and ApiCurrentUserService would
                // find nothing.
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer           = true,
                    ValidIssuer              = builder.Configuration["Jwt:Issuer"]   ?? "merkaitrial-admin",
                    ValidateAudience         = true,
                    ValidAudience            = builder.Configuration["Jwt:Audience"] ?? "merkaitrial-api",
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                    ValidateLifetime         = true,
                    ClockSkew                = TimeSpan.FromSeconds(30),

                    NameClaimType            = SignInService.ClaimUserId,
                    RoleClaimType            = System.Security.Claims.ClaimTypes.Role,
                };

                options.Events = new JwtBearerEvents
                {
                    // ── 102: REVOKED ACCESS ENDS ON THE NEXT CALL ──────
                    // A valid signature and an unexpired token used to be
                    // enough. Tokens live 5 minutes and carry the person's
                    // permissions, so someone switched off, deleted, moved
                    // out of the workspace or stripped of a role kept using
                    // the API with what they used to have until it expired.
                    //
                    // Now every call also checks the database, the same
                    // check Admin.Web makes on its cookie for every page:
                    //   • the person exists, is active and not deleted;
                    //   • they still belong to the workspace the token names;
                    //   • that workspace is active;
                    //   • their security stamp is the one the token carries.
                    // Changing roles, a role's permissions or a password
                    // rotates the stamp, so any of those ends old tokens too.
                    //
                    // One primary-key lookup per call. If the database can't
                    // be asked, the token is refused (fail closed) — the
                    // same choice the cookie check makes, for the same reason.
                    OnTokenValidated = async ctx =>
                    {
                        var p = ctx.Principal;
                        if (!Guid.TryParse(p?.FindFirst(SignInService.ClaimUserId)?.Value, out var userId) ||
                            !Guid.TryParse(p?.FindFirst(SignInService.ClaimTenantId)?.Value, out var tenantId))
                        {
                            ctx.Fail("The token names no user or workspace.");
                            return;
                        }

                        var stamp = p!.FindFirst(SignInService.ClaimSecurityStamp)?.Value;
                        var log = ctx.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>().CreateLogger("JwtBearer");

                        try
                        {
                            var db = ctx.HttpContext.RequestServices.GetRequiredService<FlowDbContext>();

                            // Runs before HttpContext.User exists, so there is
                            // no tenant for the query filters yet — see the
                            // same note in Admin.Web's AuthenticationSetup.
                            var row = await db.Users.AsNoTracking().IgnoreQueryFilters()
                                .Where(u => u.Id == userId && !u.IsDeleted && u.IsActive)
                                .Select(u => new
                                {
                                    u.TenantId,
                                    u.SecurityStamp,
                                    TenantLive = db.Tenants.IgnoreQueryFilters()
                                        .Any(t => t.Id == u.TenantId && t.IsActive && !t.IsDeleted),
                                })
                                .FirstOrDefaultAsync(ctx.HttpContext.RequestAborted);

                            string? reason =
                                row is null                  ? "the person is switched off or no longer exists" :
                                row.TenantId != tenantId     ? "the person no longer belongs to that workspace" :
                                !row.TenantLive              ? "the workspace is suspended" :
                                stamp is null || !string.Equals(row.SecurityStamp ?? string.Empty, stamp, StringComparison.Ordinal)
                                                             ? "the session was ended (roles, permissions or password changed)" :
                                null;

                            if (reason is not null)
                            {
                                log.LogWarning("JWT for user {UserId} refused: {Reason}", userId, reason);
                                ctx.Fail("Access revoked: " + reason + ".");
                            }
                        }
                        catch (Exception ex)
                        {
                            log.LogError(ex, "Could not check the token for user {UserId} against the database; refusing it", userId);
                            ctx.Fail("The token could not be checked.");
                        }
                    },

                    OnAuthenticationFailed = ctx =>
                    {
                        // Rejected tokens are worth seeing. On a public
                        // hostname this is the line that shows probing —
                        // and during setup it is how you discover the two
                        // apps have different signing keys.
                        ctx.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("JwtBearer")
                            .LogWarning("JWT rejected: {Message}", ctx.Exception.Message);
                        return Task.CompletedTask;
                    }
                };
            });

        builder.Services.AddAuthorization(options =>
        {
            // Every endpoint requires a valid token unless it opts out with
            // [AllowAnonymous]. Without this, a controller missing
            // [Authorize] is wide open — one forgotten attribute away.
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        return builder;
    }
}

/* =====================================================================
   CONFIGURATION

   Development — user secrets or appsettings.Development.json:

       "Jwt": {
         "SigningKey": "<64+ random characters>",
         "Issuer": "merkaitrial-admin",
         "Audience": "merkaitrial-api"
       }

   The SAME block must exist in Admin.Web. Generate a key with:

       [Convert]::ToBase64String((1..48 | % { Get-Random -Max 256 }))

   Azure — App Service configuration or Key Vault, never in a file that
   goes into source control. Use double underscores:

       Jwt__SigningKey
       Jwt__Issuer
       Jwt__Audience

   =====================================================================
   ENDPOINTS THAT MUST BE [AllowAnonymous] UNDER THE FALLBACK POLICY

     GET  /api/quotes/public/{token}          ✅ already has it
     PUT  /api/quotes/public/{token}/status   ✅ already has it
     GET  /health                             — set in Program.cs

   Anything else a customer or a probe reaches without a token.
   ===================================================================== */
