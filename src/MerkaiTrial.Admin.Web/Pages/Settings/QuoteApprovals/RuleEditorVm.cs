// =====================================================================
// RuleEditorVm.cs
// Location: MerkaiTrial.Admin.Web/Pages/Settings/QuoteApprovals/RuleEditorVm.cs
//
// NEW FILE (027). What _RuleEditor.cshtml needs to draw one rule's form —
// whether that is an existing rule or a blank one.
//
// A typed record rather than ViewData, so a renamed field is a build
// error instead of a blank box at run time. Same pattern as
// _StageRow.cshtml in round 023.
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Entities;

namespace MerkaiTrial.Admin.Web.Pages.Settings.QuoteApprovals;

/// <param name="Rule">The rule being edited, or null to draw a blank one.</param>
/// <param name="Screen">The whole screen, for the role and person pickers.</param>
/// <param name="CurrencySymbol">The tenant's symbol, for the total field.</param>
/// <param name="Kinds">Approver kinds, in the order they are offered.</param>
/// <param name="MaxSteps">Hard cap on the chain length.</param>
public record RuleEditorVm(
    ApprovalRuleDto? Rule,
    ApprovalRulesPageDto Screen,
    string CurrencySymbol,
    ApproverKind[] Kinds,
    int MaxSteps)
{
    public bool IsNew => Rule == null;

    public List<ApprovalStepDto> Steps => Rule?.Steps ?? new List<ApprovalStepDto>();

    /// <summary>
    /// How many rows to render: the steps the rule has, plus spares so the
    /// chain can be extended with scripting switched off. A brand-new rule
    /// gets one pre-filled row and the spares.
    /// </summary>
    public int RowCount =>
        Math.Min(MaxSteps, Math.Max(1, Steps.Count) + IndexModel.SpareStepRows);

    /// <summary>A unique suffix for element ids, so two editors on one page can't collide.</summary>
    public string Key => Rule?.Id.ToString("N") ?? "new";
}
