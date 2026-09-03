// =====================================================================
// TENANTS INDEX - Backend with PageSize Support
// Location: Pages/Admin/Tenants/Index.cshtml.cs
// =====================================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MerkaiTrial.Admin.Web.Services.Tenants;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Tenants
{
    public class IndexModel : PageModel
    {
        private readonly ITenantService _tenantService;
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(ITenantService tenantService, ILogger<IndexModel> logger)
        {
            _tenantService = tenantService;
            _logger = logger;
        }

        public PaginatedTenantsResponse Tenants { get; set; } = null!;
        public TenantStatsDto Stats { get; set; } = null!;

        [BindProperty(SupportsGet = true)]
        public string? SearchTerm { get; set; }

        [BindProperty(SupportsGet = true)]
        public bool? IsActive { get; set; }

        [BindProperty(SupportsGet = true)]
        public int Page { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public int PageSize { get; set; } = 25;

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
                await _tenantService.DeleteAsync(id);
                TempData["Success"] = "Tenant deleted successfully!";
                return RedirectToPage(new { PageSize });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete tenant {TenantId}", id);
                TempData["Error"] = $"Failed to delete tenant: {ex.Message}";
                return RedirectToPage(new { PageSize });
            }
        }

        public async Task<IActionResult> OnPostToggleStatusAsync(Guid id, bool isActive)
        {
            try
            {
                await _tenantService.UpdateStatusAsync(id, isActive);
                TempData["Success"] = $"Tenant {(isActive ? "activated" : "deactivated")} successfully!";
                return RedirectToPage(new { PageSize });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to toggle status for tenant {TenantId}", id);
                TempData["Error"] = $"Failed to update status: {ex.Message}";
                return RedirectToPage(new { PageSize });
            }
        }

        private async Task LoadDataAsync()
        {
            try
            {
                // Get aggregate stats for all tenants
                Stats = await _tenantService.GetAllTenantsStatsAsync();

                // Get paginated tenants
                Tenants = await _tenantService.GetPaginatedAsync(
                    page: Page,
                    pageSize: PageSize,
                    search: SearchTerm,
                    isActive: IsActive
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load tenant data");
                TempData["Error"] = "Failed to load tenant data. Please try again.";

                // Set defaults to prevent null reference
                Stats = new TenantStatsDto(0, 0, 0, 0, 0);
                Tenants = new PaginatedTenantsResponse(new List<TenantListItem>(), 0, 1, PageSize, 0);
            }
        }
    }
}
