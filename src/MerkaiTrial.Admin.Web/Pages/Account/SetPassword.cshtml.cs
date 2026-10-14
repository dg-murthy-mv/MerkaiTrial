// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Account/SetPassword.cshtml.cs
//
// CHANGES (086)
//   ✅ After a password RESET, a "your password was changed" email goes
//      to the account's address (IAccountEmails). If someone else reset it,
//      the owner finds out at once and has a button to take it back. Not
//      for an invite: setting your first password is not news to you.
//      A failure to queue that email never undoes the password change —
//      it is logged and the person still lands on the sign-in page.
//   ✅ The expired-link screen links straight to Forgot password (see the
//      .cshtml) instead of "ask your MadeeVision contact".
// =====================================================================

using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services.Notifications;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Account;

[AllowAnonymous]
public class SetPasswordModel : PageModel
{
    private readonly FlowDbContext _db;
    private readonly IUserTokenService _tokens;
    private readonly IPasswordService _passwords;
    private readonly IAccountEmails _accountEmails;       // 086
    private readonly IConfiguration _config;               // 086
    private readonly ILogger<SetPasswordModel> _logger;    // 086

    public SetPasswordModel(
        FlowDbContext db,
        IUserTokenService tokens,
        IPasswordService passwords,
        IAccountEmails accountEmails,
        IConfiguration config,
        ILogger<SetPasswordModel> logger)
    {
        _db = db; _tokens = tokens; _passwords = passwords;
        _accountEmails = accountEmails; _config = config; _logger = logger;
    }

    /// <summary>086. True for a reset link, false for an invite.</summary>
    public bool IsReset => string.Equals(Purpose, TokenPurpose.PasswordReset, StringComparison.OrdinalIgnoreCase);

    [BindProperty(SupportsGet = true)] public string Token { get; set; } = string.Empty;
    [BindProperty(SupportsGet = true)] public string Purpose { get; set; } = TokenPurpose.Invite;

    [BindProperty, Required, DataType(DataType.Password)]
    public string NewPassword { get; set; } = string.Empty;

    [BindProperty, Required, DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }
    public bool TokenValid { get; set; }
    public string? UserEmail { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var token = await _tokens.FindUsableAsync(Token, Purpose);
        if (token is null)
        {
            ErrorMessage = "This link is invalid or has expired. Please request a new one.";
            return Page();
        }

        TokenValid = true;
        UserEmail = token.User?.Email;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var token = await _tokens.FindUsableAsync(Token, Purpose);
        if (token is null)
        {
            ErrorMessage = "This link is invalid or has expired. Please request a new one.";
            return Page();
        }

        TokenValid = true;
        UserEmail = token.User?.Email;

        if (NewPassword != ConfirmPassword)
        {
            ErrorMessage = "The two passwords do not match.";
            return Page();
        }

        var (ok, error) = _passwords.ValidateStrength(NewPassword);
        if (!ok) { ErrorMessage = error; return Page(); }

        var user = await _db.Users.IgnoreQueryFilters()
          .FirstAsync(u => u.Id == token.UserId);

        user.PasswordHash = _passwords.Hash(NewPassword);
        user.SecurityStamp = Guid.NewGuid().ToString("N");  // invalidates existing sessions
        user.MustChangePassword = false;
        user.LastPasswordChangeUtc = DateTime.UtcNow;
        user.EmailConfirmedAtUtc ??= DateTime.UtcNow;  // using the emailed link proves the address
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;

        await _db.SaveChangesAsync();
        await _tokens.ConsumeAsync(token);

        // Any other outstanding link for this user stops working now.
        await _tokens.RevokeAllAsync(user.Id, token.Purpose);

        // 086 — tell the owner. Never allowed to undo the change above.
        if (token.Purpose == TokenPurpose.PasswordReset)
        {
            try
            {
                await _accountEmails.QueuePasswordChangedAsync(user.Id, AccountLinkBase.Resolve(_config, Request));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Password for user {UserId} was reset, but the confirmation email could not be queued", user.Id);
            }
        }

        return RedirectToPage("/Account/Login", new { passwordSet = true });
    }
}