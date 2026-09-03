// =====================================================================
// IndexModel.cs
// Location: MerkaiTrial.Web/Pages/Admin/Plans/Index.cshtml.cs
// =====================================================================

using MerkaiTrial.Application.Commands.Plans;
using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Plans;

public class IndexModel : PageModel
{
    private readonly GetPlansHandler        _getPlans;
    private readonly GetPlanSummaryHandler  _getSummary;
    private readonly DeactivatePlanHandler  _deactivate;
    private readonly ILogger<IndexModel>    _logger;

    public IndexModel(
        GetPlansHandler       getPlans,
        GetPlanSummaryHandler getSummary,
        DeactivatePlanHandler deactivate,
        ILogger<IndexModel>   logger)
    {
        _getPlans   = getPlans;
        _getSummary = getSummary;
        _deactivate = deactivate;
        _logger     = logger;
    }

    // ── Bound properties ─────────────────────────────────────────
    [BindProperty(SupportsGet = true)]
    public string Filter { get; set; } = "active";

    public List<PlanListItem>    Plans      { get; private set; } = new();
    public List<PlanSummaryDto>  PlanSummary { get; private set; } = new();
    public PlanIndexStats        Stats      { get; private set; } = new();

    // ── GET ───────────────────────────────────────────────────────
    public async Task OnGetAsync()
    {
        var includeInactive = Filter == "all";

        Plans       = await _getPlans.Handle(includeInactive);
        PlanSummary = await _getSummary.Handle();

        Stats = new PlanIndexStats
        {
            TotalPlans            = Plans.Count,
            ActivePlans           = Plans.Count(p => p.IsActive),
            TotalTenantsOnPlans   = PlanSummary.Sum(s => s.TenantCount),
            MonthlyRevenue        = PlanSummary.Sum(s => s.MonthlyRevenue)
        };
    }

    // ── POST: Deactivate ──────────────────────────────────────────
    public async Task<IActionResult> OnPostDeactivateAsync(Guid planId)
    {
        try
        {
            await _deactivate.Handle(planId, "SuperAdmin");
            TempData["Success"] = "Plan deactivated successfully.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deactivating plan {PlanId}", planId);
            TempData["Error"] = "An unexpected error occurred.";
        }

        return RedirectToPage();
    }

    // ── View model for stats row ──────────────────────────────────
    public record PlanIndexStats
    {
        public int     TotalPlans          { get; init; }
        public int     ActivePlans         { get; init; }
        public int     TotalTenantsOnPlans { get; init; }
        public decimal MonthlyRevenue      { get; init; }
    }
}
