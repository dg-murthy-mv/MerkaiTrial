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

    public void OnGet()
        => IsForced = User.HasClaim(SignInService.ClaimMustChangePassword, "true");

    public async Task<IActionResult> OnPostAsync()
    {
        IsForced = User.HasClaim(SignInService.ClaimMustChangePassword, "true");

        if (!ModelState.IsValid) return Page();

        if (NewPassword != ConfirmPassword)
        {
            ErrorMessage = "The two new passwords do not match.";
            return Page();
        }

        var (ok, error) = _passwords.ValidateStrength(NewPassword);
        if (!ok) { ErrorMessage = error; return Page(); }

        var userIdRaw = User.FindFirst(SignInService.ClaimUserId)?.Value;
        if (!Guid.TryParse(userIdRaw, out var userId)) return RedirectToPage("/Account/Login");

        var user = await _db.Users.IgnoreQueryFilters()
          .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted);

        if (user is null || string.IsNullOrEmpty(user.PasswordHash))
            return RedirectToPage("/Account/Login");

        // Requiring the current password is what stops an unattended logged-in
        // session from being used to take the account over permanently.
        var verify = _passwords.Verify(user.PasswordHash, CurrentPassword);
        if (verify == Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed)
        {
            ErrorMessage = "Your current password is incorrect.";
            return Page();
        }

        if (CurrentPassword == NewPassword)
        {
            ErrorMessage = "Your new password must be different from your current one.";
            return Page();
        }

        user.PasswordHash          = _passwords.Hash(NewPassword);
        user.SecurityStamp         = Guid.NewGuid().ToString("N");
        user.MustChangePassword    = false;
        user.LastPasswordChangeUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        // The new stamp invalidates THIS session too — including the stale
        // MustChangePassword claim, which would otherwise keep the filter
        // redirecting them back here forever. Signing out and back in is the
        // simplest correct resolution.
        await _signIn.SignOutAsync();

        return RedirectToPage("/Account/Login", new { passwordSet = true });
    }
}

