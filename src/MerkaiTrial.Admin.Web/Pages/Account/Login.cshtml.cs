// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Account/Login.cshtml.cs
// =====================================================================

using MerkaiTrial.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Account;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly ISignInService _signIn;
    private readonly AttemptLimiter _limiter;   // 104

    public LoginModel(ISignInService signIn, AttemptLimiter limiter)
    {
        _signIn = signIn;
        _limiter = limiter;
    }

    [BindProperty, Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [BindProperty, Required, DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public bool RememberMe { get; set; }

    /// <summary>Set by SetPassword's redirect (?passwordSet=true).</summary>
    public bool PasswordSet { get; set; }

    /// <summary>Plain property, NOT [TempData]: it is set and rendered within
    /// the same POST. TempData would survive into the next request and show a
    /// stale error on an unrelated page load.</summary>
    public string? ErrorMessage { get; set; }

    public void OnGet(string? returnUrl = null, bool passwordSet = false)
    {
        ViewData["ReturnUrl"] = returnUrl;
        PasswordSet = passwordSet;
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        if (!ModelState.IsValid) return Page();

        // 104 — 20 failed sign-ins a minute from one network address, then
        // a pause. Checked BEFORE the password, so once the limit is reached
        // a guesser learns nothing more — not even that a guess was right.
        var address = HttpContext.Connection.RemoteIpAddress?.ToString();
        if (_limiter.IsOver(AttemptRules.SignInFailures, address))
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            ErrorMessage = "Too many sign-in attempts from your network. Please wait a minute and try again.";
            return Page();
        }

        var result = await _signIn.PasswordSignInAsync(Email.Trim(), Password, RememberMe);

        if (result.Outcome != SignInOutcome.Success)
        {
            _limiter.Count(AttemptRules.SignInFailures, address);   // 104 — failures only
            ErrorMessage = result.Message ?? "Email or password is incorrect.";
            return Page();
        }

        // Open-redirect guard: only ever redirect within this site.
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);

        return Redirect(result.User!.IsSuperAdmin ? "/Admin" : "/Dashboard");
    }
}