// =====================================================================
// FILE: MerkaiTrial.WebApi/Program.cs   — COMPLETE, restructured
//
// Same services, same pipeline, same order as before. What moved where:
//
//   Startup/ApiServiceRegistration.cs   every builder.Services.* line,
//                                       grouped by area
//   Startup/ApiPipeline.cs              correlation id, swagger, exception
//                                       handler, / and /health
//   Startup/ApiAuthenticationSetup.cs   unchanged (JWT)
//
// NEW: ValidateOnBuild in EVERY environment. A service whose dependency
// is not registered now stops the app at startup, naming both types —
// instead of a 500 the first time someone opens that page in production.
// (Development already did this by default; Production did not.)
//
// CONFIG (App Service settings or Key Vault, NOT appsettings.json):
//   Jwt__SigningKey  = 64+ random chars, IDENTICAL to Admin.Web
//   Jwt__Issuer      = merkaitrial-admin
//   Jwt__Audience    = merkaitrial-api
//   Email__ApiKey    = Resend API key (re_...)         ← 038
//   Email__InboundWebhookSecret = whsec_... from the Resend webhook   ← 083
//   (Email__InboundDomain is not secret — appsettings is fine for it.)
//
// 038 also adds a BACKGROUND WORKER to this host. Two consequences worth
// knowing:
//
//   • "Always On" must be enabled on the App Service. Without it the host
//     is unloaded when idle and the queue stops draining until the next
//     request wakes it.
//   • If you scale to more than one instance, both run the worker. That is
//     handled — claiming is an atomic UPDATE ... OUTPUT with ROWLOCK and
//     READPAST, and every send carries a provider idempotency key — but it
//     is the reason those exist.
// =====================================================================

using MerkaiTrial.WebApi.Middleware;
using MerkaiTrial.WebApi.Startup;
using OfficeOpenXml;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ---------------- Logging ----------------
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();
builder.Host.UseSerilog();

// ---------------- Fail fast on missing registrations ----------------
builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateScopes = true;
    o.ValidateOnBuild = true;
});

// ---------------- Services ----------------
builder.AddMerkaiApiAuthentication();          // JWT — must exist before UseAuthentication()

builder.Services
    .AddApiControllers()                       // controllers, JSON, problem details
    .AddApiSwagger()                           // registered always, exposed in Development only
    .AddApiAuthorization()                     // "Module.Action" policies
    .AddApiPersistence(builder.Configuration)  // ITenantProvider + FlowDbContext
    .AddApiHandlers()                          // every ICommandHandler (scan)
    .AddApiDomainServices()                    // current user, resolvers, audit, PDFs, …
    .AddTaxAndPayments()                       // per-country tax + payment providers
    .AddApiEmail(builder.Configuration)        // 038 — Resend sender + options; 083 — receiving
    .AddApiBackgroundServices();               // 038 — outbound worker; 083 — inbound worker

// ---------------- Third-party licences ----------------
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
ExcelPackage.License.SetNonCommercialPersonal("MerkaiTrial");

// =====================================================================
var app = builder.Build();

await MerkaiTrial.Infrastructure.Persistence.DatabaseWarmup
    .WarmUpAsync(app.Services, "WebApi");

// 101: compare the app's model with the real tables and log every
// difference ([SchemaCheck] lines in webapi.out.log). Read-only, never
// stops startup. Round 101's two invoice bugs were exactly this kind of
// drift: Invoices.QuoteId NOT NULL, Invoices.Status defaulting to Sent.
await MerkaiTrial.Infrastructure.Persistence.SchemaCheck
    .RunAsync(app.Services, "WebApi");

// ---------------- Pipeline — order matters ----------------
// 063: was app.UseSerilogRequestLogging(). Same line, same position, but
// the public quote token is taken out of the path before it is written —
// /api/quotes/public/****bSxl rather than the whole thing. See
// Startup/ApiPipeline.cs. 085: invoice tokens (/api/invoices/public/…) too,
// through the same PublicLinkPaths rule.
app.UseMaskedRequestLogging();
app.UseCorrelationId();
app.UseApiSwaggerInDevelopment();
app.UseApiExceptionHandler();

app.UseAuthentication();
app.UseAuthorization();
app.UsePermissionAuthorizationMessages();

app.MapControllers();
app.MapApiUtilityEndpoints();

Log.Information("MerkaiTrial.WebApi started");

app.Run();

/* =====================================================================
   ENDPOINTS THAT MUST STAY [AllowAnonymous] under the FallbackPolicy
     GET  /api/quotes/public/{token}
     PUT  /api/quotes/public/{token}/status
     GET  /api/invoices/public/{token}      ← 085. The customer's invoice page.
     GET  /api/invoices/public/{token}/pdf  ← 085. Its "Download PDF" button.
     POST /api/inbound/resend              ← 083. Resend's webhook. Protected
                                             by Resend's signature (svix-*
                                             headers), not by a login.
                                             085: the SAME endpoint also takes
                                             email.delivered / delivery_delayed
                                             / bounced / complained / failed —
                                             tick those events on the Resend
                                             webhook (SETUP.md for 085).
     GET  /health

   AZURE, NOT CODE: restrict this App Service's inbound access to
   Admin.Web's outbound IP addresses. The JWT is the primary control; the
   IP restriction means an attacker cannot reach the endpoint to try.

   083 — IF YOU TURN THAT IP RESTRICTION ON, RESEND CAN NO LONGER REACH
   /api/inbound/resend and received email stops arriving (Resend holds it
   and retries, so it is delayed, not lost). Azure Access Restrictions are
   per app, not per path. Before restricting, either add a rule for the
   addresses Resend's webhooks come from (check Resend's documentation
   for the current list) or move the webhook behind Admin.Web — ask for
   that round when it comes up. SETUP.md for 083 says the same.
   ===================================================================== */
