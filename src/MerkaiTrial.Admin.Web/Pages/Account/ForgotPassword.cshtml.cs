// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Account/ForgotPassword.cshtml.cs
//
// CHANGES (086 — the reset link is EMAILED)
//   Before: a token was issued and thrown away, and the page told people
//   to wait for "your MadeeVision contact" — who had no way to see it.
//   A locked-out admin could not get back in at all.
//
//   Now the link is emailed (IAccountEmails → the outbound queue → Resend),
//   and works once, for 2 hours. Issuing a new one kills the older ones.
//
//   SAFETY, all deliberate:
//     • The same "check your email" answer whether or not the address has
//       an account, is deactivated, or was rate limited — the page must
//       never tell a stranger which addresses are registered.
//     • At most 3 reset emails per account per hour. Past that, nothing is
//       sent and nothing is said; it stops anyone using this page to flood
//       somebody's inbox.
//     • The link's web address comes from configuration, not from the
//       request's Host header (AccountLinkBase — "reset poisoning").
//     • The token is never logged.
// =====================================================================

using MerkaiTrial.Application.Services.Notifications;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Account
{
    [AllowAnonymous]
    public class ForgotPasswordModel : PageModel
    {
        /// <summary>Reset emails per account per hour.</summary>
        private const int MaxPerHour = 3;

        private readonly FlowDbContext _db;
        private readonly IAccountEmails _accountEmails;
        private readonly IConfiguration _config;
        private readonly ILogger<ForgotPasswordModel> _logger;
        private readonly MerkaiTrial.Application.Security.AttemptLimiter _limiter;   // 104

        public ForgotPasswordModel(
            FlowDbContext db,
            IAccountEmails accountEmails,
            IConfiguration config,
            ILogger<ForgotPasswordModel> logger,
            MerkaiTrial.Application.Security.AttemptLimiter limiter)
        {
            _limiter = limiter;
            _db = db;
            _accountEmails = accountEmails;
            _config = config;
            _logger = logger;
        }

        [BindProperty, Required(ErrorMessage = "Enter your email address."),
         EmailAddress(ErrorMessage = "That doesn't look like an email address."), StringLength(320)]
        public string Email { get; set; } = string.Empty;

        public bool Submitted { get; set; }

        /// <summary>For the hint on the confirmation screen.</summary>
        public string LifetimeText => AccountLinkLifetimes.Describe(AccountLinkLifetimes.PasswordReset);

        public void OnGet(string? email = null)
        {
            // Pre-filled from the Login page's link when someone already typed it.
            if (!string.IsNullOrWhiteSpace(email) && email.Length <= 320)
                Email = email.Trim();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!ModelState.IsValid) return Page();

            var address = Email.Trim();

            // 104 — 10 requests an hour from one network address. Past that
            // nothing is sent, and the answer is the same "check your email"
            // as always: the page never says it was limited, so it can't be
            // used to tell which addresses have accounts.
            var network = HttpContext.Connection.RemoteIpAddress?.ToString();
            if (!_limiter.TryCount(MerkaiTrial.Application.Security.AttemptRules.ResetRequests, network))
            {
                _logger.LogWarning("Password reset requests from {Address} paused: over the hourly limit", network);
                Submitted = true;
                return Page();
            }

            try
            {
                // Users is not tenant-filtered (sign-in reads it with no
                // tenant), but say so explicitly: this page is anonymous.
                var user = await _db.Users.AsNoTracking()
                    .IgnoreQueryFilters()
                    .Where(u => u.Email == address && !u.IsDeleted && u.IsActive)
                    .Select(u => new { u.Id })
                    .FirstOrDefaultAsync();

                if (user is not null)
                {
                    var recent = await _accountEmails.CountRecentResetsAsync(user.Id, TimeSpan.FromHours(1));

                    if (recent >= MaxPerHour)
                    {
                        // Nothing sent, nothing said — see the header.
                        _logger.LogWarning(
                            "Password reset for user {UserId} not sent: {Count} already in the last hour",
                            user.Id, recent);
                    }
                    else
                    {
                        await _accountEmails.IssuePasswordResetAsync(
                            user.Id,
                            AccountLinkBase.Resolve(_config, Request),
                            issuedBy: "self-service",
                            sendEmail: true);
                    }
                }
            }
            catch (Exception ex)
            {
                // Logged, never shown: an error message here would itself
                // reveal that the address belongs to an account.
                _logger.LogError(ex, "Password reset request failed");
            }

            // Same answer whether or not the account exists.
            Submitted = true;
            return Page();
        }
    }
}
