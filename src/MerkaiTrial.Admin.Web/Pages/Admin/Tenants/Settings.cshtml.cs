// =====================================================================
// TENANT SETTINGS PAGE - Backend (REFACTORED)
// Location: MerkaiTrial.Admin.Web/Pages/Admin/Tenants/Settings.cshtml.cs
//
// CHANGE 1: Injected GetPlanByNameHandler — plan limits now come from DB
// CHANGE 2: Removed GetPlanLimits() hardcoded switch
// CHANGE 3: UpdateSettingsAsync gains bypassPlanLimits param for Super Admin
// CHANGE 4: PlanLimits.StorageLimitGB now decimal (was implicit)
// Everything else is identical to original.
// =====================================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MerkaiTrial.Admin.Web.Services.Tenants;
using MerkaiTrial.Application.Commands.Plans;   // ← NEW
using MerkaiTrial.Application.DTOs;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Tenants
{
    public class SettingsModel : PageModel
    {
        private readonly ITenantService        _tenantService;
        private readonly GetPlanByNameHandler  _getPlan;          // ← NEW
        private readonly ILogger<SettingsModel> _logger;

        public SettingsModel(
            ITenantService        tenantService,
            GetPlanByNameHandler  getPlan,                        // ← NEW
            ILogger<SettingsModel> logger)
        {
            _tenantService = tenantService;
            _getPlan       = getPlan;
            _logger        = logger;
        }

        // ==================== PROPERTIES ====================
        [BindProperty]
        public InputModel Input { get; set; } = new();

        [BindProperty(SupportsGet = true)]
        public Guid Id { get; set; }

        public TenantDto   Tenant { get; set; } = null!;
        public PlanLimits  Limits { get; set; } = new();

        // Super Admin override — checked via hidden checkbox in the form
        [BindProperty]
        public bool BypassPlanLimits { get; set; } = false;

        // ==================== INPUT MODEL ====================
        public class InputModel
        {
            [Required]
            [Range(1, 10000, ErrorMessage = "Max users must be between 1 and 10,000")]
            public int MaxUsers { get; set; }

            [Required]
            [Range(1, 1000000, ErrorMessage = "Max leads must be between 1 and 1,000,000")]
            public int MaxLeads { get; set; }

            [Required]
            [Range(1, 1000000, ErrorMessage = "Max deals must be between 1 and 1,000,000")]
            public int MaxDeals { get; set; }

            [Required]
            [Range(0.1, 10000, ErrorMessage = "Storage limit must be between 0.1 GB and 10,000 GB")]
            public decimal StorageLimitGB { get; set; }

            public string? FeatureFlags { get; set; }
        }

        // ==================== PLAN LIMITS (view model) ====================
        public class PlanLimits
        {
            public string  DisplayName    { get; set; } = string.Empty;
            public int     MaxUsers       { get; set; }
            public int     MaxLeads       { get; set; }
            public int     MaxDeals       { get; set; }
            public decimal StorageLimitGB { get; set; }
        }

        // ==================== GET ====================
        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                Tenant = await _tenantService.GetByIdAsync(Id);
                await LoadPlanLimitsAsync(Tenant.Plan);

                var settings = await _tenantService.GetSettingsAsync(Id);
                Input = new InputModel
                {
                    MaxUsers       = settings.MaxUsers,
                    MaxLeads       = settings.MaxLeads,
                    MaxDeals       = settings.MaxDeals,
                    StorageLimitGB = settings.StorageLimit / 1_073_741_824m,
                    FeatureFlags   = settings.FeatureFlags
                };

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading settings for tenant {TenantId}", Id);
                TempData["ErrorMessage"] = "Failed to load tenant settings.";
                return RedirectToPage("./Detail", new { id = Id });
            }
        }

        // ==================== POST ====================
        public async Task<IActionResult> OnPostAsync()
        {
            Tenant = await _tenantService.GetByIdAsync(Id);
            await LoadPlanLimitsAsync(Tenant.Plan);

            // Validate against plan limits (skip if Super Admin bypass is checked)
            if (!BypassPlanLimits)
            {
                var errors = new List<string>();

                if (Input.MaxUsers > Limits.MaxUsers)
                    errors.Add($"Max users ({Input.MaxUsers}) exceeds '{Limits.DisplayName}' plan limit ({Limits.MaxUsers})");

                if (Input.MaxLeads > Limits.MaxLeads)
                    errors.Add($"Max leads ({Input.MaxLeads}) exceeds '{Limits.DisplayName}' plan limit ({Limits.MaxLeads})");

                if (Input.MaxDeals > Limits.MaxDeals)
                    errors.Add($"Max deals ({Input.MaxDeals}) exceeds '{Limits.DisplayName}' plan limit ({Limits.MaxDeals})");

                if (Input.StorageLimitGB > Limits.StorageLimitGB)
                    errors.Add($"Storage ({Input.StorageLimitGB:F1} GB) exceeds '{Limits.DisplayName}' plan limit ({Limits.StorageLimitGB:F1} GB)");

                foreach (var error in errors)
                    ModelState.AddModelError(string.Empty, error);
            }

            if (!ModelState.IsValid)
                return Page();

            try
            {
                var command = new UpdateTenantSettingsCommand(
                    TenantId:     Id,
                    MaxUsers:     Input.MaxUsers,
                    MaxLeads:     Input.MaxLeads,
                    MaxDeals:     Input.MaxDeals,
                    StorageLimit: (long)(Input.StorageLimitGB * 1_073_741_824),
                    FeatureFlags: Input.FeatureFlags
                );

                await _tenantService.UpdateSettingsAsync(command);

                TempData["SuccessMessage"] = BypassPlanLimits
                    ? "Settings saved with Super Admin override."
                    : "Tenant settings updated successfully!";

                return RedirectToPage("./Detail", new { id = Id });
            }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating settings for tenant {TenantId}", Id);
                ModelState.AddModelError(string.Empty, "Failed to update settings. Please try again.");
                return Page();
            }
        }

        // ==================== LOAD PLAN LIMITS FROM DB ====================
        // Replaces the old hardcoded GetPlanLimits() switch statement entirely.
        private async Task LoadPlanLimitsAsync(string? planName)
        {
            try
            {
                var plan = await _getPlan.Handle(planName ?? "starter");
                Limits = new PlanLimits
                {
                    DisplayName    = plan.DisplayName,
                    MaxUsers       = plan.MaxUsers,
                    MaxLeads       = plan.MaxLeads,
                    MaxDeals       = plan.MaxDeals,
                    StorageLimitGB = plan.StorageLimitBytes / 1_073_741_824m
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load plan limits for plan '{Plan}'", planName);
                // Safe fallback — page still renders, limits show as 0
                Limits = new PlanLimits
                {
                    DisplayName    = planName ?? "Unknown",
                    MaxUsers       = 0,
                    MaxLeads       = 0,
                    MaxDeals       = 0,
                    StorageLimitGB = 0
                };
            }
        }
    }
}
