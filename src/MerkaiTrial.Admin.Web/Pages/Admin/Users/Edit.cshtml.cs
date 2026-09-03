// =====================================================================
// USER EDIT - Backend
// Location: Pages/Admin/Users/Edit.cshtml.cs
// =====================================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MerkaiTrial.Admin.Web.Services.Users;
using MerkaiTrial.Application.DTOs;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Users
{
    public class EditModel : PageModel
    {
        private readonly IUserService _userService;
        private readonly ILogger<EditModel> _logger;

        public EditModel(IUserService userService, ILogger<EditModel> logger)
        {
            _userService = userService;
            _logger = logger;
        }

        [BindProperty(SupportsGet = true)]
        public Guid TenantId { get; set; }

        [BindProperty(SupportsGet = true)]
        public Guid Id { get; set; }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        public List<string> CurrentRoles { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public DateTime? LastLogin { get; set; }
        public string? ErrorMessage { get; set; }

        public class InputModel
        {
            [Required(ErrorMessage = "First name is required")]
            [StringLength(100)]
            public string FirstName { get; set; } = string.Empty;

            [Required(ErrorMessage = "Last name is required")]
            [StringLength(100)]
            public string LastName { get; set; } = string.Empty;

            [Required(ErrorMessage = "Email is required")]
            [EmailAddress(ErrorMessage = "Invalid email format")]
            [StringLength(320)]
            public string Email { get; set; } = string.Empty;

            [Phone(ErrorMessage = "Invalid phone format")]
            [StringLength(50)]
            public string? Phone { get; set; }

            [StringLength(100)]
            public string? Department { get; set; }

            [StringLength(100)]
            public string? JobTitle { get; set; }

            public bool IsActive { get; set; }
            public bool IsTenantAdmin { get; set; }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                var user = await _userService.GetByIdAsync(TenantId, Id);

                Input = new InputModel
                {
                    FirstName = user.FirstName,
                    LastName = user.LastName,
                    Email = user.Email,
                    Phone = user.Phone,
                    Department = user.Department,
                    JobTitle = user.JobTitle,
                    IsActive = user.IsActive,
                    IsTenantAdmin = user.IsTenantAdmin
                };

                CurrentRoles = user.Roles;
                CreatedAt = user.CreatedAtUtc;
                LastLogin = user.LastLoginUtc;

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
                TempData["Error"] = "Failed to load user.";
                return RedirectToPage("./Index", new { tenantId = TenantId });
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    var user = await _userService.GetByIdAsync(TenantId, Id);
                    CurrentRoles = user.Roles;
                    CreatedAt = user.CreatedAtUtc;
                    LastLogin = user.LastLoginUtc;
                    return Page();
                }

                var command = new UpdateUserCommand(
                    TenantId: TenantId,
                    UserId: Id,
                    FirstName: Input.FirstName,
                    LastName: Input.LastName,
                    Email: Input.Email,
                    Phone: Input.Phone,
                    Department: Input.Department,
                    JobTitle: Input.JobTitle,
                    IsActive: Input.IsActive,
                    IsTenantAdmin: Input.IsTenantAdmin
                );

                await _userService.UpdateAsync(command);

                TempData["Success"] = $"User '{Input.FirstName} {Input.LastName}' updated successfully!";
                return RedirectToPage("./Detail", new { tenantId = TenantId, id = Id });
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                var user = await _userService.GetByIdAsync(TenantId, Id);
                CurrentRoles = user.Roles;
                CreatedAt = user.CreatedAtUtc;
                LastLogin = user.LastLoginUtc;
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating user {UserId}", Id);
                ErrorMessage = "Failed to update user. Please try again.";
                var user = await _userService.GetByIdAsync(TenantId, Id);
                CurrentRoles = user.Roles;
                CreatedAt = user.CreatedAtUtc;
                LastLogin = user.LastLoginUtc;
                return Page();
            }
        }
    }
}
