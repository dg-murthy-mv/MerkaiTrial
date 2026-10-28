// =====================================================================
// USER DETAIL - Backend
// Location: Pages/Admin/Users/Detail.cshtml.cs
//
// CHANGES (086 — getting people back in)
//   ✅ "Sign-in" card with the three things support actually needs:
//        Send reset email   emails a new 2-hour reset link (older ones die)
//        Copy reset link    the same link, shown ONCE on screen and NOT
//                           emailed — for when the email isn't arriving
//                           and you send it over WhatsApp or LINE
//        Send invite        for someone who has never signed in
//      The Create page has always said "send a password reset for this
//      user" — this is the first time there was anywhere to do it.
//   ✅ Every action is a POST with an antiforgery token. The page lives
//      under /Admin, which the AuthorizeFolder("/Admin", "SuperAdmin")
//      convention restricts to super admins.
//   ✅ BUG: the Assign Roles list was every workspace's roles (unscoped
//      GetLookupAsync()), so five identical "Sales Manager" rows appeared
//      and picking another workspace's one failed. Now this workspace's
//      roles only, without tenant_admin — the same rule as Create.
//   ✅ Errors are shown: the page displayed TempData["Success"] but never
//      TempData["Error"], so a failed role save looked like nothing.
// =====================================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MerkaiTrial.Admin.Web.Pages.Account;
using MerkaiTrial.Admin.Web.Services.Users;
using MerkaiTrial.Admin.Web.Services.Roles;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services.Notifications;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Users
{
    public class DetailModel : PageModel
    {
        private readonly IUserService _userService;
        private readonly IRoleService _roleService;
        private readonly IAccountEmails _accountEmails;     // 086
        private readonly IConfiguration _config;            // 086
        private readonly ILogger<DetailModel> _logger;

        public DetailModel(
            IUserService userService,
            IRoleService roleService,
            IAccountEmails accountEmails,
            IConfiguration config,
            ILogger<DetailModel> logger)
        {
            _userService = userService;
            _roleService = roleService;
            _accountEmails = accountEmails;
            _config = config;
            _logger = logger;
        }

        [BindProperty(SupportsGet = true)]
        public Guid TenantId { get; set; }

        [BindProperty(SupportsGet = true)]
        public Guid Id { get; set; }

        public UserDto User { get; set; } = null!;
        public List<RoleLookupDto> AvailableRoles { get; set; } = new();

        // ── 086: a link shown once ────────────────────────────────────
        public string? IssuedLink { get; private set; }
        public string? IssuedLinkKind { get; private set; }      // "reset" | "invite"
        public bool IssuedLinkEmailed { get; private set; }
        public string? IssuedLinkLifetime { get; private set; }

        /// <summary>Never signed in → the invite is the right tool; otherwise a reset.</summary>
        public bool NeverSignedIn => User?.LastLoginUtc is null;

        /// <summary>Email:AppBaseUrl set on Admin.Web — links then never depend on the request's host.</summary>
        public bool LinkBaseConfigured => AccountLinkBase.IsConfigured(_config);

        public string Initials
        {
            get
            {
                var f = User?.FirstName?.Trim();
                var l = User?.LastName?.Trim();
                var s = $"{(string.IsNullOrEmpty(f) ? "" : f[..1])}{(string.IsNullOrEmpty(l) ? "" : l[..1])}";
                return string.IsNullOrEmpty(s) ? "?" : s.ToUpperInvariant();
            }
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
                TempData["Error"] = "User not found.";
                return RedirectToPage("./Index", new { tenantId = TenantId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading user {UserId}", Id);
                TempData["Error"] = "Failed to load user details.";
                return RedirectToPage("./Index", new { tenantId = TenantId });
            }
        }

        public async Task<IActionResult> OnPostAssignRolesAsync(Guid tenantId, Guid userId, List<Guid> roleIds)
        {
            try
            {
                var command = new AssignUserRolesCommand(tenantId, userId, roleIds ?? new List<Guid>());
                await _userService.AssignRolesAsync(command);

                TempData["Success"] = "Roles assigned successfully!";
                return RedirectToPage(new { tenantId, id = userId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to assign roles to user {UserId}", userId);
                TempData["Error"] = "Failed to assign roles: " + MerkaiTrial.Admin.Web.Services.Core.SafeErrorText.For(ex, "they couldn't be saved just now.");   // 105
                return RedirectToPage(new { tenantId, id = userId });
            }
        }

        // =============================================================
        // 086 — SIGN-IN
        // =============================================================

        /// <summary>Emails a fresh reset link. Nothing is shown on screen.</summary>
        public async Task<IActionResult> OnPostSendResetAsync()
        {
            try
            {
                await EnsureUserInTenantAsync();

                var result = await _accountEmails.IssuePasswordResetAsync(
                    Id, AccountLinkBase.Resolve(_config, Request), ActorName, sendEmail: true);

                if (result.EmailQueued)
                    TempData["Success"] = $"Reset email sent to {result.ToAddress}. The link works once, for {AccountLinkLifetimes.Describe(result.Lifetime)}, and any older reset link has stopped working.";
                else
                    TempData["Error"] = $"No email was sent: {result.EmailSkippedReason} Use \"Copy reset link\" instead.";
            }
            catch (InvalidOperationException ex) { TempData["Error"] = ex.Message; }
            catch (KeyNotFoundException) { TempData["Error"] = "User not found."; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send a reset email for user {UserId}", Id);
                TempData["Error"] = "The reset email could not be sent. Please try again.";
            }

            return RedirectToPage(new { tenantId = TenantId, id = Id });
        }

        /// <summary>A reset link shown ONCE, not emailed. Page(), not a redirect: the raw token exists only here.</summary>
        public async Task<IActionResult> OnPostCopyResetLinkAsync()
        {
            try
            {
                await EnsureUserInTenantAsync();

                var result = await _accountEmails.IssuePasswordResetAsync(
                    Id, AccountLinkBase.Resolve(_config, Request), ActorName, sendEmail: false);

                IssuedLink = result.Url;
                IssuedLinkKind = "reset";
                IssuedLinkEmailed = false;
                IssuedLinkLifetime = AccountLinkLifetimes.Describe(result.Lifetime);

                await LoadAsync();
                return Page();
            }
            catch (InvalidOperationException ex) { TempData["Error"] = ex.Message; }
            catch (KeyNotFoundException) { TempData["Error"] = "User not found."; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to issue a reset link for user {UserId}", Id);
                TempData["Error"] = "A reset link could not be created. Please try again.";
            }

            return RedirectToPage(new { tenantId = TenantId, id = Id });
        }

        /// <summary>Emails a new invite AND shows its link once.</summary>
        public async Task<IActionResult> OnPostSendInviteAsync()
        {
            try
            {
                await EnsureUserInTenantAsync();

                var result = await _accountEmails.IssueInviteAsync(
                    Id, AccountLinkBase.Resolve(_config, Request), ActorName, sendEmail: true);

                IssuedLink = result.Url;
                IssuedLinkKind = "invite";
                IssuedLinkEmailed = result.EmailQueued;
                IssuedLinkLifetime = AccountLinkLifetimes.Describe(result.Lifetime);

                await LoadAsync();
                return Page();
            }
            catch (InvalidOperationException ex) { TempData["Error"] = ex.Message; }
            catch (KeyNotFoundException) { TempData["Error"] = "User not found."; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send an invite for user {UserId}", Id);
                TempData["Error"] = "The invite could not be sent. Please try again.";
            }

            return RedirectToPage(new { tenantId = TenantId, id = Id });
        }

        // =============================================================

        private async Task LoadAsync()
        {
            User = await _userService.GetByIdAsync(TenantId, Id);

            // This workspace's roles only, without tenant_admin (granted by
            // the checkbox on Edit) — see the header.
            AvailableRoles = (await _roleService.GetLookupAsync(TenantId))
                .Where(r => !string.Equals(r.Name, "tenant_admin", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// The user must belong to the workspace in the URL. GetByIdAsync is
        /// scoped by tenant and throws KeyNotFoundException otherwise, so a
        /// mismatched tenantId / id pair can never mail a stranger.
        /// </summary>
        private async Task EnsureUserInTenantAsync()
            => _ = await _userService.GetByIdAsync(TenantId, Id);

        private string ActorName =>
            base.User.Identity?.Name
            ?? base.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? "SuperAdmin";
    }
}
