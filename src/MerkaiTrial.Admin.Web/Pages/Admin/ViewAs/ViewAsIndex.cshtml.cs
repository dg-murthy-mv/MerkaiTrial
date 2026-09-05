// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Admin/ViewAs/Index.cshtml.cs
//
// REWRITTEN. The previous version set ViewAs cookies directly:
//
//     Response.Cookies.Append(ViewAsCookies.TenantId, tenantId.ToString(), ...);
//     Response.Cookies.Append(ViewAsCookies.UserId,   userId.ToString(), ...);
//
// That worked because DemoAuthenticationHandler READ those cookies and
// built an identity from them — which is exactly the privilege-escalation
// hole we removed: anyone could set the same two cookies in DevTools and
// be granted IsSuperAdmin.
//
// DemoAuthenticationHandler is now gone, so those cookies are read by
// nothing. ViewAs is currently BROKEN: it sets two ignored cookies,
// redirects to /Dashboard, and AccountStatePageFilter bounces the super
// admin straight back to /Admin because they are not actually viewing as
// anyone.
//
// Impersonation now goes through ISignInService, which re-issues the auth
// cookie server-side after verifying the CURRENT principal is a real
// super admin. Identity comes from a verified session, never from a
// cookie the browser can forge.
//
// DELETE the ViewAsCookies class along with DemoAuthenticationHandler.
// =====================================================================

using MerkaiTrial.Application.Security;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Admin.Web.Pages.Admin.ViewAs;

public class IndexModel : PageModel
{
    private readonly FlowDbContext _db;
    private readonly ISignInService _signIn;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(FlowDbContext db, ISignInService signIn, ILogger<IndexModel> logger)
    {
        _db = db;
        _signIn = signIn;
        _logger = logger;
    }

    public List<TenantOption> Tenants { get; private set; } = new();

    public bool IsCurrentlyViewingAs { get; private set; }
    public Guid? CurrentTenantId { get; private set; }
    public Guid? CurrentUserId { get; private set; }

    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        // IgnoreQueryFilters: this page lists EVERY tenant by definition.
        // Tenants has no TenantId column so it is unfiltered today, but the
        // call makes the cross-tenant intent explicit.
        Tenants = await _db.Tenants
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(t => !t.IsDeleted)
            .OrderBy(t => t.Name)
            .Select(t => new TenantOption(t.Id, t.Name, t.Country != null ? t.Country.Name : null, t.Plan))
            .ToListAsync();

        // ViewAs state now comes from CLAIMS, not cookies. The claims were
        // issued server-side by SignInService and are inside a signed,
        // encrypted auth cookie — they cannot be edited in DevTools.
        IsCurrentlyViewingAs = User.HasClaim(SignInService.ClaimViewingAs, "true");

        if (IsCurrentlyViewingAs)
        {
            if (Guid.TryParse(User.FindFirst(SignInService.ClaimTenantId)?.Value, out var tId))
                CurrentTenantId = tId;

            if (Guid.TryParse(User.FindFirst(SignInService.ClaimUserId)?.Value, out var uId))
                CurrentUserId = uId;
        }
    }

    /// <summary>
    /// AJAX endpoint — users for a chosen tenant, to populate the second
    /// dropdown.
    /// </summary>
    public async Task<JsonResult> OnGetUsersForTenantAsync(Guid tenantId)
    {
        // Cross-tenant by definition: the super admin's own claim points at
        // MadeeVision, and this asks for a DIFFERENT tenant's users.
        var users = await _db.Users
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(u => u.TenantId == tenantId && !u.IsDeleted)
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
            .ToListAsync();

        var userIds = users.Select(u => u.Id).ToList();

        // Roles IS filtered (TenantId == null || == current). Without
        // IgnoreQueryFilters this would return only system roles once the
        // role split has run, so every user would show "Tenant Admin" or
        // nothing — the picker would silently stop showing real roles.
        var roleNamesByUser = await _db.UserRoles
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(ur => userIds.Contains(ur.UserId))
            .Join(_db.Roles.AsNoTracking().IgnoreQueryFilters().Where(r => !r.IsDeleted),
                  ur => ur.RoleId, r => r.Id,
                  (ur, r) => new { ur.UserId, r.DisplayName })
            .ToListAsync();

        var roleLookup = roleNamesByUser
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => x.DisplayName)));

        var options = users.Select(u => new UserOption(
            u.Id,
            string.IsNullOrWhiteSpace(u.FullName) ? u.Email : u.FullName,
            u.IsTenantAdmin,
            u.IsTenantAdmin
                ? "Tenant Admin"
                : (roleLookup.TryGetValue(u.Id, out var roleNames) ? roleNames : null)
        )).ToList();

        return new JsonResult(options);
    }

    /// <summary>
    /// Starts impersonation. StartViewAsAsync verifies the CURRENT
    /// principal is a real super admin before re-issuing the auth cookie
    /// as the target user — so this page cannot grant more than the caller
    /// already has, even if someone reaches the handler directly.
    /// </summary>
    public async Task<IActionResult> OnPostStartAsync(Guid tenantId, Guid userId)
    {
        try
        {
            await _signIn.StartViewAsAsync(userId, tenantId);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "ViewAs start rejected");
            ErrorMessage = "Only a signed-in super admin can use View As.";
            return RedirectToPage();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "ViewAs start failed — target not found");
            ErrorMessage = "That user could not be found in the selected workspace.";
            return RedirectToPage();
        }

        return RedirectToPage("/Dashboard/Index");
    }

    /// <summary>Returns to the super admin's own identity.</summary>
    public async Task<IActionResult> OnPostExitAsync()
    {
        await _signIn.ExitViewAsAsync();
        return RedirectToPage("/Admin/Plans/Index");
    }
}

public record TenantOption(Guid Id, string Name, string? CountryName, string? Plan);
public record UserOption(Guid Id, string DisplayName, bool IsTenantAdmin, string? RoleDisplayName);

/* =====================================================================
   THE .cshtml NEEDS TWO HANDLER NAMES UPDATED

   Razor Pages matches asp-page-handler to the method name minus the
   "On[Verb]" prefix and the "Async" suffix, so OnPostStartAsync is still
   handler "Start" and OnPostExitAsync is still "Exit". The existing
   markup keeps working unchanged.

   Add somewhere near the top of the page so failures are visible:

       @if (!string.IsNullOrEmpty(Model.ErrorMessage))
       {
           <div class="alert alert-danger">@Model.ErrorMessage</div>
       }

   =====================================================================
   ALSO DELETE

   The ViewAsCookies class in DemoAuthenticationHandler.cs — nothing reads
   those cookies now, and leaving a class that looks like it grants
   impersonation is an invitation for someone to wire it back up.

   If any browser still holds stale ViewAs_TenantId / ViewAs_UserId
   cookies from testing, they are inert. Clearing them is tidy, not
   urgent.
   ===================================================================== */
