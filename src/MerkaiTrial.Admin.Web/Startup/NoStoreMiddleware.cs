// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Startup/NoStoreMiddleware.cs
//
// NEW (102). Every response the app itself writes — pages, JSON handlers,
// downloads — tells the browser not to keep a copy:
//
//     Cache-Control: no-store, no-cache, max-age=0
//     Pragma: no-cache
//
// WHY
//   Nothing said this before (only the error page did), so the browser was
//   free to keep signed-in pages and show them again from its cache:
//     • After a super admin's View As moved from workspace A to workspace B,
//       Back could show A's lead list — another workspace's customers —
//       without asking the server, which would have refused.
//     • After signing out on a shared computer, Back showed the last pages
//       of the person who had just left.
//   With no-store the browser asks the server every time, and the server
//   answers for whoever is signed in NOW.
//
// WHAT IT DOESN'T TOUCH
//   Static files (CSS, JavaScript, images, fonts). UseStaticFiles runs
//   before this and answers those itself, so they keep their normal caching
//   and pages load as fast as before. A response that already set its own
//   Cache-Control keeps it.
// =====================================================================

namespace MerkaiTrial.Admin.Web.Startup;

public static class NoStoreMiddleware
{
    /// <summary>Call straight after app.UseStaticFiles().</summary>
    public static IApplicationBuilder UseNoStoreForAppResponses(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                if (!headers.ContainsKey("Cache-Control"))
                {
                    headers["Cache-Control"] = "no-store, no-cache, max-age=0";
                    headers["Pragma"] = "no-cache";
                }
                return Task.CompletedTask;
            });

            await next();
        });
}
