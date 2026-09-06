// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Settings/Roles/Index.cshtml.cs
//
// Create the folders: Pages/Settings/Roles/
//
// The tenant-facing twin of /Admin/Roles. Same IRoleService calls — the
// API scopes by tenant via IRoleScope, so this page needs no tenant logic
// of its own. What differs is WHO may reach it:
//
//   /Admin/Roles     SuperAdmin only (AuthorizeFolder), shows all tenants
//   /Settings/Roles  any user with roles.read, shows their tenant only
//
// It inherits AuthorizedPageModel rather than PageModel so the roles.*
// permissions are actually enforced and CanCreate/CanUpdate/CanDelete are
// available to the view. The /Admin version is a plain PageModel because
// the folder-level SuperAdmin policy is its only gate.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Services.Roles;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.Admin.Web.Pages.Settings.Roles;

public class IndexModel : AuthorizedPageModel
{
    private readonly IRoleService _roleService;

    protected override string ModuleName => Modules.Roles;

    public IndexModel(
        IRoleService roleService,
        IAuthorizationService authorizationService,
        ICurrentUserService currentUserService,
        ILogger<IndexModel> logger)
        : base(authorizationService, currentUserService, logger)
    {
        _roleService = roleService;
    }

    public PaginatedRolesResponse Roles { get; set; } = null!;
    public RoleStatsDto Stats { get; set; } = null!;

    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public bool? IsSystemRole { get; set; }
    [BindProperty(SupportsGet = true)] public int Page { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = 10;

    public async Task<IActionResult> OnGetAsync()
    {
        var permissionCheck = await ValidatePermissionAsync(Actions.Read);
        if (permissionCheck != null) return permissionCheck;

        if (PageSize < 5) PageSize = 5;
        if (PageSize > 100) PageSize = 100;

        // Drives which buttons the view renders. A user with roles.read but
        // not roles.create should see the list without a Create button —
        // showing a button that always fails is worse than hiding it.
        await InitializePermissionsAsync();

        await LoadDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var permissionCheck = await ValidatePermissionAsync(Actions.Delete);
        if (permissionCheck != null) return permissionCheck;

        try
        {
            await _roleService.DeleteAsync(id);
            TempData["SuccessMessage"] = "Role deleted successfully.";
        }
        catch (InvalidOperationException ex)
        {
            // "Cannot delete system roles" and "role is assigned to users"
            // both arrive here — both are messages worth showing verbatim.
            Logger.LogWarning(ex, "Failed to delete role {RoleId}", id);
            TempData["ErrorMessage"] = ex.Message;
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("404"))
        {
            // The API returns 404 for another tenant's role — deliberately
            // indistinguishable from "does not exist".
            Logger.LogWarning(ex, "Role {RoleId} not found for this tenant", id);
            TempData["ErrorMessage"] = "That role could not be found.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete role {RoleId}", id);
            TempData["ErrorMessage"] = "Failed to delete the role. Please try again.";
        }

        return RedirectToPage(new { PageSize });
    }

    private async Task LoadDataAsync()
    {
        try
        {
            Stats = await _roleService.GetStatsAsync();
            Roles = await _roleService.GetPaginatedAsync(Page, PageSize, Search, IsSystemRole);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load roles for tenant");
            TempData["ErrorMessage"] = "Failed to load roles. Please try again.";
            Stats = new RoleStatsDto(0, 0, 0, 0);
            Roles = new PaginatedRolesResponse(new List<RoleListItem>(), 0, 1, PageSize);
        }
    }
}

