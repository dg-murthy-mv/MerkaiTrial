using MerkaiTrial.Application.Security;
using MerkaiTrial.Domain.Entities;
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
        private readonly FlowDbContext _db;
        private readonly IUserTokenService _tokens;
        private readonly ILogger<ForgotPasswordModel> _logger;

        public ForgotPasswordModel(FlowDbContext db, IUserTokenService tokens,
                                   ILogger<ForgotPasswordModel> logger)
        {
            _db = db; _tokens = tokens; _logger = logger;
        }

        [BindProperty, Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        public bool Submitted { get; set; }

        public void OnGet() { }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!ModelState.IsValid) return Page();

            var user = await _db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Email == Email.Trim() && !u.IsDeleted && u.IsActive);

            if (user is not null)
            {
                // Issued and logged, not sent. Retrieve it from the SuperAdmin
                // screen and pass it to the client through whatever channel you
                // already use with them.
                var raw = await _tokens.IssueAsync(
                    user.Id, user.TenantId, TokenPurpose.PasswordReset,
                    TimeSpan.FromHours(72), "self-service");

                _logger.LogWarning(
                    "PASSWORD RESET REQUESTED for user {UserId} in tenant {TenantId}. " +
                    "No SMTP configured — issue the link manually from /Admin/Users. Token id logged only.",
                    user.Id, user.TenantId);

                _ = raw; // deliberately not logged: logging it would put a live credential in the log file
            }

            // Same response whether or not the account exists.
            Submitted = true;
            return Page();
        }
    }
}
