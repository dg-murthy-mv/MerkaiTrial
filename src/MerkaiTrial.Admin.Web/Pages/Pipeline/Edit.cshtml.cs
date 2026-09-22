// =====================================================================
// EDIT DEAL PAGE MODEL
// Location: MerkaiTrial.Admin.Web/Pages/Pipeline/Edit.cshtml.cs
//
// COMPLETE FILE — replaces the 019 version.
//
// CHANGES (020 — Blueprint transitions)
//   ✅ THE STAGE SELECTOR IS GONE. A deal no longer moves by picking a
//      destination from a list; it moves by pressing a named button on
//      the deal page — "Send Quote", "Mark as Lost" — which is what makes
//      a process a process rather than a suggestion.
//
//      This page now edits the deal's DETAILS: its title, value, currency,
//      close date, owner, tags. It posts the deal's CURRENT stage back
//      unchanged, so the API sees no stage change and the guard is never
//      consulted.
//
//   ✅ The lost-reason and reopen-reason boxes go with it. Those questions
//      belong to a move, and a move now happens elsewhere.
//
//   ✅ The stage is still SHOWN, read-only, with a link back to the deal
//      page. Hiding it entirely would leave a rep editing a deal with no
//      idea where it sits.
//
// WHAT 019 FIXED AND THIS KEEPS
//   • Stage names come from the tenant's pipeline. The page used to have
//     six stage buttons hard-coded in the markup and a matching
//     GetStageProbability switch here, so a tenant who renamed a stage
//     could not save this page at all.
//   • A refused save keeps what the rep typed. The old page called
//     OnGetAsync on failure, which overwrote Input from the database and
//     silently threw away every edit.
//
// EARLIER FIXES (kept)
//   • Currency = deal.Currency (was hardcoded "USD")
//   • ICurrentTenantService injected for consistency
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Contacts;
using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Admin.Web.Services.Deals;
using MerkaiTrial.Admin.Web.Services.Pipeline;
using MerkaiTrial.Admin.Web.Services.Users;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.PipelineStages;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
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
        private readonly IPipelineStageService _stageService;
        private readonly ILogger<EditModel> _logger;

        protected override string ModuleName => Modules.Deals;

        public EditModel(
            IDealService dealService,
            IContactService contactService,
            IUserService userService,
            ICountryService countryService,
            ICurrentUserService currentUserService,
            ICurrentTenantService currentTenantService,
            IPipelineStageService stageService,
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
            _stageService = stageService;
            _logger = logger;
        }

        // ==================== PAGE PROPERTIES ====================

        [BindProperty]
        public DealInputModel Input { get; set; } = new();

        public List<SelectListItem> SalesTeam { get; set; } = new();
        public List<CountryListItem> Countries { get; set; } = new();

        /// <summary>
        /// Loaded only to resolve the deal's current stage to its name for
        /// the read-only line. The page no longer offers a choice.
        /// </summary>
        public List<PipelineStageDto> Stages { get; set; } = new();

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

            /// <summary>
            /// The deal's CURRENT stage, carried through a hidden field and
            /// posted back unchanged. The API needs a stage on the DTO; by
            /// sending the one the deal already has, the handler sees no
            /// change and the guard is never consulted.
            ///
            /// This is not a choice. Moving a deal is the transition bar's
            /// job on the deal page.
            /// </summary>
            public string Stage { get; set; } = string.Empty;

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

        // ==================== VIEW HELPERS ====================

        public PipelineStageDto? StageByKey(string? key) =>
            string.IsNullOrEmpty(key) ? null : Stages.FirstOrDefault(s => s.Key == key);

        public string StageName(string? key) => StageByKey(key)?.Name ?? key ?? "—";

        public StageCategory CategoryOf(string? key) =>
            StageByKey(key)?.Category ?? StageCategory.Open;

        public bool IsClosed =>
            CategoryOf(Input.Stage) is StageCategory.Won or StageCategory.Lost;

        public string StageBadgeClass(string? key) => CategoryOf(key) switch
        {
            StageCategory.Won => "bg-success",
            StageCategory.Lost => "bg-danger",
            _ => "bg-secondary"
        };

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
                // activeOnly: false — a deal can sit in a retired stage, and
                // the page has to be able to name where it currently is.
                var stagesTask    = _stageService.GetAsync(activeOnly: false);

                await Task.WhenAll(dealTask, salesTeamTask, countriesTask, stagesTask);

                var deal      = await dealTask;
                var salesTeam = await salesTeamTask;
                Countries     = await countriesTask;
                Stages        = await stagesTask;

                try
                {
                    var contact = await _contactService.GetByIdAsync(tenantId, deal.ContactId);
                    ContactName = $"{contact.FirstName} {contact.LastName}".Trim();
                }
                catch
                {
                    ContactName = "Unknown Contact";
                }

                Input = new DealInputModel
                {
                    Title             = deal.Title,
                    Description       = deal.Description,
                    ExpectedValue     = deal.ExpectedValue,
                    Currency          = deal.Currency ?? _currentTenantService.GetCurrencyCode(),
                    Stage             = deal.Stage,          // carried, not chosen
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
                await ReloadListsAsync(id);
                return Page();
            }

            try
            {
                var tenantId    = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                // The stage posted back is the one the deal already has, so
                // UpdateDealHandler's `deal.Stage != dto.Stage` test is false
                // and no transition is evaluated. Belt and braces: if a stale
                // form somehow carried a different stage, the guard would
                // still apply every rule to it rather than letting this page
                // move a deal by the back door.
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
            // IApiService turns the API's 400/403 { "error": ... } into this,
            // carrying the server's own wording.
            catch (InvalidOperationException ex)
            {
                _logger.LogInformation("Deal {DealId} update refused: {Message}", id, ex.Message);

                ModelState.AddModelError(string.Empty, ex.Message);
                ErrorMessage = ex.Message;

                await ReloadListsAsync(id);
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update deal {DealId}", id);
                ErrorMessage = "Failed to update deal. Please try again.";
                await ReloadListsAsync(id);
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

        // ==================== HELPERS ====================

        /// <summary>
        /// Re-fills the dropdowns after a failed post.
        ///
        /// Deliberately NOT a call to OnGetAsync: that overwrote Input with
        /// the values from the database, so a rep whose save was refused
        /// lost every edit they had just made and had to type them again.
        /// </summary>
        private async Task ReloadListsAsync(Guid id)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();

                var salesTeamTask = _userService.GetSalesTeamAsync(tenantId);
                var countriesTask = _countryService.GetActiveAsync();
                var stagesTask    = _stageService.GetAsync(activeOnly: false);

                await Task.WhenAll(salesTeamTask, countriesTask, stagesTask);

                Countries = await countriesTask;
                Stages    = await stagesTask;

                SalesTeam = (await salesTeamTask).Select(u => new SelectListItem
                {
                    Value    = u.Id.ToString(),
                    Text     = u.FullName,
                    Selected = u.Id.ToString() == Input.OwnerUserId
                }).ToList();

                try
                {
                    var deal = await _dealService.GetByIdAsync(tenantId, id);
                    CreatedAt = deal.CreatedAtUtc.ToLocalTime();
                    CreatedBy = deal.CreatedBy;

                    var contact = await _contactService.GetByIdAsync(tenantId, deal.ContactId);
                    ContactName = $"{contact.FirstName} {contact.LastName}".Trim();
                }
                catch
                {
                    ContactName = "Unknown Contact";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload lists for deal {DealId}", id);
            }
        }

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

        // GetStageProbability(string) was deleted in 019. It hard-coded the
        // six original stages and their percentages, so a tenant who set
        // Negotiation to 75% still had this page snap the slider to 60.
    }
}
