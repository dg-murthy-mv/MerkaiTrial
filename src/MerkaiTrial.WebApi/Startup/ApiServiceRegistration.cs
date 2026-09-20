// =====================================================================
// FILE: MerkaiTrial.WebApi/Startup/ApiServiceRegistration.cs
//
// NEW FILE. Everything Program.cs used to register, grouped by area.
// Program.cs now calls one method per group and reads like a contents page.
//
// HANDLERS — ONE SCAN, NO LIST
//   Every class implementing ICommandHandler is registered by the Scrutor
//   scan in AddApiHandlers(). A new handler needs NO change here — this is
//   why the 16 lines for lead-status and record-visibility handlers are
//   gone: they implement ICommandHandler and the scan already found them.
//
//   HandlersOutsideTheScan lists the ones Program.cs registered by hand.
//   Some of them may not implement ICommandHandler; TryAddScoped registers
//   each ONLY if the scan didn't, so nothing is registered twice and
//   nothing is lost. When you next touch one of those classes, add
//   ": ICommandHandler" to it and delete its line here.
//
// NOTHING ELSE CHANGED. Same services, same lifetimes, same order where
// order matters (ITenantProvider before AddDbContext).
//
// The using list is copied from the old Program.cs on purpose — it is the
// set of namespaces these types are known to live in.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.UserManagement;
using MerkaiTrial.Application;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Activities;
using MerkaiTrial.Application.Commands.Leads.Import;
using MerkaiTrial.Application.Commands.LeadStatuses;
using MerkaiTrial.Application.Commands.PipelineStages;
using MerkaiTrial.Application.Commands.RecordVisibility;
using MerkaiTrial.Application.Commands.Tenants;
using MerkaiTrial.Application.Commands.Users;
using MerkaiTrial.Application.Queries;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Pdf;
using MerkaiTrial.Application.Services.Storage;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Infrastructure.Payments;
using MerkaiTrial.Infrastructure.Persistence;
using MerkaiTrial.Infrastructure.Tax;
using MerkaiTrial.Infrastructure.Tenancy;
using MerkaiTrial.WebApi.Filters;
using MerkaiTrial.WebApi.Services;
using MerkaiTrial.WebApi.SwaggerGen;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.OpenApi.Models;
using System.Text.Json.Serialization;

namespace MerkaiTrial.WebApi.Startup;

public static class ApiServiceRegistration
{
    // ── Controllers, JSON, Swagger ────────────────────────────────────

    public static IServiceCollection AddApiControllers(this IServiceCollection services)
    {
        services.AddControllers(options =>
        {
            options.Filters.Add<TrialActiveActionFilter>();
        })
        .AddJsonOptions(o =>
        {
            // Enums as strings — Admin.Web's ApiService reads them the same way.
            o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        services.AddProblemDetails();
        services.AddHttpContextAccessor();
        services.AddMemoryCache();
        return services;
    }

    /// <summary>Registration only. Exposure is Development-only — see ApiPipeline.</summary>
    public static IServiceCollection AddApiSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo { Title = "MerkaiTrial.WebApi", Version = "v1" });
            c.CustomSchemaIds(t => t.FullName!.Replace('+', '.'));
            c.OperationFilter<FileUploadOperationFilter>();
            c.ResolveConflictingActions(apiDescriptions => apiDescriptions.First());
            c.MapType<DateOnly>(() => new OpenApiSchema { Type = "string", Format = "date" });
            c.MapType<TimeOnly>(() => new OpenApiSchema { Type = "string", Format = "time" });
        });
        return services;
    }

    // ── Authorization plumbing ────────────────────────────────────────
    // (Authentication itself stays builder.AddMerkaiApiAuthentication().)

    public static IServiceCollection AddApiAuthorization(this IServiceCollection services)
    {
        // Builds any "Module.Action" policy at runtime.
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
        return services;
    }

    // ── Database + tenancy ────────────────────────────────────────────

    public static IServiceCollection AddApiPersistence(this IServiceCollection services, IConfiguration config)
    {
        var conn = config.GetConnectionString("Default")
                   ?? throw new InvalidOperationException("ConnectionStrings:Default missing.");

        // Feeds the global query filters. MUST be registered before AddDbContext.
        services.AddScoped<ITenantProvider, HttpTenantProvider>();
        services.AddDbContext<FlowDbContext>(opt => opt.UseSqlServer(conn));
        return services;
    }

    // ── Handlers ──────────────────────────────────────────────────────

    public static IServiceCollection AddApiHandlers(this IServiceCollection services)
    {
        // Every ICommandHandler in the Application assembly.
        services.Scan(scan => scan
            .FromAssemblyOf<ICommandHandler>()
            .AddClasses(classes => classes.AssignableTo<ICommandHandler>())
            .AsSelf()
            .WithScopedLifetime());

        // Registered by hand in the old Program.cs. TryAdd = only if the
        // scan above didn't already register it.
        services.TryAddScoped<GetSalesTeamHandler>();
        services.TryAddScoped<GetAuditLogsHandler>();
        services.TryAddScoped<GetAuditFiltersHandler>();
        services.TryAddScoped<GetAssigneesHandler>();
        services.TryAddScoped<SetActivityOutcomeHandler>();
        services.TryAddScoped<ReassignActivityHandler>();
        services.TryAddScoped<GetActivityAccessHandler>();
        services.TryAddScoped<GetTimelineHandler>();
        services.TryAddScoped<UploadLeadImportHandler>();
        services.TryAddScoped<PreviewLeadImportHandler>();
        services.TryAddScoped<CommitLeadImportHandler>();

        return services;
    }

    // ── Shared services the handlers depend on ────────────────────────

    public static IServiceCollection AddApiDomainServices(this IServiceCollection services)
    {
        // Who is calling
        services.AddScoped<ICurrentUserService, ApiCurrentUserService>();
        services.AddScoped<ICurrentTenantService, CurrentTenantService>();
        services.AddScoped<IRoleScope, RoleScope>();              // tenant-scoped role handlers
        services.AddScoped<IRecordScopeService, RecordScopeService>(); // Own / Team / All

        // Tenant configuration resolvers
        services.AddScoped<IStageResolver, StageResolver>();
        services.AddScoped<ILeadStatusResolver, LeadStatusResolver>();

        // Cross-cutting
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<ILeadScoringService, LeadScoringService>();
        services.AddScoped<IFileStorageService, LocalFileStorageService>();

        // Documents
        services.AddScoped<IInvoicePdfService, InvoicePdfService>();
        services.AddScoped<IQuotePdfService, QuotePdfService>();
        services.AddScoped<QuoteService>();
        services.AddScoped<InvoiceService>();

        // Lead import keeps parsed files between the upload and commit steps.
        services.AddSingleton<IImportSessionStore, ImportSessionStore>();

        services.AddSingleton<UiContext>();
        return services;
    }

    // ── Country-specific: tax + payments ──────────────────────────────

    public static IServiceCollection AddTaxAndPayments(this IServiceCollection services)
    {
        services.AddScoped<IndiaTaxCalculator>();
        services.AddScoped<ThailandTaxCalculator>();
        services.AddScoped<PhilippinesTaxCalculator>();
        services.AddScoped<ITaxCalculatorFactory, TaxCalculatorFactory>();

        // Several IPaymentProvider on purpose — the factory picks one per
        // tenant. AddScoped (not TryAdd) or only the first would register.
        services.AddScoped<IPaymentProvider, RazorpayProvider>();
        services.AddScoped<IPaymentProvider, XenditProvider>();
        services.AddScoped<IPaymentProvider, PayMongoProvider>();
        services.AddScoped<IPaymentProvider, PromptPayProvider>();
        services.AddScoped<IPaymentProviderFactory, PaymentProviderFactory>();
        return services;
    }
}
