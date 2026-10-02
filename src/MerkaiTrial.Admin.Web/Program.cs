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
//
// 060: app.UseTenantContextWarmup() added to the pipeline. One line, and
// nothing else in the app changes — see Startup/TenantContextWarmup.cs.
//
// 060: Pages/Error.cshtml NOW EXISTS. UseExceptionHandler("/Error")
// below has been pointing at a page that was never built, so in
// Production every unhandled exception re-executed to a route that
// returned 404 — the visitor got a bare 404 (or nothing) instead of an
// error page, and the original exception was swallowed on the way. The
// note at the bottom of this file called for that page; this round
// ships it.
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

// 063: was app.UseSerilogRequestLogging(). Same line, same position, but
// the public quote token is taken out of the path before it is written —
// /q/****bSxl rather than the whole thing. See Startup/RequestLogging.cs.
app.UseMaskedRequestLogging();

// UseHsts + UseHttpsRedirection + UseRouting + UseAuthentication +
// UseAuthorization, in that order. Do not call any of them again below.
app.UseMerkaiAuthPipeline();

// 060 — fills ICurrentTenantService's per-request cache asynchronously,
// so the ~28 sync getters on it (GetCurrencyCode, GetTaxLabel,
// FormatCurrency, and the six _Layout calls on every page) stop blocking
// a request thread on four database queries.
//
// MUST be after UseMerkaiAuthPipeline: there is no TenantId claim before
// UseAuthentication runs. MUST be before MapRazorPages: that is where
// the readers are. Adds nothing to anonymous requests and never throws —
// see Startup/TenantContextWarmup.cs.
app.UseTenantContextWarmup();

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
     Pages/Error.cshtml.cs      — HAS IT as of 060. Without it, an error
                                  on a signed-out request loops through
                                  the login page; and before 060 the page
                                  did not exist at all.
     The public quote page      — customers have no account

   PAGES THAT MUST SET Layout = null
     Pages/Public/QuoteView.cshtml   — does
     Pages/Error.cshtml              — does, as of 060

   Both are reachable with no TenantId claim, and _Layout.cshtml line 10
   is `var tenantName = Ui.GetTenantName();` — unguarded, where Ui is the
   injected ICurrentTenantService. GetTenantId() THROWS when the claim is
   missing, so either page rendering through _Layout would throw during
   view execution. On the error page that is fatal twice over: the
   exception handler itself would throw, and the visitor would get a
   blank 500 with the real error lost.
   ===================================================================== */
