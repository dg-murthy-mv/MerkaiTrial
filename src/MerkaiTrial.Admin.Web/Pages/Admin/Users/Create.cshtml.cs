// =====================================================================
// USER CREATE - Backend
// Location: Pages/Admin/Users/Create.cshtml.cs
// =====================================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MerkaiTrial.Admin.Web.Services.Users;
using MerkaiTrial.Admin.Web.Services.Roles;
using MerkaiTrial.Admin.Web.Services.Tenants;
using MerkaiTrial.Application.DTOs;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Users
{
    public class CreateModel : PageModel
    {
        private readonly IUserService _userService;
        private readonly IRoleService _roleService;
        private readonly ITenantService _tenantService;
        private readonly ILogger<CreateModel> _logger;

        public CreateModel(
            IUserService userService,
            IRoleService roleService,
            ITenantService tenantService,
            ILogger<CreateModel> logger)
        {
            _userService = userService;
            _roleService = roleService;
            _tenantService = tenantService;
            _logger = logger;
        }

        [BindProperty(SupportsGet = true)]
        public Guid TenantId { get; set; }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        public string TenantName { get; set; } = string.Empty;
        public List<RoleLookupDto> AvailableRoles { get; set; } = new();
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

            public bool IsTenantAdmin { get; set; }

            public List<Guid> RoleIds { get; set; } = new();
        }

        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                await LoadDataAsync();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading create user page");
                TempData["Error"] = "Failed to load form.";
                return RedirectToPage("./Index", new { tenantId = TenantId });
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    await LoadDataAsync();
                    return Page();
                }

                var command = new CreateUserCommand(
                    TenantId: TenantId,
                    FirstName: Input.FirstName,
                    LastName: Input.LastName,
                    Email: Input.Email,
                    Phone: Input.Phone,
                    Department: Input.Department,
                    JobTitle: Input.JobTitle,
                    IsTenantAdmin: Input.IsTenantAdmin,
                    RoleIds: Input.RoleIds
                );

                var result = await _userService.CreateAsync(command);

                TempData["Success"] = $"User '{result.FirstName} {result.LastName}' created successfully!";
                return RedirectToPage("./Detail", new { tenantId = TenantId, id = result.Id });
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                await LoadDataAsync();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating user");
                ErrorMessage = "Failed to create user. Please try again.";
                await LoadDataAsync();
                return Page();
            }
        }

        private async Task LoadDataAsync()
        {
            var tenant = await _tenantService.GetByIdAsync(TenantId);
            TenantName = tenant.Name;
            AvailableRoles = await _roleService.GetLookupAsync();
        }
    }
}
