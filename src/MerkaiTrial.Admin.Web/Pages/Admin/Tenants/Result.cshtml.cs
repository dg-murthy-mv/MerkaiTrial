// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Admin/Tenants/Result.cshtml.cs
//
// 087: the invite link now arrives READY-MADE from Create (issued and
// emailed by IAccountEmails), with whether the email was queued. This page
// no longer builds the link from the raw token and the request's Host
// header — sign-in links come from Email:AppBaseUrl since 086.
// =====================================================================

using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Tenants
{
    public class ResultModel : PageModel
    {
        public Guid TenantId { get; private set; }
        public string TenantName { get; private set; } = "";
        public string AdminEmail { get; private set; } = "";
        public string InviteUrl { get; private set; } = "";
        public DateTime? InviteExpiresUtc { get; private set; }
        public DateTime? TrialExpiresUtc { get; private set; }

        /// <summary>087. The invite email was queued for the administrator.</summary>
        public bool InviteEmailed { get; private set; }

        /// <summary>087. Why it wasn't, when it wasn't.</summary>
        public string? EmailSkippedReason { get; private set; }

        public IActionResult OnGet()
        {
            var inviteUrl = TempData["Provision.InviteUrl"] as string;

            // Direct navigation or a refresh — TempData is read-once, so
            // there is nothing to show and nothing to recover.
            if (string.IsNullOrEmpty(inviteUrl))
            {
                TempData["Error"] = "That provisioning result is no longer available. "
                                  + "If the invite didn't arrive, open the administrator in Admin → Users "
                                  + "and use Send a new invite.";
                return RedirectToPage("./Index");
            }

            InviteUrl = inviteUrl;
            InviteEmailed = TempData["Provision.Emailed"] as string == "1";
            EmailSkippedReason = TempData["Provision.EmailSkipped"] as string is { Length: > 0 } why ? why : null;

            TenantName = TempData["Provision.TenantName"] as string ?? "";
            AdminEmail = TempData["Provision.AdminEmail"] as string ?? "";

            if (Guid.TryParse(TempData["Provision.TenantId"] as string, out var id))
                TenantId = id;

            // Written with "u" (UTC, invariant). Read back the same way so
            // the time shown is UTC whatever the server's culture or zone.
            if (DateTime.TryParse(TempData["Provision.InviteUntil"] as string, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var inv))
                InviteExpiresUtc = inv;

            if (DateTime.TryParse(TempData["Provision.TrialUntil"] as string, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var tr))
                TrialExpiresUtc = tr;

            return Page();
        }
    }
}
