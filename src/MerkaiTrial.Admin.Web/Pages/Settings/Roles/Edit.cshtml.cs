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
    public Dictionary<string, List<string>> SelectedPermissions { get; set; } = new();

    public class InputModel
    {
        [Required(ErrorMessage = "Role name is required")]
        [StringLength(100, MinimumLength = 2)]
        [RegularExpression(@"^[a-z0-9-]+$",
            ErrorMessage = "Use lowercase letters, numbers and hyphens only")]
        public string Name { get; set; } = string.Empty;

        [Required(ErrorMessage = "Display name is required")]
        [StringLength(200, MinimumLength = 2)]
        public string DisplayName { get; set; } = string.Empty;

        [StringLength(500)]
        public string? Description { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var permissionCheck = await ValidatePermissionAsync(Actions.Update);
        if (permissionCheck != null) return permissionCheck;

        try
        {
            var role = await _roleService.GetByIdAsync(Id);

            // Built-in roles are shared across every tenant. Editing one
            // here would change it for every other client.
            if (role.IsSystemRole)
            {
                TempData["ErrorMessage"] = "Built-in roles cannot be edited.";
                return RedirectToPage("./Detail", new { id = Id });
            }

            Input = new InputModel
            {
                Name = role.Name,
                DisplayName = role.DisplayName,
                Description = role.Description
            };

            AvailableModules = _roleService.GetAvailableModules();
            SelectedPermissions = ParsePermissions(role.Permissions);

            return Page();
        }
        catch (KeyNotFoundException)
        {
            return RoleNotFound();
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("404"))
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

        if (!ModelState.IsValid)
        {
            SelectedPermissions = ReadPermissionsFromForm(Request.Form, AvailableModules);
            return Page();
        }

        try
        {
            var command = new UpdateRoleCommand(
                RoleId: Id,
                Name: Input.Name.Trim().ToLowerInvariant(),
                DisplayName: Input.DisplayName.Trim(),
                Description: Input.Description?.Trim(),
                Permissions: CreateModel.BuildPermissionsJson(Request.Form, AvailableModules)
            );

            await _roleService.UpdateAsync(command);

            // Everyone holding this role has their security stamp rotated by
            // UpdateRoleHandler, so they are signed out and pick up the new
            // permissions on next sign-in. Say so — otherwise the admin
            // reports "it logged everyone out" as a bug.
            TempData["SuccessMessage"] =
                $"Role '{Input.DisplayName}' updated. Anyone using it will be asked to sign in again.";

            return RedirectToPage("./Detail", new { id = Id });
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            SelectedPermissions = ReadPermissionsFromForm(Request.Form, AvailableModules);
            return Page();
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("404"))
        {
            return RoleNotFound();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error updating role {RoleId}", Id);
            ErrorMessage = "Failed to update the role. Please try again.";
            SelectedPermissions = ReadPermissionsFromForm(Request.Form, AvailableModules);
            return Page();
        }
    }

    private IActionResult RoleNotFound()
    {
        TempData["ErrorMessage"] = "That role could not be found.";
        return RedirectToPage("./Index");
    }

    /// <summary>
    /// Re-reads the posted checkboxes so a validation failure does not wipe
    /// the permission selections the user just made. The /Admin version
    /// left SelectedPermissions empty on this path, so every box came back
    /// unticked after a validation error.
    /// </summary>
    private static Dictionary<string, List<string>> ReadPermissionsFromForm(
        IFormCollection form, IEnumerable<ModuleDefinition> modules)
    {
        var result = new Dictionary<string, List<string>>();

        foreach (var module in modules)
        {
            var granted = module.Permissions
                .Where(p => form[$"Permissions_{module.Name}_{p}"].ToString() == "true")
                .ToList();

            if (granted.Count > 0) result[module.Name] = granted;
        }

        return result;
    }

    internal static Dictionary<string, List<string>> ParsePermissions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json) ?? new();
        }
        catch (JsonException)
        {
            // Corrupt JSON shows as "no permissions" rather than throwing.
            // SignInService now fails closed on the same data, so this
            // matches what the role actually grants.
            return new();
        }
    }
}