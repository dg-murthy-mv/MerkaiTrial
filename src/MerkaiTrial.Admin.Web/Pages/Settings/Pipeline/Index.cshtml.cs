// =====================================================================
// PIPELINE STAGES — Settings
// Location: MerkaiTrial.Admin.Web/Pages/Settings/Pipeline/Index.cshtml.cs
//
// NEW FILE. Lets a client shape the pipeline to their own process.
//
// WHY THIS MATTERS COMMERCIALLY
//   Sathorn is an interiors firm. Their real pipeline is closer to
//   Enquiry → Site Visit → Design → Quotation → Contract than to
//   Discovery → Qualification → Proposal. Telling a client their own
//   sales process is wrong is a poor way to start a trial, and "your
//   pipeline, your words" is something Zoho's defaults do not say.
//
// Reorder uses up/down buttons rather than drag-and-drop: no JavaScript,
// works on a phone, and a sales manager reordering six stages twice a
// year does not need the nicer interaction.
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

    public List<PipelineStageDto> OpenStages =>
        Stages.Where(s => s.Category == StageCategory.Open).ToList();

    public List<PipelineStageDto> ClosingStages =>
        Stages.Where(s => s.Category != StageCategory.Open).ToList();

    [BindProperty] public StageInput Input { get; set; } = new();

    /// <summary>Which stage is being edited inline. Null = none.</summary>
    [BindProperty(SupportsGet = true)] public Guid? EditId { get; set; }

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
            else Input = new StageInput
            {
                StageId = s.Id, Name = s.Name, Probability = s.Probability,
                Category = s.Category, IsActive = s.IsActive
            };
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
            await InitializePermissionsAsync();
            await LoadAsync();
            return Page();
        }

        try
        {
            await _stages.CreateAsync(new CreatePipelineStageDto(
                TenantId: Guid.Empty,            // set server-side
                Name: Input.Name,
                Probability: Input.Probability,
                Category: Input.Category));

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
            EditId = Input.StageId;
            await InitializePermissionsAsync();
            await LoadAsync();
            return Page();
        }

        try
        {
            await _stages.UpdateAsync(new UpdatePipelineStageDto(
                TenantId: Guid.Empty,
                StageId: Input.StageId.Value,
                Name: Input.Name,
                Probability: Input.Probability,
                IsActive: Input.IsActive));

            TempData["SuccessMessage"] = "Stage updated.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't save that change.");
        }

        return RedirectToPage();
    }

    /// <summary>
    /// Moves one stage up or down. Sends the whole new order rather than
    /// a single position, so two people reordering at once cannot leave
    /// the list with duplicate positions.
    /// </summary>
    public async Task<IActionResult> OnPostMoveAsync(Guid stageId, string direction)
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        try
        {
            await LoadAsync();

            var ordered = Stages.OrderBy(s => s.SortOrder).Select(s => s.Id).ToList();
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
            Stages = await _stages.GetAsync(activeOnly: false);
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

    // ── View helpers ──────────────────────────────────────────────────

    public bool IsEditing(PipelineStageDto s) => EditId.HasValue && EditId.Value == s.Id;

    public bool IsFirst(PipelineStageDto s) => Stages.OrderBy(x => x.SortOrder).First().Id == s.Id;
    public bool IsLast(PipelineStageDto s)  => Stages.OrderBy(x => x.SortOrder).Last().Id  == s.Id;

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
}
