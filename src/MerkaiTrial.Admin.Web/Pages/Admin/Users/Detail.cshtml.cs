// =====================================================================
// USER DETAIL - Backend
// Location: Pages/Admin/Users/Detail.cshtml.cs
// =====================================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MerkaiTrial.Admin.Web.Services.Users;
using MerkaiTrial.Admin.Web.Services.Roles;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Users
{
    public class DetailModel : PageModel
    {
        private readonly IUserService _userService;
        private readonly IRoleService _roleService;
        private readonly ILogger<DetailModel> _logger;

        public DetailModel(
            IUserService userService,
            IRoleService roleService,
            ILogger<DetailModel> logger)
        {
            _userService = userService;
            _roleService = roleService;
            _logger = logger;
        }

        [BindProperty(SupportsGet = true)]
        public Guid TenantId { get; set; }

        [BindProperty(SupportsGet = true)]
        public Guid Id { get; set; }

        public UserDto User { get; set; } = null!;
        public List<RoleLookupDto> AvailableRoles { get; set; } = new();

        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                User = await _userService.GetByIdAsync(TenantId, Id);
                AvailableRoles = await _roleService.GetLookupAsync();
                return Page();
            }
            catch (KeyNotFoundException)
            {
                TempData["Error"] = "User not found.";
                return RedirectToPage("./Index", new { tenantId = TenantId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading user {UserId}", Id);
                TempData["Error"] = "Failed to load user details.";
                return RedirectToPage("./Index", new { tenantId = TenantId });
            }
        }

        public async Task<IActionResult> OnPostAssignRolesAsync(Guid tenantId, Guid userId, List<Guid> roleIds)
        {
            try
            {
                var command = new AssignUserRolesCommand(tenantId, userId, roleIds ?? new List<Guid>());
                await _userService.AssignRolesAsync(command);

                TempData["Success"] = "Roles assigned successfully!";
                return RedirectToPage(new { tenantId, id = userId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to assign roles to user {UserId}", userId);
                TempData["Error"] = $"Failed to assign roles: {ex.Message}";
                return RedirectToPage(new { tenantId, id = userId });
            }
        }
    }
}
