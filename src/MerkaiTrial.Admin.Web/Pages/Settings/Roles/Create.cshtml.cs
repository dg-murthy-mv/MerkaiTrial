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


// =====================================================================
// FILE: Pages/Settings/Roles/Create.cshtml.cs
// =====================================================================
public class CreateModel : AuthorizedPageModel
{
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

    public string? ErrorMessage { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Role name is required")]
        [StringLength(100, MinimumLength = 2, ErrorMessage = "Name must be between 2 and 100 characters")]
        [RegularExpression(@"^[a-z0-9-]+$",
            ErrorMessage = "Use lowercase letters, numbers and hyphens only")]
        public string Name { get; set; } = string.Empty;

        [Required(ErrorMessage = "Display name is required")]
        [StringLength(200, MinimumLength = 2, ErrorMessage = "Display name must be between 2 and 200 characters")]
        public string DisplayName { get; set; } = string.Empty;

        [StringLength(500, ErrorMessage = "Description cannot exceed 500 characters")]
        public string? Description { get; set; }
    }

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

        if (!ModelState.IsValid) return Page();

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
            // "Role with name X already exists" — now scoped per tenant, so
            // this only fires on a genuine clash inside this workspace.
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error creating role");
            ErrorMessage = "Failed to create the role. Please try again.";
            return Page();
        }
    }

    /// <summary>
    /// Reads the Permissions_{module}_{action} checkboxes into the JSON
    /// shape stored on Role.Permissions. Shared with Edit so the two
    /// cannot drift — the format is read by SignInService when building
    /// permission claims, so a mismatch means silent permission loss.
    /// </summary>
    internal static string BuildPermissionsJson(
        IFormCollection form, IEnumerable<ModuleDefinition> modules)
    {
        var permissions = new Dictionary<string, List<string>>();

        foreach (var module in modules)
        {
            var granted = module.Permissions
                .Where(p => form[$"Permissions_{module.Name}_{p}"].ToString() == "true")
                .ToList();

            if (granted.Count > 0)
                permissions[module.Name] = granted;
        }

        return JsonSerializer.Serialize(permissions);
    }
}