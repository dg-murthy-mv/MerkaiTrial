using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Services.Roles;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace MerkaiTrial.Admin.Web.Pages.Settings.Roles;
public class DetailModel : AuthorizedPageModel
{
    private readonly IRoleService _roleService;

    protected override string ModuleName => Modules.Roles;

    public DetailModel(
        IRoleService roleService,
        IAuthorizationService authorizationService,
        ICurrentUserService currentUserService,
        ILogger<DetailModel> logger)
        : base(authorizationService, currentUserService, logger)
    {
        _roleService = roleService;
    }

    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }

    public RoleDto Role { get; set; } = null!;
    public List<UserLookupDto> Users { get; set; } = new();
    public Dictionary<string, List<string>> ParsedPermissions { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        var permissionCheck = await ValidatePermissionAsync(Actions.Read);
        if (permissionCheck != null) return permissionCheck;

        try
        {
            Role = await _roleService.GetByIdAsync(Id);

            // Scoped server-side: only THIS tenant's users holding the role.
            // Previously this returned matching users in every tenant.
            Users = await _roleService.GetRoleUsersAsync(Id);

            ParsedPermissions = EditModel.ParsePermissions(Role.Permissions);

            // Drives whether the Edit button renders.
            await InitializePermissionsAsync();

            return Page();
        }
        catch (KeyNotFoundException)
        {
            TempData["ErrorMessage"] = "That role could not be found.";
            return RedirectToPage("./Index");
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("404"))
        {
            TempData["ErrorMessage"] = "That role could not be found.";
            return RedirectToPage("./Index");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading role {RoleId}", Id);
            TempData["ErrorMessage"] = "Failed to load the role.";
            return RedirectToPage("./Index");
        }
    }
}
