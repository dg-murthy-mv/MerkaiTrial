// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Admin/ViewAs/Index.cshtml.cs
// Location: /Admin/ViewAs — inherits SuperAdmin-only auth from the
// existing AuthorizeFolder("/Admin", "SuperAdmin") convention in Program.cs
// =====================================================================

using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Admin.Web.Pages.Admin.ViewAs;

public class IndexModel : PageModel
{
    private readonly FlowDbContext _db;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(FlowDbContext db, ILogger<IndexModel> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public List<TenantOption> Tenants { get; private set; } = new();

    // Currently-active ViewAs state (if any), so the page can show
    // "Currently viewing as: X" and pre-select the dropdowns.
    public bool IsCurrentlyViewingAs { get; private set; }
    public Guid? CurrentTenantId { get; private set; }
    public Guid? CurrentUserId { get; private set; }

    public async Task OnGetAsync()
    {
        Tenants = await _db.Tenants
            .AsNoTracking()
            .Where(t => !t.IsDeleted)
            .OrderBy(t => t.Name)
            .Select(t => new TenantOption(t.Id, t.Name, t.Country != null ? t.Country.Name : null, t.Plan))
            .ToListAsync();

        var cookieTenant = Request.Cookies[ViewAsCookies.TenantId];
        var cookieUser    = Request.Cookies[ViewAsCookies.UserId];

        if (Guid.TryParse(cookieTenant, out var tId) && Guid.TryParse(cookieUser, out var uId))
        {
            IsCurrentlyViewingAs = true;
            CurrentTenantId = tId;
            CurrentUserId   = uId;
        }
    }

    /// <summary>
    /// AJAX endpoint — returns the users for a given tenant so the picker
    /// can populate the second dropdown after a tenant is chosen.
    /// </summary>
    public async Task<JsonResult> OnGetUsersForTenantAsync(Guid tenantId)
    {
        var users = await _db.Users
            .AsNoTracking()
            .Where(u => u.TenantId == tenantId && !u.IsDeleted)
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
            .ToListAsync();

        // ✅ Load each user's role DisplayName (e.g. "Sales Manager", "Viewer")
        // the same way DemoAuthenticationHandler resolves roles at auth time —
        // so the picker shows exactly the role that'll actually apply, for
        // every user, not just tenant admins.
        var userIds = users.Select(u => u.Id).ToList();

        var roleNamesByUser = await _db.UserRoles
            .AsNoTracking()
            .Where(ur => userIds.Contains(ur.UserId))
            .Join(_db.Roles.AsNoTracking().Where(r => !r.IsDeleted),
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

    /// <summary>Sets the ViewAs cookies and redirects into the tenant CRM.</summary>
    public IActionResult OnPostStart(Guid tenantId, Guid userId)
    {
        Response.Cookies.Append(ViewAsCookies.TenantId, tenantId.ToString(), ViewAsCookies.Options);
        Response.Cookies.Append(ViewAsCookies.UserId, userId.ToString(), ViewAsCookies.Options);

        _logger.LogInformation("SuperAdmin started ViewAs: tenant={TenantId} user={UserId}", tenantId, userId);

        return RedirectToPage("/Dashboard/Index");
    }

    /// <summary>Clears the ViewAs override and returns to the SuperAdmin console.</summary>
    public IActionResult OnPostExit()
    {
        Response.Cookies.Delete(ViewAsCookies.TenantId);
        Response.Cookies.Delete(ViewAsCookies.UserId);

        _logger.LogInformation("SuperAdmin exited ViewAs");

        return RedirectToPage("/Admin/Plans/Index");
    }
}

public record TenantOption(Guid Id, string Name, string? CountryName, string? Plan);
public record UserOption(Guid Id, string DisplayName, bool IsTenantAdmin, string? RoleDisplayName);
