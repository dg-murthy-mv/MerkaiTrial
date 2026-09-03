// =====================================================================
// CONVERT TO DEAL - Backend
// Location: MerkaiTrial.Admin.Web/Pages/Leads/ConvertToDeal.cshtml.cs
//
// MIGRATION (this pass):
//   1. Base class bare PageModel -> AuthorizedPageModel
//      This page had ZERO permission checks of any kind before this pass —
//      not even the hand-rolled AppPageModel.CanUser style. This is the
//      page _RightSidebar.cshtml's "Convert to Deal" button actually links
//      to (asp-page="/Leads/ConvertToDeal"), so it is the real, reachable
//      conversion path — not the OnPostConvertToDealAsync handler inside
//      Detail.cshtml.cs, which nothing currently appears to submit to.
//   2. OnGetAsync and OnPostAsync now both gated behind Leads.Update AND
//      Deals.Create (cross-module — converting both updates the lead and
//      creates a new Deal record), same mapping used for Detail's inline
//      handler last pass.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Leads;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Leads
{
    public class ConvertToDealModel : AuthorizedPageModel
    {
        private readonly ILeadService _leadService;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly ILogger<ConvertToDealModel> _logger;

        protected override string ModuleName => Modules.Leads;

        public ConvertToDealModel(
            ILeadService leadService,
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            ILogger<ConvertToDealModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _leadService = leadService;
            _currentUserService = currentUserService;
            _tenantService = tenantService;
            _logger = logger;
        }

        [BindProperty]
        public ConvertInputModel Input { get; set; } = new();

        // Lead display data
        public Guid LeadId { get; set; }
        public string LeadName { get; set; } = string.Empty;
        public string? LeadEmail { get; set; }
        public string? LeadPhone { get; set; }
        public Guid? ContactId { get; set; }
        public decimal EstimatedValue { get; set; }

        public string TenantCurrencySymbol { get; private set; } = "₹";
        public string TenantCurrency { get; private set; } = "INR";

        public class ConvertInputModel
        {
            [Required(ErrorMessage = "Deal title is required")]
            [StringLength(200)]
            public string DealTitle { get; set; } = string.Empty;

            public string? Description { get; set; }

            [Required(ErrorMessage = "Stage is required")]
            public string Stage { get; set; } = "Qualification";

            [Required(ErrorMessage = "Expected value is required")]
            [Range(0.01, double.MaxValue)]
            public decimal ExpectedValue { get; set; }

            public string Currency { get; set; } = string.Empty;

            [Required(ErrorMessage = "Expected close date is required")]
            [DataType(DataType.Date)]
            public DateTime ExpectedCloseDate { get; set; } = DateTime.Today.AddDays(30);
        }

        /// <summary>
        /// Cross-module check: converting a lead both updates the Lead
        /// record and creates a new Deal, so both permissions are required.
        /// Checked at the top of both handlers below.
        /// </summary>
        private async Task<IActionResult?> ValidateConvertPermissionAsync()
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            if (!UserCanCreate(Modules.Deals))
            {
                Logger.LogWarning("❌ Access denied (missing permission: {Module}.{Action}) for user {Email} (ID: {UserId})",
                    Modules.Deals, Actions.Create,
                    User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
                    User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value);
                return RedirectToPage("/AccessDenied");
            }

            return null;
        }

        public async Task<IActionResult> OnGetAsync(Guid id)
        {
            var permissionCheck = await ValidateConvertPermissionAsync();
            if (permissionCheck != null) return permissionCheck;

            try
            {
                LoadTenantContext();

                var tenantId = _currentUserService.GetCurrentTenantId();
                var lead = await _leadService.GetByIdAsync(tenantId, id);

                if (lead == null)
                {
                    TempData["ErrorMessage"] = "Lead not found.";
                    return RedirectToPage("/Leads/Index");
                }

                if (lead.Status != "Qualified")
                {
                    TempData["ErrorMessage"] = "Only qualified leads can be converted to deals.";
                    return RedirectToPage("/Leads/Detail", new { id });
                }

                if (lead.DealId.HasValue)
                {
                    TempData["ErrorMessage"] = "This lead has already been converted to a deal.";
                    return RedirectToPage("/Leads/Detail", new { id });
                }

                LeadId = lead.Id;
                LeadName = lead.FullName;
                LeadEmail = lead.Email;
                LeadPhone = lead.Phone;
                ContactId = lead.ContactId;
                EstimatedValue = lead.ExpectedValue;

                var resolvedCurrency = !string.IsNullOrEmpty(lead.Currency)
                    ? lead.Currency
                    : TenantCurrency;

                Input = new ConvertInputModel
                {
                    DealTitle = $"Deal - {lead.FullName}",
                    Stage = "Qualification",
                    ExpectedValue = lead.ExpectedValue,
                    Currency = resolvedCurrency,
                    ExpectedCloseDate = DateTime.Today.AddDays(30)
                };

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load convert page for lead {LeadId}", id);
                TempData["ErrorMessage"] = "Failed to load conversion page. Please try again.";
                return RedirectToPage("/Leads/Index");
            }
        }

        public async Task<IActionResult> OnPostAsync(Guid id)
        {
            var permissionCheck = await ValidateConvertPermissionAsync();
            if (permissionCheck != null) return permissionCheck;

            try
            {
                LoadTenantContext();

                if (!ModelState.IsValid)
                {
                    await LoadLeadDataForDisplay(id);
                    return Page();
                }

                var tenantId = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();
                var lead = await _leadService.GetByIdAsync(tenantId, id);

                if (lead == null)
                {
                    TempData["ErrorMessage"] = "Lead not found.";
                    return RedirectToPage("/Leads/Index");
                }

                if (lead.Status != "Qualified")
                {
                    TempData["ErrorMessage"] = "Only qualified leads can be converted to deals.";
                    return RedirectToPage("/Leads/Detail", new { id });
                }

                var result = await _leadService.ConvertToDealAsync(new ConvertLeadToDealDto
                {
                    TenantId = tenantId,
                    LeadId = id,
                    DealTitle = Input.DealTitle,
                    Description = Input.Description,
                    Stage = Input.Stage,
                    ExpectedValue = Input.ExpectedValue,
                    Currency = Input.Currency,
                    ExpectedCloseDateUtc = Input.ExpectedCloseDate.ToUniversalTime(),
                    OwnerUserId = lead.OwnerUserId,
                    ConvertedBy = currentUser.FullName
                });

                TempData["SuccessMessage"] =
                    $"{result.Message} Deal '{Input.DealTitle}' has been created and added to the pipeline.";

                return RedirectToPage("/Pipeline/Index");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Conversion validation failed for lead {LeadId}", id);
                TempData["ErrorMessage"] = ex.Message;
                await LoadLeadDataForDisplay(id);
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to convert lead {LeadId} to deal", id);
                TempData["ErrorMessage"] = "Failed to convert lead to deal. Please try again.";
                await LoadLeadDataForDisplay(id);
                return Page();
            }
        }

        private void LoadTenantContext()
        {
            TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
            TenantCurrency = _tenantService.GetCurrencyCode();
        }

        private async Task LoadLeadDataForDisplay(Guid id)
        {
            try
            {
                LoadTenantContext();
                var tenantId = _currentUserService.GetCurrentTenantId();
                var lead = await _leadService.GetByIdAsync(tenantId, id);
                if (lead == null) return;

                LeadId = lead.Id;
                LeadName = lead.FullName;
                LeadEmail = lead.Email;
                LeadPhone = lead.Phone;
                ContactId = lead.ContactId;
                EstimatedValue = lead.ExpectedValue;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload lead data for display");
            }
        }
    }
}
