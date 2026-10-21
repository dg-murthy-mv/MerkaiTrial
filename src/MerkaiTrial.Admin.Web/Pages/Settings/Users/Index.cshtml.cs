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
//
// CHANGES (086 — sign-in help without waiting for MadeeVision)
//   ✅ "Reset password" on anyone who has signed in before: EMAILS them a
//      new 2-hour reset link (older ones stop working). The link is NOT
//      shown to the administrator — an admin who could read a colleague's
//      reset link could sign in as that colleague, and everything they did
//      would be recorded under the colleague's name. Only the super admin
//      page offers a copyable reset link.
//        • users.update, like Invite and Deactivate
//        • not on yourself (use Forgot password, or your profile)
//        • at most 5 per person per hour, so a stuck button can't flood
//          someone's inbox
//   ✅ "Invite" now EMAILS the invite as well as showing the link once.
//
// CHANGES (096 — the shared list pieces, and three small bugs)
//   ✅ The shared _ListToolbar (search + "1–10 of 14 people") and _Pager
//      replace the hand-built search card and the old _Pagination partial.
//      `public int Page` is PageNumber now — like every other list, and no
//      longer a property hiding PageModel.Page(). 10 per page by default;
//      the size a person picks is remembered on their browser.
//   ✅ EVERY ACTION KEEPS YOUR PLACE. Deactivate, Reactivate, Reset
//      password and Invite redirected to page 1 with no search; they come
//      back to the same page of the same search now.
//   ✅ A NAME WITH AN APOSTROPHE BROKE DEACTIVATE. The confirm text was
//      built inside onsubmit="return confirm('… {FullName} …')", so
//      "Deactivate O'Brien?" was a JavaScript syntax error — and a throwing
//      onsubmit does not return false, so the form posted WITHOUT asking.
//      The text is a data-confirm attribute now, read by one listener.
//      (Leads had the same bug, fixed in 031.)
//   ✅ A FAILED LOAD USED TempData on a response that renders, so the
//      error appeared one page late. It is PageError now, shown here.
//   ✅ "Last signed in" is in the workspace's date format and timezone
//      (FormatDate) — it printed the UTC date in "d MMM yyyy".
// =====================================================================

using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Pages.Account;            // 086
using MerkaiTrial.Admin.Web.Pages.Shared;             // 096 — the shared list pieces
using MerkaiTrial.Admin.Web.Services.Users;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Users;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Notifications; // 086
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace MerkaiTrial.Admin.Web.Pages.Settings.Users;

public class IndexModel : AuthorizedPageModel
{
    private readonly IUserService _userService;
    private readonly IAccountEmails _accountEmails;   // 086
    private readonly IConfiguration _config;          // 086

    /// <summary>086. Admin-sent reset emails per person per hour.</summary>
    private const int MaxResetsPerHour = 5;

    protected override string ModuleName => Modules.Users;

    public IndexModel(
        IUserService userService,
        IAccountEmails accountEmails,
        IConfiguration config,
        IAuthorizationService authorizationService,
        ICurrentUserService currentUserService,
        ILogger<IndexModel> logger)
        : base(authorizationService, currentUserService, logger)
    {
        _userService = userService;
        _accountEmails = accountEmails;
        _config        = config;
    }

    public PaginatedUsersResponse Users { get; set; } = null!;
    public UserStatsDto Stats { get; set; } = null!;
    private string ActorName =>
       User.Identity?.Name
       ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
       ?? "TenantAdmin";

    [BindProperty(SupportsGet = true)] public string? SearchTerm { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;            // 096 — was Page
    [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = PagerVm.DefaultSize;

    /// <summary>096. A failed load, for THIS response (not TempData — it would show one page late).</summary>
    public string? PageError { get; private set; }

    // ── 096: the shared toolbar and pager ─────────────────────────────

    private int TotalCount => Users?.TotalCount ?? 0;
    private int FirstRow   => TotalCount == 0 ? 0 : (PageNumber - 1) * PageSize + 1;
    private int LastRow    => Math.Min(PageNumber * PageSize, TotalCount);

    public bool IsFiltered => !string.IsNullOrWhiteSpace(SearchTerm);

    /// <summary>The search — what the pager and every form carry.</summary>
    public Dictionary<string, string> FilterRoute()
    {
        var route = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(SearchTerm)) route[nameof(SearchTerm)] = SearchTerm.Trim();
        return route;
    }

    /// <summary>The search, the page and the size — the hidden fields of every action form.</summary>
    public Dictionary<string, string> StateRoute()
    {
        var route = FilterRoute();
        route[nameof(PageNumber)] = PageNumber.ToString();
        route[nameof(PageSize)]   = PageSize.ToString();
        return route;
    }

    public ListToolbarVm Toolbar => new()
    {
        SearchName        = nameof(SearchTerm),
        SearchValue       = SearchTerm,
        SearchPlaceholder = "Name or email",
        SearchLabel       = "Search people",
        SearchWidth       = "300px",
        Carry             = new Dictionary<string, string> { [nameof(PageSize)] = PageSize.ToString() },
        ClearRoute        = new Dictionary<string, string> { [nameof(PageSize)] = PageSize.ToString() },
        IsFiltered        = IsFiltered,
        TotalCount        = TotalCount,
        FirstRow          = FirstRow,
        LastRow           = LastRow,
        NounSingular      = "person",
        NounPlural        = "people"
    };

    public PagerVm Pager => new()
    {
        Page       = PageNumber,
        PageSize   = PageSize,
        TotalCount = TotalCount,
        TotalPages = TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize),
        PageField  = nameof(PageNumber),
        Route      = FilterRoute()
    };

    /// <summary>Back to the same page of the same search after an action. A RouteValueDictionary — RedirectToPage reflects over plain objects.</summary>
    private RouteValueDictionary ReturnRoute()
    {
        var values = new RouteValueDictionary();
        foreach (var (k, v) in FilterRoute()) values[k] = v;
        values[nameof(PageNumber)] = Math.Max(PageNumber, 1);
        values[nameof(PageSize)]   = PagerVm.Sizes.Contains(PageSize) ? PageSize : PagerVm.DefaultSize;
        return values;
    }

    /// <summary>Set after a resend. Shown once, same as on create.</summary>
    public string? InviteUrl { get; set; }
    public string? InviteForEmail { get; set; }

    /// <summary>086. The resent invite was emailed as well.</summary>
    public bool InviteEmailed { get; set; }

    /// <summary>So the view can stop someone acting on their own account.</summary>
    public Guid CurrentUserId { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var permissionCheck = await ValidatePermissionAsync(Actions.Read);
        if (permissionCheck != null) return permissionCheck;

        // 096 — the URL's size, else this browser's last choice, else 10.
        PageSize   = ListPreferences.PageSize(HttpContext, "users");
        PageNumber = Math.Max(PageNumber, 1);

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
            return RedirectToPage(ReturnRoute());   // 096
        }

        try
        {
            // Guard the last admin. Deactivating the only remaining one
            // leaves nobody who can add users or manage roles.
            if (!isActive && await WouldLeaveNoAdminAsync(tenantId, userId))
            {
                TempData["ErrorMessage"] =
                    "This is the only administrator. Make someone else an administrator first.";
                return RedirectToPage(ReturnRoute());   // 096
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

        return RedirectToPage(ReturnRoute());   // 096
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
            // Scoped to this workspace — throws for anyone else's user.
            var user = await _userService.GetByIdAsync(tenantId, userId);

            // 086: emailed AND shown once.
            var invite = await _accountEmails.IssueInviteAsync(
                userId, AccountLinkBase.Resolve(_config, Request), ActorName, sendEmail: true);

            InviteUrl      = invite.Url;
            InviteForEmail = user.Email;
            InviteEmailed  = invite.EmailQueued;

            // 096 — this response RENDERS the list, on the page the form came from.
            PageSize   = PagerVm.Sizes.Contains(PageSize) ? PageSize : PagerVm.DefaultSize;
            PageNumber = Math.Max(PageNumber, 1);

            await InitializePermissionsAsync();
            await LoadDataAsync();

            // NOT a redirect: the raw token exists only in this response.
            return Page();
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
            return RedirectToPage(ReturnRoute());   // 096
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to reissue invite for user {UserId}", userId);
            TempData["ErrorMessage"] = "Could not generate a new invite link. Please try again.";
            return RedirectToPage(ReturnRoute());   // 096
        }
    }

    // =================================================================
    // 086 — RESET PASSWORD (emailed only; see the header)
    // =================================================================
    public async Task<IActionResult> OnPostSendResetAsync(Guid userId)
    {
        var permissionCheck = await ValidatePermissionAsync(Actions.Update);
        if (permissionCheck != null) return permissionCheck;

        var tenantId = CurrentUserService.GetCurrentTenantId();

        if (userId == CurrentUserService.GetCurrentUserId())
        {
            TempData["ErrorMessage"] = "To change your own password, sign out and use \"Forgot your password?\" on the sign-in page.";
            return RedirectToPage(ReturnRoute());   // 096
        }

        try
        {
            // Scoped to this workspace — throws for anyone else's user, so
            // a hand-made POST can never mail a stranger.
            var user = await _userService.GetByIdAsync(tenantId, userId);

            if (await _accountEmails.CountRecentResetsAsync(userId, TimeSpan.FromHours(1)) >= MaxResetsPerHour)
            {
                TempData["ErrorMessage"] =
                    $"{user.FirstName} has had several reset emails in the last hour. Ask them to check their spam folder, or try again later.";
                return RedirectToPage(ReturnRoute());   // 096
            }

            var result = await _accountEmails.IssuePasswordResetAsync(
                userId, AccountLinkBase.Resolve(_config, Request), ActorName, sendEmail: true);

            TempData[result.EmailQueued ? "SuccessMessage" : "ErrorMessage"] = result.EmailQueued
                ? $"Reset email sent to {result.ToAddress}. The link works once, for {AccountLinkLifetimes.Describe(result.Lifetime)}."
                : $"No email was sent: {result.EmailSkippedReason} Add an email address to their account first.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }
        catch (KeyNotFoundException)
        {
            TempData["ErrorMessage"] = "That person could not be found.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to send a reset email for user {UserId}", userId);
            TempData["ErrorMessage"] = "The reset email could not be sent. Please try again.";
        }

        return RedirectToPage(ReturnRoute());   // 096
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

        // 096 — the stats tiles and the list load separately, so a failed
        // stats call never empties the list.
        try
        {
            Stats = await _userService.GetStatsAsync(tenantId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "User stats could not be loaded for tenant {TenantId}", tenantId);
            Stats = new UserStatsDto(0, 0, 0, 0, 0);
        }

        try
        {
            Users = await _userService.GetPaginatedAsync(tenantId, PageNumber, PageSize, SearchTerm);

            // A page past the end (the last person on it deactivated by a
            // filter change, an old bookmark) → the last real page.
            var lastPage = Users.TotalCount == 0 ? 1 : (int)Math.Ceiling(Users.TotalCount / (double)PageSize);
            if (PageNumber > lastPage)
            {
                PageNumber = lastPage;
                Users = await _userService.GetPaginatedAsync(tenantId, PageNumber, PageSize, SearchTerm);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load users for tenant {TenantId}", tenantId);
            PageError = "Failed to load users. Please try again.";        // 096 — not TempData
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
