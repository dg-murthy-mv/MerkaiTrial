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
