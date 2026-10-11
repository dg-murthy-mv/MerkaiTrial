using MerkaiTrial.Admin.Web.Services.Roles;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Settings.Roles;

// =====================================================================
// FILE: Pages/Settings/Roles/Edit.cshtml.cs
//
// 084 CHANGES
//   - Shows what the role REALLY grants. Stored keys are PascalCase
//     ("Leads"); the checkboxes are lowercase ("leads"). The old
//     case-sensitive check showed those boxes unticked.
//   - Saving never silently drops a permission. The stored role is
//     re-read on POST and only the boxes on this page are applied to
//     it; modules/actions the page does not offer are kept (and listed
//     on the page so the admin knows they exist).
//   - Underscores allowed in the role name (sales_rep).
//   - Built-in roles are refused on POST too, not just GET.
// =====================================================================
public class EditModel : AuthorizedPageModel
{
    private readonly IRoleService _roleService;

    protected override string ModuleName => Modules.Roles;

    public EditModel(
        IRoleService roleService,
        IAuthorizationService authorizationService,
        ICurrentUserService currentUserService,
        ILogger<EditModel> logger)
        : base(authorizationService, currentUserService, logger)
    {
        _roleService = roleService;
    }

    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty] public InputModel Input { get; set; } = new();

    // NOT [TempData]. That attribute writes to TempData["ErrorMessage"],
    // which the tenant layout also renders — the same error appeared twice.
    public string? ErrorMessage { get; set; }

    public List<ModuleDefinition> AvailableModules { get; set; } = new();
    public Dictionary<string, List<string>> SelectedPermissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stored grants this page does not show; kept on save.</summary>
    public Dictionary<string, List<string>> KeptPermissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>People holding this role — they are signed out on save.</summary>
    public int AssignedUsers { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Role name is required")]
        [StringLength(100, MinimumLength = 2, ErrorMessage = "Name must be between 2 and 100 characters")]
        [RegularExpression(CreateModel.NamePattern, ErrorMessage = CreateModel.NameMessage)]
        public string Name { get; set; } = string.Empty;

        [Required(ErrorMessage = "Display name is required")]
        [StringLength(200, MinimumLength = 2, ErrorMessage = "Display name must be between 2 and 200 characters")]
        public string DisplayName { get; set; } = string.Empty;

        [StringLength(500, ErrorMessage = "Description cannot exceed 500 characters")]
        public string? Description { get; set; }
    }

    public string ModuleLabel(string key) => RolePermissionForm.ModuleLabel(key, AvailableModules);

    public async Task<IActionResult> OnGetAsync()
    {
        var permissionCheck = await ValidatePermissionAsync(Actions.Update);
        if (permissionCheck != null) return permissionCheck;

        try
        {
            var role = await _roleService.GetByIdAsync(Id);

            // Built-in roles are shared across every tenant. Editing one
            // here would change it for every other client.
            if (role.IsSystemRole) return BuiltInRole();

            Input = new InputModel
            {
                Name = role.Name,
                DisplayName = role.DisplayName,
                Description = role.Description
            };

            AvailableModules = _roleService.GetAvailableModules();
            var stored = RolePermissionForm.Parse(role.Permissions);
            SelectedPermissions = stored;
            KeptPermissions = RolePermissionForm.NotShown(stored, AvailableModules);
            AssignedUsers = role.UserCount;

            return Page();
        }
        catch (KeyNotFoundException)
        {
            return RoleNotFound();
        }
        catch (HttpRequestException ex) when (IsNotFound(ex))
        {
            // Another tenant's role. The API returns 404 rather than 403 so
            // the two are indistinguishable from outside.
            return RoleNotFound();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading role {RoleId}", Id);
            TempData["ErrorMessage"] = "Failed to load the role.";
            return RedirectToPage("./Index");
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var permissionCheck = await ValidatePermissionAsync(Actions.Update);
        if (permissionCheck != null) return permissionCheck;

        AvailableModules = _roleService.GetAvailableModules();

        // Re-read the stored role. Its current permissions are the base
        // the checkboxes are applied to — without it, anything the page
        // does not show would be wiped by this save.
        RoleDto role;
        try
        {
            role = await _roleService.GetByIdAsync(Id);
        }
        catch (KeyNotFoundException)
        {
            return RoleNotFound();
        }
        catch (HttpRequestException ex) when (IsNotFound(ex))
        {
            return RoleNotFound();
        }
        catch (Exception ex)
        {
            // Refuse to save rather than save blind.
            Logger.LogError(ex, "Error re-loading role {RoleId} before update", Id);
            ErrorMessage = "Couldn't load the role's current permissions, so nothing was saved. Please try again.";
            SelectedPermissions = RolePermissionForm.ReadForm(Request.Form, AvailableModules);
            return Page();
        }

        if (role.IsSystemRole) return BuiltInRole();

        var stored = RolePermissionForm.Parse(role.Permissions);
        KeptPermissions = RolePermissionForm.NotShown(stored, AvailableModules);
        AssignedUsers = role.UserCount;

        if (!ModelState.IsValid)
        {
            SelectedPermissions = RolePermissionForm.ReadForm(Request.Form, AvailableModules);
            return Page();
        }

        try
        {
            var command = new UpdateRoleCommand(
                RoleId: Id,
                Name: Input.Name.Trim().ToLowerInvariant(),
                DisplayName: Input.DisplayName.Trim(),
                Description: Input.Description?.Trim(),
                Permissions: RolePermissionForm.BuildJson(role.Permissions, Request.Form, AvailableModules)
            );

            await _roleService.UpdateAsync(command);

            // Everyone holding this role has their security stamp rotated by
            // UpdateRoleHandler, so they are signed out and pick up the new
            // permissions on next sign-in. Say so — otherwise the admin
            // reports "it logged everyone out" as a bug.
            TempData["SuccessMessage"] = AssignedUsers > 0
                ? $"Role '{Input.DisplayName.Trim()}' updated. The {AssignedUsers} {(AssignedUsers == 1 ? "person" : "people")} using it will be asked to sign in again."
                : $"Role '{Input.DisplayName.Trim()}' updated.";

            return RedirectToPage("./Detail", new { id = Id });
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            SelectedPermissions = RolePermissionForm.ReadForm(Request.Form, AvailableModules);
            return Page();
        }
        catch (HttpRequestException ex) when (IsNotFound(ex))
        {
            return RoleNotFound();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error updating role {RoleId}", Id);
            ErrorMessage = "Failed to update the role. Please try again.";
            SelectedPermissions = RolePermissionForm.ReadForm(Request.Form, AvailableModules);
            return Page();
        }
    }

    private static bool IsNotFound(HttpRequestException ex)
        => ex.StatusCode == System.Net.HttpStatusCode.NotFound
           || ex.Message.Contains("404")
           || ex.Message.Contains("NotFound", StringComparison.OrdinalIgnoreCase);

    private IActionResult BuiltInRole()
    {
        TempData["ErrorMessage"] = "Built-in roles cannot be edited.";
        return RedirectToPage("./Detail", new { id = Id });
    }

    private IActionResult RoleNotFound()
    {
        TempData["ErrorMessage"] = "That role could not be found.";
        return RedirectToPage("./Index");
    }

    /// <summary>
    /// Kept for callers outside this folder. Case-insensitive now: the
    /// returned dictionary matches "Leads" and "leads" alike.
    /// </summary>
    internal static Dictionary<string, List<string>> ParsePermissions(string? json)
        => RolePermissionForm.Parse(json);
}
