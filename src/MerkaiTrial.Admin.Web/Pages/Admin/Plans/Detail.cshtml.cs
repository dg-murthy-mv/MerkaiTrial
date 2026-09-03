// =====================================================================
// PLANS DETAIL PAGE — Backend
// Location: MerkaiTrial.Admin.Web/Pages/Admin/Plans/Detail.cshtml.cs
//
// Uses existing GetPlanDetailHandler (already has TenantCount + FeatureList)
// Uses new GetPlanTenantsHandler for the tenant rows table.
// =====================================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MerkaiTrial.Application.Commands.Plans;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Plans
{
    public class DetailModel : PageModel
    {
        private readonly GetPlanDetailHandler   _getPlanDetail;
        private readonly GetPlanTenantsHandler  _getPlanTenants;
        private readonly DeactivatePlanHandler  _deactivatePlan;
        private readonly ILogger<DetailModel>   _logger;

        public DetailModel(
            GetPlanDetailHandler  getPlanDetail,
            GetPlanTenantsHandler getPlanTenants,
            DeactivatePlanHandler deactivatePlan,
            ILogger<DetailModel>  logger)
        {
            _getPlanDetail  = getPlanDetail;
            _getPlanTenants = getPlanTenants;
            _deactivatePlan = deactivatePlan;
            _logger         = logger;
        }

        [BindProperty(SupportsGet = true)]
        public Guid Id { get; set; }

        // PlanDto already has: FeatureList, TenantCount, StorageLimitDisplay,
        // Description, BadgeText, IsHighlighted, AnnualPrice — everything
        public PlanDto Plan { get; set; } = null!;

        // Actual tenant rows for the table
        public List<PlanTenantItem> Tenants { get; set; } = new();

        // Feature keys resolved to their display label + icon, via the same
        // catalog CreateEdit uses — so this page never shows raw JSON keys.
        public List<FeatureDefinition> PlanFeatures { get; set; } = new();

        // ==================== GET ====================

        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                Plan         = await _getPlanDetail.Handle(Id);
                Tenants      = await _getPlanTenants.Handle(Id);
                PlanFeatures = FeatureCatalog.Resolve(Plan.FeatureList);
                return Page();
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Plan not found.";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading plan detail {PlanId}", Id);
                TempData["ErrorMessage"] = "Failed to load plan details.";
                return RedirectToPage("./Index");
            }
        }

        // ==================== POST: DEACTIVATE ====================
        // DeactivatePlanHandler already guards against active tenants existing

        public async Task<IActionResult> OnPostDeactivateAsync(Guid id)
        {
            try
            {
                await _deactivatePlan.Handle(
                    planId:        id,
                    deactivatedBy: User.Identity?.Name ?? "SuperAdmin");

                TempData["SuccessMessage"] = "Plan deactivated. Existing tenants are unaffected.";
            }
            catch (InvalidOperationException ex)
            {
                TempData["ErrorMessage"] = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deactivate plan {PlanId}", id);
                TempData["ErrorMessage"] = "Failed to deactivate plan.";
            }

            return RedirectToPage(new { id });
        }
    }
}
