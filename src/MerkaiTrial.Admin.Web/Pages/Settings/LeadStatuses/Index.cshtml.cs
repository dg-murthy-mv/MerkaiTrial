// =====================================================================
// LEAD STATUSES — Settings
// Location: MerkaiTrial.Admin.Web/Pages/Settings/LeadStatuses/Index.cshtml.cs
//
// COMPLETE FILE — replaces the existing one.
//
// WHY THIS MATTERS
//   "Lead" is not a universal word. Sathorn's process starts with an
//   enquiry from a site visit request; an Indian software firm's starts
//   with a demo signup. Telling both that their first step is called
//   "New" is a small thing that reads as the software not fitting.
//
// CHANGES (025) — the same treatment Pipeline Stages got in 023
//
//   ✅ MOVE LEADS OUT OF A STATUS. The page used to say "move them first"
//      and offer nothing that moved them.
//
//   ✅ THE COUNTS ON THIS PAGE ARE THE TRUE ONES. LeadStatusDto.LeadCount
//      is scoped by record visibility, because it drives the Leads page
//      tab counts. The retire and delete rules count EVERY lead in the
//      tenant. This page reads TotalLeadCount for both display and
//      reasoning — otherwise a sales manager with Own scope would be told
//      a status is empty, offered Delete, and then refused.
//
//   ✅ REORDER BY DRAGGING, with the old arrows kept as the no-script and
//      touch fallback.
//
//   ✅ THE REASONS COME FROM THE SERVER. CanDelete / CanRetire and their
//      reasons are computed from exactly the checks the write handlers
//      enforce, so a disabled menu item says what pressing it would have
//      said.
//
//   ✅ Retired statuses get a section of their own instead of sitting in
//      the list at 50% opacity, where they read as a broken control.
//
// CHANGES (024)
//   ✅ Modules.Settings, not Modules.Leads. See the note on ModuleName.
//
// WHAT IS NOT EDITABLE, AND WHY
//   Category. Moving a status from Qualified to Open would silently
//   change what conversion allows, and every past report of qualified
//   leads would mean something different. If a client needs a different
//   category, they add a status and move the leads — which is now a
//   single action.
//
//   The system status (Converted) can be renamed and recoloured but not
//   retired, deleted, or made the starting point — conversion has to have
//   somewhere to put the lead.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Leads;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.LeadStatuses;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Settings.LeadStatuses;

public class IndexModel : AuthorizedPageModel
{
    private readonly ILeadStatusService _statuses;

    // ── 024: settings, not leads ──────────────────────────────────────
    // On Modules.Leads this page asked for leads.read to enter and
    // leads.update to change — which every Sales Rep holds, because they
    // cannot work a lead without them. So a rep could rename the statuses,
    // reorder them, change which one new leads start in, and rewrite the
    // score each one is worth. Lead scoring orders every rep's queue,
    // including their colleagues'.
    protected override string ModuleName => Modules.Settings;

    public IndexModel(
        ILeadStatusService statuses,
        IAuthorizationService authorizationService,
        ICurrentUserService currentUserService,
        ILogger<IndexModel> logger)
        : base(authorizationService, currentUserService, logger)
    {
        _statuses = statuses;
    }

    public List<LeadStatusDto> Statuses { get; private set; } = new();

    /// <summary>In use and chosen by a person — the part you can drag.</summary>
    public List<LeadStatusDto> WorkingStatuses =>
        Statuses.Where(s => !s.IsSystem && s.IsActive)
                .OrderBy(s => s.SortOrder).ToList();

    /// <summary>Set by the system. Shown, renameable, barely editable.</summary>
    public List<LeadStatusDto> SystemStatuses =>
        Statuses.Where(s => s.IsSystem).OrderBy(s => s.SortOrder).ToList();

    /// <summary>
    /// Switched off. Its own section rather than greyed out in place: a
    /// faded row reads as "disabled control", not "status you retired in
    /// March". The system status can never be here.
    /// </summary>
    public List<LeadStatusDto> RetiredStatuses =>
        Statuses.Where(s => !s.IsActive && !s.IsSystem)
                .OrderBy(s => s.SortOrder).ToList();

    /// <summary>Every status a bulk move could send leads into.</summary>
    public List<LeadStatusDto> MoveTargets =>
        Statuses.Where(s => s.IsActive && !s.IsSystem)
                .OrderBy(s => s.SortOrder).ToList();

    public bool AnyStatusBlockedByLeads => Statuses.Any(s => s.BlockedOnlyByLeads);

    [BindProperty] public StatusInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)] public Guid? EditId { get; set; }

    public class StatusInput
    {
        public Guid? StatusId { get; set; }

        [Required(ErrorMessage = "Give the status a name")]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        [Range(0, 15, ErrorMessage = "Score must be between 0 and 15")]
        public int Score { get; set; } = 5;

        public LeadStatusCategory Category { get; set; } = LeadStatusCategory.Open;

        public bool IsActive { get; set; } = true;

        /// <summary>"#RRGGBB" from a native colour input.</summary>
        public string? Color { get; set; }

        [StringLength(500, ErrorMessage = "Keep the description under 500 characters")]
        public string? Description { get; set; }
    }

    // =================================================================

    public async Task<IActionResult> OnGetAsync()
    {
        var check = await ValidatePermissionAsync(Actions.Read);
        if (check != null) return check;

        await InitializePermissionsAsync();
        await LoadAsync();

        if (EditId.HasValue)
        {
            var s = Statuses.FirstOrDefault(x => x.Id == EditId.Value);
            if (s is null) EditId = null;
            else Input = ToInput(s);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        if (InputFailedToBind(out var bindError))
        {
            TempData["ErrorMessage"] = bindError;
            return RedirectToPage();
        }

        ModelState.Clear();
        if (!TryValidateModel(Input, nameof(Input)))
        {
            // Redirect with the message rather than re-rendering. The row
            // partial binds every field from the status DTO, so a
            // re-render would show the stored values with no sign that
            // anything was rejected — a page that looks like it simply
            // ignored you. _Layout renders TempData alerts globally.
            TempData["ErrorMessage"] = FirstValidationError("Couldn't add that status.");
            return RedirectToPage();
        }

        try
        {
            await _statuses.CreateAsync(new CreateLeadStatusDto(
                TenantId: Guid.Empty,              // set server-side
                Name: Input.Name,
                Score: Input.Score,
                Category: Input.Category,
                CreatedBy: null,
                Color: Input.Color,
                Description: Input.Description));

            TempData["SuccessMessage"] = $"\"{Input.Name}\" added.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't add that status.");
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpdateAsync()
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        if (Input.StatusId is null) return RedirectToPage();

        if (InputFailedToBind(out var bindError))
        {
            TempData["ErrorMessage"] = bindError;
            return RedirectToPage(new { EditId = Input.StatusId });
        }

        ModelState.Clear();
        if (!TryValidateModel(Input, nameof(Input)))
        {
            TempData["ErrorMessage"] = FirstValidationError("Couldn't save that change.");
            return RedirectToPage(new { EditId = Input.StatusId });
        }

        try
        {
            await _statuses.UpdateAsync(new UpdateLeadStatusDefDto(
                TenantId: Guid.Empty,
                StatusId: Input.StatusId.Value,
                Name: Input.Name,
                Score: Input.Score,
                IsActive: Input.IsActive,
                UpdatedBy: null,
                Color: Input.Color,
                Description: Input.Description));

            TempData["SuccessMessage"] = "Status updated.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't save that change.");
        }

        return RedirectToPage();
    }

    /// <summary>
    /// The no-script fallback: one step up or down. Sends the whole new
    /// order rather than a single position, so two people reordering at
    /// once cannot leave the list with duplicate positions.
    ///
    /// 025: scoped to the working group. The system status sorts last and
    /// the handler enforces that anyway.
    /// </summary>
    public async Task<IActionResult> OnPostMoveAsync(Guid statusId, string direction)
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        try
        {
            await LoadAsync();

            var ordered = WorkingStatuses.Select(s => s.Id).ToList();
            var i = ordered.IndexOf(statusId);
            if (i < 0) return RedirectToPage();

            var j = direction == "up" ? i - 1 : i + 1;
            if (j < 0 || j >= ordered.Count) return RedirectToPage();

            (ordered[i], ordered[j]) = (ordered[j], ordered[i]);

            await _statuses.ReorderAsync(new ReorderLeadStatusesDto(Guid.Empty, ordered));
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't reorder the statuses.");
        }

        return RedirectToPage();
    }

    /// <summary>
    /// What dragging posts: the working statuses in their new order, as a
    /// comma-separated list of ids.
    ///
    /// A CSV rather than a repeated form field because a repeated field
    /// binds by index and one gap silently truncates the post. The handler
    /// puts the system status last whatever this list says.
    /// </summary>
    public async Task<IActionResult> OnPostReorderAsync(string? order)
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        var ids = (order ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToList();

        if (ids.Count == 0) return RedirectToPage();

        try
        {
            await _statuses.ReorderAsync(new ReorderLeadStatusesDto(Guid.Empty, ids));
            TempData["SuccessMessage"] = "Status order saved.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't save the new order.");
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetDefaultAsync(Guid statusId)
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        try
        {
            await _statuses.SetDefaultAsync(statusId);
            TempData["SuccessMessage"] = "New leads will start here.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't change the starting status.");
        }

        return RedirectToPage();
    }

    /// <summary>
    /// Empties a status so it can be retired. The thing this page has
    /// been telling people to do without giving them any way to do it.
    /// </summary>
    public async Task<IActionResult> OnPostMoveLeadsAsync(
        Guid fromStatusId, Guid toStatusId, bool thenRetire = false)
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        try
        {
            var result = await _statuses.MoveLeadsAsync(new MoveStatusLeadsDto(
                TenantId: Guid.Empty,
                FromStatusId: fromStatusId,
                ToStatusId: toStatusId,
                MovedBy: null,
                ThenRetire: thenRetire));

            var leads = result.Moved == 1 ? "1 lead" : $"{result.Moved} leads";

            TempData["SuccessMessage"] = result.Retired
                ? $"{leads} moved to \"{result.ToName}\", and \"{result.FromName}\" is now retired."
                : $"{leads} moved from \"{result.FromName}\" to \"{result.ToName}\".";

            // Both messages at once, deliberately. The move committed, so
            // reporting it as a failure would be a lie — but a lead whose
            // timeline entry did not get written is worth knowing about,
            // and burying it in a log nobody reads is how that becomes a
            // support call six weeks later.
            if (result.Problems is { Count: > 0 })
                TempData["ErrorMessage"] = result.Problems.Count == 1
                    ? result.Problems[0]
                    : $"{result.Problems.Count} of those leads moved but couldn't be fully recorded. " +
                      $"The first: {result.Problems[0]}";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't move those leads.");
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid statusId)
    {
        var check = await ValidatePermissionAsync(Actions.Delete);
        if (check != null) return check;

        try
        {
            await _statuses.DeleteAsync(statusId);
            TempData["SuccessMessage"] = "Status removed.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't remove that status.");
        }

        return RedirectToPage();
    }

    // =================================================================

    private async Task LoadAsync()
    {
        try
        {
            // selectableOnly: false — settings must show retired statuses
            // and the system one, or they cannot be managed at all.
            //
            // detail: true — this is the ONE page that asks for the true
            // (unscoped) lead counts and the delete/retire reasons. See
            // the note on GetLeadStatusesHandler for why it is not the
            // default.
            Statuses = await _statuses.GetAsync(selectableOnly: false, detail: true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load lead statuses");
            TempData["ErrorMessage"] = "Couldn't load your lead statuses. Please try again.";
            Statuses = new();
        }
    }

    /// <summary>
    /// True when the model binder itself failed on one of the Input
    /// fields — a number field that could not be parsed, say.
    ///
    /// This has to be asked BEFORE ModelState.Clear(), which the two
    /// handlers below call so that errors on unrelated properties do not
    /// block the save. Clearing also throws away conversion failures, and
    /// a property whose value never arrived keeps its C# initializer — so
    /// [Range] sees a perfectly valid 5 and the real input is lost with
    /// no message at all.
    /// </summary>
    private bool InputFailedToBind(out string? error)
    {
        error = ModelState
            .Where(kv => kv.Key.StartsWith("Input.", StringComparison.Ordinal))
            .SelectMany(kv => kv.Value!.Errors)
            .Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage)
                ? "Some of those values weren't in a format we could read."
                : e.ErrorMessage)
            .FirstOrDefault();

        return error is not null;
    }

    private static string Explain(Exception ex, string fallback)
        => string.IsNullOrWhiteSpace(ex.Message) || ex is NullReferenceException
            ? fallback
            : ex.Message;

    /// <summary>
    /// The first thing the validator objected to, in the wording the
    /// attribute already gives — "Give the status a name" rather than a
    /// field path nobody outside the codebase recognises.
    /// </summary>
    private string FirstValidationError(string fallback)
        => ModelState.Values
               .SelectMany(v => v.Errors)
               .Select(e => e.ErrorMessage)
               .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))
           ?? fallback;

    private static StatusInput ToInput(LeadStatusDto s) => new()
    {
        StatusId = s.Id,
        Name = s.Name,
        Score = s.Score,
        Category = s.Category,
        IsActive = s.IsActive,
        Color = s.Color,
        Description = s.Description
    };

    // ── View helpers ──────────────────────────────────────────────────

    public bool IsEditing(LeadStatusDto s) => EditId.HasValue && EditId.Value == s.Id;

    public bool IsFirstWorking(LeadStatusDto s)
    {
        var w = WorkingStatuses;
        return w.Count == 0 || w[0].Id == s.Id;
    }

    public bool IsLastWorking(LeadStatusDto s)
    {
        var w = WorkingStatuses;
        return w.Count == 0 || w[^1].Id == s.Id;
    }

    /// <summary>
    /// Where the score ramp runs backwards among the OPEN statuses. A
    /// lead moving forward and scoring less makes every rep's queue order
    /// slightly wrong, and nothing else on the page would say so.
    ///
    /// Only Open statuses are compared: Unqualified is worth 0 and comes
    /// after Qualified's 15, which is correct and not a ramp at all.
    /// </summary>
    public List<string> ScoreWarnings()
    {
        var open = WorkingStatuses
            .Where(s => s.Category == LeadStatusCategory.Open)
            .ToList();

        var warnings = new List<string>();

        for (var i = 0; i + 1 < open.Count; i++)
            if (open[i + 1].Score < open[i].Score)
                warnings.Add(
                    $"\"{open[i + 1].Name}\" ({open[i + 1].Score} points) comes after " +
                    $"\"{open[i].Name}\" ({open[i].Score}) but is worth less.");

        return warnings;
    }

    /// <summary>A sensible starting score for a brand-new open status:
    /// a step beyond the furthest one you already have.</summary>
    public int SuggestedScore()
    {
        var open = WorkingStatuses
            .Where(s => s.Category == LeadStatusCategory.Open)
            .ToList();

        if (open.Count == 0) return 5;

        return Math.Clamp(open.Max(s => s.Score) + 5, 0, 15);
    }

    public string CategoryBadge(LeadStatusCategory c) => c switch
    {
        LeadStatusCategory.Qualified    => "bg-success",
        LeadStatusCategory.Disqualified => "bg-secondary",
        LeadStatusCategory.Converted    => "bg-warning text-dark",
        _ => "bg-primary"
    };

    public string CategoryLabel(LeadStatusCategory c) => c switch
    {
        LeadStatusCategory.Qualified    => "Qualified",
        LeadStatusCategory.Disqualified => "Not pursuing",
        LeadStatusCategory.Converted    => "Converted",
        _ => "Open"
    };

    public static string LeadsPhrase(int n) => n switch
    {
        0 => "no leads",
        1 => "1 lead",
        _ => $"{n} leads"
    };

    /// <summary>
    /// Everything _StatusRow.cshtml needs, in one typed object. A partial
    /// gets its own model, not the page's, so the alternative was
    /// smuggling CanUpdate, CanDelete and the edit state through ViewData
    /// as loose strings. One record is checked by the compiler.
    /// </summary>
    public StatusRowVm Row(LeadStatusDto s, bool draggable) => new(
        Status: s,
        Draggable: draggable && CanUpdate && !s.IsSystem,
        CanUpdate: CanUpdate,
        CanDelete: CanDelete,
        IsEditing: IsEditing(s),
        IsFirst: IsFirstWorking(s),
        IsLast: IsLastWorking(s));
}

/// <summary>Model for _StatusRow.cshtml. See IndexModel.Row.</summary>
public record StatusRowVm(
    LeadStatusDto Status,
    bool Draggable,
    bool CanUpdate,
    bool CanDelete,
    bool IsEditing,
    bool IsFirst,
    bool IsLast);
