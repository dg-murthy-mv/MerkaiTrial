// =====================================================================
// TENANT DETAIL PAGE - Backend (REFACTORED)
// Location: MerkaiTrial.Admin.Web/Pages/Admin/Tenants/Detail.cshtml.cs
//
// CHANGE 1: Injected GetPlanByNameHandler, GetPublicPlansHandler, ChangeTenantPlanHandler
// CHANGE 2: Added ChangePlanModel property — feeds _ChangePlanPartial
// CHANGE 3: Added LoadChangePlanModelAsync() — called in OnGetAsync
// CHANGE 4: Added OnPostChangePlanAsync() — handles plan change form
// Everything else (Toggle, Delete, AJAX handlers) is identical to original.
// =====================================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MerkaiTrial.Admin.Web.Services.Tenants;
using MerkaiTrial.Application.Commands.Plans;   // ← NEW
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Web.Pages.Admin.Tenants;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Tenants
{
    public class DetailModel : PageModel
    {
        private readonly ITenantService          _tenantService;
        private readonly GetPlanByNameHandler    _getPlan;          // ← NEW
        private readonly GetPublicPlansHandler   _getPublicPlans;   // ← NEW
        private readonly ChangeTenantPlanHandler _changePlan;       // ← NEW
        private readonly ILogger<DetailModel>    _logger;

        public DetailModel(
            ITenantService          tenantService,
            GetPlanByNameHandler    getPlan,                        // ← NEW
            GetPublicPlansHandler   getPublicPlans,                 // ← NEW
            ChangeTenantPlanHandler changePlan,                     // ← NEW
            ILogger<DetailModel>    logger)
        {
            _tenantService  = tenantService;
            _getPlan        = getPlan;
            _getPublicPlans = getPublicPlans;
            _changePlan     = changePlan;
            _logger         = logger;
        }

        // ==================== PROPERTIES ====================
        public TenantDto          Tenant          { get; set; } = null!;
        public TenantStatsDto     Stats           { get; set; } = null!;
        public List<UserListItem> Users           { get; set; } = new();
        public ChangePlanViewModel ChangePlanModel { get; set; } = new(); // ← NEW

        [BindProperty(SupportsGet = true)]
        public Guid Id { get; set; }

        [BindProperty(SupportsGet = true)]
        public string ActiveTab { get; set; } = "overview";

        // ==================== GET ====================
        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                Tenant = await _tenantService.GetByIdAsync(Id);
                Stats  = await _tenantService.GetStatsAsync(Id);

                if (ActiveTab == "users")
                    Users = await _tenantService.GetUsersAsync(Id);

                await LoadChangePlanModelAsync();  // ← NEW

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading tenant detail {TenantId}", Id);
                TempData["ErrorMessage"] = "Failed to load tenant details.";
                return RedirectToPage("./Index");
            }
        }

        // ==================== POST: CHANGE PLAN (NEW) ====================
        public async Task<IActionResult> OnPostChangePlanAsync(
            Guid   id,
            string newPlanName,
            bool   applyLimitsImmediately = true)
        {
            try
            {
                var cmd = new ChangeTenantPlanCommand(
                    TenantId:               id,
                    NewPlanName:            newPlanName,
                    ApplyLimitsImmediately: applyLimitsImmediately,
                    ChangedBy:              User.Identity?.Name ?? "SuperAdmin"
                );

                var result = await _changePlan.Handle(cmd);

                TempData["SuccessMessage"] =
                    $"Plan changed from '{result.OldPlan}' → '{result.NewPlan}'" +
                    (result.LimitsApplied ? " — limits updated." : " — limits unchanged (custom deal).");
            }
            catch (InvalidOperationException ex)
            {
                TempData["ErrorMessage"] = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to change plan for tenant {TenantId}", id);
                TempData["ErrorMessage"] = "Failed to change plan. Please try again.";
            }

            return RedirectToPage(new { id });
        }

        // ==================== AJAX: GET USERS ====================
        public async Task<IActionResult> OnGetUsersAsync(Guid id)
        {
            try
            {
                var users = await _tenantService.GetUsersAsync(id);
                return new JsonResult(users);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading users for tenant {TenantId}", id);
                return new JsonResult(new { error = "Failed to load users" }) { StatusCode = 500 };
            }
        }

        // ==================== AJAX: GET STATISTICS ====================
        public async Task<IActionResult> OnGetStatsAsync(Guid id)
        {
            try
            {
                var stats = await _tenantService.GetStatsAsync(id);
                return new JsonResult(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading stats for tenant {TenantId}", id);
                return new JsonResult(new { error = "Failed to load statistics" }) { StatusCode = 500 };
            }
        }

        // ==================== TOGGLE STATUS ====================
        public async Task<IActionResult> OnPostToggleStatusAsync(Guid id, bool isActive)
        {
            try
            {
                await _tenantService.UpdateStatusAsync(id, isActive);
                TempData["SuccessMessage"] =
                    $"Tenant {(isActive ? "activated" : "deactivated")} successfully!";
                return RedirectToPage(new { id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to toggle status for tenant {TenantId}", id);
                TempData["ErrorMessage"] = "Failed to update tenant status.";
                return RedirectToPage(new { id });
            }
        }

        // ==================== DELETE TENANT ====================
        public async Task<IActionResult> OnPostDeleteAsync(Guid id)
        {
            try
            {
                await _tenantService.DeleteAsync(id);
                TempData["SuccessMessage"] = "Tenant deleted successfully!";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete tenant {TenantId}", id);
                TempData["ErrorMessage"] = "Failed to delete tenant.";
                return RedirectToPage(new { id });
            }
        }

        // ==================== HELPER: BUILD CHANGE PLAN VIEW MODEL ====================
        private async Task LoadChangePlanModelAsync()
        {
            try
            {
                var settings    = await _tenantService.GetSettingsAsync(Id);
                var currentPlan = await _getPlan.Handle(Tenant.Plan ?? "starter");
                var allPlans    = await _getPublicPlans.Handle();

                ChangePlanModel = new ChangePlanViewModel
                {
                    TenantId               = Id,
                    CurrentPlanName        = Tenant.Plan ?? "starter",
                    CurrentPlanDisplayName = currentPlan.DisplayName,
                    CurrentPlanPrice       = currentPlan.MonthlyPrice,
                    MaxUsers               = settings.MaxUsers,
                    MaxLeads               = settings.MaxLeads,
                    MaxDeals               = settings.MaxDeals,
                    AvailablePlans         = allPlans
                };
            }
            catch (Exception ex)
            {
                // Non-fatal — page still loads, plan section degrades gracefully
                _logger.LogWarning(ex, "Could not load ChangePlanModel for tenant {TenantId}", Id);
                ChangePlanModel = new ChangePlanViewModel
                {
                    TenantId               = Id,
                    CurrentPlanName        = Tenant.Plan ?? "starter",
                    CurrentPlanDisplayName = Tenant.Plan ?? "starter",
                    AvailablePlans         = new List<PlanCardDto>()
                };
            }
        }
    }
}
