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
using MerkaiTrial.Application.Commands.Automation;   // 098
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Plans
{
    public class DetailModel : PageModel
    {
        private readonly GetPlanDetailHandler   _getPlanDetail;
        private readonly GetPlanTenantsHandler  _getPlanTenants;
        private readonly DeactivatePlanHandler  _deactivatePlan;
        private readonly GetPlanAutomationLimitHandler _getAutomationLimit;   // 098
        private readonly SetPlanAutomationLimitHandler _setAutomationLimit;   // 098
        private readonly ILogger<DetailModel>   _logger;

        public DetailModel(
            GetPlanDetailHandler  getPlanDetail,
            GetPlanTenantsHandler getPlanTenants,
            DeactivatePlanHandler deactivatePlan,
            GetPlanAutomationLimitHandler getAutomationLimit,
            SetPlanAutomationLimitHandler setAutomationLimit,
            ILogger<DetailModel>  logger)
        {
            _getAutomationLimit = getAutomationLimit;
            _setAutomationLimit = setAutomationLimit;
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

        /// <summary>
        /// 098. How many automation rules a workspace on this plan may have
        /// switched ON. Read live by every workspace on the plan, so a change
        /// here applies to all of them at once.
        /// </summary>
        public int MaxAutomationRules { get; set; }

        // ==================== GET ====================

        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                Plan         = await _getPlanDetail.Handle(Id);
                Tenants      = await _getPlanTenants.Handle(Id);
                PlanFeatures = FeatureCatalog.Resolve(Plan.FeatureList);
                MaxAutomationRules = (await _getAutomationLimit.Handle(Id))?.MaxAutomationRules ?? 0;   // 098
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

        // ==================== POST: AUTOMATION LIMIT (098) ====================
        // Its own small form rather than a field on Edit Plan, so the plan
        // editor and its DTOs stay as they were. Lowering it never switches
        // a workspace's rules off — the newest ones above the limit are held
        // back by the engine and can't be switched on again until there's room.

        public async Task<IActionResult> OnPostAutomationLimitAsync(Guid id, int maxAutomationRules)
        {
            try
            {
                await _setAutomationLimit.Handle(id, maxAutomationRules, User.Identity?.Name ?? "SuperAdmin");
                TempData["SuccessMessage"] = maxAutomationRules == 0
                    ? "Automation is now not included in this plan. Existing rules are kept, switched on rules stop running."
                    : $"Workspaces on this plan can now have {maxAutomationRules} automation rule{(maxAutomationRules == 1 ? "" : "s")} switched on.";
            }
            catch (InvalidOperationException ex)
            {
                TempData["ErrorMessage"] = ex.Message;
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Plan not found.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set the automation limit on plan {PlanId}", id);
                TempData["ErrorMessage"] = "Failed to save the automation limit.";
            }

            return RedirectToPage(new { id });
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
