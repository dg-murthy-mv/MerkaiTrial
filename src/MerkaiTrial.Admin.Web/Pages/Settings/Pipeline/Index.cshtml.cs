// =====================================================================
// PIPELINE STAGES — Settings
// Location: MerkaiTrial.Admin.Web/Pages/Settings/Pipeline/Index.cshtml.cs
//
// COMPLETE FILE — replaces the existing one.
//
// WHY THIS MATTERS COMMERCIALLY
//   Sathorn is an interiors firm. Their real pipeline is closer to
//   Enquiry → Site Visit → Design → Quotation → Contract than to
//   Discovery → Qualification → Proposal. Telling a client their own
//   sales process is wrong is a poor way to start a trial, and "your
//   pipeline, your words" is something Zoho's defaults do not say.
//
// CHANGES (023)
//
//   ✅ MOVE DEALS OUT OF A STAGE. The page used to say "move them first"
//      and offer nothing that moved them. OnPostMoveDeals does it.
//
//   ✅ REORDER BY DRAGGING, with the old arrows kept as the no-script
//      fallback. Dragging posts the whole new order once, instead of one
//      page load per position.
//
//   ✅ THE REASONS COME FROM THE SERVER. The view no longer decides
//      whether a stage can be deleted by looking at DealCount — the DTO
//      carries CanDelete and the reason, computed from exactly the checks
//      the write handler enforces. A disabled menu item that says why
//      teaches the rule; a button that vanishes teaches nothing, and a
//      button that appears and then refuses is worse than either.
//
//   ✅ FIRST AND LAST ARE SCOPED TO THE WORKING GROUP. They used to be
//      computed over every stage, so the last working stage's down-arrow
//      was enabled and pushed it past a closing stage in SortOrder — see
//      the note in StageOrdering.Renumber for what that silently did to
//      the deal page.
//
//   ✅ Retired stages get a section of their own instead of being greyed
//      out in place, where they were indistinguishable at a glance.
//
//   ✅ A warning when the likelihood ramp runs backwards, which makes the
//      weighted forecast quietly wrong and is invisible otherwise.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Pipeline;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.PipelineStages;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Settings.Pipeline;

public class IndexModel : AuthorizedPageModel
{
    private readonly IPipelineStageService _stages;

    protected override string ModuleName => Modules.Deals;

    public IndexModel(
        IPipelineStageService stages,
        IAuthorizationService authorizationService,
        ICurrentUserService currentUserService,
        ILogger<IndexModel> logger)
        : base(authorizationService, currentUserService, logger)
    {
        _stages = stages;
    }

    public List<PipelineStageDto> Stages { get; private set; } = new();

    /// <summary>Open and in use — the part of the list you can drag.</summary>
    public List<PipelineStageDto> WorkingStages =>
        Stages.Where(s => s.Category == StageCategory.Open && s.IsActive)
              .OrderBy(s => s.SortOrder).ToList();

    /// <summary>Won and Lost, in use. Order is fixed by category.</summary>
    public List<PipelineStageDto> ClosingStages =>
        Stages.Where(s => s.Category != StageCategory.Open && s.IsActive)
              .OrderBy(s => s.SortOrder).ToList();

    /// <summary>
    /// Everything switched off, of any category. Its own section rather
    /// than greyed out in place: a faded row reads as "disabled control",
    /// not "stage you retired in March".
    /// </summary>
    public List<PipelineStageDto> RetiredStages =>
        Stages.Where(s => !s.IsActive).OrderBy(s => s.SortOrder).ToList();

    /// <summary>Every stage a bulk move could send deals into.</summary>
    public List<PipelineStageDto> MoveTargets =>
        Stages.Where(s => s.IsActive).OrderBy(s => s.SortOrder).ToList();

    /// <summary>True when at least one stage can offer the move dialog.</summary>
    public bool AnyStageBlockedByDeals => Stages.Any(s => s.BlockedOnlyByDeals);

    [BindProperty] public StageInput Input { get; set; } = new();

    /// <summary>Which stage is being edited. Null = none.</summary>
    [BindProperty(SupportsGet = true)] public Guid? EditId { get; set; }

    /// <summary>
    /// Reserved for opening the add row on load. Nothing sets it today —
    /// a rejected create redirects with the message rather than
    /// re-rendering, so there is no half-filled form to reopen.
    /// </summary>
    public bool AddOpen { get; private set; }

    public class StageInput
    {
        public Guid? StageId { get; set; }

        [Required(ErrorMessage = "Give the stage a name")]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        [Range(0, 100, ErrorMessage = "Probability must be between 0 and 100")]
        public int Probability { get; set; } = 20;

        public StageCategory Category { get; set; } = StageCategory.Open;

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
            var s = Stages.FirstOrDefault(x => x.Id == EditId.Value);
            if (s is null) EditId = null;
            else Input = ToInput(s);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        ModelState.Clear();
        if (!TryValidateModel(Input, nameof(Input)))
        {
            // Redirect with the message rather than re-rendering. The row
            // partial binds every field from the stage DTO, so a
            // re-render would show the stored values with no sign that
            // anything was rejected — a page that looks like it simply
            // ignored you. _Layout renders TempData alerts globally.
            TempData["ErrorMessage"] = FirstValidationError("Couldn't add that stage.");
            return RedirectToPage();
        }

        try
        {
            await _stages.CreateAsync(new CreatePipelineStageDto(
                TenantId: Guid.Empty,            // set server-side
                Name: Input.Name,
                Probability: Input.Probability,
                Category: Input.Category,
                CreatedBy: null,
                Color: Input.Color,
                Description: Input.Description));

            TempData["SuccessMessage"] = $"\"{Input.Name}\" added to your pipeline.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't add that stage.");
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpdateAsync()
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        if (Input.StageId is null) return RedirectToPage();

        ModelState.Clear();
        if (!TryValidateModel(Input, nameof(Input)))
        {
            TempData["ErrorMessage"] = FirstValidationError("Couldn't save that change.");
            return RedirectToPage(new { EditId = Input.StageId });
        }

        try
        {
            await _stages.UpdateAsync(new UpdatePipelineStageDto(
                TenantId: Guid.Empty,
                StageId: Input.StageId.Value,
                Name: Input.Name,
                Probability: Input.Probability,
                IsActive: Input.IsActive,
                UpdatedBy: null,
                Color: Input.Color,
                Description: Input.Description));

            TempData["SuccessMessage"] = "Stage updated.";
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
    /// 023: scoped to the working group. Moving a working stage past a
    /// closing one is not a reorder anybody wanted, and the handler now
    /// refuses to honour it anyway.
    /// </summary>
    public async Task<IActionResult> OnPostMoveAsync(Guid stageId, string direction)
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        try
        {
            await LoadAsync();

            var ordered = WorkingStages.Select(s => s.Id).ToList();
            var i = ordered.IndexOf(stageId);
            if (i < 0) return RedirectToPage();

            var j = direction == "up" ? i - 1 : i + 1;
            if (j < 0 || j >= ordered.Count) return RedirectToPage();

            (ordered[i], ordered[j]) = (ordered[j], ordered[i]);

            await _stages.ReorderAsync(new ReorderPipelineStagesDto(Guid.Empty, ordered));
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't reorder the stages.");
        }

        return RedirectToPage();
    }

    /// <summary>
    /// What dragging posts: the working stages in their new order, as a
    /// comma-separated list of ids.
    ///
    /// A CSV rather than a repeated form field because a repeated field
    /// binds by index and one gap silently truncates the post — the same
    /// reason 022 put transition actions in a hidden JSON field. The
    /// handler puts closing stages after these, in their existing order,
    /// whatever this list says.
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
            await _stages.ReorderAsync(new ReorderPipelineStagesDto(Guid.Empty, ids));
            TempData["SuccessMessage"] = "Stage order saved.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't save the new order.");
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetDefaultAsync(Guid stageId)
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        try
        {
            await _stages.SetDefaultAsync(stageId);
            TempData["SuccessMessage"] = "New deals will start here.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't change the starting stage.");
        }

        return RedirectToPage();
    }

    /// <summary>
    /// Empties a stage so it can be retired. The thing the page promised
    /// for three rounds and never delivered.
    /// </summary>
    public async Task<IActionResult> OnPostMoveDealsAsync(
        Guid fromStageId, Guid toStageId, bool thenRetire = false)
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        try
        {
            var result = await _stages.MoveDealsAsync(new MoveStageDealsDto(
                TenantId: Guid.Empty,
                FromStageId: fromStageId,
                ToStageId: toStageId,
                MovedBy: null,
                ThenRetire: thenRetire));

            var deals = result.Moved == 1 ? "1 deal" : $"{result.Moved} deals";

            TempData["SuccessMessage"] = result.Retired
                ? $"{deals} moved to \"{result.ToName}\", and \"{result.FromName}\" is now retired."
                : $"{deals} moved from \"{result.FromName}\" to \"{result.ToName}\".";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't move those deals.");
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid stageId)
    {
        var check = await ValidatePermissionAsync(Actions.Delete);
        if (check != null) return check;

        try
        {
            await _stages.DeleteAsync(stageId);
            TempData["SuccessMessage"] = "Stage removed.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't remove that stage.");
        }

        return RedirectToPage();
    }

    // =================================================================

    private async Task LoadAsync()
    {
        try
        {
            // activeOnly: false — settings must show retired stages too,
            // or a client cannot bring one back.
            //
            // detail: true — this is the ONE page that asks for the ways
            // in/out and the delete/retire reasons. See the note on
            // GetPipelineStagesHandler for why it is not the default.
            Stages = await _stages.GetAsync(activeOnly: false, detail: true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load pipeline stages");
            TempData["ErrorMessage"] = "Couldn't load your pipeline. Please try again.";
            Stages = new();
        }
    }

    private static string Explain(Exception ex, string fallback)
        => string.IsNullOrWhiteSpace(ex.Message) || ex is NullReferenceException
            ? fallback
            : ex.Message;

    /// <summary>
    /// The first thing the validator objected to, in the wording the
    /// attribute already gives — "Give the stage a name" rather than a
    /// field path nobody outside the codebase recognises.
    /// </summary>
    private string FirstValidationError(string fallback)
        => ModelState.Values
               .SelectMany(v => v.Errors)
               .Select(e => e.ErrorMessage)
               .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))
           ?? fallback;

    private static StageInput ToInput(PipelineStageDto s) => new()
    {
        StageId = s.Id,
        Name = s.Name,
        Probability = s.Probability,
        Category = s.Category,
        IsActive = s.IsActive,
        Color = s.Color,
        Description = s.Description
    };

    // ── View helpers ──────────────────────────────────────────────────

    public bool IsEditing(PipelineStageDto s) => EditId.HasValue && EditId.Value == s.Id;

    public bool IsFirstWorking(PipelineStageDto s)
    {
        var w = WorkingStages;
        return w.Count == 0 || w[0].Id == s.Id;
    }

    public bool IsLastWorking(PipelineStageDto s)
    {
        var w = WorkingStages;
        return w.Count == 0 || w[^1].Id == s.Id;
    }

    /// <summary>
    /// Where the likelihood ramp runs backwards. A deal moving forward
    /// through the pipeline and getting LESS likely makes the weighted
    /// forecast quietly wrong, and nothing else on the page would ever
    /// say so. Empty when the ramp is sane.
    /// </summary>
    public List<string> ProbabilityWarnings()
    {
        var w = WorkingStages;
        var warnings = new List<string>();

        for (var i = 0; i + 1 < w.Count; i++)
            if (w[i + 1].Probability < w[i].Probability)
                warnings.Add(
                    $"\"{w[i + 1].Name}\" ({w[i + 1].Probability}%) comes after " +
                    $"\"{w[i].Name}\" ({w[i].Probability}%) but is less likely to close.");

        return warnings;
    }

    /// <summary>A sensible starting number for a brand-new working stage:
    /// a step beyond the furthest one you already have.</summary>
    public int SuggestedProbability()
    {
        var w = WorkingStages;
        if (w.Count == 0) return 20;

        return Math.Clamp(w.Max(s => s.Probability) + 10, 5, 90);
    }

    public string CategoryBadge(StageCategory c) => c switch
    {
        StageCategory.Won  => "bg-success",
        StageCategory.Lost => "bg-danger",
        _ => "bg-secondary"
    };

    public string CategoryLabel(StageCategory c) => c switch
    {
        StageCategory.Won  => "Won",
        StageCategory.Lost => "Lost",
        _ => "Open"
    };

    public static string DealsPhrase(int n) => n switch
    {
        0 => "no deals",
        1 => "1 deal",
        _ => $"{n} deals"
    };

    /// <summary>
    /// Everything _StageRow.cshtml needs, in one typed object.
    ///
    /// A partial gets its own model, not the page's, so the alternative
    /// was smuggling CanUpdate, CanDelete and the edit state through
    /// ViewData as loose strings. One record is checked by the compiler;
    /// ViewData keys are checked by whoever notices the page went blank.
    /// </summary>
    public StageRowVm Row(PipelineStageDto s, bool draggable) => new(
        Stage: s,
        Draggable: draggable && CanUpdate,
        CanUpdate: CanUpdate,
        CanDelete: CanDelete,
        IsEditing: IsEditing(s),
        IsFirst: IsFirstWorking(s),
        IsLast: IsLastWorking(s));
}

/// <summary>Model for _StageRow.cshtml. See IndexModel.Row.</summary>
public record StageRowVm(
    PipelineStageDto Stage,
    bool Draggable,
    bool CanUpdate,
    bool CanDelete,
    bool IsEditing,
    bool IsFirst,
    bool IsLast);
