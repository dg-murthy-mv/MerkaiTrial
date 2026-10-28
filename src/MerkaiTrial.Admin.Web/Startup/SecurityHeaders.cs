// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Startup/SecurityHeaders.cs
//
// NEW FILE (106 — VUL-22). What the browser is told about every answer
// from the website — pages, static files and downloads alike.
//
//   X-Frame-Options: DENY and CSP frame-ancestors 'none'
//       No other website can show these pages inside a frame. Without it a
//       page elsewhere could lay the app, invisible, under its own buttons
//       and have a signed-in person click "Delete" or "Accept" (clickjacking).
//   X-Content-Type-Options: nosniff
//       A file is what its Content-Type says; the browser never guesses
//       that a download is really a page.
//   Referrer-Policy
//       strict-origin-when-cross-origin everywhere: another site is told
//       only "https://our-host", never the path or the query.
//       no-referrer on the customer links (/q/…, /i/…): the secret is the
//       path itself, so not even that much leaves the page.
//   Permissions-Policy
//       camera, microphone, location, payment and USB off — the
//       app uses none of them, so nothing injected into a page can either.
//   Content-Security-Policy (agreed in 106)
//       Scripts, styles and fonts only from this site and the CDNs the
//       pages use (cdnjs, jsDelivr, code.jquery.com, Google Fonts); no
//       plugins (object-src 'none'); forms post only to this site; <base>
//       only this site; fetch/XHR only to this site.
//       Inline scripts are still allowed ('unsafe-inline') because 76 pages
//       have <script> blocks and 51 have onclick= handlers. Moving them out
//       (or to nonces) is its own round; until then this policy still stops
//       scripts from any other host, framing, plugins and form hijacking.
//       Images may come from any https address (a company's logo URL, email
//       pictures the person chooses to show).
//   HSTS is set by UseMerkaiAuthPipeline outside Development, as before.
//
// Headers already set by a page (the email preview's own CSP, a download's
// Content-Type) are left alone. Call FIRST in the pipeline, so even a
// static file or an error page carries them.
// =====================================================================

namespace MerkaiTrial.Admin.Web.Startup;

public static class SecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com https://cdn.jsdelivr.net https://code.jquery.com; " +
        "style-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com https://cdn.jsdelivr.net https://fonts.googleapis.com; " +
        "font-src 'self' data: https://fonts.gstatic.com https://cdn.jsdelivr.net https://cdnjs.cloudflare.com; " +
        "img-src 'self' data: blob: https:; " +
        "connect-src 'self'; " +
        "frame-src 'self' blob:; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    public const string PermissionsPolicy =
        "camera=(), microphone=(), geolocation=(), payment=(), usb=()";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var h = context.Response.Headers;
                var path = context.Request.Path.Value ?? string.Empty;
                var customerLink = path.StartsWith("/q/", StringComparison.OrdinalIgnoreCase)
                                || path.StartsWith("/i/", StringComparison.OrdinalIgnoreCase);

                h["X-Frame-Options"] = "DENY";
                h["X-Content-Type-Options"] = "nosniff";
                h["Referrer-Policy"] = customerLink ? "no-referrer" : "strict-origin-when-cross-origin";
                h["Permissions-Policy"] = PermissionsPolicy;
                h["Cross-Origin-Opener-Policy"] = "same-origin";
                if (!h.ContainsKey("Content-Security-Policy"))
                    h["Content-Security-Policy"] = ContentSecurityPolicy;
                return Task.CompletedTask;
            });

            await next();
        });
}
