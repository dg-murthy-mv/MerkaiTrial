using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Services.Roles;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.Admin.Web.Pages.Settings.Roles;

// =====================================================================
// FILE: Pages/Settings/Roles/Detail.cshtml.cs
//
// 084 CHANGES
//   - Permissions are read case-insensitively and shown with the module's
//     real name and icon ("Shared Inbox", not "INBOX" / "LEADS").
//   - Every module is listed, including ones the role has NO access to,
//     so "can a rep see the Inbox?" is answerable at a glance.
//   - The Edit button needs roles.update (it used to show to anyone who
//     could open the page and then bounce them).
// =====================================================================
public class DetailModel : AuthorizedPageModel
{
    private readonly IRoleService _roleService;

    protected override string ModuleName => Modules.Roles;

    public DetailModel(
        IRoleService roleService,
        IAuthorizationService authorizationService,
        ICurrentUserService currentUserService,
        ILogger<DetailModel> logger)
        : base(authorizationService, currentUserService, logger)
    {
        _roleService = roleService;
    }

    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }

    public RoleDto Role { get; set; } = null!;
    public List<UserLookupDto> Users { get; set; } = new();

    /// <summary>Raw stored permissions, case-insensitive.</summary>
    public Dictionary<string, List<string>> ParsedPermissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One row per module, in the editor's order, then any extras.</summary>
    public List<RolePermissionRow> PermissionRows { get; set; } = new();

    public int GrantedModuleCount => PermissionRows.Count(r => r.Actions.Count > 0);

    public sealed record RolePermissionRow(string Label, string IconClass, List<string> Actions, bool InEditor);

    public async Task<IActionResult> OnGetAsync()
    {
        var permissionCheck = await ValidatePermissionAsync(Actions.Read);
        if (permissionCheck != null) return permissionCheck;

        try
        {
            Role = await _roleService.GetByIdAsync(Id);

            // Scoped server-side: only THIS tenant's users holding the role.
            Users = await _roleService.GetRoleUsersAsync(Id);

            ParsedPermissions = RolePermissionForm.Parse(Role.Permissions);
            PermissionRows = BuildRows(ParsedPermissions, _roleService.GetAvailableModules());

            // Drives whether the Edit button renders.
            await InitializePermissionsAsync();

            return Page();
        }
        catch (KeyNotFoundException)
        {
            TempData["ErrorMessage"] = "That role could not be found.";
            return RedirectToPage("./Index");
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("404") || ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            TempData["ErrorMessage"] = "That role could not be found.";
            return RedirectToPage("./Index");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading role {RoleId}", Id);
            TempData["ErrorMessage"] = "Failed to load the role.";
            return RedirectToPage("./Index");
        }
    }

    private static List<RolePermissionRow> BuildRows(
        Dictionary<string, List<string>> stored, List<ModuleDefinition> modules)
    {
        var rows = new List<RolePermissionRow>();

        foreach (var module in modules)
        {
            stored.TryGetValue(module.Name, out var granted);
            granted ??= new List<string>();

            // Offered actions in the editor's order, then any stored extras.
            var actions = module.Permissions
                .Where(p => granted.Contains(p, StringComparer.OrdinalIgnoreCase))
                .Concat(granted.Where(g => !module.Permissions.Contains(g, StringComparer.OrdinalIgnoreCase)))
                .ToList();

            rows.Add(new RolePermissionRow(
                module.DisplayName, RolePermissionForm.IconClass(module.Icon), actions, InEditor: true));
        }

        foreach (var (key, actions) in stored)
        {
            if (modules.Any(m => string.Equals(m.Name, key, StringComparison.OrdinalIgnoreCase))) continue;
            if (actions.Count == 0) continue;

            rows.Add(new RolePermissionRow(key, "bi bi-gear", actions.ToList(), InEditor: false));
        }

        return rows;
    }
}
