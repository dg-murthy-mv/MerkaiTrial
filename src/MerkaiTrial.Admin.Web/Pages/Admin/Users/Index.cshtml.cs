// =====================================================================
// USERS INDEX - Backend with PageSize Support
// Location: Pages/Admin/Users/Index.cshtml.cs
// =====================================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MerkaiTrial.Admin.Web.Services.Users;
using MerkaiTrial.Admin.Web.Services.Tenants;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Users
{
    public class IndexModel : PageModel
    {
        private readonly IUserService _userService;
        private readonly ITenantService _tenantService;
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(
            IUserService userService,
            ITenantService tenantService,
            ILogger<IndexModel> logger)
        {
            _userService = userService;
            _tenantService = tenantService;
            _logger = logger;
        }

        public PaginatedUsersResponse Users { get; set; } = null!;
        public UserStatsDto Stats { get; set; } = null!;
        public List<TenantLookupDto> Tenants { get; set; } = new();

        [BindProperty(SupportsGet = true)]
        public Guid? TenantId { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? SearchTerm { get; set; }

        [BindProperty(SupportsGet = true)]
        public int Page { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public int PageSize { get; set; } = 5;

        public async Task OnGetAsync()
        {
            // Validate page size
            if (PageSize < 5) PageSize = 5;
            if (PageSize > 100) PageSize = 100;

            await LoadDataAsync();
        }

        public async Task<IActionResult> OnPostDeleteAsync(Guid tenantId, Guid userId)
        {
            try
            {
                await _userService.DeleteAsync(tenantId, userId);
                TempData["Success"] = "User deleted successfully!";
                return RedirectToPage(new { TenantId = tenantId, PageSize });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete user {UserId}", userId);
                TempData["Error"] = $"Failed to delete user: {ex.Message}";
                return RedirectToPage(new { TenantId = tenantId, PageSize });
            }
        }

        private async Task LoadDataAsync()
        {
            try
            {
                // Always load tenants list
                Tenants = await _tenantService.GetLookupAsync();

                // Set default tenant if none selected and tenants exist
                if (!TenantId.HasValue && Tenants.Any())
                {
                    TenantId = Tenants.First().Id;
                }

                // Load tenant-specific data
                if (TenantId.HasValue)
                {
                    Stats = await _userService.GetStatsAsync(TenantId.Value);
                    Users = await _userService.GetPaginatedAsync(
                        TenantId.Value, Page, PageSize, SearchTerm);
                }
                else
                {
                    Stats = new UserStatsDto(0, 0, 0, 0, 0);
                    Users = new PaginatedUsersResponse(new List<UserListItem>(), 0, 1, PageSize, 0);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load user data");
                TempData["Error"] = "Failed to load user data. Please try again.";

                Stats = new UserStatsDto(0, 0, 0, 0, 0);
                Users = new PaginatedUsersResponse(new List<UserListItem>(), 0, 1, PageSize, 0);
                Tenants = new List<TenantLookupDto>();
            }
        }
    }
}
