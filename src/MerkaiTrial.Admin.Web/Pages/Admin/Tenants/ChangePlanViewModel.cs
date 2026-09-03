// =====================================================================
// ChangePlanViewModel.cs
// Location: MerkaiTrial.Web/Pages/Admin/Tenants/ChangePlanViewModel.cs
//
// ViewModel for _ChangePlanPartial.cshtml
// Populate this inside your TenantDetail PageModel and pass it to
// the partial: <partial name="_ChangePlanPartial" model="@Model.ChangePlanModel" />
// =====================================================================

using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Web.Pages.Admin.Tenants;

public class ChangePlanViewModel
{
    // ── Current state ─────────────────────────────────────────────
    public Guid   TenantId              { get; set; }
    public string CurrentPlanName       { get; set; } = string.Empty;
    public string CurrentPlanDisplayName { get; set; } = string.Empty;
    public decimal CurrentPlanPrice     { get; set; }

    // Applied TenantSettings limits (may differ from plan defaults for custom deals)
    public int  MaxUsers  { get; set; }
    public int  MaxLeads  { get; set; }
    public int  MaxDeals  { get; set; }

    // ── Available plans for the dropdown ─────────────────────────
    public List<PlanCardDto> AvailablePlans { get; set; } = new();

    // ── CSS helper — color the plan icon by tier ──────────────────
    public string PlanColorClass => CurrentPlanName switch
    {
        "starter"      => "bg-secondary",
        "professional" => "bg-primary",
        "enterprise"   => "bg-purple",
        _              => "bg-secondary"
    };
}

// =====================================================================
// HOW TO USE — Add to your TenantDetailModel
// =====================================================================
//
// 1. Inject the handlers:
//
//    private readonly GetPublicPlansHandler    _getPublicPlans;
//    private readonly ChangeTenantPlanHandler  _changePlan;
//    private readonly GetPlanByNameHandler     _getPlanByName;
//
// 2. In OnGetAsync(), populate ChangePlanModel:
//
//    var currentPlan = await _getPlanByName.Handle(tenant.Plan ?? "starter");
//    var allPlans    = await _getPublicPlans.Handle();
//    var settings    = await _getSettings.Handle(tenantId);
//
//    ChangePlanModel = new ChangePlanViewModel
//    {
//        TenantId               = tenant.Id,
//        CurrentPlanName        = tenant.Plan ?? "starter",
//        CurrentPlanDisplayName = currentPlan.DisplayName,
//        CurrentPlanPrice       = currentPlan.MonthlyPrice,
//        MaxUsers               = settings.MaxUsers,
//        MaxLeads               = settings.MaxLeads,
//        MaxDeals               = settings.MaxDeals,
//        AvailablePlans         = allPlans
//    };
//
// 3. Add the POST handler:
//
//    public async Task<IActionResult> OnPostChangePlanAsync(
//        Guid   tenantId,
//        string newPlanName,
//        bool   applyLimitsImmediately = true)
//    {
//        try
//        {
//            var cmd = new ChangeTenantPlanCommand(
//                TenantId:                tenantId,
//                NewPlanName:             newPlanName,
//                ApplyLimitsImmediately:  applyLimitsImmediately,
//                ChangedBy:               User.Identity?.Name ?? "SuperAdmin"
//            );
//            var result = await _changePlan.Handle(cmd);
//            TempData["Success"] = $"Plan changed from '{result.OldPlan}' to '{result.NewPlan}'.";
//        }
//        catch (Exception ex)
//        {
//            TempData["Error"] = ex.Message;
//        }
//        return RedirectToPage(new { tenantId });
//    }
//
// 4. In TenantDetail.cshtml, place the partial:
//
//    <partial name="_ChangePlanPartial" model="@Model.ChangePlanModel" />
