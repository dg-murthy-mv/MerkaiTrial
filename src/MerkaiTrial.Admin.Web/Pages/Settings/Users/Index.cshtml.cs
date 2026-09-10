// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Settings/Users/Index.cshtml.cs
//
// The tenant-facing users list. Lets a client's admin add and manage
// their own colleagues instead of emailing you.
//
// DIFFERENCES FROM /Admin/Users
//
//   No tenant selector — the tenant comes from the signed-in user.
//   No delete — deactivate only. Deletion frees the email for reuse and
//     hides the person's history; deactivation is reversible and covers
//     "someone left". Deletion stays a SuperAdmin action.
//   Quota is visible — "7 of 10 users" before they hit the wall, rather
//     than discovering the limit when a create fails.
//   Resend invite — the answer to "I never got the email", which today
//     means deleting and recreating the person.
//
// GUARDS: you cannot deactivate or demote YOURSELF, and the workspace
// must keep at least one admin. Without those, one click locks the client
// out of their own workspace and only you can fix it.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Services.Users;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Users;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.Admin.Web.Pages.Settings.Users;

public class IndexModel : AuthorizedPageModel
{
    private readonly IUserService _userService;
    private readonly IUserTokenService _tokens;

    private static readonly TimeSpan InviteLifetime = TimeSpan.FromHours(72);

    protected override string ModuleName => Modules.Users;

    public IndexModel(
        IUserService userService,
        IUserTokenService tokens,
        IAuthorizationService authorizationService,
        ICurrentUserService currentUserService,
        ILogger<IndexModel> logger)
        : base(authorizationService, currentUserService, logger)
    {
        _userService = userService;
        _tokens      = tokens;
    }

    public PaginatedUsersResponse Users { get; set; } = null!;
    public UserStatsDto Stats { get; set; } = null!;
    private string ActorName =>
       User.Identity?.Name
       ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
       ?? "TenantAdmin";

    [BindProperty(SupportsGet = true)] public string? SearchTerm { get; set; }
    [BindProperty(SupportsGet = true)] public int Page { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = 10;

    /// <summary>Set after a resend. Shown once, same as on create.</summary>
    public string? InviteUrl { get; set; }
    public string? InviteForEmail { get; set; }

    /// <summary>So the view can stop someone acting on their own account.</summary>
    public Guid CurrentUserId { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var permissionCheck = await ValidatePermissionAsync(Actions.Read);
        if (permissionCheck != null) return permissionCheck;

        if (PageSize < 5) PageSize = 5;
        if (PageSize > 100) PageSize = 100;

        await InitializePermissionsAsync();
        await LoadDataAsync();

        return Page();
    }

    // =================================================================
    // DEACTIVATE / REACTIVATE
    // =================================================================
    public async Task<IActionResult> OnPostToggleStatusAsync(Guid userId, bool isActive)
    {
        var permissionCheck = await ValidatePermissionAsync(Actions.Update);
        if (permissionCheck != null) return permissionCheck;

        var tenantId = CurrentUserService.GetCurrentTenantId();
        var me = CurrentUserService.GetCurrentUserId();

        // Deactivating yourself signs you out of a workspace you may be the
        // only admin of. The recovery is a database edit by us.
        if (userId == me)
        {
            TempData["ErrorMessage"] = "You cannot deactivate your own account.";
            return RedirectToPage(new { PageSize });
        }

        try
        {
            // Guard the last admin. Deactivating the only remaining one
            // leaves nobody who can add users or manage roles.
            if (!isActive && await WouldLeaveNoAdminAsync(tenantId, userId))
            {
                TempData["ErrorMessage"] =
                    "This is the only administrator. Make someone else an administrator first.";
                return RedirectToPage(new { PageSize });
            }

            await _userService.UpdateStatusAsync(
                new UpdateUserStatusCommand(TenantId: tenantId, UserId: userId, IsActive: isActive));

            TempData["SuccessMessage"] = isActive
                ? "User reactivated. They can sign in again."
                : "User deactivated. They can no longer sign in, and their records are kept.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to change status for user {UserId}", userId);
            TempData["ErrorMessage"] = "Could not update that user. Please try again.";
        }

        return RedirectToPage(new { PageSize });
    }

    // =================================================================
    // RESEND INVITE
    //
    // The old invite is not revoked — it simply expires. Revoking would
    // mean a client who forwards the first link and then clicks resend
    // breaks the one their colleague already has.
    // =================================================================
    public async Task<IActionResult> OnPostResendInviteAsync(Guid userId)
    {
        var permissionCheck = await ValidatePermissionAsync(Actions.Update);
        if (permissionCheck != null) return permissionCheck;

        var tenantId = CurrentUserService.GetCurrentTenantId();

        try
        {
            var user = await _userService.GetByIdAsync(tenantId, userId);

            var token = await _tokens.IssueAsync(
                userId, tenantId, TokenPurpose.Invite,
                                InviteLifetime, ActorName);

            InviteUrl      = $"{Request.Scheme}://{Request.Host}/Account/SetPassword"
                           + $"?token={Uri.EscapeDataString(token)}&purpose=Invite";
            InviteForEmail = user.Email;

            await InitializePermissionsAsync();
            await LoadDataAsync();

            // NOT a redirect: the raw token exists only in this response.
            return Page();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to reissue invite for user {UserId}", userId);
            TempData["ErrorMessage"] = "Could not generate a new invite link. Please try again.";
            return RedirectToPage(new { PageSize });
        }
    }

    // =================================================================
    private async Task<bool> WouldLeaveNoAdminAsync(Guid tenantId, Guid userBeingDeactivated)
    {
        // Read a generous page rather than paging: a tenant has at most a
        // few dozen users, and the check has to be right rather than fast.
        var all = await _userService.GetPaginatedAsync(tenantId, 1, 100, null);

        var remainingAdmins = all.Items
            .Where(u => u.IsTenantAdmin && u.IsActive && u.Id != userBeingDeactivated)
            .Count();

        return remainingAdmins == 0;
    }

    private async Task LoadDataAsync()
    {
        CurrentUserId = CurrentUserService.GetCurrentUserId();
        var tenantId  = CurrentUserService.GetCurrentTenantId();

        try
        {
            Stats = await _userService.GetStatsAsync(tenantId);
            Users = await _userService.GetPaginatedAsync(tenantId, Page, PageSize, SearchTerm);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load users for tenant {TenantId}", tenantId);
            TempData["ErrorMessage"] = "Failed to load users. Please try again.";
            Stats = new UserStatsDto(0, 0, 0, 0, 0);
            Users = new PaginatedUsersResponse(new List<UserListItem>(), 0, 1, PageSize, 0);
        }
    }
}

/* =====================================================================
   TWO THINGS TO CHECK

   1. ICurrentUserService needs GetCurrentUserId() and
      GetCurrentUserEmail(). If they are named differently, adjust — the
      claims are SignInService.ClaimUserId and the email claim.

   2. AuthorizedPageModel needs a MaxUsers property for the quota bar,
      alongside the MaxLeads / MaxContacts it already exposes. If it is
      missing, add it the same way:

          public int MaxUsers => CurrentTenant.GetMaxUsers();

   =====================================================================
   NAV — add to _Layout.cshtml, in the Workspace Settings block you
   already have, above Roles:

       @if (CanNav("users"))
       {
           <a class="lf-nav-item @(IsActive("/Settings/Users") ? "active" : "")"
              asp-page="/Settings/Users/Index">
               <i class="bi bi-people"></i> Users
           </a>
       }

   The section header and the roles.read check are already there; this
   just adds a second item inside it. Note the section condition should
   become:

       @if (CanNav("roles") || CanNav("users"))
   ===================================================================== */
