// =====================================================================
// Index.cshtml.cs
// Location: MerkaiTrial.Admin.Web/Pages/Settings/InboundEmail/Index.cshtml.cs
//
// NEW FILE (083). Settings → Inbound Email.
//
// What it shows:
//   • the workspace's own address, with a copy button — for the team to
//     BCC on their emails and to forward customer emails to
//   • whether receiving is set up on this server, as a checklist, so
//     "replies are not arriving" has a visible reason
//   • how much has arrived, and how much is waiting in the Inbox
//
// What it changes: only "give us a new address" (the old one stops
// working), for when the address has leaked onto a spam list.
//
// MODULE: settings, like Company Profile. Opening the page creates the
// workspace's address the first time — so it never needs a separate setup
// step, and is never created for a workspace that never looks.
//
// THE SERVER SETTINGS (domain, webhook secret, API key) ARE NOT EDITABLE
// HERE, and must never be: they are environment configuration, and the
// webhook secret in particular lives only in user-secrets / App Service
// settings. This page reports whether each is present — never its value.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.InboundEmails;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.Admin.Web.Pages.Settings.InboundEmail
{
    public class IndexModel : AuthorizedPageModel
    {
        private readonly IInboundEmailService _emails;

        public IndexModel(
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ILogger<IndexModel> logger,
            IInboundEmailService emails)
            : base(authorizationService, currentUserService, logger)
        {
            _emails = emails;
        }

        protected override string ModuleName => Modules.Settings;

        public InboundEmailSettingsDto? Settings { get; private set; }
        public string? LoadError { get; private set; }

        public async Task<IActionResult> OnGetAsync()
        {
            var denied = await ValidatePermissionAsync(Actions.Read);
            if (denied is not null) return denied;

            await InitializePermissionsAsync();

            try
            {
                Settings = await _emails.GetSettingsAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load inbound email settings");
                LoadError = "The inbound email settings could not be loaded just now. Please try again.";
            }

            return Page();
        }

        public async Task<IActionResult> OnPostNewAddressAsync()
        {
            var denied = await ValidatePermissionAsync(Actions.Update);
            if (denied is not null) return denied;

            try
            {
                var result = await _emails.NewAddressAsync();
                TempData["SuccessMessage"] = result.WorkspaceAddress is null
                    ? "A new address was created."
                    : $"Your new address is {result.WorkspaceAddress}. The old one no longer works — tell your team.";
            }
            catch (InvalidOperationException ex)
            {
                TempData["ErrorMessage"] = ex.Message;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to create a new inbound address");
                TempData["ErrorMessage"] = "A new address could not be created. Please try again.";
            }

            return RedirectToPage();
        }
    }
}
