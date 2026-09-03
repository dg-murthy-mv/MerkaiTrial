// =====================================================================
// ROLE CREATE - Backend
// Location: Pages/Admin/Roles/Create.cshtml.cs
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
    public class CreateModel : PageModel
    {
        private readonly IRoleService _roleService;
        private readonly ILogger<CreateModel> _logger;

        public CreateModel(IRoleService roleService, ILogger<CreateModel> logger)
        {
            _roleService = roleService;
            _logger = logger;
        }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        public List<ModuleDefinition> AvailableModules { get; set; } = new();

        public class InputModel
        {
            [Required(ErrorMessage = "Role name is required")]
            [StringLength(100, MinimumLength = 2, ErrorMessage = "Name must be between 2-100 characters")]
            [RegularExpression(@"^[a-z0-9-]+$", ErrorMessage = "Role name must be lowercase letters, numbers, and hyphens only")]
            public string Name { get; set; } = string.Empty;

            [Required(ErrorMessage = "Display name is required")]
            [StringLength(200, MinimumLength = 2, ErrorMessage = "Display name must be between 2-200 characters")]
            public string DisplayName { get; set; } = string.Empty;

            [StringLength(500, ErrorMessage = "Description cannot exceed 500 characters")]
            public string? Description { get; set; }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                AvailableModules = _roleService.GetAvailableModules();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading create role page");
                TempData["Error"] = "Failed to load form.";
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

                var command = new CreateRoleCommand(
                    Name: Input.Name.Trim().ToLower(),
                    DisplayName: Input.DisplayName.Trim(),
                    Description: Input.Description?.Trim(),
                    Permissions: permissionsJson
                );

                var result = await _roleService.CreateAsync(command);

                TempData["Success"] = $"Role '{result.DisplayName}' created successfully!";
                return RedirectToPage("./Detail", new { id = result.Id });
            }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                AvailableModules = _roleService.GetAvailableModules();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating role");
                ModelState.AddModelError(string.Empty, "Failed to create role. Please try again.");
                AvailableModules = _roleService.GetAvailableModules();
                return Page();
            }
        }
    }
}
