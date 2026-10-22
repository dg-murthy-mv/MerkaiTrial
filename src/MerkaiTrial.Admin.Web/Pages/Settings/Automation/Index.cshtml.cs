// =====================================================================
// Settings → Automation — the rules list and the rule log
// Location: MerkaiTrial.Admin.Web/Pages/Settings/Automation/Index.cshtml.cs
//
// NEW FILE (098 — workflow automation).
//
//   GET                 rules, the plan's limit, the newest 100 log lines
//   POST ?handler=Toggle   switch a rule on / off   (automation.update)
//   POST ?handler=Delete   remove a rule            (automation.delete)
//   POST ?handler=CheckTime   099: check time-based rules now (automation.update)
//
// The Automation module gates everything: tenant admins pass through
// PermissionHandler's bypass, anyone else needs the tick in the role
// editor. The API checks the same policies, so a page that let someone
// through would still be refused there.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Automation;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Automation;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.Admin.Web.Pages.Settings.Automation
{
    public class IndexModel : AuthorizedPageModel
    {
        private readonly IAutomationService _automation;
        private readonly ILogger<IndexModel> _logger;

        protected override string ModuleName => Modules.Automation;

        public IndexModel(
            IAutomationService automation,
            ICurrentUserService currentUserService,
            IAuthorizationService authorizationService,
            ILogger<IndexModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _automation = automation;
            _logger = logger;
        }

        public AutomationRulesPageDto Data { get; private set; } = new(new(), 0, 0, null);
        public List<AutomationRunDto> Runs { get; private set; } = new();
        public bool LoadFailed { get; private set; }

        /// <summary>Show only this rule's lines in the log.</summary>
        [BindProperty(SupportsGet = true)] public Guid? RuleId { get; set; }

        public string? FilterRuleName => RuleId is { } id ? Data.Rules.FirstOrDefault(r => r.Id == id)?.Name : null;

        public bool AtLimit => Data.ActiveCount >= Data.Limit;

        /// <summary>099. Any time-based rule at all — shows "Check time-based rules now".</summary>
        public bool HasTimeRules => Data.Rules.Any(r => r.IsTimeBased);

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            var check = await ValidatePermissionAsync(Actions.Read);
            if (check != null) return check;
            await InitializePermissionsAsync();

            try
            {
                Data = await _automation.GetRulesAsync();
                Runs = await _automation.GetRunsAsync(RuleId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load the automation rules");
                LoadFailed = true;
            }
            return Page();
        }

        public async Task<IActionResult> OnPostToggleAsync(Guid id, bool isActive)
        {
            var check = await ValidatePermissionAsync(Actions.Update);
            if (check != null) return check;

            try
            {
                await _automation.SetActiveAsync(id, isActive);
                SuccessMessage = isActive ? "Rule switched on." : "Rule switched off.";
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to switch automation rule {RuleId}", id);
                ErrorMessage = "The rule couldn't be switched. Please try again.";
            }
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostDeleteAsync(Guid id)
        {
            var check = await ValidatePermissionAsync(Actions.Delete);
            if (check != null) return check;

            try
            {
                await _automation.DeleteAsync(id);
                SuccessMessage = "Rule deleted. Its lines stay in the log.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete automation rule {RuleId}", id);
                ErrorMessage = "The rule couldn't be deleted. Please try again.";
            }
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostCheckTimeAsync()
        {
            var check = await ValidatePermissionAsync(Actions.Update);
            if (check != null) return check;

            try
            {
                var r = await _automation.CheckTimeRulesNowAsync();
                SuccessMessage = r.RulesChecked == 0
                    ? "No time-based rules are switched on, so there was nothing to check."
                    : $"Checked {r.RulesChecked} time-based rule{(r.RulesChecked == 1 ? "" : "s")}: " +
                      (r.RecordsDue == 0
                          ? "nothing is due right now."
                          : $"{r.RecordsDue} record{(r.RecordsDue == 1 ? " was" : "s were")} due, and the rules ran for {r.RulesRan}. See the log below.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check time-based rules");
                ErrorMessage = "The time-based rules couldn't be checked just now. They are also checked automatically every 15 minutes.";
            }
            return RedirectToPage();
        }

        // ── view helpers ───────────────────────────────────────────────

        public static string StatusBadge(AutomationRunStatus s) => s switch
        {
            AutomationRunStatus.Done    => "bg-success-subtle text-success-emphasis",
            AutomationRunStatus.Skipped => "bg-secondary-subtle text-secondary-emphasis",
            AutomationRunStatus.Partial => "bg-warning-subtle text-warning-emphasis",
            _                           => "bg-danger-subtle text-danger-emphasis"
        };

        public static string StatusWord(AutomationRunStatus s) => s switch
        {
            AutomationRunStatus.Done    => "Done",
            AutomationRunStatus.Skipped => "Skipped",
            AutomationRunStatus.Partial => "Partly done",
            _                           => "Failed"
        };

        /// <summary>The page a log line's record opens on.</summary>
        public static string RecordPage(string entityType)
            => entityType == AutomationEntity.Deal ? "/Pipeline/Detail" : "/Leads/Detail";

        /// <summary>Starter rules, opened in the editor pre-filled. Nothing is saved until Save.</summary>
        public static IReadOnlyList<(string Key, string Icon, string Name, string Line)> Templates { get; } = new[]
        {
            ("welcome", "bi-telephone-outbound", "Call every new lead", "When a lead is added, give its owner a call task due today."),
            ("share",   "bi-arrow-repeat",       "Share new leads in turn", "When a lead is added with no owner, give it to the next person on a list."),
            ("bigdeal", "bi-bell",               "Big deal alert", "When a deal over a value moves stage, tell a manager."),
            ("won",     "bi-trophy",             "Handover when won", "When a deal reaches a stage you choose, give its owner a handover task."),
            ("quiet",   "bi-hourglass-split",    "Nudge when a lead goes quiet", "When a lead has had no activity for 7 days, give its owner a follow-up task."),
        };
    }
}
