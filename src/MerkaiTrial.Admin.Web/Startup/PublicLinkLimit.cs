// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Startup/PublicLinkLimit.cs
//
// NEW FILE (106 — VUL-21). The customer links — /q/{token} (a quote) and
// /i/{token} (an invoice) — are the only pages anyone can open without
// signing in. Their secret is 40 random letters and digits, far beyond
// guessing; this makes trying anyway pointless as well.
//
//   • Every answer "not found" on /q/… or /i/… (a token that leads
//     nowhere — wrong, deleted, or a quote taken back for editing) counts
//     against the visitor's network address: AttemptRules.PublicLinkMisses,
//     20 per 10 minutes (agreed in 106).
//   • Past that, the address gets a short "too many tries" page (429) on
//     every customer link — including a genuine one — until the 10 minutes
//     are up. Other addresses, and every page behind sign-in, are not
//     affected.
//   • Covers the page, the Accept / Decline buttons and the PDF download
//     alike, because it looks at the address, not the handler.
//
// The network address is the visitor's own (UseForwardedHeaders runs
// first and only believes a proxy on this machine — see 104).
// =====================================================================

using MerkaiTrial.Application.Security;

namespace MerkaiTrial.Admin.Web.Startup;

public static class PublicLinkLimit
{
    public const string TooManyPage =
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
        "<title>Too many tries</title>" +
        "<style>body{font-family:system-ui,-apple-system,'Segoe UI',Arial,sans-serif;background:#f8fafc;color:#1e293b;" +
        "display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0;padding:16px;}" +
        ".box{max-width:440px;background:#fff;border:1px solid #e2e8f0;border-radius:12px;padding:28px;}" +
        "h1{font-size:20px;margin:0 0 8px;}p{margin:0;color:#475569;line-height:1.5;}</style></head>" +
        "<body><div class=\"box\"><h1>Too many tries</h1>" +
        "<p>Several links opened from your network didn't lead anywhere, so links are paused for a few minutes. " +
        "Please wait 10 minutes and open the link from your email again.</p></div></body></html>";

    public static IApplicationBuilder UsePublicLinkLimit(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            var isLink = path.StartsWith("/q/", StringComparison.OrdinalIgnoreCase)
                      || path.StartsWith("/i/", StringComparison.OrdinalIgnoreCase);
            if (!isLink) { await next(); return; }

            var limiter = context.RequestServices.GetRequiredService<AttemptLimiter>();
            var address = context.Connection.RemoteIpAddress?.ToString();

            if (limiter.IsOver(AttemptRules.PublicLinkMisses, address))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = "600";
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(TooManyPage);
                return;
            }

            await next();

            if (context.Response.StatusCode == StatusCodes.Status404NotFound)
            {
                limiter.Count(AttemptRules.PublicLinkMisses, address);
                if (limiter.IsOver(AttemptRules.PublicLinkMisses, address))
                    context.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("PublicLinkLimit")
                        .LogWarning("Customer links from {Address} paused: too many links that lead nowhere", address);
            }
        });
}
