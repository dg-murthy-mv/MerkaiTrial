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
using MerkaiTrial.Application.Commands.Users;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Storage;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Infrastructure.Persistence;
using MerkaiTrial.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Serilog;
using System.Data.Common;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

// ---------------- Serilog ----------------
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();
builder.Host.UseSerilog();

var devCtx = builder.Configuration.GetSection("DevelopmentTenantContext");
builder.AddMerkaiAuthentication();

// ---------------- Services ----------------
builder.Services.AddScoped<NavigationService>();
builder.Services.AddScoped<IClaimsTransformation, ClaimsTransformer>();

builder.Services.AddAuthorization(options =>
{
    // Any authenticated tenant user
    options.AddPolicy("TenantAccess", policy =>
        policy.RequireAuthenticatedUser());

    // MadeeVision SuperAdmin — add IsSuperAdmin claim to DemoAuth for staff
    // In production this comes from JWT. For demo, we check IsTenantAdmin on
    // the hardcoded admin users, or add a separate appsettings flag.
    options.AddPolicy("SuperAdmin", policy =>
    policy.RequireClaim("IsSuperAdmin", "true")); // ← tighten to RequireClaim("IsSuperAdmin","true") post-demo

    // ✅ REMOVED: four stale module policies (Leads.Read/Create, Deals.Read/Create).
    // PermissionPolicyProvider parses ANY "module.action" policy name at runtime
    // and builds the requirement on demand, so these four were dead weight —
    // and actively misleading, since only 4 of ~28 module/action pairs were
    // listed, implying the others were unregistered. Adding a module needs NO
    // change to this file.
    //
    // Note the casing they used ("Leads.Read") differed from the requirement
    // they built (new PermissionRequirement("Leads", "read")). That mismatch is
    // the same class of bug that silently denied every non-admin role before
    // PermissionHandler was made case-insensitive.
});

builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();



// Simple Razor Pages registration (no localization)
builder.Services.AddRazorPages(options =>
{
    // Blocks non-SuperAdmin from accessing /Admin/* — returns 403
    options.Conventions.AuthorizeFolder("/Admin", "SuperAdmin");
});

// ---------------- Config / Tenancy ----------------
builder.Configuration.AddJsonFile("appsettings.tenants.json", optional: true, reloadOnChange: true);

builder.Services.AddScoped<ITenantUiService, TenantUiService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddTransient<TenantContextForwardingHandler>();
builder.Services.AddScoped<ITenantContext, TenantContext>();

// ---------------- EF Core + SQL session context ----------------


builder.Services.AddDbContext<FlowDbContext>((sp, o) =>
{
    var connStr = builder.Configuration.GetConnectionString("Default")
                 ?? throw new Exception("ConnectionStrings:Default missing.");
    o.UseSqlServer(connStr);
   
});

// ========== BASE API SERVICE ==========
var apiBaseUrl = builder.Configuration["WebApi:BaseUrl"] ?? "https://localhost:61091/";
builder.Services.AddHttpClient<IApiService, ApiService>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    client.Timeout = TimeSpan.FromMinutes(30);
})
.AddHttpMessageHandler<TenantContextForwardingHandler>();
builder.Services.Configure<MerkaiTrial.Application.Configuration.DevelopmentTenantContext>(
    builder.Configuration.GetSection(
        MerkaiTrial.Application.Configuration.DevelopmentTenantContext.SectionName));

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
    // Headers
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
    .AddHttpMessageHandler<TenantContextForwardingHandler>()
    .AddHttpMessageHandler<HttpLoggingHandler>();


// ----------------------------------------------------
var app = builder.Build();
// ── Static files (no auth needed) ────────────────────────────────
await MerkaiTrial.Infrastructure.Persistence.DatabaseWarmup
    .WarmUpAsync(app.Services, "Admin.Web");
app.UseStaticFiles();                        // wwwroot


// ── Exception handling ────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

// ── Routing FIRST ────────────────────────────────────────────────
app.UseSerilogRequestLogging();
app.UseMerkaiAuthPipeline();
app.UseRouting();


// ── Auth AFTER routing ───────────────────────────────────────────
app.UseAuthentication();
app.UseAuthorization();    // ✅ NOW between UseRouting and MapRazorPages


// ── Endpoints ────────────────────────────────────────────────────
app.MapRazorPages();

app.MapGet("/", context =>
{
    var isSuperAdmin = context.User.HasClaim("IsSuperAdmin", "true");
    context.Response.Redirect(isSuperAdmin ? "/Admin" : "/Dashboard");
    return Task.CompletedTask;
});

app.Run();

// ✅ REMOVED: a second app.Run() sat below a commented-out MapGet block.
// app.Run() blocks until shutdown, so it was unreachable code.

