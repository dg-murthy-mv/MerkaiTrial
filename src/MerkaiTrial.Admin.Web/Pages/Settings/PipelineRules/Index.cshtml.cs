// =====================================================================
// Settings/PipelineRules/Index.cshtml.cs
// Location: MerkaiTrial.Admin.Web/Pages/Settings/PipelineRules/Index.cshtml.cs
//
// COMPLETE FILE — replaces the 019 version.
//
// WHAT THIS PAGE IS NOW
//   The sales process. For each stage, the moves a deal can make out of
//   it: what the button says, who may press it, and what the deal needs
//   first. Plus the one rule that belongs to no single move — an invoiced
//   deal cannot be reopened.
//
// WHAT WENT
//   019's five per-stage requirement checkboxes and its three whole-
//   pipeline switches. Requirements moved onto the transition, where the
//   same stage can ask different things depending on where the deal came
//   from. ForwardOnly is now expressed by which moves exist; the two
//   reopen rules by the Actor and the note on the moves out of a closed
//   stage.
//
// THE GRID IS ALWAYS COMPLETE
//   Every from-to pair is rendered whether a row exists or not. A tenant
//   who ran the migration with ForwardOnly on has no backward rows at
//   all, and a grid with holes in it would be impossible to reason about.
//   Saving creates whatever is missing.
//
// Admin-only to change; readable by anyone who can read deals, because a
// manager who cannot work out why a deal will not move should be able to
// look the rule up rather than ask.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Pipeline;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.PipelineStages;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.Admin.Web.Pages.Settings.PipelineRules
{
    public class IndexModel : AuthorizedPageModel
    {
        private readonly IPipelineRuleService _ruleService;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<IndexModel> _logger;

        protected override string ModuleName => Modules.Deals;

        public IndexModel(
            IPipelineRuleService ruleService,
            ICurrentUserService currentUserService,
            IAuthorizationService authorizationService,
            ILogger<IndexModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _ruleService = ruleService;
            _currentUserService = currentUserService;
            _logger = logger;
        }

        // ── View state ─────────────────────────────────────────────────

        /// <summary>
        /// Never null once a handler has run. A failed load leaves an empty
        /// process here rather than null, so the view renders the page and
        /// its error message instead of bailing out halfway through its own
        /// markup.
        /// </summary>
        public PipelineRulesDto Rules { get; private set; } = Empty();

        public bool LoadFailed { get; private set; }

        /// <summary>
        /// Admin-only to change, and the page says so rather than hiding
        /// itself: a sales manager who wonders why a deal will not move
        /// should be able to read the rule that stopped it.
        /// </summary>
        public bool CanEditRules { get; private set; }

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

        // ── Posted form ────────────────────────────────────────────────

        [BindProperty] public bool BlockReopenWithIssuedInvoice { get; set; }

        /// <summary>
        /// One entry per from-to pair, in grid order. A list of a flat
        /// input model rather than the DTO: Razor Pages needs settable
        /// properties and an index to bind a collection, and a positional
        /// record gives it neither.
        /// </summary>
        [BindProperty] public List<CellInput> Cells { get; set; } = new();

        public class CellInput
        {
            // Identity — hidden fields, so the server knows which cell this
            // row of checkboxes belongs to.
            public string FromStageKey { get; set; } = string.Empty;
            public string ToStageKey { get; set; } = string.Empty;

            // Display only; re-resolved on the server after a failed post.
            public string FromStageName { get; set; } = string.Empty;
            public string ToStageName { get; set; } = string.Empty;
            public StageCategory ToCategory { get; set; }
            public bool ToIsActive { get; set; } = true;

            public bool IsActive { get; set; }
            public string Label { get; set; } = string.Empty;
            public TransitionActor Actor { get; set; }

            public bool RequiresQuote { get; set; }
            public bool RequiresAcceptedQuote { get; set; }
            public bool RequiresValue { get; set; }
            public bool RequiresCloseDate { get; set; }
            public bool RequiresAttachment { get; set; }
            public bool RequiresNote { get; set; }
            public string? NotePrompt { get; set; }
        }

        // ── GET ────────────────────────────────────────────────────────

        public async Task<IActionResult> OnGetAsync()
        {
            var check = await ValidatePermissionAsync(Actions.Read);
            if (check != null) return check;

            await InitializePermissionsAsync();

            var me = await _currentUserService.GetCurrentUserAsync();
            CanEditRules = me.IsTenantAdmin;

            try
            {
                Rules = await _ruleService.GetAsync();
                BuildGrid();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load the pipeline process");
                LoadFailed = true;
                ErrorMessage = "Failed to load the sales process. Please try again.";
                return Page();
            }
        }

        // ── POST: save ─────────────────────────────────────────────────

        public async Task<IActionResult> OnPostAsync()
        {
            var check = await ValidatePermissionAsync(Actions.Update);
            if (check != null) return check;

            await InitializePermissionsAsync();

            var me = await _currentUserService.GetCurrentUserAsync();
            CanEditRules = me.IsTenantAdmin;

            if (!CanEditRules)
            {
                // The API refuses this too. Checked here as well so the
                // message is the page's rather than a bare 403.
                ErrorMessage = "Only a workspace admin can change the sales process.";
                await ReloadAsync();
                return Page();
            }

            try
            {
                var dto = new SaveAllPipelineRulesDto(
                    BlockReopenWithIssuedInvoice,
                    Cells.Select(c => new SaveTransitionDto(
                        c.FromStageKey,
                        c.ToStageKey,
                        c.Label,
                        c.IsActive,
                        c.Actor,
                        c.RequiresQuote,
                        c.RequiresAcceptedQuote,
                        c.RequiresValue,
                        c.RequiresCloseDate,
                        c.RequiresNote,
                        c.NotePrompt,
                        c.RequiresAttachment)).ToList());

                await _ruleService.SaveAsync(dto);

                SuccessMessage = "Sales process saved.";
                return RedirectToPage();
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                await ReloadAsync();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save the pipeline process");
                ErrorMessage = "Failed to save the sales process. Please try again.";
                await ReloadAsync();
                return Page();
            }
        }

        // ── POST: apply the suggested process ──────────────────────────

        public async Task<IActionResult> OnPostSuggestAsync()
        {
            var check = await ValidatePermissionAsync(Actions.Update);
            if (check != null) return check;

            var me = await _currentUserService.GetCurrentUserAsync();
            if (!me.IsTenantAdmin)
            {
                ErrorMessage = "Only a workspace admin can change the sales process.";
                return RedirectToPage();
            }

            try
            {
                await _ruleService.ApplySuggestedAsync();
                SuccessMessage =
                    "Suggested process applied. Nothing was deleted — review the steps below and save if you want to adjust them.";
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply the suggested process");
                ErrorMessage = "Failed to apply the suggested process. Please try again.";
            }

            return RedirectToPage();
        }

        // ── building the grid ──────────────────────────────────────────

        /// <summary>
        /// Every from-to pair, whether a transition row exists or not. The
        /// migration seeds a full matrix, but a tenant who ran it with
        /// ForwardOnly on has no backward rows, and a stage added later has
        /// none at all until it is saved — a grid with holes would be
        /// impossible to reason about.
        /// </summary>
        private void BuildGrid()
        {
            BlockReopenWithIssuedInvoice = Rules.BlockReopenWithIssuedInvoice;

            var byPair = Rules.Transitions
                .GroupBy(t => (t.FromStageKey, t.ToStageKey))
                .ToDictionary(g => g.Key, g => g.First());

            var cells = new List<CellInput>();

            // Out of EVERY stage, including retired ones: a deal parked in a
            // stage the tenant has withdrawn still needs a way forward.
            foreach (var from in Rules.Stages.OrderBy(s => s.SortOrder))
            {
                // Into ACTIVE stages only. Offering a retired stage as a
                // destination is exactly what retiring it was meant to stop.
                foreach (var to in Rules.Stages.Where(s => s.IsActive).OrderBy(s => s.SortOrder))
                {
                    if (from.Key == to.Key) continue;

                    byPair.TryGetValue((from.Key, to.Key), out var t);

                    cells.Add(new CellInput
                    {
                        FromStageKey = from.Key,
                        ToStageKey = to.Key,
                        FromStageName = from.Name,
                        ToStageName = to.Name,
                        ToCategory = to.Category,
                        ToIsActive = to.IsActive,

                        IsActive = t?.IsActive ?? false,
                        Label = t?.Label ?? DefaultLabel(from, to),
                        Actor = t?.Actor ?? TransitionActor.Anyone,

                        RequiresQuote = t?.RequiresQuote ?? false,
                        RequiresAcceptedQuote = t?.RequiresAcceptedQuote ?? false,
                        RequiresValue = t?.RequiresValue ?? false,
                        RequiresCloseDate = t?.RequiresCloseDate ?? false,
                        RequiresAttachment = t?.RequiresAttachment ?? false,
                        RequiresNote = t?.RequiresNote ?? false,
                        NotePrompt = t?.NotePrompt
                    });
                }
            }

            Cells = cells;
        }

        private static string DefaultLabel(StageLiteDto from, StageLiteDto to)
            => TransitionLabels.For(from.Category, to.Category, to.Name);

        /// <summary>
        /// After a failed save, reload what the page needs to RENDER — the
        /// stage names and categories — and leave the posted checkbox values
        /// alone, so an admin who ticked twenty boxes and hit a failure does
        /// not have to tick them again.
        /// </summary>
        private async Task ReloadAsync()
        {
            try
            {
                Rules = await _ruleService.GetAsync();

                var stages = Rules.Stages.ToDictionary(s => s.Key);

                foreach (var c in Cells)
                {
                    if (stages.TryGetValue(c.FromStageKey, out var f))
                        c.FromStageName = f.Name;

                    if (stages.TryGetValue(c.ToStageKey, out var t))
                    {
                        c.ToStageName = t.Name;
                        c.ToCategory = t.Category;
                        c.ToIsActive = t.IsActive;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload the pipeline process after a failed save");
                LoadFailed = true;
            }
        }

        // ── view helpers ───────────────────────────────────────────────

        /// <summary>The grid, grouped into one section per source stage.</summary>
        public IEnumerable<IGrouping<string, (CellInput Cell, int Index)>> Sections =>
            Cells.Select((c, i) => (Cell: c, Index: i))
                 .GroupBy(x => x.Cell.FromStageKey);

        public StageLiteDto? StageByKey(string key) =>
            Rules.Stages.FirstOrDefault(s => s.Key == key);

        /// <summary>
        /// A stage nothing can leave. Deals there are stuck unless an admin
        /// overrides on the deal page, so the page says so loudly.
        /// </summary>
        public bool IsDeadEnd(string stageKey) =>
            Cells.Where(c => c.FromStageKey == stageKey).All(c => !c.IsActive);

        public int LiveCount => Cells.Count(c => c.IsActive);

        public string CategoryLabel(StageCategory c) => c switch
        {
            StageCategory.Won => "Won",
            StageCategory.Lost => "Lost",
            _ => "Open"
        };

        public string CategoryBadge(StageCategory c) => c switch
        {
            StageCategory.Won => "bg-success",
            StageCategory.Lost => "bg-danger",
            _ => "bg-secondary"
        };

        public string ActorLabel(TransitionActor a) => a switch
        {
            TransitionActor.DealOwner => "Deal owner",
            TransitionActor.TeamManagers => "Managers + admins",
            TransitionActor.Admins => "Admins only",
            _ => "Anyone"
        };

        private static PipelineRulesDto Empty() => new(
            PipelineRuleDefaults.BlockReopenWithIssuedInvoice,
            IsDefault: true,
            UpdatedAtUtc: null,
            UpdatedBy: null,
            Stages: new List<StageLiteDto>(),
            Transitions: new List<TransitionDto>(),
            DeadEndStageKeys: new List<string>());
    }
}
