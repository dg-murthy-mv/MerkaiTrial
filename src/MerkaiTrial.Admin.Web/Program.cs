// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Program.cs   — COMPLETE, restructured
//
// Same services, same pipeline, same order as before. What moved where:
//
//   Startup/AdminWebServiceRegistration.cs  every builder.Services.* line,
//                                           grouped by area
//   Startup/(existing)                      AddMerkaiAuthentication,
//                                           UseMerkaiAuthPipeline — unchanged
//
// NEW: ValidateOnBuild in EVERY environment. A page model or service whose
// dependency is not registered now stops the app at startup, naming both
// types — instead of a 500 the first time someone opens that page.
// (Development already did this by default; Production did not.)
// =====================================================================

using MerkaiTrial.Admin.Web.Startup;
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

// ---------------- Config ----------------
builder.Configuration.AddJsonFile("appsettings.tenants.json", optional: true, reloadOnChange: true);

// ---------------- Services ----------------
// Cookie auth, IPasswordService, IUserTokenService, ISignInService, the
// TenantAccess / SuperAdmin policies and the FallbackPolicy. Refuses to
// boot if demo auth is enabled outside Development/Demo.
builder.AddMerkaiAuthentication();

builder.Services
    .AddAdminWebPages()                                 // Razor Pages, /Admin guard, account-state filter
    .AddAdminWebAuthorization()                         // "Module.Action" policies
    .AddAdminWebPersistence(builder.Configuration)      // ITenantProvider + FlowDbContext
    .AddAdminWebApiClients(builder.Configuration)       // IApiService / ApiClient with JWT forwarding
    .AddAdminWebCoreServices()                          // current user/tenant, audit, provisioning
    .AddAdminWebModuleServices()                        // ILeadService, IDealService, …
    .AddAdminWebDirectHandlers();                       // plans, public quote, sales team

// =====================================================================
var app = builder.Build();

await MerkaiTrial.Infrastructure.Persistence.DatabaseWarmup
    .WarmUpAsync(app.Services, "Admin.Web");

// ---------------- Pipeline — order matters ----------------

// wwwroot only. Tenant attachments are NOT served from disk here — they
// need an authenticated endpoint.
app.UseStaticFiles();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseSerilogRequestLogging();

// UseHsts + UseHttpsRedirection + UseRouting + UseAuthentication +
// UseAuthorization, in that order. Do not call any of them again below.
app.UseMerkaiAuthPipeline();

app.MapRazorPages();

app.MapGet("/", context =>
{
    var isSuperAdmin = context.User.HasClaim("IsSuperAdmin", "true");
    context.Response.Redirect(isSuperAdmin ? "/Admin" : "/Dashboard");
    return Task.CompletedTask;
});

Log.Information("MerkaiTrial.Admin.Web started");

app.Run();

/* =====================================================================
   PAGES NEEDING [AllowAnonymous] under the FallbackPolicy
     Account/Login, SetPassword, ForgotPassword, Denied   — have it
     Pages/Error.cshtml.cs      — must have it, or an error on a signed-out
                                  request loops through the login page
     The public quote page      — customers have no account
   ===================================================================== */
