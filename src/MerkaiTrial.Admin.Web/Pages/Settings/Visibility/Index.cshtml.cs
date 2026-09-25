// =====================================================================
// RECORD VISIBILITY — Settings
// Location: MerkaiTrial.Admin.Web/Pages/Settings/Visibility/Index.cshtml.cs
//
// COMPLETE FILE — replaces the previous version.
//
// Two things on one page, because they only make sense together:
//   1. For each role: can it see Own / Team / All records?
//   2. Teams, who belongs to which (one each) and who manages which
//      (any number) — together, what "Team" means.
//
// Gated by Roles.* — who-sees-what sits beside who-can-do-what.
// Changes apply on the affected users' NEXT page load (scope is read per
// request, not baked into the sign-in cookie), so nobody is logged out.
//
// CHANGES (026)
//   ✅ THE MATRIX IS ONE FORM WITH ONE SAVE. It used to post on every
//      select change: a full page reload and a separate green banner per
//      cell, and no way to see what you were about to do before doing it.
//
//   ✅ EVERY FORM CARRIES THE VALUE IT WAS RENDERED WITH, and the server
//      compares three things, not two:
//
//        rendered  what this page showed when it loaded (posted back)
//        posted    what the user chose
//        current   what is in the database right now
//
//      posted == rendered            → the user didn't touch it. Do
//                                      nothing, even if current differs
//                                      (that is somebody else's change
//                                      and must NOT be reverted).
//      rendered != current           → somebody changed it while this page
//                                      was open. Refuse that one and say
//                                      so. Never overwrite blind.
//      otherwise                     → write it.
//
//      Without the rendered value, saving one cell silently reverted
//      every other admin's change back to whatever this page happened to
//      load with — including widening a role somebody had just narrowed.
//      The same applies to a person's teams, where
//      SetManagedTeamsAsync REPLACES the whole set: changing somebody's
//      team would have wiped managerships added since the page loaded.
//
//   ✅ ONE SAVE PER PERSON. Team membership and the teams they manage are
//      saved together from a single expanded panel — and each half is
//      only written if that half actually changed.
//
//   ✅ PARTIAL FAILURE IS REPORTED HONESTLY. If the team saved and the
//      managed teams didn't, the message says exactly that rather than
//      claiming the whole thing failed. (Round 025's lesson.)
//
//   ✅ AN ABSENT SCOPE IS SKIPPED, NOT TREATED AS "Own". RecordScope.Own
//      is 0, so a missing radio used to bind to the NARROWEST setting and
//      be saved as if the user had chosen it. Scope is now nullable.
//
//   ✅ A LOAD FAILURE NO LONGER MASQUERADES AS A FACT. An empty matrix
//      because the API was down used to render "no module enforces record
//      visibility yet". The two cases now have their own messages, shown
//      inside the affected card.
//
//   ✅ Every handler validates a permission before doing anything, and
//      the page never renders its own global alert block — _Layout does
//      that once.
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
    /// <summary>
    /// A ceiling on one submit. Roles × modules is small, so anything past
    /// this is a malformed or hand-crafted post, not somebody using the
    /// page. Two form values per cell keeps us well inside ASP.NET's
    /// default form value limit of 1024.
    /// </summary>
    private const int MaxCellsPerPost = 400;

    /// <summary>How many changes to spell out in the confirmation message.</summary>
    private const int MaxChangesListed = 4;

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

    /// <summary>Set when the matrix couldn't be read, so the view doesn't claim there is nothing to set.</summary>
    public string? MatrixError { get; private set; }

    /// <summary>Set when teams and people couldn't be read.</summary>
    public string? TeamsError { get; private set; }

    /// <summary>Kept so older links (?RenameId=…) still open the right panel, and set again after a failed rename.</summary>
    [BindProperty(SupportsGet = true)] public Guid? RenameId { get; set; }

    /// <summary>
    /// One posted cell of the scope grid.
    ///
    /// Ref packs the three things the server needs to identify and verify
    /// the cell — "roleId|module|renderedRank" — into a single form value,
    /// so a cell costs two values instead of four and a big workspace
    /// can't quietly blow ASP.NET's form value limit.
    /// </summary>
    public class ScopeCellInput
    {
        public string? Ref { get; set; }

        /// <summary>Nullable on purpose: absent must not mean Own (which is 0).</summary>
        public RecordScope? Scope { get; set; }
    }

    [BindProperty] public List<ScopeCellInput> Cells { get; set; } = new();

    // =================================================================
    // GET
    // =================================================================

    public async Task<IActionResult> OnGetAsync()
    {
        var check = await ValidatePermissionAsync(Actions.Read);
        if (check != null) return check;

        await InitializePermissionsAsync();
        await LoadAsync();
        return Page();
    }

    // =================================================================
    // THE SCOPE GRID — one submit for the whole thing
    // =================================================================

    public async Task<IActionResult> OnPostScopesAsync()
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        var posted = (Cells ?? new())
            .Where(c => !string.IsNullOrWhiteSpace(c.Ref))
            .ToList();

        if (posted.Count == 0)
        {
            // Reached when there is nothing editable at all — for example a
            // brand-new workspace whose only role is Tenant Administrator,
            // which is locked. The view hides the Save button in that case,
            // so this is the belt to its braces.
            TempData["ErrorMessage"] = "There was nothing to save. Every role on this page is either locked or read-only for you.";
            return RedirectToPage(null, null, "visibility");
        }

        if (posted.Count > MaxCellsPerPost)
        {
            TempData["ErrorMessage"] = "That submit had far more settings in it than this page has. Nothing was changed — please reload and try again.";
            return RedirectToPage(null, null, "visibility");
        }

        // Read the CURRENT state before writing anything, so a value
        // somebody else changed while this page was open is never silently
        // overwritten with the one this page was rendered with.
        RecordScopeMatrixDto current;
        try
        {
            current = await _visibility.GetScopesAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to re-read record scopes before saving");
            TempData["ErrorMessage"] = "Couldn't check the current settings, so nothing was changed. Please try again.";
            return RedirectToPage(null, null, "visibility");
        }

        var byRole = current.Roles.ToDictionary(r => r.RoleId);

        // Keyed, so a duplicated cell in a malformed post collapses to one
        // change rather than two calls that fight each other.
        var changes = new Dictionary<(Guid RoleId, string Module), (RoleScopeRowDto Role, string Module, RecordScope From, RecordScope To)>();
        var conflicts = new List<string>();

        foreach (var c in posted)
        {
            if (!TryParseRef(c.Ref, out var roleId, out var rawModule, out var renderedScope)) continue;
            if (c.Scope is not RecordScope chosen) continue;                        // absent radio — skip, never assume
            if (!Enum.IsDefined(typeof(RecordScope), chosen)) continue;

            var touched = chosen != renderedScope;

            // From here on, anything that stops us has to be REPORTED when
            // the user actually changed this cell. Dropping it quietly would
            // end in "nothing to save" after they plainly changed something.
            if (!byRole.TryGetValue(roleId, out var role))
            {
                if (touched) conflicts.Add($"a role that has since been removed / {rawModule}");
                continue;
            }

            if (role.IsLocked)
            {
                if (touched) conflicts.Add($"{role.DisplayName} / {rawModule} — that role now sees everything and can't be narrowed");
                continue;
            }

            var module = current.Modules
                .FirstOrDefault(m => string.Equals(m, rawModule, StringComparison.OrdinalIgnoreCase));

            if (module == null)
            {
                if (touched) conflicts.Add($"{role.DisplayName} / {rawModule} — that module no longer enforces record visibility");
                continue;
            }

            // The user left this cell alone. Do nothing — in particular, do
            // NOT push the rendered value back over somebody else's change.
            if (!touched) continue;

            var live = role.Scopes.TryGetValue(module, out var l) ? l : RecordScope.All;

            if (live == chosen)
            {
                // Somebody else already set it to exactly what was wanted.
                // Nothing to write, and nothing to complain about.
                continue;
            }

            if (live != renderedScope)
            {
                conflicts.Add($"{role.DisplayName} / {module} — somebody changed this to {Describe(live)} while you had the page open");
                continue;
            }

            changes[(role.RoleId, module)] = (role, module, renderedScope, chosen);
        }

        if (changes.Count == 0 && conflicts.Count == 0)
        {
            TempData["SuccessMessage"] = "Nothing to save — those are the settings already.";
            return RedirectToPage(null, null, "visibility");
        }

        var saved = new List<string>();
        var failed = new List<string>();

        foreach (var ch in changes.Values)
        {
            try
            {
                await _visibility.SetScopeAsync(ch.Role.RoleId, ch.Module, ch.To);
                saved.Add($"{ch.Role.DisplayName} → {ch.Module}: {Describe(ch.From)} to {Describe(ch.To)}");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to set record scope for role {RoleId} / {Module}", ch.Role.RoleId, ch.Module);
                failed.Add($"{ch.Role.DisplayName} / {ch.Module} — {Explain(ex, "couldn't be saved")}");
            }
        }

        var notSaved = failed.Concat(conflicts).ToList();

        // Report exactly what committed. Never say "failed" for work that
        // has already been written, and never say "saved" for work that
        // hasn't.
        if (notSaved.Count == 0)
        {
            TempData["SuccessMessage"] = saved.Count == 1
                ? $"Saved — {saved[0]}. It applies on their next click; nobody is signed out."
                : $"Saved {saved.Count} changes: {Join(saved)}. They apply on the next click; nobody is signed out.";
        }
        else if (saved.Count == 0)
        {
            TempData["ErrorMessage"] = $"Nothing was changed. {Join(notSaved)}. Reload the page to see where things stand.";
        }
        else
        {
            TempData["ErrorMessage"] =
                $"{saved.Count} of {saved.Count + notSaved.Count} changes were saved ({Join(saved)}). " +
                $"These were not: {Join(notSaved)}. Reload the page to see where things stand.";
        }

        return RedirectToPage(null, null, "visibility");
    }

    /// <summary>
    /// "roleId|module|renderedRank". Split on the FIRST and LAST separator
    /// so a module name containing a pipe can't break it.
    /// </summary>
    private static bool TryParseRef(string? value, out Guid roleId, out string module, out RecordScope rendered)
    {
        roleId = Guid.Empty;
        module = string.Empty;
        rendered = RecordScope.Own;

        if (string.IsNullOrWhiteSpace(value)) return false;

        var first = value.IndexOf('|');
        var last = value.LastIndexOf('|');
        if (first <= 0 || last <= first || last == value.Length - 1) return false;

        if (!Guid.TryParse(value[..first], out roleId) || roleId == Guid.Empty) return false;

        module = value[(first + 1)..last];
        if (string.IsNullOrWhiteSpace(module)) return false;

        if (!int.TryParse(value[(last + 1)..], out var rank)) return false;

        var match = AllScopes.Cast<RecordScope?>().FirstOrDefault(s => Rank(s!.Value) == rank);
        if (match == null) return false;

        rendered = match.Value;
        return true;
    }

    // =================================================================
    // TEAMS
    // =================================================================

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
            TempData["SuccessMessage"] = $"Team \"{name.Trim()}\" added. Next: put people in it, and give it a manager — until it has one who can sign in, its quote approvals all go to workspace admins.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create team");
            TempData["ErrorMessage"] = Explain(ex, "Couldn't add that team.");
        }

        return RedirectToPage(null, null, "teams");
    }

    public async Task<IActionResult> OnPostRenameTeamAsync(Guid teamId, string name, string? description)
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        if (teamId == Guid.Empty)
        {
            TempData["ErrorMessage"] = "That team couldn't be identified. Please reload the page.";
            return RedirectToPage(null, null, "teams");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["ErrorMessage"] = "Give the team a name.";
            // Reopen the panel so whatever they typed is in front of them.
            // Four arguments: (pageName, pageHandler, routeValues, fragment).
            // The three-argument form takes the handler second, not route
            // values, so an anonymous type there doesn't compile.
            return RedirectToPage(null, null, new { RenameId = teamId }, "teams");
        }

        try
        {
            await _visibility.UpdateTeamAsync(teamId, name.Trim(), description);
            TempData["SuccessMessage"] = $"Team saved as \"{name.Trim()}\".";
            return RedirectToPage(null, null, "teams");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update team {TeamId}", teamId);
            TempData["ErrorMessage"] = Explain(ex, "Couldn't rename that team.");
            return RedirectToPage(null, null, new { RenameId = teamId }, "teams");
        }
    }

    public async Task<IActionResult> OnPostDeleteTeamAsync(Guid teamId)
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        if (teamId == Guid.Empty)
        {
            TempData["ErrorMessage"] = "That team couldn't be identified. Please reload the page.";
            return RedirectToPage(null, null, "teams");
        }

        try
        {
            await _visibility.DeleteTeamAsync(teamId);
            TempData["SuccessMessage"] = "Team removed. Its members are now in no team, and nobody manages it any more.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete team {TeamId}", teamId);
            TempData["ErrorMessage"] = Explain(ex, "Couldn't remove that team.");
        }

        return RedirectToPage(null, null, "teams");
    }

    // =================================================================
    // ONE PERSON — their team and the teams they manage, saved together
    // =================================================================

    /// <summary>
    /// <paramref name="originalTeamId"/> and <paramref name="originalTeamIds"/>
    /// are what the panel was RENDERED with. They are what makes this safe:
    ///
    ///   • a half nobody touched is not written at all, so changing
    ///     somebody's team cannot wipe managerships added since this page
    ///     loaded (SetManagedTeamsAsync replaces the whole set);
    ///   • a half that HAS changed is checked against the live value
    ///     first, so two admins can't silently overwrite each other.
    ///
    /// <paramref name="managesRendered"/> says whether the checkbox list
    /// was actually on the page. Without it an absent list binds to an
    /// empty one, which would read as "clear everything".
    /// </summary>
    public async Task<IActionResult> OnPostPersonAsync(
        Guid userId,
        Guid? teamId,
        List<Guid>? teamIds,
        Guid? originalTeamId,
        List<Guid>? originalTeamIds,
        bool managesRendered)
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        if (userId == Guid.Empty)
        {
            TempData["ErrorMessage"] = "That person couldn't be identified. Please reload the page.";
            return RedirectToPage(null, null, "people");
        }

        TeamsOverviewDto live;
        try
        {
            live = await _visibility.GetTeamsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to re-read teams before saving person {UserId}", userId);
            TempData["ErrorMessage"] = "Couldn't check the current settings, so nothing was changed. Please try again.";
            return RedirectToPage(null, null, "people");
        }

        var person = live.Users.FirstOrDefault(u => u.Id == userId);
        if (person == null)
        {
            TempData["ErrorMessage"] = "That person is no longer in this workspace. Please reload the page.";
            return RedirectToPage(null, null, "people");
        }

        var wantedTeam = Normalise(teamId);
        var renderedTeam = Normalise(originalTeamId);

        var wantedManaged = new HashSet<Guid>((teamIds ?? new()).Where(id => id != Guid.Empty));
        var renderedManaged = new HashSet<Guid>((originalTeamIds ?? new()).Where(id => id != Guid.Empty));
        var liveManaged = new HashSet<Guid>(person.ManagedTeamIds);

        var teamChanged = wantedTeam != renderedTeam;
        var managedChanged = managesRendered && !wantedManaged.SetEquals(renderedManaged);

        if (!teamChanged && !managedChanged)
        {
            TempData["SuccessMessage"] = $"Nothing to save — {person.FullName} is already set up that way.";
            return RedirectToPage(null, null, "people");
        }

        // ── stale page? refuse the halves that moved under us ──────────
        var stale = new List<string>();

        if (teamChanged && renderedTeam != person.TeamId)
            stale.Add($"their team is now {NameOf(live, person.TeamId)}");

        if (managedChanged && !renderedManaged.SetEquals(liveManaged))
            stale.Add($"the teams they manage are now {(liveManaged.Count == 0 ? "none" : string.Join(", ", liveManaged.Select(id => NameOf(live, id))))}");

        if (stale.Count > 0)
        {
            TempData["ErrorMessage"] =
                $"Nothing was changed — somebody edited {person.FullName} while you had this page open ({string.Join("; and ", stale)}). Reload and try again.";
            return RedirectToPage(null, null, "people");
        }

        // ── the team ───────────────────────────────────────────────────
        var teamSaved = false;

        if (teamChanged)
        {
            try
            {
                await _visibility.SetUserTeamAsync(userId, wantedTeam);
                teamSaved = true;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to set team for user {UserId}", userId);
                TempData["ErrorMessage"] = Explain(ex, "Couldn't change that person's team.");
                return RedirectToPage(null, null, "people");
            }
        }

        // ── the teams they manage ──────────────────────────────────────
        if (!managedChanged)
        {
            TempData["SuccessMessage"] = teamSaved
                ? $"Saved. {person.FullName} is now in {NameOf(live, wantedTeam)}."
                : "Saved.";
            return RedirectToPage(null, null, "people");
        }

        try
        {
            await _visibility.SetManagedTeamsAsync(userId, wantedManaged.ToList());
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to set managed teams for user {UserId}", userId);
            // The team change is already written. Say so, rather than
            // letting them think nothing happened and try again.
            TempData["ErrorMessage"] = teamSaved
                ? $"Their team was saved, but the teams they manage were not: {Explain(ex, "the change was refused.")}"
                : $"Couldn't save the teams they manage: {Explain(ex, "the change was refused.")}";
            return RedirectToPage(null, null, "people");
        }

        TempData["SuccessMessage"] = BuildManagedMessage(live, person, wantedManaged, renderedManaged, teamSaved, wantedTeam);
        return RedirectToPage(null, null, "people");
    }

    /// <summary>
    /// Says what happened AND what it costs — in particular, a team that
    /// has just lost its last manager who can sign in now sends every
    /// quote approval to a workspace admin, which is not obvious from
    /// "saved".
    /// </summary>
    private string BuildManagedMessage(
        TeamsOverviewDto live, TeamUserDto person,
        HashSet<Guid> wanted, HashSet<Guid> rendered,
        bool teamSaved, Guid? wantedTeam)
    {
        var parts = new List<string>();

        if (teamSaved) parts.Add($"{person.FullName} is now in {NameOf(live, wantedTeam)}");

        parts.Add(wanted.Count == 0
            ? $"{(teamSaved ? "and they" : person.FullName)} manage{(teamSaved ? "" : "s")} no team"
            : $"{(teamSaved ? "and they" : person.FullName)} now manage{(teamSaved ? "" : "s")} {string.Join(", ", wanted.Select(id => NameOf(live, id)))}");

        var message = "Saved — " + string.Join(", ", parts) + ".";

        // Which teams they were taken off, and which of those are now
        // without a manager who can sign in.
        var orphaned = rendered
            .Except(wanted)
            .Select(id => live.Teams.FirstOrDefault(t => t.Id == id))
            .Where(t => t != null)
            .Where(t =>
            {
                var active = t!.ActiveManagerCount ?? t.ManagerCount;
                var theyCounted = person.IsActive ? 1 : 0;
                return active - theyCounted <= 0;
            })
            .Select(t => t!.Name)
            .ToList();

        if (orphaned.Count > 0)
            message += $" {string.Join(" and ", orphaned)} now {(orphaned.Count == 1 ? "has" : "have")} nobody managing {(orphaned.Count == 1 ? "it" : "them")}, so {(orphaned.Count == 1 ? "its" : "their")} quote approvals will go to a workspace admin.";

        return message;
    }

    private static Guid? Normalise(Guid? id) => id.HasValue && id.Value != Guid.Empty ? id : null;

    private static string NameOf(TeamsOverviewDto overview, Guid? teamId)
        => teamId.HasValue
            ? overview.Teams.FirstOrDefault(t => t.Id == teamId)?.Name ?? "a team that has since been removed"
            : "no team";

    // =================================================================
    // Loading
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
            // Shown inside the card, not as a global banner: a global one
            // would fight the TempData alert from whatever POST sent us
            // here, and _Layout only renders one of each.
            MatrixError = "These settings couldn't be loaded just now, so nothing is shown below. Reload the page to try again.";
        }

        try
        {
            TeamsOverview = await _visibility.GetTeamsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load teams");
            TeamsError = "Teams and people couldn't be loaded just now. Reload the page to try again.";
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

    private static string Join(List<string> items)
        => items.Count <= MaxChangesListed
            ? string.Join("; ", items)
            : string.Join("; ", items.Take(MaxChangesListed)) + $"; and {items.Count - MaxChangesListed} more";

    // ── View helpers ──────────────────────────────────────────────────

    public static readonly RecordScope[] AllScopes = { RecordScope.Own, RecordScope.Team, RecordScope.All };

    public static string Describe(RecordScope s) => s switch
    {
        RecordScope.Own => "Own",
        RecordScope.Team => "Team",
        _ => "All"
    };

    public static string ScopeHint(RecordScope s) => s switch
    {
        RecordScope.Own => "Only records assigned to them",
        RecordScope.Team => "Their team's records, plus unassigned ones",
        _ => "Every record in the workspace"
    };

    // Instance wrappers, so the view doesn't have to name the class.
    public IReadOnlyList<RecordScope> Scopes => AllScopes;
    public string ScopeName(RecordScope s) => Describe(s);
    public string ScopeTip(RecordScope s) => ScopeHint(s);

    /// <summary>
    /// Rank is taken from the enum itself, never hard-coded, so "narrower
    /// than now" stays right if the enum ever gains a member. The same
    /// list is handed to the browser (see ScopeWordsAttribute) rather than
    /// repeated in JavaScript.
    /// </summary>
    public static int Rank(RecordScope s) => (int)s;

    /// <summary>
    /// The cell's identity and the value it was rendered with, in one form
    /// value: "roleId|module|renderedRank".
    /// </summary>
    public static string CellRef(Guid roleId, string module, RecordScope rendered)
        => $"{roleId}|{module}|{Rank(rendered)}";

    /// <summary>Rank-ordered scope names for the browser, e.g. "Own,Team,All".</summary>
    public string ScopeWordsAttribute =>
        string.Join(",", AllScopes.OrderBy(Rank).Select(Describe));

    public string TeamName(Guid? teamId) =>
        teamId.HasValue && teamId.Value != Guid.Empty
            ? TeamsOverview.Teams.FirstOrDefault(t => t.Id == teamId)?.Name ?? "(team removed)"
            : "No team";

    public List<string> ManagedTeamNameList(TeamUserDto u) =>
        TeamsOverview.Teams
            .Where(t => u.ManagedTeamIds.Contains(t.Id))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

    public string ManagedTeamNames(TeamUserDto u)
    {
        var names = ManagedTeamNameList(u);
        return names.Count == 0 ? "—" : string.Join(", ", names);
    }

    public bool IsRenaming(TeamDto t) => RenameId.HasValue && RenameId.Value == t.Id;

    /// <summary>Managers of this team who can actually sign in — the only ones the approval engine will ask.</summary>
    public static int ActiveManagers(TeamDto t) => t.ActiveManagerCount ?? t.ManagerCount;

    /// <summary>
    /// Teams with no manager who can sign in. This — not ManagerCount — is
    /// what decides whether the team has an approver, because
    /// QuoteApprovalEngine only ever picks active users. A team whose only
    /// manager has been deactivated belongs in this list.
    /// </summary>
    public List<TeamDto> TeamsWithoutActiveManager =>
        TeamsOverview.Teams.Where(t => ActiveManagers(t) == 0).ToList();

    /// <summary>
    /// "Leads — 2 All · 1 Team · 3 Own" across the roles somebody actually
    /// holds, so an admin can see the shape of the workspace without
    /// reading the whole grid. Roles nobody holds are left out: they
    /// describe nothing about today.
    /// </summary>
    public string ScopeSummary(string module)
    {
        var inUse = Matrix.Roles.Where(r => r.UserCount > 0).ToList();
        if (inUse.Count == 0) return "nobody holds a role yet";

        var counts = AllScopes.ToDictionary(s => s, _ => 0);

        foreach (var role in inUse)
        {
            var s = role.Scopes.TryGetValue(module, out var v) ? v : RecordScope.All;
            if (counts.ContainsKey(s)) counts[s]++;
        }

        var parts = AllScopes
            .OrderByDescending(Rank)
            .Where(s => counts[s] > 0)
            .Select(s => $"{counts[s]} {Describe(s)}")
            .ToList();

        return parts.Count == 0 ? "nobody holds a role yet" : string.Join(" · ", parts);
    }

    /// <summary>
    /// The widest scope this person actually gets for a module, across all
    /// their roles. Workspace admins see everything regardless of role.
    /// </summary>
    public RecordScope EffectiveScope(TeamUserDto u, string module)
    {
        if (u.IsTenantAdmin) return RecordScope.All;

        var ids = u.RoleIds ?? new List<Guid>();
        var widest = (RecordScope?)null;

        foreach (var role in Matrix.Roles.Where(r => ids.Contains(r.RoleId)))
        {
            var s = role.Scopes.TryGetValue(module, out var v) ? v : RecordScope.All;
            if (widest == null || Rank(s) > Rank(widest.Value)) widest = s;
        }

        // No role at all: nothing widens anything, so the narrowest applies.
        return widest ?? RecordScope.Own;
    }

    /// <summary>
    /// Plain-English summary of what one person can see, e.g.
    /// "All leads · Own deals". Returns null when it can't be worked out,
    /// so the view can stay quiet instead of printing a blank.
    /// </summary>
    public string? SeesSummary(TeamUserDto u)
    {
        if (u.IsTenantAdmin) return "Everything (workspace admin)";

        // No matrix (not loaded, or no module enforces visibility) means no
        // honest answer to give.
        if (Matrix.Modules.Count == 0) return null;

        var parts = Matrix.Modules
            .Select(m => $"{Describe(EffectiveScope(u, m))} {m.ToLowerInvariant()}")
            .ToList();

        return string.Join(" · ", parts);
    }
}
