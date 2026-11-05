// =====================================================================
// FILE: Pages/Admin/StartingRoles/Edit.cshtml.cs
//
// NEW (117). Add a starting role (no id) or change one (/{id}): its name,
// description, order, whether new workspaces get it, and the permission
// grid — the same grid workspace admins use for their own roles
// (Settings/Roles/_RolePermissionsGrid), so the two cannot drift.
//
// Saving changes ONLY what the grid shows; anything stored that the grid
// doesn't offer is kept (RolePermissionForm.BuildJson). The API checks
// every module and action against the product's list and refuses
// anything else.
// =====================================================================

using System.ComponentModel.DataAnnotations;
using MerkaiTrial.Admin.Web.Pages.Settings.Roles;
using MerkaiTrial.Admin.Web.Services.RoleTemplates;
using MerkaiTrial.Application.Commands.RoleTemplates;
using MerkaiTrial.Application.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages.Admin.StartingRoles
{
    public class EditModel : PageModel
    {
        private readonly IRoleTemplateService _templates;
        private readonly ILogger<EditModel> _logger;

        public EditModel(IRoleTemplateService templates, ILogger<EditModel> logger)
        {
            _templates = templates;
            _logger = logger;
        }

        [BindProperty(SupportsGet = true)]
        public Guid? Id { get; set; }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        public bool IsNew => Id is null || Id == Guid.Empty;
        public string? Key { get; private set; }
        public int WorkspacesWithIt { get; private set; }
        public string? ErrorMessage { get; private set; }

        public IReadOnlyList<ModuleDefinition> Modules { get; } = ModulesConfiguration.GetAllModules();
        public IReadOnlyDictionary<string, List<string>> Selected { get; private set; } =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public class InputModel
        {
            [Display(Name = "Key")]
            [StringLength(50)]
            [RegularExpression("^[a-z][a-z0-9_]{1,49}$",
                ErrorMessage = "Use lower-case letters, digits and _ only, starting with a letter, like support_agent.")]
            public string? Name { get; set; }

            [Required(ErrorMessage = "Give the role a name people will see, like Support Agent.")]
            [StringLength(200, ErrorMessage = "The name can be up to 200 characters.")]
            [Display(Name = "Name")]
            public string DisplayName { get; set; } = string.Empty;

            [StringLength(500, ErrorMessage = "The description can be up to 500 characters.")]
            public string? Description { get; set; }

            [Range(0, 1000, ErrorMessage = "The order must be between 0 and 1000.")]
            [Display(Name = "Order in lists")]
            public int SortOrder { get; set; }

            [Display(Name = "New workspaces get this role")]
            public bool IsActive { get; set; } = true;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            if (IsNew)
            {
                Input = new InputModel { IsActive = true, SortOrder = 100 };
                return Page();
            }

            try
            {
                var t = await _templates.GetAsync(Id!.Value);
                Key = t.Name;
                WorkspacesWithIt = t.WorkspacesWithIt;
                Selected = RolePermissionForm.Parse(t.Permissions);
                Input = new InputModel
                {
                    Name = t.Name, DisplayName = t.DisplayName, Description = t.Description,
                    SortOrder = t.SortOrder, IsActive = t.IsActive,
                };
                return Page();
            }
            catch (KeyNotFoundException)
            {
                TempData["SuccessMessage"] = null;
                return RedirectToPage("./Index");
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            RoleTemplateDto? existing = null;
            if (!IsNew)
            {
                try { existing = await _templates.GetAsync(Id!.Value); }
                catch (KeyNotFoundException) { return RedirectToPage("./Index"); }
                Key = existing.Name;
                WorkspacesWithIt = existing.WorkspacesWithIt;
                ModelState.Remove("Input.Name");
            }
            else if (string.IsNullOrWhiteSpace(Input.Name))
            {
                ModelState.AddModelError("Input.Name", "Give the role a key, like support_agent.");
            }

            var json = RolePermissionForm.BuildJson(existing?.Permissions, Request.Form, Modules);
            Selected = RolePermissionForm.Parse(json);   // keep the ticks if the page comes back

            if (!ModelState.IsValid) return Page();

            var dto = new SaveRoleTemplateDto
            {
                Name = Input.Name?.Trim().ToLowerInvariant(),
                DisplayName = Input.DisplayName.Trim(),
                Description = Input.Description?.Trim(),
                Permissions = json,
                SortOrder = Input.SortOrder,
                IsActive = Input.IsActive,
            };

            try
            {
                if (IsNew)
                {
                    await _templates.CreateAsync(dto);
                    TempData["SuccessMessage"] = $"Starting role '{dto.DisplayName}' added. New workspaces get it from now on.";
                }
                else
                {
                    await _templates.UpdateAsync(Id!.Value, dto);
                    TempData["SuccessMessage"] = $"Starting role '{dto.DisplayName}' saved. Existing workspaces keep their own copy.";
                }
                return RedirectToPage("./Index");
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving starting role {Id} failed", Id);
                ErrorMessage = "The starting role could not be saved. Please try again.";
                return Page();
            }
        }
    }
}
