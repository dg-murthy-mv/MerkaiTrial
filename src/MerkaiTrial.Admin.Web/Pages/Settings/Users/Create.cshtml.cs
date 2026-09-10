// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Settings/Users/Create.cshtml.cs
//
// The tenant-facing "add a colleague" page.
//
// DIFFERENCES FROM /Admin/Users/Create
//
//   The tenant is taken from the signed-in user, never from the URL. The
//   API ignores a tenantId sent by a non-super-admin anyway, but the page
//   should not offer one in the first place.
//
//   The quota is checked BEFORE showing the form, so someone at 10 of 10
//   is told up front rather than after typing everything in.
//
//   Only this tenant's roles are offered, and tenant_admin is filtered
//   out — the "make them an administrator" checkbox is the one control
//   for that, matching what PermissionHandler actually reads.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Services.Roles;
using MerkaiTrial.Admin.Web.Services.Users;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Users;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Settings.Users;

public class CreateModel : AuthorizedPageModel
{
    private readonly IUserService _userService;
    private readonly IRoleService _roleService;
    private readonly IUserTokenService _tokens;

    private static readonly TimeSpan InviteLifetime = TimeSpan.FromHours(72);

    protected override string ModuleName => Modules.Users;

    public CreateModel(
        IUserService userService,
        IRoleService roleService,
        IUserTokenService tokens,
        IAuthorizationService authorizationService,
        ICurrentUserService currentUserService,
        ILogger<CreateModel> logger)
        : base(authorizationService, currentUserService, logger)
    {
        _userService = userService;
        _roleService = roleService;
        _tokens      = tokens;
    }

    [BindProperty] public InputModel Input { get; set; } = new();

    public List<RoleLookupDto> AvailableRoles { get; set; } = new();
    public string? ErrorMessage { get; set; }

    public int CurrentUserCount { get; private set; }
    public bool AtLimit => CurrentUserCount >= MaxUsers;

    public string? InviteUrl { get; set; }
    public string? CreatedUserName { get; set; }
    public string? CreatedUserEmail { get; set; }
    public bool InviteFailed { get; set; }
    private string ActorName =>
       User.Identity?.Name
       ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
       ?? "TenantAdmin";
    public class InputModel
    {
        [Required(ErrorMessage = "First name is required")]
        [StringLength(100)]
        public string FirstName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Last name is required")]
        [StringLength(100)]
        public string LastName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "That does not look like an email address")]
        [StringLength(320)]
        public string Email { get; set; } = string.Empty;

        [Phone(ErrorMessage = "That does not look like a phone number")]
        [StringLength(50)]
        public string? Phone { get; set; }

        [StringLength(100)] public string? JobTitle { get; set; }
        [StringLength(100)] public string? Department { get; set; }

        public bool IsTenantAdmin { get; set; }
        public List<Guid> RoleIds { get; set; } = new();
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var permissionCheck = await ValidatePermissionAsync(Actions.Create);
        if (permissionCheck != null) return permissionCheck;

        await InitializePermissionsAsync();
        await LoadDataAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var permissionCheck = await ValidatePermissionAsync(Actions.Create);
        if (permissionCheck != null) return permissionCheck;

        await InitializePermissionsAsync();
        await LoadDataAsync();

        if (AtLimit)
        {
            // The API enforces this too. Checking here as well means the
            // message is immediate rather than after a round trip.
            ErrorMessage = $"Your plan allows {MaxUsers} users and you have {CurrentUserCount}. "
                         + "Deactivate someone, or upgrade to add more.";
            return Page();
        }

        if (!ModelState.IsValid) return Page();

        var tenantId = CurrentUserService.GetCurrentTenantId();
        UserDto result;

        try
        {
            result = await _userService.CreateAsync(new CreateUserCommand(
                TenantId:      tenantId,
                FirstName:     Input.FirstName,
                LastName:      Input.LastName,
                Email:         Input.Email,
                Phone:         Input.Phone,
                Department:    Input.Department,
                JobTitle:      Input.JobTitle,
                IsTenantAdmin: Input.IsTenantAdmin,
                RoleIds:       Input.RoleIds,
                CreatedBy: ActorName));
        }
        catch (InvalidOperationException ex)
        {
            // Carries the real message now — over quota, email already in
            // use, a role that is not this workspace's.
            ErrorMessage = ex.Message;
            return Page();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create user in tenant {TenantId}", tenantId);
            ErrorMessage = "Could not add that person. Please try again.";
            return Page();
        }

        // Outside the try above on purpose: if the token fails, the person
        // still exists and is correct. Saying "failed" flatly would send
        // the admin looking for someone who is already there.
        try
        {
            var token = await _tokens.IssueAsync(
                result.Id, tenantId, TokenPurpose.Invite,InviteLifetime, ActorName);

            InviteUrl = $"{Request.Scheme}://{Request.Host}/Account/SetPassword"
                      + $"?token={Uri.EscapeDataString(token)}&purpose=Invite";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "User {UserId} created but invite could not be issued", result.Id);
            InviteFailed = true;
        }

        CreatedUserName  = $"{result.FirstName} {result.LastName}";
        CreatedUserEmail = result.Email;

        // Page(), not a redirect: the raw token exists only here.
        return Page();
    }

    private async Task LoadDataAsync()
    {
        var tenantId = CurrentUserService.GetCurrentTenantId();

        try
        {
            var stats = await _userService.GetStatsAsync(tenantId);
            CurrentUserCount = stats.TotalUsers;

            var roles = await _roleService.GetLookupAsync(tenantId);

            // tenant_admin is granted by the checkbox, which sets
            // User.IsTenantAdmin — the flag PermissionHandler reads.
            // Offering it as a role as well means ticking only the role
            // produces someone who looks like an admin and can do nothing.
            AvailableRoles = roles
                .Where(r => !string.Equals(r.Name, "tenant_admin", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load the add-person form for tenant {TenantId}", tenantId);
            AvailableRoles = new List<RoleLookupDto>();
        }
    }
}
