// =====================================================================
// ROLES INDEX - Backend with PageSize Support
// Location: Pages/Admin/Roles/Index.cshtml.cs
// =====================================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MerkaiTrial.Admin.Web.Services.Roles;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Roles
{
    public class IndexModel : PageModel
    {
        private readonly IRoleService _roleService;
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(IRoleService roleService, ILogger<IndexModel> logger)
        {
            _roleService = roleService;
            _logger = logger;
        }

        public PaginatedRolesResponse Roles { get; set; } = null!;
        public RoleStatsDto Stats { get; set; } = null!;

        [BindProperty(SupportsGet = true)]
        public string? Search { get; set; }

        [BindProperty(SupportsGet = true)]
        public bool? IsSystemRole { get; set; }

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

        public async Task<IActionResult> OnPostDeleteAsync(Guid id)
        {
            try
            {
                await _roleService.DeleteAsync(id);
                TempData["Success"] = "Role deleted successfully!";
                return RedirectToPage(new { PageSize });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Failed to delete role {RoleId}", id);
                TempData["Error"] = ex.Message;
                return RedirectToPage(new { PageSize });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete role {RoleId}", id);
                TempData["Error"] = "Failed to delete role. Please try again.";
                return RedirectToPage(new { PageSize });
            }
        }

        private async Task LoadDataAsync()
        {
            try
            {
                Stats = await _roleService.GetStatsAsync();
                Roles = await _roleService.GetPaginatedAsync(Page, PageSize, Search, IsSystemRole);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load roles data");
                TempData["Error"] = "Failed to load roles data. Please try again.";

                Stats = new RoleStatsDto(0, 0, 0, 0);
                Roles = new PaginatedRolesResponse(
                    new List<RoleListItem>(), 
                    TotalCount: 0, 
                    Page: 1, 
                    PageSize: PageSize
                );
            }
        }
    }
}
