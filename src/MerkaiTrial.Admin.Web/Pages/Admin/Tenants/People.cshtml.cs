// =====================================================================
// FILE: Pages/Admin/Tenants/People.cshtml.cs
//
// NEW (118). Admin → Tenants → a workspace → People. What MadeeVision
// support may do for the people in a customer's workspace — and only that.
// Adding, editing and removing people is the workspace admin's job
// (Settings → Users); the old Admin → Users pages that did it from here
// are gone.
//
//   Send invite         someone who has never signed in (emailed + shown once)
//   Send reset email    a new 2-hour reset link, emailed
//   Copy reset link     the same, NOT emailed, shown once (WhatsApp / LINE)
//   Unlock              locked out by wrong passwords
//   Make admin / Remove admin / Switch off / Switch on
//                       never leaves the workspace without an active admin
//   Add a new admin     by invite, for when the admin has left and nobody
//                       else can take over
//
// Everything is written to the WORKSPACE's Activity Log ("… by MadeeVision
// support"), so the customer can see it. Rules live in
// WorkspacePeopleService; this page only shows them.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages.Account;
using MerkaiTrial.Application.Commands.Platform;
using MerkaiTrial.Application.Services.Notifications;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Tenants
{
    [MerkaiTrial.Admin.Web.Filters.RecordsOwnAudit]   // 119 — writes its own rows into the workspace's log
    public class PeopleModel : PageModel
    {
        private readonly WorkspacePeopleService _people;
        private readonly IAccountEmails _accountEmails;
        private readonly IConfiguration _config;
        private readonly ILogger<PeopleModel> _logger;

        public PeopleModel(WorkspacePeopleService people, IAccountEmails accountEmails,
            IConfiguration config, ILogger<PeopleModel> logger)
        {
            _people = people; _accountEmails = accountEmails; _config = config; _logger = logger;
        }

        [BindProperty(SupportsGet = true)]
        public Guid Id { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? Search { get; set; }

        [BindProperty]
        public NewAdminInput NewAdmin { get; set; } = new();

        public WorkspacePeopleDto Data { get; private set; } = new();
        public IssuedLinkVm? Issued { get; private set; }
        public string? FormError { get; private set; }
        public bool ShowAddAdmin { get; private set; }

        /// <summary>The super admin's own workspace — its people are on the Team page.</summary>
        public bool IsOwnWorkspace =>
            Guid.TryParse(User.FindFirst(MerkaiTrial.Application.Security.SignInService.ClaimTenantId)?.Value, out var t) && t == Id;

        public class NewAdminInput
        {
            public string? FirstName { get; set; }
            public string? LastName { get; set; }
            public string? Email { get; set; }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                await LoadAsync();
                return Page();
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "That workspace does not exist.";
                return RedirectToPage("./Index");
            }
        }

        // ── Simple changes: done, then back to the list with a message ──

        public Task<IActionResult> OnPostUnlockAsync(Guid userId) =>
            ChangeAsync(() => _people.UnlockAsync(Id, userId));

        public Task<IActionResult> OnPostMakeAdminAsync(Guid userId) =>
            ChangeAsync(() => _people.MakeAdminAsync(Id, userId));

        public Task<IActionResult> OnPostRemoveAdminAsync(Guid userId) =>
            ChangeAsync(() => _people.RemoveAdminAsync(Id, userId));

        public Task<IActionResult> OnPostSwitchOffAsync(Guid userId) =>
            ChangeAsync(() => _people.SetActiveAsync(Id, userId, false));

        public Task<IActionResult> OnPostSwitchOnAsync(Guid userId) =>
            ChangeAsync(() => _people.SetActiveAsync(Id, userId, true));

        // ── Sign-in links ─────────────────────────────────────────────

        /// <summary>Emails a fresh reset link. Nothing is shown on screen.</summary>
        public async Task<IActionResult> OnPostSendResetAsync(Guid userId)
        {
            try
            {
                var person = await _people.GetAsync(Id, userId);
                var r = await _accountEmails.IssuePasswordResetAsync(userId, LinkBase, Actor, sendEmail: true);
                await _people.RecordLinkIssuedAsync(Id, userId, person.Name, invite: false, emailed: r.EmailQueued);

                if (r.EmailQueued)
                    TempData["SuccessMessage"] = $"Reset email sent to {r.ToAddress}. The link works once, for {AccountLinkLifetimes.Describe(r.Lifetime)}, and any older reset link has stopped working.";
                else
                    TempData["ErrorMessage"] = $"No email was sent: {r.EmailSkippedReason} Use \"Copy reset link\" instead.";
            }
            catch (InvalidOperationException ex) { TempData["ErrorMessage"] = ex.Message; }
            catch (KeyNotFoundException ex) { TempData["ErrorMessage"] = ex.Message; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Support: reset email for user {UserId} in workspace {TenantId} failed", userId, Id);
                TempData["ErrorMessage"] = "The reset email could not be sent. Please try again.";
            }
            return Back();
        }

        /// <summary>A reset link shown ONCE, not emailed. Page(), not a redirect: the raw token exists only here.</summary>
        public Task<IActionResult> OnPostCopyResetLinkAsync(Guid userId) => IssueAsync(userId, invite: false, email: false);

        /// <summary>Emails a new invite AND shows its link once.</summary>
        public Task<IActionResult> OnPostSendInviteAsync(Guid userId) => IssueAsync(userId, invite: true, email: true);

        // ── A new workspace admin ─────────────────────────────────────

        public async Task<IActionResult> OnPostAddAdminAsync()
        {
            Guid newId;
            try
            {
                newId = await _people.AddAdminAsync(Id, NewAdmin.FirstName ?? "", NewAdmin.LastName,
                    NewAdmin.Email ?? "", Actor);
            }
            catch (InvalidOperationException ex)
            {
                FormError = ex.Message;
                ShowAddAdmin = true;
                return await ReloadAsync();
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "That workspace does not exist.";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Support: adding an admin to workspace {TenantId} failed", Id);
                FormError = "The new admin could not be added. Please try again.";
                ShowAddAdmin = true;
                return await ReloadAsync();
            }

            ModelState.Clear();
            NewAdmin = new NewAdminInput();
            return await IssueAsync(newId, invite: true, email: true,
                lead: "Added as a workspace admin. ");
        }

        // ── plumbing ──────────────────────────────────────────────────

        private async Task<IActionResult> IssueAsync(Guid userId, bool invite, bool email, string lead = "")
        {
            try
            {
                var person = await _people.GetAsync(Id, userId);
                var r = invite
                    ? await _accountEmails.IssueInviteAsync(userId, LinkBase, Actor, sendEmail: email)
                    : await _accountEmails.IssuePasswordResetAsync(userId, LinkBase, Actor, sendEmail: email);
                await _people.RecordLinkIssuedAsync(Id, userId, person.Name, invite, r.EmailQueued);

                Issued = new IssuedLinkVm(invite ? "invite" : "reset", r.Url, r.ToAddress, r.EmailQueued,
                    AccountLinkLifetimes.Describe(r.Lifetime));
                if (lead.Length > 0 || (email && !r.EmailQueued))
                    TempData["SuccessMessage"] = lead + (email && !r.EmailQueued
                        ? $"The email could not be sent ({r.EmailSkippedReason}) — give them the link below yourself."
                        : "The invite has been emailed; the link is below as well.");
                return await ReloadAsync();
            }
            catch (InvalidOperationException ex) { TempData["ErrorMessage"] = lead + ex.Message; }
            catch (KeyNotFoundException ex) { TempData["ErrorMessage"] = ex.Message; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Support: a sign-in link for user {UserId} in workspace {TenantId} failed", userId, Id);
                TempData["ErrorMessage"] = lead + "The link could not be created. Please try again.";
            }
            return Back();
        }

        private async Task<IActionResult> ChangeAsync(Func<Task<string>> change)
        {
            try { TempData["SuccessMessage"] = await change(); }
            catch (InvalidOperationException ex) { TempData["ErrorMessage"] = ex.Message; }
            catch (KeyNotFoundException ex) { TempData["ErrorMessage"] = ex.Message; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Support: a change in workspace {TenantId} failed", Id);
                TempData["ErrorMessage"] = "That could not be saved. Please try again.";
            }
            return Back();
        }

        private async Task<IActionResult> ReloadAsync()
        {
            try { await LoadAsync(); return Page(); }
            catch (KeyNotFoundException) { return RedirectToPage("./Index"); }
        }

        private async Task LoadAsync() => Data = await _people.ListAsync(Id, Search);

        private IActionResult Back() => RedirectToPage(new { id = Id, search = string.IsNullOrWhiteSpace(Search) ? null : Search });

        private string LinkBase => AccountLinkBase.Resolve(_config, Request);

        private string Actor =>
            User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? User.Identity?.Name ?? "MadeeVision support";
    }
}
