// =====================================================================
// EDIT DEAL PAGE MODEL - FIXED
// Location: MerkaiTrial.Admin.Web/Pages/Pipeline/Edit.cshtml.cs
// Fixes:
//   - Currency = deal.Currency (was hardcoded "USD")
//   - ICurrentTenantService injected for consistency
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Contacts;
using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Admin.Web.Services.Deals;
using MerkaiTrial.Admin.Web.Services.Users;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Pipeline
{
    public class EditModel : AuthorizedPageModel
    {
        private readonly IDealService _dealService;
        private readonly IContactService _contactService;
        private readonly IUserService _userService;
        private readonly ICountryService _countryService;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _currentTenantService;
        private readonly ILogger<EditModel> _logger;

        protected override string ModuleName => Modules.Deals;

        public EditModel(
            IDealService dealService,
            IContactService contactService,
            IUserService userService,
            ICountryService countryService,
            ICurrentUserService currentUserService,
            ICurrentTenantService currentTenantService,
            IAuthorizationService authorizationService,
            ILogger<EditModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _dealService = dealService;
            _contactService = contactService;
            _userService = userService;
            _countryService = countryService;
            _currentUserService = currentUserService;
            _currentTenantService = currentTenantService;
            _logger = logger;
        }

        // ==================== PAGE PROPERTIES ====================

        [BindProperty]
        public DealInputModel Input { get; set; } = new();

        public List<SelectListItem> SalesTeam { get; set; } = new();
        public List<CountryListItem> Countries { get; set; } = new();

        public Guid DealId { get; set; }
        public string ContactName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }

        [TempData]
        public string? SuccessMessage { get; set; }

        [TempData]
        public string? ErrorMessage { get; set; }

        // ==================== INPUT MODEL ====================

        public class DealInputModel
        {
            [Required(ErrorMessage = "Deal title is required")]
            [StringLength(200, ErrorMessage = "Title cannot exceed 200 characters")]
            [Display(Name = "Deal Title")]
            public string Title { get; set; } = string.Empty;

            [Display(Name = "Description")]
            [StringLength(2000, ErrorMessage = "Description cannot exceed 2000 characters")]
            public string? Description { get; set; }

            [Required(ErrorMessage = "Expected value is required")]
            [Range(0.01, 999999999, ErrorMessage = "Expected value must be greater than 0")]
            [Display(Name = "Expected Value")]
            public decimal ExpectedValue { get; set; }

            [Required(ErrorMessage = "Please select a currency")]
            [Display(Name = "Currency")]
            public string Currency { get; set; } = "INR";

            [Required(ErrorMessage = "Please select a stage")]
            [Display(Name = "Stage")]
            public string Stage { get; set; } = "Discovery";

            [Range(0, 100, ErrorMessage = "Probability must be between 0 and 100")]
            [Display(Name = "Probability (%)")]
            public int Probability { get; set; } = 20;

            [Required(ErrorMessage = "Expected close date is required")]
            [Display(Name = "Expected Close Date")]
            public DateTime ExpectedCloseDate { get; set; }

            [Display(Name = "Owner")]
            public string? OwnerUserId { get; set; }

            public Guid? SourceId { get; set; }
            public string? Tags { get; set; }
        }

        // ==================== GET HANDLER ====================

        public async Task<IActionResult> OnGetAsync(Guid id)
        {
            var check = await ValidatePermissionAsync(Actions.Update);
            if (check != null) return check;

            await InitializePermissionsAsync();

            try
            {
                DealId = id;
                var tenantId = _currentUserService.GetCurrentTenantId();

                var dealTask      = _dealService.GetByIdAsync(tenantId, id);
                var salesTeamTask = _userService.GetSalesTeamAsync(tenantId);
                var countriesTask = _countryService.GetActiveAsync();

                await Task.WhenAll(dealTask, salesTeamTask, countriesTask);

                var deal      = await dealTask;
                var salesTeam = await salesTeamTask;
                Countries     = await countriesTask;

                try
                {
                    var contact = await _contactService.GetByIdAsync(tenantId, deal.ContactId);
                    ContactName = $"{contact.FirstName} {contact.LastName}".Trim();
                }
                catch
                {
                    ContactName = "Unknown Contact";
                }

                // ✅ FIXED: Currency = deal.Currency (was hardcoded "USD")
                Input = new DealInputModel
                {
                    Title             = deal.Title,
                    Description       = deal.Description,
                    ExpectedValue     = deal.ExpectedValue,
                    Currency          = deal.Currency ?? _currentTenantService.GetCurrencyCode(),
                    Stage             = deal.Stage,
                    Probability       = deal.Probability,
                    ExpectedCloseDate = deal.ExpectedCloseDateUtc.ToLocalTime(),
                    OwnerUserId       = deal.OwnerUserId,
                    SourceId          = deal.SourceId,
                    Tags              = deal.Tags
                };

                CreatedAt  = deal.CreatedAtUtc.ToLocalTime();
                CreatedBy  = deal.CreatedBy;

                SalesTeam = salesTeam.Select(u => new SelectListItem
                {
                    Value    = u.Id.ToString(),
                    Text     = u.FullName,
                    Selected = u.Id.ToString() == deal.OwnerUserId
                }).ToList();

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load edit deal page for {DealId}", id);
                ErrorMessage = "Failed to load deal. Please try again.";
                return RedirectToPage("/Pipeline/Index");
            }
        }

        // ==================== POST HANDLER ====================

        public async Task<IActionResult> OnPostAsync(Guid id)
        {
            DealId = id;

            var check = await ValidatePermissionAsync(Actions.Update);
            if (check != null) return check;

            if (!ModelState.IsValid)
            {
                await OnGetAsync(id);
                return Page();
            }

            try
            {
                var tenantId    = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                var updateDto = new UpdateDealDto
                {
                    Title                = Input.Title,
                    Description          = Input.Description,
                    Stage                = Input.Stage,
                    ExpectedValue        = Input.ExpectedValue,
                    Currency             = Input.Currency,
                    Probability          = Input.Probability,
                    ExpectedCloseDateUtc = Input.ExpectedCloseDate.ToUniversalTime(),
                    OwnerUserId          = Input.OwnerUserId,
                    SourceId             = Input.SourceId,
                    Tags                 = Input.Tags,
                    UpdatedBy            = currentUser.FullName
                };

                await _dealService.UpdateAsync(tenantId, id, updateDto);

                SuccessMessage = $"Deal '{Input.Title}' updated successfully!";
                return RedirectToPage("/Pipeline/Detail", new { id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update deal {DealId}", id);
                ErrorMessage = "Failed to update deal. Please try again.";
                await OnGetAsync(id);
                return Page();
            }
        }

        // ==================== DELETE HANDLER ====================

        public async Task<IActionResult> OnPostDeleteAsync(Guid id)
        {
            try
            {
                var check = await ValidatePermissionAsync(Actions.Delete);
                if (check != null) return check;

                var tenantId = _currentUserService.GetCurrentTenantId();
                await _dealService.DeleteAsync(tenantId, id);

                SuccessMessage = "Deal deleted successfully!";
                return RedirectToPage("/Pipeline/Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete deal {DealId}", id);
                ErrorMessage = "Failed to delete deal. Please try again.";
                return RedirectToPage(new { id });
            }
        }

        // ==================== HELPER METHODS ====================

        public string GetCurrencySymbol(string currencyCode)
        {
            return currencyCode switch
            {
                "INR" => "₹",
                "USD" => "$",
                "EUR" => "€",
                "GBP" => "£",
                "THB" => "฿",
                "PHP" => "₱",
                "KES" => "KES",
                "IDR" => "Rp",
                "TWD" => "NT$",
                "COP" => "COL$",
                "SAR" => "﷼",
                "ARS" => "AR$",
                "DKK" => "kr",
                "AED" => "د.إ",
                "ZAR" => "R",
                _ => currencyCode
            };
        }

        public int GetStageProbability(string stage) => stage switch
        {
            "Discovery"     => 20,
            "Qualification" => 30,
            "Proposal"      => 40,
            "Negotiation"   => 60,
            "ClosedWon"     => 100,
            "ClosedLost"    => 0,
            _               => 20
        };


    }
}
