// =====================================================================
// Settings/PipelineRules/Index.cshtml.cs
// Location: MerkaiTrial.Admin.Web/Pages/Settings/PipelineRules/Index.cshtml.cs
//
// NEW FILE (019).
//
// Deliberately a SEPARATE page from Settings/Pipeline/Index, which is
// about what a stage is — its name, order, probability and category.
// This is about what a stage demands. Putting both on one screen would
// mean a tenant renaming a stage has to scroll past five checkboxes, and
// a tenant tightening a rule has to be careful not to nudge the order.
//
// Admin-only. The rules decide who may reopen a closed deal, so a rep who
// could edit them could simply switch the restriction off.
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
        /// set here rather than null, so the view renders the page and its
        /// error message instead of having to bail out halfway through its
        /// own markup and close no tags.
        /// </summary>
        public PipelineRulesDto Rules { get; private set; } = EmptyRules();

        /// <summary>True when the rules could not be read at all.</summary>
        public bool LoadFailed { get; private set; }

        private static PipelineRulesDto EmptyRules() => new(
            PipelineRuleDefaults.ForwardOnly,
            PipelineRuleDefaults.ReopenRequiresReason,
            PipelineRuleDefaults.ReopenRestrictedToManagers,
            PipelineRuleDefaults.BlockReopenWithIssuedInvoice,
            IsDefault: true,
            UpdatedAtUtc: null,
            UpdatedBy: null,
            Stages: new List<StageRequirementsDto>());

        /// <summary>
        /// Admin-only, and the page says so rather than hiding itself. A
        /// sales manager who wonders why a deal will not reopen should be
        /// able to read the rule that stopped it.
        /// </summary>
        public bool CanEditRules { get; private set; }

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

        // ── Posted form ────────────────────────────────────────────────

        [BindProperty] public bool ForwardOnly { get; set; }
        [BindProperty] public bool ReopenRequiresReason { get; set; }
        [BindProperty] public bool ReopenRestrictedToManagers { get; set; }
        [BindProperty] public bool BlockReopenWithIssuedInvoice { get; set; }

        /// <summary>
        /// One row per stage. A list of a flat input model rather than the
        /// DTO itself: Razor Pages needs settable properties and an index
        /// to bind a collection, and a positional record gives it neither.
        /// </summary>
        [BindProperty] public List<StageRuleInput> StageRules { get; set; } = new();

        public class StageRuleInput
        {
            public Guid StageId { get; set; }
            public string Key { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public StageCategory Category { get; set; }
            public bool IsActive { get; set; }

            public bool RequiresQuote { get; set; }
            public bool RequiresAcceptedQuote { get; set; }
            public bool RequiresCloseDate { get; set; }
            public bool RequiresValue { get; set; }
            public bool RequiresLostReason { get; set; }
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
                Bind(Rules);
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load pipeline rules");
                LoadFailed = true;
                ErrorMessage = "Failed to load the pipeline rules. Please try again.";
                return Page();
            }
        }

        // ── POST ───────────────────────────────────────────────────────

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
                ErrorMessage = "Only a workspace admin can change the pipeline rules.";
                await ReloadAsync();
                return Page();
            }

            try
            {
                var dto = new SaveAllPipelineRulesDto(
                    new SavePipelineRulesDto(
                        ForwardOnly,
                        ReopenRequiresReason,
                        ReopenRestrictedToManagers,
                        BlockReopenWithIssuedInvoice),
                    StageRules.Select(s => new SaveStageRequirementsDto(
                        s.StageId,
                        s.RequiresQuote,
                        s.RequiresAcceptedQuote,
                        s.RequiresCloseDate,
                        s.RequiresValue,
                        s.RequiresLostReason)).ToList());

                await _ruleService.SaveAsync(dto);

                SuccessMessage = "Pipeline rules saved.";
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
                _logger.LogError(ex, "Failed to save pipeline rules");
                ErrorMessage = "Failed to save the pipeline rules. Please try again.";
                await ReloadAsync();
                return Page();
            }
        }

        // ── helpers ────────────────────────────────────────────────────

        private void Bind(PipelineRulesDto rules)
        {
            ForwardOnly = rules.ForwardOnly;
            ReopenRequiresReason = rules.ReopenRequiresReason;
            ReopenRestrictedToManagers = rules.ReopenRestrictedToManagers;
            BlockReopenWithIssuedInvoice = rules.BlockReopenWithIssuedInvoice;

            StageRules = rules.Stages.Select(s => new StageRuleInput
            {
                StageId = s.StageId,
                Key = s.Key,
                Name = s.Name,
                Category = s.Category,
                IsActive = s.IsActive,
                RequiresQuote = s.RequiresQuote,
                RequiresAcceptedQuote = s.RequiresAcceptedQuote,
                RequiresCloseDate = s.RequiresCloseDate,
                RequiresValue = s.RequiresValue,
                RequiresLostReason = s.RequiresLostReason
            }).ToList();
        }

        /// <summary>
        /// After a failed save, reload only what the page needs to RENDER —
        /// the stage names and categories. The posted checkbox values are
        /// left alone, so an admin who ticked six boxes and hit a failure
        /// does not have to tick them again.
        /// </summary>
        private async Task ReloadAsync()
        {
            try
            {
                Rules = await _ruleService.GetAsync();

                foreach (var row in StageRules)
                {
                    var known = Rules.Stages.FirstOrDefault(s => s.StageId == row.StageId);
                    if (known is null) continue;

                    row.Key = known.Key;
                    row.Name = known.Name;
                    row.Category = known.Category;
                    row.IsActive = known.IsActive;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload pipeline rules after a failed save");
                LoadFailed = true;
            }
        }

        // ── view helpers ───────────────────────────────────────────────

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
    }
}
