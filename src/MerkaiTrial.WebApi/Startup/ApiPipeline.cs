// =====================================================================
// FILE: MerkaiTrial.WebApi/Startup/ApiPipeline.cs
//
// NEW FILE. The request pipeline pieces that were inline in Program.cs.
// ORDER MATTERS — Program.cs calls these in the same order as before:
//   correlation id → swagger (dev) → exception handler → auth → endpoints
//
// 105 — VUL-12
//   • Error details follow ONE switch, Diagnostics:ShowErrorDetails
//     (Application/Common/ErrorDetails.cs) — Development by default, and
//     off on the E2E servers so they answer errors the way production does.
//   • The answer to an unexpected error carries a reference (traceId, the
//     same id as X-Correlation-Id and every log line of the request), so a
//     person can quote it and the log line can be found — and nothing else.
//   • X-Correlation-Id from the caller is reused only when it is a plain
//     id (letters, digits, dashes, ≤ 64) — otherwise it would be copied
//     into the log lines and the answer as sent.
//   • 106 — UseApiSecurityHeaders: nosniff, no framing, no referrer, and a
//     content policy that lets an API answer load nothing (VUL-22).
//   • MapApiFaultProbe: GET /__e2e/fault, Development + Diagnostics:FaultProbe
//     only. See ErrorDetails.cs.
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
            // 105: only a plain id is reused (letters, digits, dashes, at
            // most 64). Anything else — a line break, a script, a page of
            // text — would be copied into every log line of the request and
            // back into the answer, so it gets a fresh id instead.
            var sent = ctx.Request.Headers["X-Correlation-Id"].ToString();
            var cid = IsPlainId(sent) ? sent : Guid.NewGuid().ToString("N");

            ctx.Items["CorrelationId"] = cid;
            ctx.Response.Headers["X-Correlation-Id"] = cid;

            using (Serilog.Context.LogContext.PushProperty("CorrelationId", cid))
                await next();
        });
        return app;
    }

    /// <summary>
    /// 106 — VUL-22. The API answers data, never pages, so the browser is told
    /// exactly that: nothing may frame it, nothing may be guessed from it,
    /// it may load nothing, and it sends no referrer. Swagger (Development
    /// only) is left without the content policy — its page needs scripts.
    /// Call FIRST, so error answers carry the headers too.
    /// </summary>
    public static WebApplication UseApiSecurityHeaders(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            ctx.Response.OnStarting(() =>
            {
                var h = ctx.Response.Headers;
                h["X-Content-Type-Options"] = "nosniff";
                h["X-Frame-Options"] = "DENY";
                h["Referrer-Policy"] = "no-referrer";
                if (!ctx.Request.Path.StartsWithSegments("/swagger") && !h.ContainsKey("Content-Security-Policy"))
                    h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
                return Task.CompletedTask;
            });
            await next();
        });
        return app;
    }

    /// <summary>105. 1–64 characters, each a letter, a digit or a dash.</summary>
    private static bool IsPlainId(string? value)
        => !string.IsNullOrEmpty(value) && value.Length <= 64
           && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>
    /// Swagger and the developer exception page — Development ONLY.
    /// Previously served unconditionally, publishing every endpoint to
    /// anyone who found the hostname.
    /// </summary>
    public static WebApplication UseApiSwaggerInDevelopment(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return app;

        // 105: the developer page only while details may be shown.
        if (ErrorDetails.Show(app.Configuration, app.Environment))
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
        var showDetails = ErrorDetails.Show(app.Configuration, app.Environment);   // 105

        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                var ex = context.Features.Get<IExceptionHandlerFeature>()?.Error;

                // 106 — a request the server itself refused (too large: 413,
                // malformed: 400) is the CALLER's mistake, not a crash: its
                // own status, a plain sentence, a warning in the log.
                if (ex is BadHttpRequestException bad)
                {
                    Log.Warning("Request refused before it reached the app: {Status} {Message}", bad.StatusCode, bad.Message);
                    context.Response.StatusCode = bad.StatusCode;
                    context.Response.ContentType = "application/problem+json";
                    await context.Response.WriteAsJsonAsync(new ProblemDetails
                    {
                        Title = bad.StatusCode == StatusCodes.Status413PayloadTooLarge
                            ? "That request is too large."
                            : "That request could not be read.",
                        Status = bad.StatusCode,
                    });
                    return;
                }

                Log.Error(ex, "Unhandled exception");

                // 105: a reference to quote, never the exception itself.
                var reference = context.Items["CorrelationId"] as string ?? context.TraceIdentifier;

                var problem = new ProblemDetails
                {
                    Title = "Something went wrong. Please try again.",
                    Status = StatusCodes.Status500InternalServerError,
                    Detail = showDetails ? ex?.ToString() : null,
                };
                problem.Extensions["traceId"] = reference;

                context.Response.StatusCode = problem.Status.Value;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(problem);
            });
        });
        return app;
    }

    /// <summary>
    /// 105. GET /__e2e/fault — throws ErrorDetails.FaultProbeMessage, so the
    /// tests can check what an unexpected error looks like from outside.
    /// Mapped ONLY in Development with Diagnostics:FaultProbe = true.
    /// </summary>
    public static WebApplication MapApiFaultProbe(this WebApplication app)
    {
        if (!ErrorDetails.FaultProbeOn(app.Configuration, app.Environment)) return app;

        app.MapGet(ErrorDetails.FaultProbePath, IResult () =>
                throw new InvalidOperationException(ErrorDetails.FaultProbeMessage))
           .AllowAnonymous()
           .ExcludeFromDescription();

        Log.Warning("Diagnostics:FaultProbe is on: {Path} throws on purpose (E2E servers only)", ErrorDetails.FaultProbePath);
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
