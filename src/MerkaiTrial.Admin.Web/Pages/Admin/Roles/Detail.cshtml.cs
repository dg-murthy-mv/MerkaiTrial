// =====================================================================
// ROLE DETAIL - Backend
// Location: Pages/Admin/Roles/Detail.cshtml.cs
// =====================================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MerkaiTrial.Admin.Web.Services.Roles;
using MerkaiTrial.Application.DTOs;
using System.Text.Json;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Roles
{
    public class DetailModel : PageModel
    {
        private readonly IRoleService _roleService;
        private readonly ILogger<DetailModel> _logger;

        public DetailModel(IRoleService roleService, ILogger<DetailModel> logger)
        {
            _roleService = roleService;
            _logger = logger;
        }

        [BindProperty(SupportsGet = true)]
        public Guid Id { get; set; }

        public RoleDto Role { get; set; } = null!;
        public List<UserLookupDto> Users { get; set; } = new();
        public Dictionary<string, List<string>> ParsedPermissions { get; set; } = new();

        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                Role = await _roleService.GetByIdAsync(Id);
                Users = await _roleService.GetRoleUsersAsync(Id);
                
                // Parse permissions JSON
                if (!string.IsNullOrWhiteSpace(Role.Permissions))
                {
                    try
                    {
                        ParsedPermissions = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(Role.Permissions) 
                            ?? new Dictionary<string, List<string>>();
                    }
                    catch
                    {
                        ParsedPermissions = new Dictionary<string, List<string>>();
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
                TempData["Error"] = "Failed to load role details.";
                return RedirectToPage("./Index");
            }
        }
    }
}
