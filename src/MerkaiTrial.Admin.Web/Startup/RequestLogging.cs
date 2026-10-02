// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Startup/RequestLogging.cs
//
// NEW FILE (063). The twin of ApiPipeline.UseMaskedRequestLogging.
//
// Two thin wirings rather than one shared file because Serilog's
// extension hangs off WebApplication and each host owns its own
// pipeline. THE RULE ITSELF IS NOT DUPLICATED — both call
// PublicLinkPaths.Mask, which lives in MerkaiTrial.Application. When a
// new public link appears, it is added there once and both hosts mask it.
//
// WHAT THIS STOPS. Serilog's request-completion line prints the request
// path, and on this host that path is the customer's own link:
//
//     HTTP GET /q/9JGgqZBbZwdAXuuWnbC9OSNLEqT5xz2fyCKqbSxl responded 200
//
// That token is the whole security of the link — anyone who can read the
// log can open the quote and accept it as the customer. 061 masked the
// places our own code logged it; 062 silenced the framework loggers that
// printed whole URLs; this is the last one.
//
// IT IS MASKED, NOT DROPPED. "HTTP GET /q/****bSxl responded 200 in
// 200.6ms" still records that a customer opened their quote, how often,
// how fast, and whether it failed. Dropping the line would lose that
// entirely, and knowing whether the customer ever opened the thing is
// half of why a sales team looks at a quote at all.
// =====================================================================

using MerkaiTrial.Application.Common;
using Serilog;
using Serilog.Events;

namespace MerkaiTrial.Admin.Web.Startup;

public static class RequestLogging
{
    /// <summary>
    /// Replaces app.UseSerilogRequestLogging() in Program.cs. Keep it in
    /// the same position — before UseMerkaiAuthPipeline, so a request
    /// rejected by authentication is still logged.
    ///
    /// Supplies the SAME four properties as Serilog's default, with the
    /// path masked, so the default message template still matches and
    /// nothing else about the line changes. Needs Serilog.AspNetCore 6.1
    /// or later; this solution is on 9.0.0.
    /// </summary>
    public static WebApplication UseMaskedRequestLogging(this WebApplication app)
    {
        app.UseSerilogRequestLogging(options =>
        {
            options.GetMessageTemplateProperties =
                (ctx, path, elapsedMs, statusCode) => new[]
                {
                    new LogEventProperty("RequestMethod", new ScalarValue(ctx.Request.Method)),
                    new LogEventProperty("RequestPath",   new ScalarValue(PublicLinkPaths.Mask(path))),
                    new LogEventProperty("StatusCode",    new ScalarValue(statusCode)),
                    new LogEventProperty("Elapsed",       new ScalarValue(elapsedMs))
                };
        });

        return app;
    }
}
