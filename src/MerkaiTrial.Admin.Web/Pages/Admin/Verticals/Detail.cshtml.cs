// =====================================================================
// COMPANY VERTICAL DETAIL - Backend
// Location: Pages/Admin/Verticals/Detail.cshtml.cs
// =====================================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MerkaiTrial.Admin.Web.Services.Verticals;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Pages.Verticals
{
    public class DetailModel : PageModel
    {
        private readonly ICompanyVerticalService _verticalService;
        private readonly ILogger<DetailModel> _logger;

        public DetailModel(
            ICompanyVerticalService verticalService,
            ILogger<DetailModel> logger)
        {
            _verticalService = verticalService;
            _logger = logger;
        }

        public CompanyVerticalDto Vertical { get; set; } = null!;

        [BindProperty(SupportsGet = true)]
        public Guid Id { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                if (Id == Guid.Empty)
                {
                    TempData["ErrorMessage"] = "Vertical ID is required";
                    return RedirectToPage("./Index");
                }

                Vertical = await _verticalService.GetByIdAsync(Id);
                return Page();
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = $"Vertical not found";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading vertical detail for {Id}", Id);
                TempData["ErrorMessage"] = "Failed to load vertical details";
                return RedirectToPage("./Index");
            }
        }
    }
}
