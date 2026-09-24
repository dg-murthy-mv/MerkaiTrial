// =====================================================================
// Denied.cshtml.cs
// Location: MerkaiTrial.Admin.Web/Pages/Account/Denied.cshtml.cs
//
// COMPLETE FILE — replaces the existing one.
//
// THIS PAGE NO LONGER RENDERS ANYTHING. It redirects to /AccessDenied.
//
// WHY
//   There were two access-denied pages with two designs and two wordings
//   for the same event: this one (the cookie middleware's
//   AccessDeniedPath) and /AccessDenied (where AuthorizedPageModel
//   redirects). Only one of them was any good, and both had a broken link
//   out — this one's said asp-page="/Dashboard", which is not a page name,
//   so link generation returned null and the anchor rendered with no href
//   at all.
//
//   AuthenticationSetup now points AccessDeniedPath at /AccessDenied. This
//   page stays as a redirect rather than being deleted so an old bookmark,
//   a link in an email, or a stale AccessDeniedPath in someone's local
//   config still lands somewhere sensible instead of a 404.
//
// ReturnUrl is carried across so the page it lands on can still say what
// was refused.
//
// [AllowAnonymous] is kept: a redirect must not itself require a
// permission, or a misconfiguration turns into a loop.
// =====================================================================

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages.Account
{
    [AllowAnonymous]
    public class DeniedModel : PageModel
    {
        public IActionResult OnGet(string? returnUrl)
        {
            var safe = Url.IsLocalUrl(returnUrl) ? returnUrl : null;

            // /AccessDenied is covered by the fallback policy, so an
            // ANONYMOUS visitor on an old bookmark would be sent to Login
            // and then dumped on a page telling them they lack permission
            // for something they never asked for. Send them straight to
            // Login instead, which is what the cookie middleware would do
            // for any other page.
            if (User.Identity?.IsAuthenticated != true)
                return RedirectToPage("/Account/Login", new { ReturnUrl = safe });

            return RedirectToPage("/AccessDenied", new { returnUrl = safe });
        }
    }
}
