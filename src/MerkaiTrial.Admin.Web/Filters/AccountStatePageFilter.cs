// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Filters/AccountStatePageFilter.cs
//
// Create the folder: MerkaiTrial.Admin.Web/Filters/
//
// Three rules that all sit in the same place — between "authenticated"
// and "sees the page" — so they belong in one filter rather than
// scattered through page models where one forgotten call reopens the gap.
//
//   1. A super admin browsing tenant pages directly (not via ViewAs) is
//      sent back to /Admin.
//   2. A user with MustChangePassword can only reach the change-password
//      page until they change it.
//   3. An expired trial becomes READ-ONLY: GETs work so the client can
//      review and export their data, writes are refused.
//
// WHY (1): every User row has a TenantId, including yours. Browsing to
// /Dashboard as a super admin would otherwise silently show YOUR user's
// tenant CRM — outside the ViewAs audit trail, with no "Exit View As"
// banner, and no record of which tenant's data was opened.
//
// WHY (3) IS READ-ONLY, NOT A LOCKOUT: at expiry the client still owns
// their data. Blocking sign-in means they cannot export it, which turns
// "your trial ended" into a reason not to buy.
// =====================================================================

using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MerkaiTrial.Admin.Web.Filters;

public class AccountStatePageFilter : IAsyncPageFilter
{
    private readonly ILogger<AccountStatePageFilter> _logger;

    public AccountStatePageFilter(ILogger<AccountStatePageFilter> logger) => _logger = logger;

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context)
        => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(
        PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var http = context.HttpContext;
        var user = http.User;

        // Anonymous pages (login, set password, error) are none of this
        // filter's business — and checking tenant state there would throw,
        // since there is no TenantId claim yet.
        var allowsAnonymous = context.ActionDescriptor.EndpointMetadata
            .OfType<IAllowAnonymous>().Any();

        if (allowsAnonymous || user.Identity?.IsAuthenticated != true)
        {
            await next();
            return;
        }

        var page = context.ActionDescriptor.ViewEnginePath ?? string.Empty;
        var isAdminPage   = page.StartsWith("/Admin", StringComparison.OrdinalIgnoreCase);
        var isAccountPage = page.StartsWith("/Account", StringComparison.OrdinalIgnoreCase);

        var isSuperAdmin = user.HasClaim(SignInService.ClaimIsSuperAdmin, "true");
        var isViewingAs  = user.HasClaim(SignInService.ClaimViewingAs, "true");

        // ── RULE 2: forced password change ───────────────────────────
        // Checked FIRST, so someone who must change their password is not
        // bounced around by the other rules before they can.
        //
        // Skipped during ViewAs: the flag belongs to the impersonated
        // user, and a super admin should not be sent to a change-password
        // page because the client they are previewing has a temporary
        // password.
        if (!isViewingAs
            && user.HasClaim(SignInService.ClaimMustChangePassword, "true")
            && !page.Equals("/Account/ChangePassword", StringComparison.OrdinalIgnoreCase)
            && !page.Equals("/Account/Logout", StringComparison.OrdinalIgnoreCase))
        {
            context.Result = new RedirectToPageResult("/Account/ChangePassword");
            return;
        }

        // ── RULE 1: super admins stay on the admin side ──────────────
        if (isSuperAdmin && !isViewingAs && !isAdminPage && !isAccountPage)
        {
            _logger.LogDebug(
                "Super admin {User} redirected from {Page} to /Admin — tenant pages are reachable only through View As.",
                user.FindFirst(SignInService.ClaimUserId)?.Value, page);

            context.Result = new RedirectToPageResult("/Admin/Index");
            return;
        }

        // ── RULE 3: expired trial is read-only ───────────────────────
        // Admin pages are exempt: a super admin must still be able to
        // manage and extend an expired tenant.
        if (!isAdminPage && !isAccountPage && !HttpMethods.IsGet(http.Request.Method))
        {
            var tenantCtx = http.RequestServices.GetRequiredService<ICurrentTenantService>();

            // A super admin viewing as a tenant user is still bound by the
            // tenant's state — otherwise a write during ViewAs would
            // bypass the very limit being demonstrated.
            if (!tenantCtx.IsAccountActive())
            {
                _logger.LogInformation(
                    "Write blocked on {Page}: tenant account is not active (trial expired or suspended).", page);

                context.Result = new RedirectToPageResult("/TrialExpired");
                return;
            }
        }

        await next();
    }
}

/* =====================================================================
   IF THIS DOES NOT COMPILE

   "SignInService does not contain ClaimMustChangePassword" — add to
   SignInService.cs, alongside the other claim constants:

       public const string ClaimMustChangePassword = "MustChangePassword";

   and in BuildPrincipalAsync, in the claims list:

       new(ClaimMustChangePassword, user.MustChangePassword.ToString().ToLowerInvariant()),

   =====================================================================
   PAGES THIS FILTER EXPECTS TO EXIST

     /Account/ChangePassword   — rule 2 redirects here
     /TrialExpired             — rule 3 redirects here
     /Admin/Index              — rule 1 redirects here

   If /Admin/Index does not exist, change rule 1's target to whatever your
   admin landing page is (/Admin/Plans/Index looks like the current one).
   A RedirectToPageResult pointing at a page that does not exist throws.

   =====================================================================
   REGISTRATION — already in the corrected Program.cs:

       builder.Services.AddRazorPages(options => { ... })
           .AddMvcOptions(options => options.Filters.Add<AccountStatePageFilter>());

   Once active you can no longer browse /Dashboard as yourself — use
   ViewAs. That is intended, but it will surprise you the first time.
   ===================================================================== */
