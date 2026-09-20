// =====================================================================
// FILE: MerkaiTrial.WebApi/Startup/ApiPipeline.cs
//
// NEW FILE. The request pipeline pieces that were inline in Program.cs.
// ORDER MATTERS — Program.cs calls these in the same order as before:
//   correlation id → swagger (dev) → exception handler → auth → endpoints
// =====================================================================

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace MerkaiTrial.WebApi.Startup;

public static class ApiPipeline
{
    /// <summary>
    /// Reuses the caller's X-Correlation-Id or makes one, echoes it on the
    /// response, and stamps every log line of the request with it.
    /// </summary>
    public static WebApplication UseCorrelationId(this WebApplication app)
    {
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
        return app;
    }

    /// <summary>
    /// Swagger and the developer exception page — Development ONLY.
    /// Previously served unconditionally, publishing every endpoint to
    /// anyone who found the hostname.
    /// </summary>
    public static WebApplication UseApiSwaggerInDevelopment(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return app;

        app.UseDeveloperExceptionPage();
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "MerkaiTrial.WebApi v1");
            c.RoutePrefix = "swagger";
        });
        return app;
    }

    /// <summary>Unhandled exceptions → ProblemDetails. Stack trace in Development only.</summary>
    public static WebApplication UseApiExceptionHandler(this WebApplication app)
    {
        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                var ex = context.Features.Get<IExceptionHandlerFeature>()?.Error;
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
        return app;
    }

    /// <summary>"/" and "/health". Both anonymous on purpose.</summary>
    public static WebApplication MapApiUtilityEndpoints(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
            app.MapGet("/", () => Results.Redirect("/swagger")).AllowAnonymous();
        else
            app.MapGet("/", () => Results.NotFound()).AllowAnonymous();   // say nothing about what runs here

        // Azure's health probe carries no bearer token; under the
        // FallbackPolicy it would get 401 and mark the app unhealthy.
        app.MapGet("/health", () => Results.Ok(new { ok = true, now = DateTime.UtcNow }))
           .AllowAnonymous();

        return app;
    }
}
