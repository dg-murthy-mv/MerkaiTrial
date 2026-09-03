using MerkaiTrial.Application.Security;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Account;

// =====================================================================
// FILE: Pages/Account/Logout.cshtml.cs
// =====================================================================
public class LogoutModel : PageModel
{
    private readonly ISignInService _signIn;
    public LogoutModel(ISignInService signIn) => _signIn = signIn;

    // GET does NOT sign out. A <img src="/Account/Logout"> on any page would
    // otherwise sign your users out — low harm, but it is free to avoid.
    public IActionResult OnGet() => Page();

    public async Task<IActionResult> OnPostAsync()
    {
        await _signIn.SignOutAsync();
        return RedirectToPage("/Account/Login");
    }
}