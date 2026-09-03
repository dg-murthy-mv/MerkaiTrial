// =====================================================================
// VERTICALS CREATE - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Admin/Verticals/Create.cshtml.cs
// =====================================================================

using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using MerkaiTrial.Admin.Web.Services.Verticals;
using MerkaiTrial.Admin.Web.Services.Tenants;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Verticals
{
    public class CreateModel : PageModel
    {
        private readonly ICompanyVerticalService _verticalService;
        private readonly ITenantService _tenantService;
        private readonly ILogger<CreateModel> _logger;

        public CreateModel(
            ICompanyVerticalService verticalService,
            ITenantService tenantService,
            ILogger<CreateModel> logger)
        {
            _verticalService = verticalService;
            _tenantService = tenantService;
            _logger = logger;
        }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        public SelectList TenantOptions { get; set; } = new SelectList(Enumerable.Empty<SelectListItem>());
        public SelectList IconOptions { get; set; } = new SelectList(Enumerable.Empty<SelectListItem>());
        public SelectList ColorOptions { get; set; } = new SelectList(Enumerable.Empty<SelectListItem>());

        [TempData]
        public string? ErrorMessage { get; set; }

        public class InputModel
        {
            [Required(ErrorMessage = "Please select vertical type")]
            public string VerticalType { get; set; } = "System";

            public Guid? TenantId { get; set; }

            [Required(ErrorMessage = "Vertical name is required")]
            [StringLength(100, ErrorMessage = "Vertical name cannot exceed 100 characters")]
            public string Name { get; set; } = string.Empty;

            [StringLength(500, ErrorMessage = "Description cannot exceed 500 characters")]
            public string? Description { get; set; }

            [Required(ErrorMessage = "Please select an icon")]
            public string? Icon { get; set; }

            [Required(ErrorMessage = "Please select a color")]
            public string? Color { get; set; }
        }

        public async Task OnGetAsync()
        {
            await LoadDropdownsAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            try
            {
                if (Input.VerticalType == "Custom" && !Input.TenantId.HasValue)
                {
                    ModelState.AddModelError("Input.TenantId", "Please select a tenant for custom vertical");
                }

                if (!ModelState.IsValid)
                {
                    await LoadDropdownsAsync();
                    return Page();
                }

                var dto = new CreateCompanyVerticalDto
                {
                    TenantId = Input.VerticalType == "System" ? Guid.Empty : Input.TenantId!.Value,
                    Name = Input.Name.Trim(),
                    Description = Input.Description?.Trim(),
                    Icon = Input.Icon,
                    Color = Input.Color,
                    CreatedBy = "admin"
                };

                var vertical = await _verticalService.CreateAsync(dto);

                var verticalTypeText = Input.VerticalType == "System" ? "System" : "Custom";
                TempData["SuccessMessage"] = $"{verticalTypeText} vertical '{Input.Name}' created successfully!";
                return RedirectToPage("./Detail", new { id = vertical.Id });
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                await LoadDropdownsAsync();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating vertical");
                ErrorMessage = "Failed to create vertical. Please try again.";
                await LoadDropdownsAsync();
                return Page();
            }
        }

        private async Task LoadDropdownsAsync()
        {
            try
            {
                var tenants = await _tenantService.GetLookupAsync();
                TenantOptions = new SelectList(
                    tenants.OrderBy(t => t.Name),
                    nameof(TenantLookupDto.Id),
                    nameof(TenantLookupDto.Name)
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load tenants");
                TenantOptions = new SelectList(Enumerable.Empty<SelectListItem>());
            }

            var icons = new List<SelectListItem>
            {
                new SelectListItem { Value = "briefcase", Text = "Briefcase - Business" },
                new SelectListItem { Value = "building", Text = "Building - Office" },
                new SelectListItem { Value = "shop", Text = "Shop - Retail" },
                new SelectListItem { Value = "truck", Text = "Truck - Logistics" },
                new SelectListItem { Value = "laptop", Text = "Laptop - Technology" },
                new SelectListItem { Value = "hospital", Text = "Hospital - Healthcare" },
                new SelectListItem { Value = "mortarboard", Text = "Mortarboard - Education" },
                new SelectListItem { Value = "house", Text = "House - Real Estate" },
                new SelectListItem { Value = "bank", Text = "Bank - Finance" },
                new SelectListItem { Value = "tools", Text = "Tools - Manufacturing" },
                new SelectListItem { Value = "megaphone", Text = "Megaphone - Marketing" },
                new SelectListItem { Value = "camera", Text = "Camera - Media" },
                new SelectListItem { Value = "cart", Text = "Cart - E-commerce" },
                new SelectListItem { Value = "geo-alt", Text = "Location - Services" },
                new SelectListItem { Value = "people", Text = "People - Consulting" }
            };
            IconOptions = new SelectList(icons, "Value", "Text", Input.Icon);

            var colors = new List<SelectListItem>
            {
                new SelectListItem { Value = "primary", Text = "Primary - Blue" },
                new SelectListItem { Value = "success", Text = "Success - Green" },
                new SelectListItem { Value = "danger", Text = "Danger - Red" },
                new SelectListItem { Value = "warning", Text = "Warning - Orange" },
                new SelectListItem { Value = "info", Text = "Info - Cyan" },
                new SelectListItem { Value = "secondary", Text = "Secondary - Gray" }
            };
            ColorOptions = new SelectList(colors, "Value", "Text", Input.Color);
        }
    }
}
