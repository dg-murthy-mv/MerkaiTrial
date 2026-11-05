// =====================================================================
// FILE: Pages/Admin/Team/Index.cshtml.cs
//
// NEW (118). Admin → MadeeVision Team: the super admins — MadeeVision's
// own staff, who can see and run every workspace.
//
//   Add a member      by invite; they set their own password
//   Send invite again / Send reset email / Get a reset link (not emailed)
//   Switch off / on   never yourself, never the last active super admin;
//                     switching off signs them out everywhere
//
// There is no delete: a switched-off account keeps its name on everything
// it did. Rules live in PlatformTeamService.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages.Account;
using MerkaiTrial.Application.Commands.Platform;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services.Notifications;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Team
{
    [MerkaiTrial.Admin.Web.Filters.RecordsOwnAudit]   // 119 — writes its own rows
    public class IndexModel : PageModel
    {
        private readonly PlatformTeamService _team;
        private readonly IAccountEmails _accountEmails;
        private readonly IConfiguration _config;
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(PlatformTeamService team, IAccountEmails accountEmails,
            IConfiguration config, ILogger<IndexModel> logger)
        {
            _team = team; _accountEmails = accountEmails; _config = config; _logger = logger;
        }

        [BindProperty]
        public NewMemberInput NewMember { get; set; } = new();

        public List<SupportPersonDto> Team { get; private set; } = new();
        public IssuedLinkVm? Issued { get; private set; }
        public string? FormError { get; private set; }
        public string? LoadError { get; private set; }

        public Guid MyUserId =>
            Guid.TryParse(User.FindFirst(SignInService.ClaimUserId)?.Value, out var id) ? id : Guid.Empty;

        public class NewMemberInput
        {
            public string? FirstName { get; set; }
            public string? LastName { get; set; }
            public string? Email { get; set; }
        }

        public async Task OnGetAsync() => await LoadAsync();

        public async Task<IActionResult> OnPostAddAsync()
        {
            var myTenant = Guid.TryParse(User.FindFirst(SignInService.ClaimTenantId)?.Value, out var t) ? t : Guid.Empty;
            Guid newId;
            try
            {
                newId = await _team.AddAsync(myTenant, NewMember.FirstName ?? "", NewMember.LastName,
                    NewMember.Email ?? "", Actor);
            }
            catch (InvalidOperationException ex)
            {
                FormError = ex.Message;
                await LoadAsync();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Adding a MadeeVision team member failed");
                FormError = "They could not be added. Please try again.";
                await LoadAsync();
                return Page();
            }

            ModelState.Clear();
            NewMember = new NewMemberInput();
            return await IssueAsync(newId, invite: true, email: true, lead: "Added to the MadeeVision team. ");
        }

        public Task<IActionResult> OnPostSendInviteAsync(Guid userId) => IssueAsync(userId, invite: true, email: true);

        public Task<IActionResult> OnPostCopyResetLinkAsync(Guid userId) => IssueAsync(userId, invite: false, email: false);

        public async Task<IActionResult> OnPostSendResetAsync(Guid userId)
        {
            try
            {
                _ = await _team.GetAsync(userId);
                var r = await _accountEmails.IssuePasswordResetAsync(userId, LinkBase, Actor, sendEmail: true);
                await _team.RecordLinkIssuedAsync(userId, invite: false, emailed: r.EmailQueued);
                if (r.EmailQueued)
                    TempData["SuccessMessage"] = $"Reset email sent to {r.ToAddress}. The link works once, for {AccountLinkLifetimes.Describe(r.Lifetime)}.";
                else
                    TempData["ErrorMessage"] = $"No email was sent: {r.EmailSkippedReason} Use \"Get a reset link\" instead.";
            }
            catch (InvalidOperationException ex) { TempData["ErrorMessage"] = ex.Message; }
            catch (KeyNotFoundException ex) { TempData["ErrorMessage"] = ex.Message; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reset email for team member {UserId} failed", userId);
                TempData["ErrorMessage"] = "The reset email could not be sent. Please try again.";
            }
            return RedirectToPage();
        }

        public Task<IActionResult> OnPostSwitchOffAsync(Guid userId) => SetActiveAsync(userId, false);
        public Task<IActionResult> OnPostSwitchOnAsync(Guid userId) => SetActiveAsync(userId, true);

        // ── plumbing ──────────────────────────────────────────────────

        private async Task<IActionResult> SetActiveAsync(Guid userId, bool active)
        {
            try { TempData["SuccessMessage"] = await _team.SetActiveAsync(userId, active, MyUserId); }
            catch (InvalidOperationException ex) { TempData["ErrorMessage"] = ex.Message; }
            catch (KeyNotFoundException ex) { TempData["ErrorMessage"] = ex.Message; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Switching team member {UserId} failed", userId);
                TempData["ErrorMessage"] = "That could not be saved. Please try again.";
            }
            return RedirectToPage();
        }

        private async Task<IActionResult> IssueAsync(Guid userId, bool invite, bool email, string lead = "")
        {
            try
            {
                _ = await _team.GetAsync(userId);
                var r = invite
                    ? await _accountEmails.IssueInviteAsync(userId, LinkBase, Actor, sendEmail: email)
                    : await _accountEmails.IssuePasswordResetAsync(userId, LinkBase, Actor, sendEmail: email);
                await _team.RecordLinkIssuedAsync(userId, invite, r.EmailQueued);

                Issued = new IssuedLinkVm(invite ? "invite" : "reset", r.Url, r.ToAddress, r.EmailQueued,
                    AccountLinkLifetimes.Describe(r.Lifetime));
                if (lead.Length > 0 || (email && !r.EmailQueued))
                    TempData["SuccessMessage"] = lead + (email && !r.EmailQueued
                        ? $"The email could not be sent ({r.EmailSkippedReason}) — give them the link below yourself."
                        : "The invite has been emailed; the link is below as well.");
                await LoadAsync();
                return Page();
            }
            catch (InvalidOperationException ex) { TempData["ErrorMessage"] = lead + ex.Message; }
            catch (KeyNotFoundException ex) { TempData["ErrorMessage"] = ex.Message; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A sign-in link for team member {UserId} failed", userId);
                TempData["ErrorMessage"] = lead + "The link could not be created. Please try again.";
            }
            return RedirectToPage();
        }

        private async Task LoadAsync()
        {
            try { Team = await _team.ListAsync(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Loading the MadeeVision team failed");
                LoadError = "The team could not be loaded. Please refresh the page.";
            }
        }

        private string LinkBase => AccountLinkBase.Resolve(_config, Request);

        private string Actor =>
            User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? User.Identity?.Name ?? "MadeeVision";
    }
}
