// =====================================================================
// FILE: MerkaiTrial.WebApi/Startup/ApiLimits.cs
//
// NEW FILE (106 — VUL-23). How big a request may be.
//
//   Ordinary requests: 2 MB (agreed in 106). The biggest real one — a
//   quote with a hundred lines, or an email with a long message — is a few
//   tens of kilobytes.
//
//   Uploads keep their own limit: [RequestSizeLimit(10 MB)] on the
//   attachment and import actions, and the Resend webhook its own. MVC
//   applies those when the action is chosen, after this has set 2 MB, and
//   the later setting wins — the body has not been read yet.
//
//   A bigger body is refused (413) as soon as it goes past the limit,
//   instead of being read into memory first.
//
// Set per request through IHttpMaxRequestBodySizeFeature rather than in
// Kestrel's options, so it holds whichever web server runs the app (Kestrel
// or IIS in-process on Azure App Service).
// =====================================================================

using Microsoft.AspNetCore.Http.Features;

namespace MerkaiTrial.WebApi.Startup;

public static class ApiLimits
{
    public const long MaxRequestBytes = 2 * 1024 * 1024;

    public static WebApplication UseRequestSizeCap(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            var feature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false } && (feature.MaxRequestBodySize is null || feature.MaxRequestBodySize > MaxRequestBytes))
                feature.MaxRequestBodySize = MaxRequestBytes;
            await next();
        });
        return app;
    }
}
