// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Program.cs   — COMPLETE, corrected
//
// FIXES APPLIED
//   1. Pipeline no longer runs twice. UseMerkaiAuthPipeline() already
//      does UseHsts + UseHttpsRedirection + UseRouting + UseAuthentication
//      + UseAuthorization; the three duplicate calls after it are gone.
//      Two UseRouting() calls means endpoint matching runs twice and the
//      authorization between the first pair applies to an endpoint the
//      second pass re-resolves.
//   2. Duplicate AddAuthorization block removed. AddMerkaiAuthentication
//      registers TenantAccess, SuperAdmin AND the FallbackPolicy. Two
//      calls both apply (configure delegates accumulate), so it worked by
//      accident — authorization should be defined in one place.
//   3. ClaimsTransformer registration removed. SignInService already
//      writes TenantId, UserId, IsTenantAdmin and permissions_loaded, so
//      the transformer early-exits having done nothing on every request.
//   4. TenantContextForwardingHandler → ApiTokenForwardingHandler.
//      The old handler put the tenant id in a plain header the API
//      trusted; the new one sends a signed JWT.
//   5. IApiTokenService registered (the JWT issuer).
//   6. AccountStatePageFilter registered.
//   7. Unused devCtx variable and the DevelopmentTenantContext binding
//      removed — nothing reads them now.
//
// PREREQUISITES — comment the line out if the file is not in yet:
//   ApiTokenForwardingHandler.cs   (Services/Core)
//   AccountStatePageFilter.cs      (Filters)
//   ITenantProvider.cs             (Infrastructure/Tenancy)
// =====================================================================

using MerkaiTrial.Admin.Web.Filters;
using MerkaiTrial.Admin.Web.Services;
using MerkaiTrial.Admin.Web.Services.Activities;
using MerkaiTrial.Admin.Web.Services.Companies;
using MerkaiTrial.Admin.Web.Services.Contacts;
using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Admin.Web.Services.Deals;
using MerkaiTrial.Admin.Web.Services.Invoices;
using MerkaiTrial.Admin.Web.Services.Leads;
using MerkaiTrial.Admin.Web.Services.Meta;
using MerkaiTrial.Admin.Web.Services.Products;
using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Admin.Web.Services.Reports;
using MerkaiTrial.Admin.Web.Services.Roles;
using MerkaiTrial.Admin.Web.Services.TaxRates;
using MerkaiTrial.Admin.Web.Services.Tenants;
using MerkaiTrial.Admin.Web.Services.UserManagement;
using MerkaiTrial.Admin.Web.Services.Users;
using MerkaiTrial.Admin.Web.Services.Verticals;
using MerkaiTrial.Admin.Web.Startup;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Plans;
using MerkaiTrial.Application.Commands.Quotes;
using MerkaiTrial.Application.Commands.Tenants;
using MerkaiTrial.Application.Commands.Users;
using MerkaiTrial.Application.Queries;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Storage;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Infrastructure.Persistence;
using MerkaiTrial.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ---------------- Serilog ----------------
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();
builder.Host.UseSerilog();

// ---------------- Authentication ----------------
// Registers cookie auth, IPasswordService, IUserTokenService,
// ISignInService, both policies AND the FallbackPolicy. Also refuses to
// boot if demo auth is enabled outside Development/Demo.
builder.AddMerkaiAuthentication();

// ---------------- Authorization plumbing ----------------
// PermissionPolicyProvider builds any "module.action" policy at runtime,
// so adding a module needs no change here.
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();

// ---------------- Razor Pages ----------------
builder.Services.AddRazorPages(options =>
{
    // Blocks non-SuperAdmin from /Admin/*.
    options.Conventions.AuthorizeFolder("/Admin", "SuperAdmin");
})
.AddMvcOptions(options =>
{
    // Super admins stay on the admin side unless in ViewAs;
    // MustChangePassword is enforced; expired trials go read-only.
    options.Filters.Add<AccountStatePageFilter>();
});

// ---------------- Config / Tenancy ----------------
builder.Configuration.AddJsonFile("appsettings.tenants.json", optional: true, reloadOnChange: true);

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();

// Feeds the global query filters in FlowDbContext. Must be registered
// BEFORE AddDbContext resolves a context.
builder.Services.AddScoped<ITenantProvider, HttpTenantProvider>();

builder.Services.AddScoped<ITenantUiService, TenantUiService>();
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();

// ---------------- EF Core ----------------
builder.Services.AddDbContext<FlowDbContext>((sp, o) =>
{
    var connStr = builder.Configuration.GetConnectionString("Default")
                 ?? throw new Exception("ConnectionStrings:Default missing.");
    o.UseSqlServer(connStr);
});

// ---------------- API token forwarding ----------------
// Replaces TenantContextForwardingHandler. That handler sent the tenant
// id as a plain header the API believed; anyone could curl the API with
// any tenant id. This mints a short-lived signed JWT from the CURRENT
// authenticated principal instead.
builder.Services.AddScoped<IApiTokenService, ApiTokenService>();
builder.Services.AddTransient<ApiTokenForwardingHandler>();

// ========== BASE API SERVICE ==========
var apiBaseUrl = builder.Configuration["WebApi:BaseUrl"] ?? "https://localhost:61091/";
builder.Services.AddHttpClient<IApiService, ApiService>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    client.Timeout = TimeSpan.FromMinutes(30);
})
.AddHttpMessageHandler<ApiTokenForwardingHandler>();

builder.Services.AddScoped<ICurrentTenantService, CurrentTenantService>();
builder.Services.AddScoped<IFileStorageService, LocalFileStorageService>();

// ========== MODULAR SERVICES ==========
builder.Services.AddScoped<ILeadService, LeadService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IRoleService, RoleService>();
builder.Services.AddScoped<IDealService, DealService>();
builder.Services.AddScoped<ITenantService, TenantService>();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<IPermissionValidator, PermissionValidator>();
builder.Services.AddScoped<GetSalesTeamHandler>();
builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<IQuoteService, QuoteService>();
builder.Services.AddScoped<IInvoiceService, InvoiceService>();
builder.Services.AddScoped<IContactService, ContactService>();
builder.Services.AddScoped<ICompanyService, CompanyService>();
builder.Services.AddScoped<IActivityService, ActivityService>();
builder.Services.AddScoped<IMetaService, MetaService>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddScoped<IRoleScope, RoleScope>();
builder.Services.AddScoped<NavigationService>();

//-----Admin Module---
builder.Services.AddScoped<ICountryService, CountryService>();
builder.Services.AddScoped<ICompanyVerticalService, CompanyVerticalService>();
builder.Services.AddScoped<ITaxRateService, TaxRateService>();
builder.Services.AddScoped<GetPlanByNameHandler>();
builder.Services.AddScoped<GetPlansHandler>();
builder.Services.AddScoped<GetPlanDetailHandler>();
builder.Services.AddScoped<GetPublicPlansHandler>();
builder.Services.AddScoped<GetPlanSummaryHandler>();
builder.Services.AddScoped<CreatePlanHandler>();
builder.Services.AddScoped<UpdatePlanHandler>();
builder.Services.AddScoped<ChangeTenantPlanHandler>();
builder.Services.AddScoped<DeactivatePlanHandler>();
builder.Services.AddScoped<GetPlanTenantsHandler>();
builder.Services.AddScoped<GetQuoteByTokenHandler>();

Console.WriteLine("✅ Registered modular services (Lead, User, Role, Tenant, Product, Quote)");

// ---------------- API Client ----------------
builder.Services.AddTransient<HttpLoggingHandler>();
builder.Services.AddHttpClient<ApiClient>((sp, http) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var baseUrl = cfg["WebApi:BaseUrl"] ?? cfg["ApiBaseUrl"]
                  ?? throw new Exception("Set WebApi:BaseUrl (or ApiBaseUrl).");
    http.BaseAddress = new Uri(baseUrl);
    http.Timeout = TimeSpan.FromMinutes(5);
    http.DefaultRequestHeaders.Add("Accept", "application/json");
    http.DefaultRequestHeaders.Add("User-Agent", "MerkaiTrial-Admin-Web");
})
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        MaxConnectionsPerServer = 20,
        UseProxy = false,
        AllowAutoRedirect = false
    })
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .AddHttpMessageHandler<ApiTokenForwardingHandler>()
    .AddHttpMessageHandler<HttpLoggingHandler>();

// =====================================================================
var app = builder.Build();

await MerkaiTrial.Infrastructure.Persistence.DatabaseWarmup
    .WarmUpAsync(app.Services, "Admin.Web");

// wwwroot only. The /Uploads PhysicalFileProvider is correctly gone — it
// ran before authentication and served every tenant's attachments to
// anyone. File downloads need an authenticated endpoint (phase 1 task).
app.UseStaticFiles();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseSerilogRequestLogging();

// UseHsts + UseHttpsRedirection + UseRouting + UseAuthentication +
// UseAuthorization, in that order. Nothing else needed — calling any of
// them again below would run the pipeline twice.
app.UseMerkaiAuthPipeline();

app.MapRazorPages();

app.MapGet("/", context =>
{
    var isSuperAdmin = context.User.HasClaim("IsSuperAdmin", "true");
    context.Response.Redirect(isSuperAdmin ? "/Admin" : "/Dashboard");
    return Task.CompletedTask;
});


app.Run();

/* =====================================================================
   PAGES NEEDING [AllowAnonymous] under the FallbackPolicy

     Account/Login, Account/SetPassword, Account/ForgotPassword,
     Account/Denied           ✅ already have it
     Pages/Error.cshtml.cs    ⚠️  ADD IT — otherwise an exception on an
                                  unauthenticated request redirects the
                                  error page to login, which can itself
                                  error: a loop exactly when you need to
                                  read the error.
     The public quote page    ⚠️  CHECK IT — customers have no account.

   Find the rest by browsing the app signed out.
   ===================================================================== */
