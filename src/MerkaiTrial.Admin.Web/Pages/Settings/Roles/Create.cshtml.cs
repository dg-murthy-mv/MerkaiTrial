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
// FILE: Pages/Settings/Roles/Create.cshtml.cs
//
// 084 CHANGES
//   - Role names may contain underscores. Built-in roles are named
//     "sales_rep", "sales_manager"; the old rule (letters, digits,
//     hyphens) rejected them, so a role copied from one could never be
//     saved. Upper case is accepted and stored lower case.
//   - A validation error no longer unticks every permission box.
//   - Checkbox reading/saving moved to RolePermissionForm (shared with
//     Edit and Detail). BuildPermissionsJson keeps its signature.
// =====================================================================
public class CreateModel : AuthorizedPageModel
{
    /// <summary>Shared by Create and Edit so the rule cannot drift.</summary>
    internal const string NamePattern = @"^[A-Za-z0-9_-]+$";
    internal const string NameMessage =
        "Use letters, numbers, hyphens (-) or underscores (_). No spaces.";

    private readonly IRoleService _roleService;

    protected override string ModuleName => Modules.Roles;

    public CreateModel(
        IRoleService roleService,
        IAuthorizationService authorizationService,
        ICurrentUserService currentUserService,
        ILogger<CreateModel> logger)
        : base(authorizationService, currentUserService, logger)
    {
        _roleService = roleService;
    }

    [BindProperty] public InputModel Input { get; set; } = new();

    public List<ModuleDefinition> AvailableModules { get; set; } = new();

    /// <summary>What to tick when the form is redisplayed after an error.</summary>
    public Dictionary<string, List<string>> SelectedPermissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? ErrorMessage { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Role name is required")]
        [StringLength(100, MinimumLength = 2, ErrorMessage = "Name must be between 2 and 100 characters")]
        [RegularExpression(NamePattern, ErrorMessage = NameMessage)]
        public string Name { get; set; } = string.Empty;

        [Required(ErrorMessage = "Display name is required")]
        [StringLength(200, MinimumLength = 2, ErrorMessage = "Display name must be between 2 and 200 characters")]
        public string DisplayName { get; set; } = string.Empty;

        [StringLength(500, ErrorMessage = "Description cannot exceed 500 characters")]
        public string? Description { get; set; }
    }

    public bool IsChecked(string module, string action)
        => RolePermissionForm.Has(SelectedPermissions, module, action);

    public async Task<IActionResult> OnGetAsync()
    {
        var permissionCheck = await ValidatePermissionAsync(Actions.Create);
        if (permissionCheck != null) return permissionCheck;

        AvailableModules = _roleService.GetAvailableModules();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var permissionCheck = await ValidatePermissionAsync(Actions.Create);
        if (permissionCheck != null) return permissionCheck;

        AvailableModules = _roleService.GetAvailableModules();

        if (!ModelState.IsValid)
        {
            SelectedPermissions = RolePermissionForm.ReadForm(Request.Form, AvailableModules);
            return Page();
        }

        try
        {
            var command = new CreateRoleCommand(
                Name: Input.Name.Trim().ToLowerInvariant(),
                DisplayName: Input.DisplayName.Trim(),
                Description: Input.Description?.Trim(),
                Permissions: BuildPermissionsJson(Request.Form, AvailableModules)
            );

            var result = await _roleService.CreateAsync(command);

            TempData["SuccessMessage"] = $"Role '{result.DisplayName}' created.";
            return RedirectToPage("./Detail", new { id = result.Id });
        }
        catch (InvalidOperationException ex)
        {
            // "Role with name X already exists" — scoped per tenant, so
            // this only fires on a genuine clash inside this workspace.
            ModelState.AddModelError(string.Empty, ex.Message);
            SelectedPermissions = RolePermissionForm.ReadForm(Request.Form, AvailableModules);
            return Page();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error creating role");
            ErrorMessage = "Failed to create the role. Please try again.";
            SelectedPermissions = RolePermissionForm.ReadForm(Request.Form, AvailableModules);
            return Page();
        }
    }

    /// <summary>
    /// Reads the Permissions_{module}_{action} checkboxes into the JSON
    /// shape stored on Role.Permissions, for a NEW role. Edit merges with
    /// the stored role instead (RolePermissionForm.BuildJson with the
    /// stored JSON) so nothing the page does not show is lost.
    /// </summary>
    internal static string BuildPermissionsJson(
        IFormCollection form, IEnumerable<ModuleDefinition> modules)
        => RolePermissionForm.BuildJson(null, form, modules);
}
