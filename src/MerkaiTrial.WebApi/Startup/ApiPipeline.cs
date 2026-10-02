// =====================================================================
// FILE: MerkaiTrial.WebApi/Startup/ApiPipeline.cs
//
// NEW FILE. The request pipeline pieces that were inline in Program.cs.
// ORDER MATTERS — Program.cs calls these in the same order as before:
//   correlation id → swagger (dev) → exception handler → auth → endpoints
// =====================================================================

using MerkaiTrial.Application.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using Serilog.Events;

namespace MerkaiTrial.WebApi.Startup;

public static class ApiPipeline
{
    /// <summary>
    /// 063. UseSerilogRequestLogging, with the public quote token taken
    /// out of the path.
    ///
    /// Serilog's request-completion line prints the request path, which
    /// is the whole point of it — and for the public quote endpoints the
    /// path CONTAINS the token:
    ///
    ///   HTTP GET /api/quotes/public/9JGgqZBbZwdAXuu… responded 200 in 117ms
    ///
    /// That token is the entire security of the customer's link. 061
    /// masked the places our own code logged it; 062's MinimumLevel
    /// overrides silenced the framework loggers that printed whole URLs.
    /// This line is the last one, and it cannot simply be silenced —
    /// losing it would lose the only record that a customer opened their
    /// quote.
    ///
    /// GetMessageTemplateProperties supplies the SAME four properties as
    /// Serilog's default, with the path masked, so the default message
    /// template still matches and nothing else about the line changes.
    /// It needs Serilog.AspNetCore 6.1 or later; this solution is on
    /// 9.0.0.
    ///
    /// Replaces app.UseSerilogRequestLogging() in Program.cs. Keep it in
    /// the same position — first, before anything that can short-circuit,
    /// so a rejected request is still logged.
    /// </summary>
    public static WebApplication UseMaskedRequestLogging(this WebApplication app)
    {
        app.UseSerilogRequestLogging(options =>
        {
            options.GetMessageTemplateProperties =
                (ctx, path, elapsedMs, statusCode) => new[]
                {
                    new LogEventProperty("RequestMethod", new ScalarValue(ctx.Request.Method)),

                    // The one line that matters. PublicLinkPaths holds the
                    // rule for both hosts — add new public links there.
                    new LogEventProperty("RequestPath",   new ScalarValue(PublicLinkPaths.Mask(path))),

                    new LogEventProperty("StatusCode",    new ScalarValue(statusCode)),
                    new LogEventProperty("Elapsed",       new ScalarValue(elapsedMs))
                };
        });

        return app;
    }

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
