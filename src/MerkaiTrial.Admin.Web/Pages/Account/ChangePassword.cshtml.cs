// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Account/ChangePassword.cshtml.cs
//
// The signed-in equivalent of SetPassword. SetPassword is token-based and
// anonymous (invite / reset links); this one is for a user who is already
// authenticated — either voluntarily, or because MustChangePassword is set
// and AccountStatePageFilter is holding them here.
//
// NOT [AllowAnonymous]: the caller must be signed in. It is exempted from
// the filter's redirect loop by name, not by attribute.
//
// 106 — VUL-26 / VUL-27
//   • The new password must not be built from the person's own name, email
//     or workspace name, nor be a common password (PasswordService).
//   • A wrong CURRENT password counts like a wrong password at sign-in:
//     the fifth in a row signs the person out everywhere and locks the
//     account for 15 minutes. Without this, someone at an unattended
//     signed-in computer could guess the password here without limit.
//   • Not during View As: a super admin viewing as someone may not change
//     that person's password.
// =====================================================================

using MerkaiTrial.Application.Security;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Account;

public class ChangePasswordModel : PageModel
{
    private readonly FlowDbContext _db;
    private readonly IPasswordService _passwords;
    private readonly ISignInService _signIn;

    public ChangePasswordModel(FlowDbContext db, IPasswordService passwords, ISignInService signIn)
    {
        _db = db; _passwords = passwords; _signIn = signIn;
    }

    [BindProperty, Required, DataType(DataType.Password)]
    public string CurrentPassword { get; set; } = string.Empty;

    [BindProperty, Required, DataType(DataType.Password)]
    public string NewPassword { get; set; } = string.Empty;

    [BindProperty, Required, DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    /// <summary>True when the filter sent them here rather than them choosing to come.</summary>
    public bool IsForced { get; set; }

    /// <summary>106. True during View As — the page then explains instead of offering the form.</summary>
    public bool IsViewingAs => User.HasClaim(SignInService.ClaimViewingAs, "true");

    public const string ViewingAsMessage =
        "You're viewing as this person, so you can't change their password. They can change it themselves, or use Forgot password.";

    /// <summary>106. As at sign-in: the fifth wrong password in a row locks the account for 15 minutes.</summary>
    private const int MaxWrongCurrentPasswords = 5;
    private static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);

    public void OnGet()
    {
        IsForced = User.HasClaim(SignInService.ClaimMustChangePassword, "true");
        if (IsViewingAs) ErrorMessage = ViewingAsMessage;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        IsForced = User.HasClaim(SignInService.ClaimMustChangePassword, "true");

        // 106 — never during View As, whatever the form says.
        if (IsViewingAs)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            ErrorMessage = ViewingAsMessage;
            return Page();
        }

        if (!ModelState.IsValid) return Page();

        if (NewPassword != ConfirmPassword)
        {
            ErrorMessage = "The two new passwords do not match.";
            return Page();
        }

        var userIdRaw = User.FindFirst(SignInService.ClaimUserId)?.Value;
        if (!Guid.TryParse(userIdRaw, out var userId)) return RedirectToPage("/Account/Login");

        var user = await _db.Users.IgnoreQueryFilters()
          .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted);

        if (user is null || string.IsNullOrEmpty(user.PasswordHash))
            return RedirectToPage("/Account/Login");

        // 106 — locked (by wrong passwords here or at sign-in): out you go.
        if (user.IsLockedOut())
        {
            await _signIn.SignOutAsync();
            return RedirectToPage("/Account/Login");
        }

        // Requiring the current password is what stops an unattended logged-in
        // session from being used to take the account over permanently.
        var verify = _passwords.Verify(user.PasswordHash, CurrentPassword);
        if (verify == Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed)
        {
            // 106 — counted, like a wrong password at sign-in.
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxWrongCurrentPasswords)
            {
                user.FailedLoginCount = 0;
                user.LockoutEndUtc    = DateTime.UtcNow.Add(LockFor);
                user.SecurityStamp    = Guid.NewGuid().ToString("N");   // every session of theirs ends
                await _db.SaveChangesAsync();
                await _signIn.SignOutAsync();
                return RedirectToPage("/Account/Login");
            }

            await _db.SaveChangesAsync();
            ErrorMessage = "Your current password is incorrect.";
            return Page();
        }

        var workspaceName = await _db.Tenants.AsNoTracking().IgnoreQueryFilters()
            .Where(t => t.Id == user.TenantId).Select(t => t.Name).FirstOrDefaultAsync();
        var (ok, error) = _passwords.ValidateStrength(NewPassword,
            new PasswordOwner(user.Email, user.FirstName, user.LastName, workspaceName));
        if (!ok) { ErrorMessage = error; return Page(); }

        if (CurrentPassword == NewPassword)
        {
            ErrorMessage = "Your new password must be different from your current one.";
            return Page();
        }

        user.PasswordHash          = _passwords.Hash(NewPassword);
        user.SecurityStamp         = Guid.NewGuid().ToString("N");
        user.MustChangePassword    = false;
        user.LastPasswordChangeUtc = DateTime.UtcNow;
        user.FailedLoginCount      = 0;          // 106

        await _db.SaveChangesAsync();

        // The new stamp invalidates THIS session too — including the stale
        // MustChangePassword claim, which would otherwise keep the filter
        // redirecting them back here forever. Signing out and back in is the
        // simplest correct resolution.
        await _signIn.SignOutAsync();

        return RedirectToPage("/Account/Login", new { passwordSet = true });
    }
}

