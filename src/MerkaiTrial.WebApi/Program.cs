using MerkaiTrial.Admin.Web.Services.UserManagement;
using MerkaiTrial.Application;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Users;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Pdf;
using MerkaiTrial.Application.Services.Storage;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Infrastructure.Payments;
using MerkaiTrial.Infrastructure.Persistence;
using MerkaiTrial.Infrastructure.Tax;
using MerkaiTrial.WebApi.Middleware;
using MerkaiTrial.WebApi.Services;
using MerkaiTrial.WebApi.SwaggerGen;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Serilog;
using System.Text.Json.Serialization;
using QuestPDF.Infrastructure;
using OfficeOpenXml;
var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();
builder.Host.UseSerilog();

// Controllers & JSON options
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });
builder.Services.AddProblemDetails();
builder.Services.AddHttpContextAccessor();
// Swagger/OpenAPI
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
// ✅ FIX 2: Read from correct config path DevelopmentTenantContext:UserId / TenantId
// Previous keys "DemoUserId" / "DemoTenantId" don't exist in appsettings →
// both were empty string → Demo auth produced anonymous identity →
// ClaimsTransformer fired on every request with unauthenticated principal


// ========== DATABASE ==========
var conn = builder.Configuration.GetConnectionString("Default");
builder.Services.AddDbContext<FlowDbContext>(opt => opt.UseSqlServer(conn));

builder.Services.AddScoped<IClaimsTransformation, ClaimsTransformer>();
builder.Services.AddAuthorization();

builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();


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
builder.Services.AddScoped<PublicLinkService>();
builder.Services.AddSingleton<UiContext>();
// ✅ FIX 1: Bind DevelopmentTenantContext from appsettings so IOptions<DevelopmentTenantContext>
// is populated. Without this, _devCtx.Value.Enabled is always false (class default)
// and CurrentTenantService falls through to the JWT check → throws on every request.
builder.Services.Configure<MerkaiTrial.Application.Configuration.DevelopmentTenantContext>(
    builder.Configuration.GetSection(
        MerkaiTrial.Application.Configuration.DevelopmentTenantContext.SectionName));

builder.Services.AddScoped<ICurrentUserService, ApiCurrentUserService>();
builder.Services.AddScoped<ICurrentTenantService, CurrentTenantService>();
builder.Services.AddScoped<IFileStorageService, LocalFileStorageService>();
builder.Services.AddScoped<ILeadScoringService, LeadScoringService>();
builder.Services.AddScoped<IInvoicePdfService, InvoicePdfService>();
builder.Services.AddScoped<IQuotePdfService, QuotePdfService>();
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
ExcelPackage.License.SetNonCommercialPersonal("MerkaiTrial");
builder.Services.AddMemoryCache();
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

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "MerkaiTrial.WebApi v1");
    c.RoutePrefix = "swagger";
});

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

app.UseAuthentication();  // ✅ First
app.UseAuthorization();   // ✅ Second
app.UsePermissionAuthorizationMessages();
app.MapControllers();

// Convenience endpoints
app.MapGet("/", () => Results.Redirect("/swagger"));
app.MapGet("/health", () => Results.Ok(new { ok = true, now = DateTime.UtcNow }));

Console.WriteLine("✅ MerkaiTrial.WebApi started successfully");

app.Run();
