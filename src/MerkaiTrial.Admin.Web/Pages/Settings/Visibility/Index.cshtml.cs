// =====================================================================
// RECORD VISIBILITY — Settings
// Location: MerkaiTrial.Admin.Web/Pages/Settings/Visibility/Index.cshtml.cs
//
// NEW FILE (rename to Index.cshtml.cs in Pages/Settings/Visibility/).
//
// Two things on one page, because they only make sense together:
//   1. For each role: can it see Own / Team / All leads?
//   2. Teams, who belongs to which (one each) and who manages which
//      (any number) — together, what "Team" means.
//
// Gated by Roles.* — who-sees-what sits beside who-can-do-what.
// Changes apply on the affected users' NEXT page load (scope is read per
// request, not baked into the sign-in cookie), so nobody is logged out.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Security;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.RecordVisibility;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.Admin.Web.Pages.Settings.Visibility;

public class IndexModel : AuthorizedPageModel
{
    private readonly IRecordVisibilityService _visibility;

    protected override string ModuleName => Modules.Roles;

    public IndexModel(
        IRecordVisibilityService visibility,
        IAuthorizationService authorizationService,
        ICurrentUserService currentUserService,
        ILogger<IndexModel> logger)
        : base(authorizationService, currentUserService, logger)
    {
        _visibility = visibility;
    }

    public RecordScopeMatrixDto Matrix { get; private set; } = new(new(), new());
    public TeamsOverviewDto TeamsOverview { get; private set; } = new(new(), new());

    [BindProperty(SupportsGet = true)] public Guid? RenameId { get; set; }

    // =================================================================

    public async Task<IActionResult> OnGetAsync()
    {
        var check = await ValidatePermissionAsync(Actions.Read);
        if (check != null) return check;

        await InitializePermissionsAsync();
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostScopeAsync(Guid roleId, string module, RecordScope scope)
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        try
        {
            await _visibility.SetScopeAsync(roleId, module, scope);
            TempData["SuccessMessage"] = $"Saved. People with that role now see {Describe(scope).ToLowerInvariant()} {module.ToLowerInvariant()}.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't save that change.");
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateTeamAsync(string name, string? description)
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["ErrorMessage"] = "Give the team a name.";
            return RedirectToPage(null, null, "teams");
        }

        try
        {
            await _visibility.CreateTeamAsync(name.Trim(), description);
            TempData["SuccessMessage"] = $"Team \"{name.Trim()}\" added. Now put people in it below.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't add that team.");
        }
        return RedirectToPage(null, null, "teams");
    }

    public async Task<IActionResult> OnPostRenameTeamAsync(Guid teamId, string name, string? description)
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        try
        {
            await _visibility.UpdateTeamAsync(teamId, name, description);
            TempData["SuccessMessage"] = "Team updated.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't rename that team.");
        }
        return RedirectToPage(null, null, "teams");
    }

    public async Task<IActionResult> OnPostDeleteTeamAsync(Guid teamId)
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        try
        {
            await _visibility.DeleteTeamAsync(teamId);
            TempData["SuccessMessage"] = "Team removed. Its members are now in no team.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't remove that team.");
        }
        return RedirectToPage(null, null, "teams");
    }

    public async Task<IActionResult> OnPostUserTeamAsync(Guid userId, Guid? teamId)
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        try
        {
            await _visibility.SetUserTeamAsync(userId, teamId);
            TempData["SuccessMessage"] = "Team membership saved.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't change that person's team.");
        }
        return RedirectToPage(null, null, "teams");
    }

    /// <summary>
    /// Full replacement of the teams this user manages. Unticking every box
    /// posts no teamIds at all, which binds to an empty list = manages none.
    /// </summary>
    public async Task<IActionResult> OnPostManagedTeamsAsync(Guid userId, List<Guid>? teamIds)
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        try
        {
            await _visibility.SetManagedTeamsAsync(userId, teamIds ?? new());
            TempData["SuccessMessage"] = (teamIds?.Count ?? 0) == 0
                ? "Saved. They no longer manage any team."
                : $"Saved. They now manage {teamIds!.Count} team{(teamIds.Count == 1 ? "" : "s")}.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't save the teams they manage.");
        }
        return RedirectToPage(null, null, "teams");
    }

    // =================================================================

    private async Task LoadAsync()
    {
        try
        {
            Matrix = await _visibility.GetScopesAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load record scopes");
            TempData["ErrorMessage"] = "Couldn't load the visibility settings. Please try again.";
        }

        try
        {
            TeamsOverview = await _visibility.GetTeamsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load teams");
            TempData["ErrorMessage"] = "Couldn't load teams. Please try again.";
        }
    }

    /// <summary>
    /// Handler refusals ("You already have a team called …") arrive as
    /// InvalidOperationException with a message written for the user.
    /// Anything else gets the generic fallback, never a raw HTTP dump.
    /// </summary>
    private static string Explain(Exception ex, string fallback)
        => ex is InvalidOperationException && !string.IsNullOrWhiteSpace(ex.Message)
            ? ex.Message
            : fallback;

    // ── View helpers ──────────────────────────────────────────────────

    public static readonly RecordScope[] AllScopes = { RecordScope.Own, RecordScope.Team, RecordScope.All };

    public static string Describe(RecordScope s) => s switch
    {
        RecordScope.Own  => "Own",
        RecordScope.Team => "Team",
        _                => "All"
    };

    public static string ScopeHint(RecordScope s) => s switch
    {
        RecordScope.Own  => "Only records assigned to them",
        RecordScope.Team => "Their team's records, plus unassigned ones",
        _                => "Every record in the workspace"
    };

    public string TeamName(Guid? teamId) =>
        teamId.HasValue
            ? TeamsOverview.Teams.FirstOrDefault(t => t.Id == teamId)?.Name ?? "—"
            : "No team";

    public string ManagedTeamNames(TeamUserDto u)
    {
        var names = TeamsOverview.Teams
            .Where(t => u.ManagedTeamIds.Contains(t.Id))
            .Select(t => t.Name)
            .ToList();
        return names.Count == 0 ? "—" : string.Join(", ", names);
    }

    public bool IsRenaming(TeamDto t) => RenameId.HasValue && RenameId.Value == t.Id;
}
