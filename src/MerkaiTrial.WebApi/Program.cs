// =====================================================================
// FILE: MerkaiTrial.WebApi/Program.cs   — COMPLETE, corrected
//
// FIXES APPLIED
//   1. builder.AddMerkaiApiAuthentication() ADDED. Without it the app
//      THROWS ON STARTUP: UseAuthentication() needs
//      IAuthenticationSchemeProvider, which only AddAuthentication
//      registers. The Demo scheme was removed and nothing replaced it —
//      this compiles fine and dies on run.
//   2. IRoleScope registered. The role handlers live here, Scrutor wires
//      them up, and they take IRoleScope — without it every /api/roles
//      call fails with a DI resolution error.
//   3. ClaimsTransformer removed. The JWT already carries the full claim
//      set; the transformer does nothing.
//   4. Bare AddAuthorization() removed — the extension registers it with
//      the FallbackPolicy so an endpoint missing [Authorize] is closed by
//      default rather than open.
//   5. Swagger gated behind IsDevelopment(). It was published at the API
//      root: a browsable catalogue of every endpoint on a public host.
//   6. /health marked AllowAnonymous, or Azure's probe gets 401 under the
//      fallback policy and the App Service reports unhealthy.
//   7. PublicLinkService registration removed — dead code with a second,
//      unused token scheme (see below).
//   8. DevelopmentTenantContext binding removed; nothing reads it now
//      that ApiCurrentUserService is claims-based.
//
// PREREQUISITE: ApiAuthenticationSetup.cs in WebApi/Startup, and the
// NuGet package Microsoft.AspNetCore.Authentication.JwtBearer.
//
// CONFIG (App Service settings or Key Vault, NOT appsettings.json):
//   Jwt__SigningKey  = 64+ random chars, IDENTICAL to Admin.Web
//   Jwt__Issuer      = merkaitrial-admin
//   Jwt__Audience    = merkaitrial-api
// If the keys differ between the two apps, every API call returns 401 and
// the dashboard goes blank with no obvious cause.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.UserManagement;
using MerkaiTrial.Application;
using MerkaiTrial.Application.Authorization;
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
using MerkaiTrial.WebApi.Middleware;
using MerkaiTrial.WebApi.Services;
using MerkaiTrial.WebApi.Startup;
using MerkaiTrial.WebApi.SwaggerGen;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using OfficeOpenXml;
using QuestPDF.Infrastructure;
using Serilog;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();
builder.Host.UseSerilog();

// ---------------- Controllers & JSON ----------------
builder.Services.AddControllers(options =>
{
    options.Filters.Add<TrialActiveActionFilter>();
})
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });
builder.Services.AddProblemDetails();
builder.Services.AddHttpContextAccessor();

// ---------------- Swagger (registration only; exposure is gated below) ----
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "MerkaiTrial.WebApi", Version = "v1" });
    c.CustomSchemaIds(t => t.FullName!.Replace('+', '.'));
    c.OperationFilter<FileUploadOperationFilter>();
    c.ResolveConflictingActions(apiDescriptions => apiDescriptions.First());
    c.MapType<DateOnly>(() => new OpenApiSchema { Type = "string", Format = "date" });
    c.MapType<TimeOnly>(() => new OpenApiSchema { Type = "string", Format = "time" });
});

builder.Services.AddScoped<GetSalesTeamHandler>();

// ========== AUTO-REGISTER ALL HANDLERS (Scrutor) ==========
builder.Services.Scan(scan => scan
    .FromAssemblyOf<ICommandHandler>()
    .AddClasses(classes => classes.AssignableTo<ICommandHandler>())
    .AsSelf()
    .WithScopedLifetime());

Console.WriteLine("✅ Auto-registered all command handlers via Scrutor");

// ---------------- Authentication (JWT) ----------------
// MUST be present. UseAuthentication() below needs the scheme provider
// this registers, and ApiCurrentUserService reads the claims it produces.
builder.AddMerkaiApiAuthentication();

// ---------------- Authorization plumbing ----------------
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();

// ========== DATABASE ==========
var conn = builder.Configuration.GetConnectionString("Default");

// Feeds the global query filters. Register BEFORE AddDbContext.
builder.Services.AddScoped<ITenantProvider, HttpTenantProvider>();

builder.Services.AddDbContext<FlowDbContext>(opt => opt.UseSqlServer(conn));

// ========== TAX CALCULATORS ==========
builder.Services.AddScoped<IndiaTaxCalculator>();
builder.Services.AddScoped<ThailandTaxCalculator>();
builder.Services.AddScoped<PhilippinesTaxCalculator>();
builder.Services.AddScoped<ITaxCalculatorFactory, TaxCalculatorFactory>();

// ========== PAYMENT PROVIDERS ==========
builder.Services.AddScoped<IPaymentProvider, RazorpayProvider>();
builder.Services.AddScoped<IPaymentProvider, XenditProvider>();
builder.Services.AddScoped<IPaymentProvider, PayMongoProvider>();
builder.Services.AddScoped<IPaymentProvider, PromptPayProvider>();
builder.Services.AddScoped<IPaymentProviderFactory, PaymentProviderFactory>();

// ========== APP SERVICES ==========
builder.Services.AddScoped<QuoteService>();
builder.Services.AddScoped<InvoiceService>();
builder.Services.AddSingleton<UiContext>();

builder.Services.AddScoped<ICurrentUserService, ApiCurrentUserService>();
builder.Services.AddScoped<ICurrentTenantService, CurrentTenantService>();
builder.Services.AddScoped<IFileStorageService, LocalFileStorageService>();
builder.Services.AddScoped<ILeadScoringService, LeadScoringService>();
builder.Services.AddScoped<IInvoicePdfService, InvoicePdfService>();
builder.Services.AddScoped<IQuotePdfService, QuotePdfService>();
builder.Services.AddScoped<IAuditService, AuditService>();


// Required by the tenant-scoped role handlers, which run in THIS host.
builder.Services.AddScoped<IRoleScope, RoleScope>();

QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
ExcelPackage.License.SetNonCommercialPersonal("MerkaiTrial");
builder.Services.AddMemoryCache();

// =====================================================================
var app = builder.Build();

await MerkaiTrial.Infrastructure.Persistence.DatabaseWarmup
    .WarmUpAsync(app.Services, "WebApi");

app.UseSerilogRequestLogging();

app.Use(async (ctx, next) =>
{
    var cid = ctx.Request.Headers.ContainsKey("X-Correlation-Id")
        ? ctx.Request.Headers["X-Correlation-Id"].ToString()
        : Guid.NewGuid().ToString("N");
    ctx.Items["CorrelationId"] = cid;
    ctx.Response.Headers["X-Correlation-Id"] = cid;
    using (Serilog.Context.LogContext.PushProperty("CorrelationId", cid))
        await next();
});

// ---------------- Swagger — DEVELOPMENT ONLY ----------------
// Previously served unconditionally with the API root redirecting to it,
// which published a browsable list of every endpoint to anyone who found
// the hostname.
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();

    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "MerkaiTrial.WebApi v1");
        c.RoutePrefix = "swagger";
    });
}

// Global exception handler
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        var ex = feature?.Error;
        Log.Error(ex, "Unhandled exception");

        var problem = new ProblemDetails
        {
            Title = "Unexpected error",
            Status = StatusCodes.Status500InternalServerError,
            Detail = app.Environment.IsDevelopment() ? ex?.ToString() : null,
            Instance = context.TraceIdentifier
        };
        context.Response.StatusCode = problem.Status.Value;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(problem);
    });
});

app.UseAuthentication();
app.UseAuthorization();
app.UsePermissionAuthorizationMessages();

app.MapControllers();

// ---------------- Convenience endpoints ----------------
if (app.Environment.IsDevelopment())
{
    app.MapGet("/", () => Results.Redirect("/swagger")).AllowAnonymous();
}
else
{
    // Say nothing about what runs here.
    app.MapGet("/", () => Results.NotFound()).AllowAnonymous();
}

// Anonymous on purpose: Azure's health probe carries no bearer token, and
// under the FallbackPolicy it would get 401 and mark the app unhealthy.
app.MapGet("/health", () => Results.Ok(new { ok = true, now = DateTime.UtcNow }))
   .AllowAnonymous();

Console.WriteLine("✅ MerkaiTrial.WebApi started successfully");

app.Run();

/* =====================================================================
   ALSO DELETE — PublicLinkService.cs

   Its registration is removed above. The class is dead code and
   misleading: it builds HMAC tokens from tenant.PublicLinkSecret, while
   the live flow generates a different random token in
   UpdateQuoteStatusHandler and stores it in Quote.PublicLinkToken — that
   is what /q/{token} actually resolves. Two schemes, one wired up.

   It could not serve the public route anyway: ValidateAsync needs a
   tenantId the public URL does not carry, and it throws a
   NullReferenceException on any tenant whose PublicLinkSecret is null.

   =====================================================================
   ENDPOINTS THAT MUST STAY [AllowAnonymous] under the FallbackPolicy

     GET  /api/quotes/public/{token}
     PUT  /api/quotes/public/{token}/status
     GET  /health

   The two quote endpoints already have the attribute. They also need
   IgnoreQueryFilters in their handlers — see PublicQuote_Fixes.cs — or
   they return 404 and 500 respectively once filters are on.

   =====================================================================
   AZURE, NOT CODE

   Restrict this App Service's inbound access to Admin.Web's outbound IP
   addresses. The JWT is the primary control; the IP restriction means an
   attacker cannot reach the endpoint to try. Both, not either.
   ===================================================================== */
