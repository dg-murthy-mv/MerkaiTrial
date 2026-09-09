using System.ComponentModel.DataAnnotations;
using MerkaiTrial.Admin.Web.Services.Tenants;
using MerkaiTrial.Application.Commands.Plans;
using MerkaiTrial.Application.Commands.Tenants;
using MerkaiTrial.Application.DTOs;
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

        public IActionResult OnGet()
        {
            var token = TempData["Provision.Token"] as string;

            // Direct navigation or a refresh — TempData is read-once, so
            // there is nothing to show and nothing to recover.
            if (string.IsNullOrEmpty(token))
            {
                TempData["Error"] = "That provisioning result is no longer available. "
                                  + "If the invite link was not copied, issue a password reset for the workspace administrator.";
                return RedirectToPage("./Index");
            }

            TenantName = TempData["Provision.TenantName"] as string ?? "";
            AdminEmail = TempData["Provision.AdminEmail"] as string ?? "";

            if (Guid.TryParse(TempData["Provision.TenantId"] as string, out var id))
                TenantId = id;

            if (DateTime.TryParse(TempData["Provision.InviteUntil"] as string, out var inv))
                InviteExpiresUtc = inv;

            if (DateTime.TryParse(TempData["Provision.TrialUntil"] as string, out var tr))
                TrialExpiresUtc = tr;

            // Built from the current request so it works on localhost and in
            // Azure without configuration.
            InviteUrl = $"{Request.Scheme}://{Request.Host}/Account/SetPassword"
                      + $"?token={Uri.EscapeDataString(token)}&purpose=Invite";

            return Page();
        }
    }
}
