// =====================================================================
// COMPANY VERTICALS INDEX - MOBILE BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Admin/Verticals/Index.cshtml.cs
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Verticals;
using MerkaiTrial.Admin.Web.Services.Tenants;
using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MerkaiTrial.Admin.Web.Pages.Verticals
{
    public class IndexModel : PageModel
    {
        private readonly ICompanyVerticalService _verticalService;
        private readonly ITenantService _tenantService;
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(
            ICompanyVerticalService verticalService,
            ITenantService tenantService,
            ILogger<IndexModel> logger)
        {
            _verticalService = verticalService;
            _tenantService = tenantService;
            _logger = logger;
        }

        public PaginatedResult<CompanyVerticalListItem> PaginatedVerticals { get; set; } = new();
        public VerticalStatsDto Stats { get; set; } = null!;
        public List<TenantLookupDto> Tenants { get; set; } = new();

        [BindProperty(SupportsGet = true)]
        public int Page { get; set; } = 1;  // ✅ Changed from PageNumber

        [BindProperty(SupportsGet = true)]
        public int PageSize { get; set; } = 25;  // ✅ ADDED

        [BindProperty(SupportsGet = true)]
        public string? SearchTerm { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? VerticalTypeFilter { get; set; }

        [BindProperty(SupportsGet = true)]
        public Guid? TenantFilter { get; set; }
        [TempData]
        public string? SuccessMessage { get; set; }

        [TempData]
        public string? ErrorMessage { get; set; }
        public async Task OnGetAsync()
        {
            // Validate PageSize
            if (PageSize < 5) PageSize = 5;
            if (PageSize > 100) PageSize = 100;

            await LoadDataAsync();
        }

        private async Task LoadDataAsync()
        {
            try
            {
                Guid? apiTenantId = null;
                bool showSystemOnly = false;
                bool showCustomOnly = false;
                bool showAllVerticals = false;

                // Determine filter flags
                if (VerticalTypeFilter == "System")
                {
                    showSystemOnly = true;
                }
                else if (VerticalTypeFilter == "Custom")
                {
                    showCustomOnly = true;
                    if (TenantFilter.HasValue)
                    {
                        apiTenantId = TenantFilter.Value;
                    }
                }
                else
                {
                    if (TenantFilter.HasValue)
                    {
                        apiTenantId = TenantFilter.Value;
                    }
                    else
                    {
                        showAllVerticals = true;
                    }
                }

                // Get statistics
                Stats = await _verticalService.GetStatsAsync();

                // Get paginated verticals
                PaginatedVerticals = await _verticalService.GetAllAsync(
                    tenantId: apiTenantId,
                    pageNumber: Page,  // ✅ Uses Page
                    pageSize: PageSize,  // ✅ Uses PageSize
                    searchTerm: SearchTerm,
                    showSystemOnly: showSystemOnly,
                    showCustomOnly: showCustomOnly,
                    showAllVerticals: showAllVerticals
                );

                // Load tenants for filter
                Tenants = await _tenantService.GetLookupAsync();

                _logger.LogInformation(
                    "Loaded page {Page} with {Count} verticals (Total: {Total})",
                    Page,
                    PaginatedVerticals.Items.Count,
                    PaginatedVerticals.TotalCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading verticals");
                TempData["ErrorMessage"] = "Failed to load verticals. Please try again.";

                Stats = new VerticalStatsDto(0, 0, 0, 0);
                PaginatedVerticals = new PaginatedResult<CompanyVerticalListItem>
                {
                    Items = new List<CompanyVerticalListItem>(),
                    Page = 1,
                    PageSize = PageSize,
                    TotalCount = 0
                };
                Tenants = new List<TenantLookupDto>();
            }
        }

        public async Task<IActionResult> OnPostDeleteAsync(Guid id)
        {
            try
            {
                await _verticalService.DeleteAsync(id);
                TempData["SuccessMessage"] = "Vertical deleted successfully!";
                return RedirectToPage();
            }
            catch (InvalidOperationException ex)
            {
                TempData["ErrorMessage"] = ex.Message;
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting vertical {Id}", id);
                TempData["ErrorMessage"] = "Failed to delete vertical. Please try again.";
                return RedirectToPage();
            }
        }

        public SelectList GetVerticalTypeFilterOptions()
        {
            var items = new List<SelectListItem>
            {
                new SelectListItem { Value = "", Text = "All Types" },
                new SelectListItem { Value = "System", Text = "System Only" },
                new SelectListItem { Value = "Custom", Text = "Custom Only" }
            };
            return new SelectList(items, "Value", "Text", VerticalTypeFilter);
        }

        public SelectList GetTenantFilterOptions()
        {
            var items = new List<SelectListItem>
            {
                new SelectListItem { Value = "", Text = "All Tenants" }
            };
            items.AddRange(Tenants.Select(t => new SelectListItem
            {
                Value = t.Id.ToString(),
                Text = t.Name
            }));
            return new SelectList(items, "Value", "Text", TenantFilter?.ToString());
        }
    }
}
