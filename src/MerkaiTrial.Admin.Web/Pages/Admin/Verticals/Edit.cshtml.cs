// =====================================================================
// VERTICALS EDIT - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Admin/Verticals/Edit.cshtml.cs
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Verticals;
using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Verticals
{
    public class EditModel : PageModel
    {
        private readonly ICompanyVerticalService _verticalService;
        private readonly ILogger<EditModel> _logger;

        public EditModel(
            ICompanyVerticalService verticalService,
            ILogger<EditModel> logger)
        {
            _verticalService = verticalService;
            _logger = logger;
        }

        [BindProperty]
        public Guid Id { get; set; }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        public bool IsSystemVertical { get; set; } = false;
        public SelectList IconOptions { get; set; } = new SelectList(Enumerable.Empty<SelectListItem>());
        public SelectList ColorOptions { get; set; } = new SelectList(Enumerable.Empty<SelectListItem>());

        [TempData]
        public string? ErrorMessage { get; set; }

        public class InputModel
        {
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

        public async Task<IActionResult> OnGetAsync(Guid id)
        {
            try
            {
                if (id == Guid.Empty)
                {
                    TempData["ErrorMessage"] = "Vertical ID is required.";
                    return RedirectToPage("./Index");
                }

                Id = id;
                var vertical = await _verticalService.GetByIdAsync(id);

                IsSystemVertical = vertical.IsSystem;

                Input = new InputModel
                {
                    Name = vertical.Name,
                    Description = vertical.Description,
                    Icon = vertical.Icon,
                    Color = vertical.Color
                };

                LoadDropdowns();
                return Page();
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Vertical not found.";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading vertical {Id}", id);
                TempData["ErrorMessage"] = "Failed to load vertical. Please try again.";
                return RedirectToPage("./Index");
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            try
            {
                CompanyVerticalDto? vertical = null;
                try
                {
                    vertical = await _verticalService.GetByIdAsync(Id);
                    IsSystemVertical = vertical.IsSystem;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error loading vertical {Id} during post", Id);
                    TempData["ErrorMessage"] = "Vertical not found.";
                    return RedirectToPage("./Index");
                }

                if (!ModelState.IsValid)
                {
                    LoadDropdowns();
                    return Page();
                }

                var dto = new UpdateCompanyVerticalDto
                {
                    Id = Id,
                    TenantId = vertical.TenantId ?? Guid.Empty,
                    Name = Input.Name.Trim(),
                    Description = Input.Description?.Trim(),
                    Icon = Input.Icon,
                    Color = Input.Color,
                    UpdatedBy = "admin"
                };

                await _verticalService.UpdateAsync(Id, dto);

                TempData["SuccessMessage"] = $"Vertical '{Input.Name}' updated successfully!";
                return RedirectToPage("./Detail", new { id = Id });
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Vertical not found.";
                return RedirectToPage("./Index");
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;

                try
                {
                    var vertical = await _verticalService.GetByIdAsync(Id);
                    IsSystemVertical = vertical.IsSystem;
                }
                catch
                {
                    IsSystemVertical = false;
                }

                LoadDropdowns();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating vertical {Id}", Id);
                ErrorMessage = "Failed to update vertical. Please try again.";

                try
                {
                    var vertical = await _verticalService.GetByIdAsync(Id);
                    IsSystemVertical = vertical.IsSystem;
                }
                catch
                {
                    IsSystemVertical = false;
                }

                LoadDropdowns();
                return Page();
            }
        }

        private void LoadDropdowns()
        {
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
