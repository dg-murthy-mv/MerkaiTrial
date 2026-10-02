// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Error.cshtml.cs
//
// NEW FILE (060). This page did not exist.
//
// Program.cs has had this line since before 038:
//
//     if (!app.Environment.IsDevelopment())
//         app.UseExceptionHandler("/Error");
//
// UseExceptionHandler re-executes the pipeline at that path. With no
// page there, Production answered every unhandled exception with a 404
// — and because the re-execution happened inside the handler, the
// original exception was swallowed on the way out. A visitor saw a bare
// 404; nobody saw the real error. The note at the bottom of Program.cs
// asked for this page and it was never built.
//
// THREE THINGS THIS PAGE MUST DO, each for a reason:
//
//   1. [AllowAnonymous]. There is a FallbackPolicy, so without this an
//      error on a signed-out request redirects to Login — which, if the
//      error came from the login flow, loops.
//
//   2. Layout = null (in the .cshtml). _ViewStart would otherwise give
//      it _Layout, whose line 10 is an unguarded
//      Ui.GetTenantName() — and GetTenantId() throws when the TenantId
//      claim is missing. An error page that throws while rendering is
//      worse than no error page: the visitor gets a blank 500 and the
//      real exception is gone.
//
//   3. No exception details. Not the message, not the stack, not the
//      type. This page is reachable by anyone, including a customer who
//      followed a quote link into a bad state, and a stack trace names
//      internal types, file paths and sometimes connection strings.
//      Serilog already has the full exception with the correlation id;
//      the page shows the visitor that id and nothing else.
//
// WHAT THIS PAGE DELIBERATELY DOES NOT HANDLE: status codes. There is no
// UseStatusCodePagesWithReExecute in the pipeline, so a plain 404 still
// gets the server's default page. That is a separate decision and it
// interacts with the 403 -> Account/Denied flow, so it is not changed
// here — SETUP.md explains the trade-off.
// =====================================================================

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Diagnostics;

namespace MerkaiTrial.Admin.Web.Pages
{
    [AllowAnonymous]
    // The browser must never serve this from cache. Without NoStore, a
    // back-button press can show the error page in place of a page that
    // now works perfectly well.
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [IgnoreAntiforgeryToken]
    public class ErrorModel : PageModel
    {
        private readonly ILogger<ErrorModel> _logger;

        public ErrorModel(ILogger<ErrorModel> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// What the visitor reads out to you. Activity.Current.Id when
        /// there is a trace (Serilog's correlation id rides on it),
        /// falling back to the connection/request id.
        /// </summary>
        public string? RequestId { get; private set; }

        public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

        /// <summary>
        /// True when the visitor has a session, so the page can offer
        /// "back to your dashboard" rather than "back to sign in".
        /// Read from the claim directly — nothing on this page may touch
        /// ICurrentTenantService, which would throw for a signed-out
        /// visitor and take the error page down with it.
        /// </summary>
        public bool IsSignedIn => User?.Identity?.IsAuthenticated == true;

        public void OnGet()  => Capture();
        public void OnPost() => Capture();

        private void Capture()
        {
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;

            // UseExceptionHandler puts the original exception here. This
            // is the one place that can log it with the same id the
            // visitor sees, which is what makes the id worth printing.
            var handler = HttpContext.Features.Get<IExceptionHandlerPathFeature>();

            if (handler?.Error is not null)
            {
                _logger.LogError(handler.Error,
                    "Unhandled exception on {Path} (RequestId {RequestId})",
                    handler.Path, RequestId);
            }
            else
            {
                // Reached directly — someone typed /Error, or a status
                // code re-execution was wired up later. Not an error in
                // itself, and not logged as one.
                _logger.LogInformation(
                    "Error page reached with no exception feature (RequestId {RequestId})",
                    RequestId);
            }
        }
    }
}
