// =====================================================================
// FILE: Pages/Admin/StartingRoles/Index.cshtml.cs
//
// NEW (117). Starting Roles — the roles every NEW workspace is given.
// Replaces the old super admin "Roles" page, which edited the SYSTEM roles
// every workspace shares (one save changed every customer). Here:
//   • each starting role: what it may do, whether new workspaces get it,
//     and how many workspaces have it today (their own copy);
//   • the system roles, read only, so the page explains the whole picture.
// Super admin only (the /Admin folder convention), and the API checks too.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.RoleTemplates;
using MerkaiTrial.Application.Commands.RoleTemplates;
using MerkaiTrial.Admin.Web.Pages.Settings.Roles;
using MerkaiTrial.Application.Configuration;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages.Admin.StartingRoles
{
    public class IndexModel : PageModel
    {
        private readonly IRoleTemplateService _templates;
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(IRoleTemplateService templates, ILogger<IndexModel> logger)
        {
            _templates = templates;
            _logger = logger;
        }

        public RoleTemplatesOverviewDto Data { get; private set; } = new();
        public string? LoadError { get; private set; }

        public async Task OnGetAsync()
        {
            try
            {
                Data = await _templates.GetAllAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not load the starting roles");
                LoadError = "The starting roles could not be loaded. Please reload the page.";
            }
        }

        /// <summary>"Leads (view, add, edit) · Deals (view)" — the areas a role can reach, in words.</summary>
        public static IReadOnlyList<string> Summary(string? json)
        {
            var modules = ModulesConfiguration.GetAllModules();
            return RolePermissionForm.Parse(json)
                .Where(kv => kv.Value.Count > 0)
                .Select(kv =>
                {
                    var label = RolePermissionForm.ModuleLabel(kv.Key, modules);
                    var actions = string.Join(", ", kv.Value.Select(a => RolePermissionForm.ActionLabel(a).ToLowerInvariant()));
                    return $"{label} ({actions})";
                })
                .ToList();
        }
    }
}
