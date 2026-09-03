using MerkaiTrial.Application.Security;
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

    public SetPasswordModel(FlowDbContext db, IUserTokenService tokens, IPasswordService passwords)
    {
        _db = db; _tokens = tokens; _passwords = passwords;
    }

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

        var user = await _db.Users.FirstAsync(u => u.Id == token.UserId);

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

        return RedirectToPage("/Account/Login", new { passwordSet = true });
    }
}