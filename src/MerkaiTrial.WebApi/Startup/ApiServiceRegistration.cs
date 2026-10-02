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

// ── 066a: THE API NO LONGER REFERENCES Admin.Web ─────────────────────
//
// This file used to open with:
//
//     using MerkaiTrial.Admin.Web.Services.UserManagement;
//
// and MerkaiTrial.WebApi.csproj carried a <ProjectReference> to
// MerkaiTrial.Admin.Web to satisfy it. Both are gone. The using resolved
// nothing — it was copied wholesale from the old Program.cs along with
// the rest of this list (see the note below) and never used.
//
// WHY IT MATTERED, so nobody puts it back. Admin.Web calls the API over
// HTTP; the API is the thing being called. A reference pointing the other
// way means:
//
//   • a change to a Razor page can break the API build
//   • the API's publish output drags the whole web app with it
//   • nothing stops a controller reaching for a page model, and the
//     compiler would have allowed it
//
// The API may depend on Domain, Application and Infrastructure. It must
// not depend on a host. If something in Application is ever needed by
// both, it belongs in Application — which both hosts already reference —
// not in whichever host happened to define it first.
// ─────────────────────────────────────────────────────────────────────

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
using MerkaiTrial.Application.Services.Fiscal;
using MerkaiTrial.Application.Services.Notifications;
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
using MerkaiTrial.WebApi.Workers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
        services.AddScoped<IFiscalYearService, FiscalYearService>();   // 035 — fiscal year
        services.AddScoped<INotificationDispatcher, NotificationDispatcher>();   // 037

        // 061. The quote email to the CUSTOMER. Not the dispatcher's job —
        // that one resolves per-user notification preferences, and a
        // customer is not a user. Registered by hand because it implements
        // no ICommandHandler, so the Scrutor scan above does not see it.
        services.AddScoped<IQuoteCustomerEmail, QuoteCustomerEmail>();

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

    // ── Email + the outbound worker (038) ─────────────────────────────

    /// <summary>
    /// Outbound messaging — email, WhatsApp (045) and the worker that
    /// drains the outbox.
    ///
    /// 045: still called AddApiEmail so Program.cs needs no change, but it
    /// now registers two senders. Rename both together when you next touch
    /// Program.cs; renaming it here alone would not compile.
    ///
    /// THE API KEY IS NOT IN appsettings.json. Same rule as
    /// Jwt__SigningKey: App Service settings or Key Vault.
    ///
    ///     Email__ApiKey       re_xxxxxxxxxxxx   (Resend)
    ///     Email__FromAddress  noreply@madeevision.com
    ///     Email__FromName     Merkai CRM
    ///     Email__AppBaseUrl   https://your-admin-web-host
    ///
    /// WITH NO KEY the app still starts and still queues messages — it just
    /// registers NullEmailSender, which logs instead of sending and reports
    /// a RETRYABLE failure so nothing is lost. Add a key later and the
    /// backlog goes out. That is what makes it safe to run locally.
    /// </summary>
    public static IServiceCollection AddApiEmail(
        this IServiceCollection services, IConfiguration config)
    {
        services.Configure<EmailOptions>(config.GetSection(EmailOptions.SectionName));
        services.Configure<WhatsAppOptions>(config.GetSection(WhatsAppOptions.SectionName));
        services.Configure<OutboundMessageWorkerOptions>(
            config.GetSection(OutboundMessageWorkerOptions.SectionName));

        var options = config.GetSection(EmailOptions.SectionName).Get<EmailOptions>()
                      ?? new EmailOptions();

        if (options.IsConfigured)
        {
            // A typed client: one HttpClient, pooled connections, and the
            // timeout lives with the rest of the email configuration rather
            // than in three places.
            services.AddHttpClient<IEmailSender, ResendEmailSender>(http =>
            {
                http.BaseAddress = new Uri(options.ApiBaseUrl.TrimEnd('/') + "/");
                http.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 120));
            });
        }
        else
        {
            services.AddSingleton<IEmailSender, NullEmailSender>();
        }

        // ── 045: WhatsApp, the same shape ─────────────────────────
        //
        // THE ACCESS TOKEN IS NOT IN appsettings.json, for the same reason
        // as the Resend key and with sharper teeth — it can send from your
        // verified business number to anyone:
        //
        //     WhatsApp__AccessToken       EAAG...   (System User, permanent)
        //     WhatsApp__PhoneNumberId     1253492501190525
        //     WhatsApp__BusinessAccountId 2296342614475662
        //     WhatsApp__Enabled           true
        //
        // WITH NO TOKEN the app still starts and still queues — it
        // registers NullWhatsAppSender, which logs and reports a RETRYABLE
        // failure, so the backlog goes out once a token appears.
        //
        // The token is NOT set on the HttpClient's default headers: it is
        // added per request in the sender, so rotating it in configuration
        // does not need the pooled client recycled.
        var whatsApp = config.GetSection(WhatsAppOptions.SectionName).Get<WhatsAppOptions>()
                       ?? new WhatsAppOptions();

        if (whatsApp.IsConfigured)
        {
            services.AddHttpClient<IWhatsAppSender, CloudApiWhatsAppSender>(http =>
            {
                http.BaseAddress = new Uri(whatsApp.ApiBaseUrl.TrimEnd('/') + "/");
                http.Timeout = TimeSpan.FromSeconds(Math.Clamp(whatsApp.TimeoutSeconds, 5, 120));
            });

            // 046. Reads Meta's own view of our templates — approved,
            // pending, rejected. Its OWN client rather than sharing the
            // sender's: a slow directory read must never delay a send, and
            // the two have different timeouts for that reason.
            services.AddHttpClient<IWhatsAppTemplateDirectory, MetaWhatsAppTemplateDirectory>(http =>
            {
                http.BaseAddress = new Uri(whatsApp.ApiBaseUrl.TrimEnd('/') + "/");
                http.Timeout = TimeSpan.FromSeconds(15);
            });
        }
        else
        {
            services.AddSingleton<IWhatsAppSender, NullWhatsAppSender>();
            services.AddSingleton<IWhatsAppTemplateDirectory, NullWhatsAppTemplateDirectory>();   // 046
        }

        return services;
    }

    /// <summary>
    /// Hosted services. Registered separately from the rest so Program.cs
    /// reads as "these are the things that run on their own".
    ///
    /// The worker is a SINGLETON — it cannot take FlowDbContext or any other
    /// scoped service, and with ValidateScopes = true the app refuses to
    /// start if it tries. It creates a scope per tick instead.
    /// </summary>
    public static IServiceCollection AddApiBackgroundServices(this IServiceCollection services)
    {
        services.AddHostedService<OutboundMessageWorker>();
        return services;
    }
}
