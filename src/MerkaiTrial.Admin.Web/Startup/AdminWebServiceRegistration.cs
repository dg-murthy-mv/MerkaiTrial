// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Startup/AdminWebServiceRegistration.cs
//
// NEW FILE. Everything Admin.Web's Program.cs used to register, grouped
// by area. Same services, same lifetimes.
//
// WHY THE MODULE SERVICES ARE STILL A LIST (not a scan)
//   They are thin wrappers over IApiService. A naming-convention scan
//   ("LeadService implements ILeadService") would also find ApiService and
//   register it as a plain scoped IApiService — silently replacing the
//   typed HttpClient registration, so every call would lose the JWT
//   forwarding handler and return 401. One line per module is the safer
//   trade. Add a new module service in AddAdminWebModuleServices().
//
// WHERE TO ADD THINGS FROM NOW ON
//   New page-facing API wrapper (ILeadService style) → AddAdminWebModuleServices
//   Handler Admin.Web runs directly against the DB   → AddAdminWebDirectHandlers
//   Anything about who is signed in / tenancy        → AddAdminWebCoreServices
//
// The using list is copied from the old Program.cs on purpose — it is the
// set of namespaces these types are known to live in.
// =====================================================================

using MerkaiTrial.Admin.Web.Filters;
using MerkaiTrial.Admin.Web.Services;
using MerkaiTrial.Admin.Web.Services.Activities;
using MerkaiTrial.Admin.Web.Services.Audit;
using MerkaiTrial.Admin.Web.Services.Companies;
using MerkaiTrial.Admin.Web.Services.Contacts;
using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Admin.Web.Services.Deals;
using MerkaiTrial.Admin.Web.Services.Invoices;
using MerkaiTrial.Admin.Web.Services.Leads;
using MerkaiTrial.Admin.Web.Services.Meta;
using MerkaiTrial.Admin.Web.Services.Pipeline;
using MerkaiTrial.Admin.Web.Services.Products;
using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Admin.Web.Services.Reports;
using MerkaiTrial.Admin.Web.Services.Roles;
using MerkaiTrial.Admin.Web.Services.Security;
using MerkaiTrial.Admin.Web.Services.TaxRates;
using MerkaiTrial.Admin.Web.Services.Tenants;
using MerkaiTrial.Admin.Web.Services.UserManagement;
using MerkaiTrial.Admin.Web.Services.Users;
using MerkaiTrial.Admin.Web.Services.Verticals;
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
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

namespace MerkaiTrial.Admin.Web.Startup;

public static class AdminWebServiceRegistration
{
    // ── Razor Pages ───────────────────────────────────────────────────

    public static IServiceCollection AddAdminWebPages(this IServiceCollection services)
    {
        services.AddRazorPages(options =>
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

        services.Configure<JsonOptions>(o =>
        {
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        return services;
    }

    // ── Authorization plumbing ────────────────────────────────────────
    // (Cookie auth + policies stay builder.AddMerkaiAuthentication().)

    public static IServiceCollection AddAdminWebAuthorization(this IServiceCollection services)
    {
        // Builds any "Module.Action" policy at runtime — a new module needs no change.
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
        return services;
    }

    // ── Database + tenancy ────────────────────────────────────────────

    public static IServiceCollection AddAdminWebPersistence(this IServiceCollection services, IConfiguration config)
    {
        services.AddHttpContextAccessor();
        services.AddMemoryCache();

        // Feeds the global query filters. MUST be registered before AddDbContext.
        services.AddScoped<ITenantProvider, HttpTenantProvider>();

        services.AddDbContext<FlowDbContext>((sp, o) =>
        {
            var connStr = config.GetConnectionString("Default")
                          ?? throw new InvalidOperationException("ConnectionStrings:Default missing.");
            o.UseSqlServer(connStr);
        });

        return services;
    }

    // ── Calling the WebApi ────────────────────────────────────────────

    public static IServiceCollection AddAdminWebApiClients(this IServiceCollection services, IConfiguration config)
    {
        // Mints a short-lived signed JWT from the CURRENT signed-in user
        // for every API call. Replaced the old plain tenant-id header.
        services.AddScoped<IApiTokenService, ApiTokenService>();
        services.AddTransient<ApiTokenForwardingHandler>();
        services.AddTransient<HttpLoggingHandler>();

        var apiBaseUrl = config["WebApi:BaseUrl"] ?? config["ApiBaseUrl"] ?? "https://localhost:61091/";

        // IApiService — what every module service uses.
        services.AddHttpClient<IApiService, ApiService>(client =>
        {
            client.BaseAddress = new Uri(apiBaseUrl);
            client.Timeout = TimeSpan.FromMinutes(30);
        })
        .AddHttpMessageHandler<ApiTokenForwardingHandler>();

        // ApiClient — older typed client, still used by some pages.
        services.AddHttpClient<ApiClient>(http =>
        {
            http.BaseAddress = new Uri(apiBaseUrl);
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

        return services;
    }

    // ── Who is signed in, which tenant, what they may do ──────────────

    public static IServiceCollection AddAdminWebCoreServices(this IServiceCollection services)
    {
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddScoped<ICurrentTenantService, CurrentTenantService>();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<ITenantUiService, TenantUiService>();
        services.AddScoped<IPermissionValidator, PermissionValidator>();
        services.AddScoped<IRoleScope, RoleScope>();
        services.AddScoped<NavigationService>();

        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();
        services.AddScoped<IFileStorageService, LocalFileStorageService>();
        return services;
    }

    // ── Page-facing module services (thin wrappers over IApiService) ──

    public static IServiceCollection AddAdminWebModuleServices(this IServiceCollection services)
    {
        // Sales
        services.AddScoped<ILeadService, LeadService>();
        services.AddScoped<ILeadStatusService, LeadStatusService>();
        services.AddScoped<ILeadImportService, LeadImportService>();
        services.AddScoped<IDealService, DealService>();
        services.AddScoped<IPipelineStageService, PipelineStageService>();
        services.AddScoped<IQuoteService, QuoteService>();
        services.AddScoped<IInvoiceService, InvoiceService>();
        services.AddScoped<IActivityService, ActivityService>();

        // Customers & catalogue
        services.AddScoped<IContactService, ContactService>();
        services.AddScoped<ICompanyService, CompanyService>();
        services.AddScoped<IProductService, ProductService>();

        // Workspace settings
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IRecordVisibilityService, RecordVisibilityService>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<IMetaService, MetaService>();

        // Super admin
        services.AddScoped<ITenantService, TenantService>();
        services.AddScoped<ICountryService, CountryService>();
        services.AddScoped<ICompanyVerticalService, CompanyVerticalService>();
        services.AddScoped<ITaxRateService, TaxRateService>();

        return services;
    }

    // ── Handlers Admin.Web runs directly against the DB ───────────────
    // Most handlers run in the WebApi. These few are called in-process by
    // Admin.Web pages (plans admin, the public quote page, sales team).

    public static IServiceCollection AddAdminWebDirectHandlers(this IServiceCollection services)
    {
        services.AddScoped<GetSalesTeamHandler>();
        services.AddScoped<GetQuoteByTokenHandler>();

        // Plans (super admin)
        services.AddScoped<GetPlanByNameHandler>();
        services.AddScoped<GetPlansHandler>();
        services.AddScoped<GetPlanDetailHandler>();
        services.AddScoped<GetPublicPlansHandler>();
        services.AddScoped<GetPlanSummaryHandler>();
        services.AddScoped<CreatePlanHandler>();
        services.AddScoped<UpdatePlanHandler>();
        services.AddScoped<ChangeTenantPlanHandler>();
        services.AddScoped<DeactivatePlanHandler>();
        services.AddScoped<GetPlanTenantsHandler>();

        return services;
    }
}
