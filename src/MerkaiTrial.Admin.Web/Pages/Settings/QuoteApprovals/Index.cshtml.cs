// =====================================================================
// QUOTE APPROVAL RULES — Settings
// Location: MerkaiTrial.Admin.Web/Pages/Settings/QuoteApprovals/Index.cshtml.cs
// URL:      /Settings/QuoteApprovals
//
// COMPLETE FILE — replaces the 017 version.
//
// WHAT THE OLD PAGE WAS
//   Two hard-coded checkboxes over a single settings row: a discount
//   limit and a total limit. There was no Add button because there was
//   nothing to add to — the workspace had exactly one "rule" and exactly
//   one approver step, and a two-level sign-off was impossible.
//
// WHAT THIS IS
//   A rule editor. Add named rules, order them (first match wins), give
//   each one an ordered chain of steps, and say who signs each step: the
//   deal owner's team managers, everyone holding a named ROLE (the
//   "Senior Manager approves it" case), one named person, or any
//   workspace admin.
//
// AUTHORIZATION
//   ModuleName is now Settings, not Quotes, and the hardcoded
//   `if (!me.IsTenantAdmin)` in OnPost is gone — the same anti-pattern
//   round 024 removed from PipelineRulesController. Reading needs
//   settings.read, changing needs settings.update, and the API enforces
//   the same policies independently. A Sales Manager can now be given
//   ownership of quote policy without being made a full workspace admin.
//
//   CONSEQUENCE, BY DESIGN: a role that could see this page before
//   because it had quotes.read no longer can. Tick "Sales Configuration"
//   (settings) in the role editor for anyone who should. Workspace admins
//   are unaffected — PermissionHandler bypasses everything for them.
//
// FORM BINDING
//   Every rule card carries its own copy of the same form fields, so only
//   the card you submit posts. Steps bind by index (Steps[0].Kind, …) so
//   an unticked checkbox can't shift the rows out of alignment — the
//   classic parallel-array bug. A row whose Kind is blank is an unused
//   spare and is skipped.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.Admin.Web.Pages.Settings.QuoteApprovals;

public class IndexModel : AuthorizedPageModel
{
    /// <summary>Blank rows offered beyond the ones a rule already has, so the page works without JavaScript.</summary>
    public const int SpareStepRows = 3;

    private readonly IQuoteApprovalService _approvals;

    protected override string ModuleName => Modules.Settings;

    public IndexModel(
        IQuoteApprovalService approvals,
        IAuthorizationService authorizationService,
        ICurrentUserService currentUserService,
        ILogger<IndexModel> logger)
        : base(authorizationService, currentUserService, logger)
    {
        _approvals = approvals;
    }

    public ApprovalRulesPageDto? Screen { get; private set; }
    public string? LoadError { get; private set; }

    /// <summary>Which rule's editor to open on load — after a failed save, the one being edited.</summary>
    [BindProperty(SupportsGet = true)] public Guid? EditId { get; set; }

    /// <summary>True to open the "add a rule" panel on load.</summary>
    [BindProperty(SupportsGet = true)] public bool AddNew { get; set; }

    // ── The rule form ─────────────────────────────────────────────────

    public class StepInput
    {
        /// <summary>Blank = an unused spare row. Otherwise an ApproverKind name.</summary>
        public string? Kind { get; set; }

        public string? Name { get; set; }
        public Guid? RoleId { get; set; }
        public Guid? UserId { get; set; }
        public bool Self { get; set; }
    }

    [BindProperty] public Guid? RuleId { get; set; }
    [BindProperty] public string? RuleName { get; set; }
    [BindProperty] public string? RuleDescription { get; set; }
    [BindProperty] public bool RuleActive { get; set; } = true;

    [BindProperty] public bool UseDiscount { get; set; }
    [BindProperty] public decimal? DiscountOverPercent { get; set; }
    [BindProperty] public bool UseTotal { get; set; }
    [BindProperty] public decimal? TotalOverAmount { get; set; }
    [BindProperty] public ApprovalConditionMode ConditionMode { get; set; } = ApprovalConditionMode.Any;

    [BindProperty] public List<StepInput> Steps { get; set; } = new();

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
    // Master switch
    // =================================================================

    public async Task<IActionResult> OnPostToggleEnabledAsync(bool enabled)
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        try
        {
            await _approvals.SetEnabledAsync(enabled);
            TempData["SuccessMessage"] = enabled
                ? "Quote approvals are on. A quote that matches a rule now needs sign-off before it can be sent."
                : "Quote approvals are off. Every quote can be sent straight away — the rules below are kept but ignored.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to toggle quote approvals");
            TempData["ErrorMessage"] = Explain(ex, "Couldn't change that. Please try again.");
        }

        return RedirectToPage();
    }

    // =================================================================
    // Save one rule and its whole chain
    // =================================================================

    public async Task<IActionResult> OnPostSaveRuleAsync()
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        var steps = BuildSteps(out var stepError);

        if (stepError != null)
        {
            TempData["ErrorMessage"] = stepError;
            return Reopen();
        }

        if (steps.Count == 0)
        {
            TempData["ErrorMessage"] = "Add at least one approval step — otherwise a quote matching this rule could never be approved.";
            return Reopen();
        }

        var dto = new SaveApprovalRuleDto(
            RuleId,
            RuleName?.Trim() ?? string.Empty,
            RuleDescription,
            RuleActive,
            UseDiscount ? DiscountOverPercent : null,
            UseTotal ? TotalOverAmount : null,
            ConditionMode,
            steps);

        try
        {
            await _approvals.SaveRuleAsync(dto);

            var isNew = RuleId == null;
            var chain = steps.Count == 1 ? "1 step" : $"{steps.Count} steps";

            TempData["SuccessMessage"] = isNew
                ? $"Rule \"{dto.Name}\" added with {chain}. It goes last in the order, so it only applies to quotes no earlier rule catches."
                : $"Rule \"{dto.Name}\" saved with {chain}. Quotes already waiting keep the number of steps they started with, but from their next step onward they follow the chain as you've just set it.";

            return RedirectToPage();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save approval rule {RuleId}", RuleId);
            TempData["ErrorMessage"] = Explain(ex, "Couldn't save that rule. Please try again.");
            return Reopen();
        }
    }

    /// <summary>
    /// Reopens the editor that was being used, so the refusal message isn't
    /// pointing at a collapsed panel.
    ///
    /// This is a redirect, so the form comes back showing what is SAVED, not
    /// what was typed — the same trade every other write on this page makes
    /// (post-redirect-get, one TempData banner, no double submits). The
    /// refusals are all things you can see at a glance and re-enter: a
    /// missing name, a role not picked, a duplicate rule name.
    /// </summary>
    private IActionResult Reopen()
        => RuleId.HasValue
            ? RedirectToPage(null, null, new { EditId = RuleId.Value }, "rules")
            : RedirectToPage(null, null, new { AddNew = true }, "rules");

    /// <summary>
    /// Turns the posted rows into steps, dropping unused spares and
    /// refusing a row that names a kind without its target. Order is the
    /// order of the rows.
    /// </summary>
    private List<SaveApprovalStepDto> BuildSteps(out string? error)
    {
        error = null;
        var result = new List<SaveApprovalStepDto>();

        foreach (var row in Steps ?? new List<StepInput>())
        {
            if (string.IsNullOrWhiteSpace(row.Kind)) continue;          // unused spare row

            if (!Enum.TryParse<ApproverKind>(row.Kind, ignoreCase: true, out var kind) ||
                !Enum.IsDefined(typeof(ApproverKind), kind))
            {
                error = "One of the steps has an approver we don't recognise. Reload the page and try again.";
                return result;
            }

            if (kind == ApproverKind.Role && (!row.RoleId.HasValue || row.RoleId.Value == Guid.Empty))
            {
                error = $"Step {result.Count + 1} says a role approves it — pick which role.";
                return result;
            }

            if (kind == ApproverKind.User && (!row.UserId.HasValue || row.UserId.Value == Guid.Empty))
            {
                error = $"Step {result.Count + 1} says one person approves it — pick who.";
                return result;
            }

            result.Add(new SaveApprovalStepDto(
                string.IsNullOrWhiteSpace(row.Name) ? null : row.Name.Trim(),
                kind,
                kind == ApproverKind.Role ? row.RoleId : null,
                kind == ApproverKind.User ? row.UserId : null,
                row.Self));

            if (result.Count >= ApprovalStep.MaxStepsPerRule) break;
        }

        return result;
    }

    // =================================================================
    // Order — which rule is tried first
    // =================================================================

    public async Task<IActionResult> OnPostMoveAsync(Guid ruleId, string direction)
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        if (ruleId == Guid.Empty)
        {
            TempData["ErrorMessage"] = "That rule couldn't be identified. Please reload the page.";
            return RedirectToPage(null, null, "rules");
        }

        ApprovalRulesPageDto page;
        try
        {
            page = await _approvals.GetRulesAsync(CurrencySymbol);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to re-read approval rules before reordering");
            TempData["ErrorMessage"] = "Couldn't check the current order, so nothing was changed. Please try again.";
            return RedirectToPage(null, null, "rules");
        }

        var ids = page.Rules.Select(r => r.Id).ToList();
        var at = ids.IndexOf(ruleId);

        if (at < 0)
        {
            TempData["ErrorMessage"] = "That rule no longer exists. Please reload the page.";
            return RedirectToPage(null, null, "rules");
        }

        var up = string.Equals(direction, "up", StringComparison.OrdinalIgnoreCase);
        var to = up ? at - 1 : at + 1;

        if (to < 0 || to >= ids.Count)
        {
            // Already at the end it was asked to move towards — a double
            // click, or a stale page. Nothing to do, nothing to apologise for.
            return RedirectToPage(null, null, "rules");
        }

        (ids[at], ids[to]) = (ids[to], ids[at]);

        try
        {
            await _approvals.ReorderRulesAsync(ids);

            var moved = page.Rules.First(r => r.Id == ruleId).Name;
            TempData["SuccessMessage"] = to == 0
                ? $"\"{moved}\" is now tried first."
                : $"\"{moved}\" moved {(up ? "up" : "down")} to position {to + 1}.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to reorder approval rules");
            TempData["ErrorMessage"] = Explain(ex, "Couldn't change the order. Please try again.");
        }

        return RedirectToPage(null, null, "rules");
    }

    // =================================================================
    // Delete
    // =================================================================

    public async Task<IActionResult> OnPostDeleteRuleAsync(Guid ruleId)
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        if (ruleId == Guid.Empty)
        {
            TempData["ErrorMessage"] = "That rule couldn't be identified. Please reload the page.";
            return RedirectToPage(null, null, "rules");
        }

        // Asked BEFORE the delete, so the message can say what it means for
        // the quotes already walking this chain.
        var inFlight = 0;
        try { inFlight = await _approvals.InFlightCountAsync(ruleId); }
        catch (Exception ex) { Logger.LogWarning(ex, "Couldn't count in-flight requests for rule {RuleId}", ruleId); }

        try
        {
            await _approvals.DeleteRuleAsync(ruleId);

            TempData["SuccessMessage"] = inFlight == 0
                ? "Rule removed. New quotes are measured against the rules that are left."
                : $"Rule removed. {inFlight} quote{(inFlight == 1 ? "" : "s")} part-way through that chain keep{(inFlight == 1 ? "s" : "")} the same number of steps, but the steps still to come now go to the deal owner's team managers instead of whoever the rule named.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete approval rule {RuleId}", ruleId);
            TempData["ErrorMessage"] = Explain(ex, "Couldn't remove that rule. Please try again.");
        }

        return RedirectToPage(null, null, "rules");
    }

    // =================================================================

    private async Task LoadAsync()
    {
        try
        {
            Screen = await _approvals.GetRulesAsync(CurrencySymbol);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load approval rules");
            // Shown inside the card, not as a global banner: a global one
            // would fight the TempData alert from whatever POST sent us here,
            // and _Layout renders only one of each.
            LoadError = "The approval rules couldn't be loaded just now. Reload the page to try again.";
        }
    }

    private static string Explain(Exception ex, string fallback)
        => ex is InvalidOperationException && !string.IsNullOrWhiteSpace(ex.Message)
            ? ex.Message
            : fallback;

    // ── View helpers ──────────────────────────────────────────────────

    public bool IsEditing(Guid ruleId) => EditId.HasValue && EditId.Value == ruleId;

    public static readonly ApproverKind[] AllKinds =
    {
        ApproverKind.TeamManagers,
        ApproverKind.Role,
        ApproverKind.User,
        ApproverKind.WorkspaceAdmin
    };

    public static string KindLabel(ApproverKind k) => k switch
    {
        ApproverKind.TeamManagers => "The deal owner's team managers",
        ApproverKind.Role => "Everyone with a role…",
        ApproverKind.User => "One specific person…",
        _ => "Any workspace admin"
    };

    public static string KindShort(ApproverKind k) => k switch
    {
        ApproverKind.TeamManagers => "Team managers",
        ApproverKind.Role => "Role",
        ApproverKind.User => "Person",
        _ => "Workspace admin"
    };

    public static string KindIcon(ApproverKind k) => k switch
    {
        ApproverKind.TeamManagers => "bi-people",
        ApproverKind.Role => "bi-shield-check",
        ApproverKind.User => "bi-person",
        _ => "bi-star"
    };

    // Row counting lives on RuleEditorVm, which is what the editor uses.
    // A second copy here disagreed with it by one row.

    public static int MaxSteps => ApprovalStep.MaxStepsPerRule;

    /// <summary>"1st", "2nd", "3rd" — the order a rule is tried in.</summary>
    public static string Ordinal(int zeroBased)
    {
        var n = zeroBased + 1;
        var suffix = (n % 100 is >= 11 and <= 13) ? "th" : (n % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th"
        };
        return $"{n}{suffix}";
    }
}
