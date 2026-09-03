// =====================================================================
// ROLE EDIT - Backend
// Location: Pages/Admin/Roles/Edit.cshtml.cs
// =====================================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MerkaiTrial.Admin.Web.Services.Roles;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Roles
{
    public class EditModel : PageModel
    {
        private readonly IRoleService _roleService;
        private readonly ILogger<EditModel> _logger;

        public EditModel(IRoleService roleService, ILogger<EditModel> logger)
        {
            _roleService = roleService;
            _logger = logger;
        }

        [BindProperty(SupportsGet = true)]
        public Guid Id { get; set; }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        [TempData]
        public string? ErrorMessage { get; set; }

        public List<ModuleDefinition> AvailableModules { get; set; } = new();
        public Dictionary<string, List<string>> SelectedPermissions { get; set; } = new();

        public class InputModel
        {
            [Required(ErrorMessage = "Role name is required")]
            [StringLength(100, MinimumLength = 2)]
            public string Name { get; set; } = string.Empty;

            [Required(ErrorMessage = "Display name is required")]
            [StringLength(200, MinimumLength = 2)]
            public string DisplayName { get; set; } = string.Empty;

            [StringLength(500)]
            public string? Description { get; set; }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                var role = await _roleService.GetByIdAsync(Id);

                if (role.IsSystemRole)
                {
                    TempData["Error"] = "System roles cannot be edited.";
                    return RedirectToPage("./Detail", new { id = Id });
                }

                Input = new InputModel
                {
                    Name = role.Name,
                    DisplayName = role.DisplayName,
                    Description = role.Description
                };

                // Load available modules
                AvailableModules = _roleService.GetAvailableModules();

                // Parse existing permissions
                if (!string.IsNullOrWhiteSpace(role.Permissions))
                {
                    try
                    {
                        SelectedPermissions = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(role.Permissions)
                            ?? new Dictionary<string, List<string>>();
                    }
                    catch
                    {
                        SelectedPermissions = new Dictionary<string, List<string>>();
                    }
                }

                return Page();
            }
            catch (KeyNotFoundException)
            {
                TempData["Error"] = "Role not found.";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading role {RoleId}", Id);
                TempData["Error"] = "Failed to load role.";
                return RedirectToPage("./Index");
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    AvailableModules = _roleService.GetAvailableModules();
                    return Page();
                }

                // Build permissions JSON from form
                var permissions = new Dictionary<string, List<string>>();
                
                foreach (var module in _roleService.GetAvailableModules())
                {
                    var modulePermissions = new List<string>();
                    
                    foreach (var permission in module.Permissions)
                    {
                        var fieldName = $"Permissions_{module.Name}_{permission}";
                        if (Request.Form[fieldName].ToString() == "true")
                        {
                            modulePermissions.Add(permission);
                        }
                    }

                    if (modulePermissions.Any())
                    {
                        permissions[module.Name] = modulePermissions;
                    }
                }

                var permissionsJson = JsonSerializer.Serialize(permissions);

                var command = new UpdateRoleCommand(
                    RoleId: Id,
                    Name: Input.Name.Trim(),
                    DisplayName: Input.DisplayName.Trim(),
                    Description: Input.Description?.Trim(),
                    Permissions: permissionsJson
                );

                await _roleService.UpdateAsync(command);

                TempData["Success"] = $"Role '{Input.DisplayName}' updated successfully!";
                return RedirectToPage("./Detail", new { id = Id });
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                AvailableModules = _roleService.GetAvailableModules();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating role {RoleId}", Id);
                ErrorMessage = "Failed to update role. Please try again.";
                AvailableModules = _roleService.GetAvailableModules();
                return Page();
            }
        }
    }
}
