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
    private readonly AttemptLimiter _limiter;              // 104

    public SetPasswordModel(
        FlowDbContext db,
        IUserTokenService tokens,
        IPasswordService passwords,
        IAccountEmails accountEmails,
        IConfiguration config,
        ILogger<SetPasswordModel> logger,
        AttemptLimiter limiter)
    {
        _limiter = limiter;
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

    /// <summary>104 — what a refused link says. The same words whatever the reason, so the page never tells a guesser which part was wrong.</summary>
    private const string BadLink = "This link is invalid or has expired. Please request a new one.";

    private string? Address => HttpContext.Connection.RemoteIpAddress?.ToString();

    /// <summary>
    /// 104 — 10 links that don't work per 15 minutes from one network
    /// address, then a pause. A link is 32 random bytes, so guessing one is
    /// hopeless anyway; this stops anyone trying at scale.
    /// </summary>
    private bool TooManyBadLinks()
    {
        if (!_limiter.IsOver(AttemptRules.SetPasswordFails, Address)) return false;
        Response.StatusCode = StatusCodes.Status429TooManyRequests;
        ErrorMessage = "Too many attempts from your network with links that don't work. " +
                       "Please wait 15 minutes, or request a new link.";
        return true;
    }

    /// <summary>A usable link for a person who can still use it (104: not deleted, not switched off).</summary>
    private async Task<UserToken?> FindLinkAsync()
    {
        var token = await _tokens.FindUsableAsync(Token, Purpose);
        if (token is null || token.User is null || token.User.IsDeleted || !token.User.IsActive)
        {
            _limiter.Count(AttemptRules.SetPasswordFails, Address);
            return null;
        }
        return token;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (TooManyBadLinks()) return Page();

        var token = await FindLinkAsync();
        if (token is null)
        {
            ErrorMessage = BadLink;
            return Page();
        }

        TokenValid = true;
        UserEmail = token.User?.Email;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (TooManyBadLinks()) return Page();

        var token = await FindLinkAsync();
        if (token is null)
        {
            ErrorMessage = BadLink;
            return Page();
        }

        TokenValid = true;
        UserEmail = token.User?.Email;

        if (NewPassword != ConfirmPassword)
        {
            ErrorMessage = "The two passwords do not match.";
            return Page();
        }

        // 106 — not built from the person's own name, email or workspace.
        var workspaceName = token.User is null ? null : await _db.Tenants.AsNoTracking().IgnoreQueryFilters()
            .Where(t => t.Id == token.User.TenantId).Select(t => t.Name).FirstOrDefaultAsync();
        var (ok, error) = _passwords.ValidateStrength(NewPassword, token.User is null ? null
            : new PasswordOwner(token.User.Email, token.User.FirstName, token.User.LastName, workspaceName));
        if (!ok) { ErrorMessage = error; return Page(); }

        // 104 — the link is used up FIRST, in one statement, and only the
        // request that wins goes on to set a password. Before, it was marked
        // used after the save, so the same link sent twice at the same moment
        // set the password twice (the second one won). The password change
        // and the link's use are one transaction: either both happen or
        // neither does, and the link can be tried again.
        await using var tx = await _db.Database.BeginTransactionAsync();
        if (!await _tokens.TryConsumeAsync(token))
        {
            await tx.RollbackAsync();
            TokenValid = false;
            ErrorMessage = BadLink;
            return Page();
        }

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

        // Any other outstanding link for this user stops working now.
        await _tokens.RevokeAllAsync(user.Id, token.Purpose);
        await tx.CommitAsync();

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