using MerkaiTrial.Admin.Web.Services.Roles;
using MerkaiTrial.Admin.Web.Services.Tenants;
using MerkaiTrial.Admin.Web.Services.Users;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Users;

public class CreateModel : PageModel
{
    private readonly IUserService _userService;
    private readonly IRoleService _roleService;
    private readonly ITenantService _tenantService;
    private readonly IUserTokenService _tokens;
    private readonly ILogger<CreateModel> _logger;

    private static readonly TimeSpan InviteLifetime = TimeSpan.FromHours(72);

    public CreateModel(
        IUserService userService,
        IRoleService roleService,
        ITenantService tenantService,
        IUserTokenService tokens,
        ILogger<CreateModel> logger)
    {
        _userService = userService;
        _roleService = roleService;
        _tenantService = tenantService;
        _tokens = tokens;
        _logger = logger;
    }

    [BindProperty(SupportsGet = true)] public Guid TenantId { get; set; }
    [BindProperty] public InputModel Input { get; set; } = new();

    public string TenantName { get; set; } = string.Empty;
    public List<RoleLookupDto> AvailableRoles { get; set; } = new();
    public string? ErrorMessage { get; set; }

    /// <summary>Populated after a successful create. Shown once.</summary>
    public string? InviteUrl { get; set; }
    public string? CreatedUserName { get; set; }
    public string? CreatedUserEmail { get; set; }
    public bool InviteFailed { get; set; }

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

        [StringLength(100)] public string? Department { get; set; }
        [StringLength(100)] public string? JobTitle { get; set; }

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
        await LoadDataAsync();

        if (!ModelState.IsValid) return Page();

        UserDto result;

        try
        {
            var command = new CreateUserCommand(
                TenantId: TenantId,
                FirstName: Input.FirstName,
                LastName: Input.LastName,
                Email: Input.Email,
                Phone: Input.Phone,
                Department: Input.Department,
                JobTitle: Input.JobTitle,
                IsTenantAdmin: Input.IsTenantAdmin,
                RoleIds: Input.RoleIds,
                CreatedBy: User.Identity?.Name ?? "SuperAdmin"
            );

            result = await _userService.CreateAsync(command);
        }
        catch (InvalidOperationException ex)
        {
            // Now carries the real API message — over quota, duplicate
            // email, a role from another workspace — rather than
            // "please try again".
            ErrorMessage = ex.Message;
            return Page();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating user");
            ErrorMessage = "Failed to create the user. Please try again.";
            return Page();
        }

        // ── INVITE ───────────────────────────────────────────────────
        // Deliberately AFTER the user is created and outside the try above.
        // If token issuing fails the user still exists and is correct — so
        // the page says so plainly and points at the reset route, rather
        // than implying nothing happened.
        try
        {
            var token = await _tokens.IssueAsync(
                result.Id, TenantId, TokenPurpose.Invite,
                InviteLifetime, User.Identity?.Name ?? "SuperAdmin");

            InviteUrl = $"{Request.Scheme}://{Request.Host}/Account/SetPassword"
                      + $"?token={Uri.EscapeDataString(token)}&purpose=Invite";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "User {UserId} was created but the invite could not be issued", result.Id);
            InviteFailed = true;
        }

        CreatedUserName = $"{result.FirstName} {result.LastName}";
        CreatedUserEmail = result.Email;

        // NOT a redirect. The raw token exists only in memory here — only
        // its hash is stored — so redirecting would lose it and leave a
        // user who can never sign in.
        return Page();
    }

    private async Task LoadDataAsync()
    {
        var tenant = await _tenantService.GetByIdAsync(TenantId);
        TenantName = tenant.Name;

        // Scoped to THIS tenant. Unscoped it returned every tenant's roles —
        // five identical "Sales Manager" rows with no way to tell them apart,
        // and picking the wrong one now fails validation.
        AvailableRoles = await _roleService.GetLookupAsync(TenantId);

        // tenant_admin is granted by the checkbox (User.IsTenantAdmin), which
        // is what PermissionHandler actually reads. Offering it as a role too
        // gives two controls for one thing, and ticking only the role
        // produces a user who looks like an admin and can do nothing.
        AvailableRoles = AvailableRoles
            .Where(r => !string.Equals(r.Name, "tenant_admin", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}