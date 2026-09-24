// =====================================================================
// AccessDenied.cshtml.cs
// Location: MerkaiTrial.Admin.Web/Pages/AccessDenied.cshtml.cs
//
// COMPLETE FILE — replaces the existing one.
//
// Unchanged in substance: the page has nothing to compute. The one
// addition is ReturnUrl, so the page can say what was refused when the
// cookie middleware sends someone here — it appends ?ReturnUrl= on its
// own, and the old page threw that information away.
//
// NO [AllowAnonymous]. The cookie middleware only redirects to
// AccessDeniedPath for a request that WAS authenticated and failed a
// policy; an unauthenticated request goes to LoginPath instead. So
// everyone who reaches this page is signed in, which is also why it
// renders inside the app shell rather than the marketing layout.
// =====================================================================

using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages
{
    public class AccessDeniedModel : PageModel
    {
        /// <summary>
        /// Where the person was trying to go. Appended by the cookie
        /// middleware as ?ReturnUrl=. Displayed only when it is a local
        /// path — an absolute URL in that parameter is someone trying to
        /// get an off-site link rendered on your page.
        /// </summary>
        public string? AttemptedPath { get; private set; }

        public void OnGet(string? returnUrl)
        {
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                AttemptedPath = returnUrl;
        }
    }
}
