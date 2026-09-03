// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Pipeline/Create.cshtml.cs
//
// CHANGES vs previous version:
//   ✅ ContactDropdownItem replaces SelectListItem for Contacts
//      — carries CompanyId + CompanyName for JS auto-fill
//   ✅ ContactItems property replaces Contacts (List<SelectListItem>)
//   ✅ All other functionality unchanged
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Companies;
using MerkaiTrial.Admin.Web.Services.Contacts;
using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Admin.Web.Services.Deals;
using MerkaiTrial.Admin.Web.Services.Meta;
using MerkaiTrial.Admin.Web.Services.Users;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Pipeline
{
    // ── Rich contact item — carries company data for JS auto-fill ─────
    public class ContactDropdownItem
    {
        public string  Id          { get; set; } = string.Empty;
        public string  Text        { get; set; } = string.Empty;   // "Full Name (email)"
        public string? CompanyId   { get; set; }                   // Guid string or null
        public string? CompanyName { get; set; }
    }

    public class CreateModel : AuthorizedPageModel
    {
        private readonly IDealService             _dealService;
        private readonly IContactService          _contactService;
        private readonly IUserService             _userService;
        private readonly ICountryService          _countryService;
        private readonly ICompanyService          _companyService;
        private readonly ICurrentUserService      _currentUserService;
        private readonly ICurrentTenantService    _currentTenantService;
        private readonly IMetaService             _metaService;
        private readonly ILogger<CreateModel>     _logger;

        protected override string ModuleName => Modules.Deals;

        public CreateModel(
            IDealService          dealService,
            IContactService       contactService,
            IUserService          userService,
            ICountryService       countryService,
            ICompanyService       companyService,
            ICurrentUserService   currentUserService,
            ICurrentTenantService currentTenantService,
            IMetaService          metaService,
            IAuthorizationService authorizationService,
            ILogger<CreateModel>  logger)
            : base(authorizationService, currentUserService, logger)
        {
            _dealService          = dealService;
            _contactService       = contactService;
            _userService          = userService;
            _countryService       = countryService;
            _companyService       = companyService;
            _currentUserService   = currentUserService;
            _currentTenantService = currentTenantService;
            _metaService          = metaService;
            _logger               = logger;
        }

        // ── Page Properties ───────────────────────────────────────────

        [BindProperty]
        public DealInputModel Input { get; set; } = new();

        // ✅ Rich contact list — carries CompanyId/Name for auto-fill
        public List<ContactDropdownItem> ContactItems { get; set; } = new();

        public List<SelectListItem> SalesTeam       { get; set; } = new();
        public List<SelectListItem> Companies        { get; set; } = new();
        public List<SelectListItem> Sources          { get; set; } = new();
        public List<SelectListItem> VerticalOptions  { get; set; } = new();

        // ✅ Multi-tenant: exposed for Razor — no hardcoding
        public string TenantCurrencyCode   { get; set; } = string.Empty;
        public string TenantCurrencySymbol { get; set; } = string.Empty;
        public string TenantTaxLabel       { get; set; } = "Tax";

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage   { get; set; }

        // ── Input Model ───────────────────────────────────────────────
        public class DealInputModel
        {
            [Required(ErrorMessage = "Deal title is required")]
            [StringLength(200)]
            public string Title { get; set; } = string.Empty;

            [StringLength(2000)]
            public string? Description { get; set; }

            [Required(ErrorMessage = "Please select a contact")]
            public Guid ContactId { get; set; }

            public Guid? CompanyId { get; set; }

            [Required(ErrorMessage = "Expected value is required")]
            [Range(0.01, 999999999)]
            public decimal ExpectedValue { get; set; }

            // ✅ Currency set from tenant on GET — never hardcoded
            [Required]
            [StringLength(3)]
            public string Currency { get; set; } = string.Empty;

            // ✅ Default = Discovery — matches CK_Deals_Stage constraint
            [Required]
            public string Stage { get; set; } = DealStages.Discovery;

            [Range(0, 100)]
            public int? Probability { get; set; } = 20;

            [Required(ErrorMessage = "Expected close date is required")]
            public DateTime ExpectedCloseDate { get; set; } = DateTime.Today.AddDays(30);

            public string? OwnerUserId { get; set; }
            public string? Tags        { get; set; }
            public Guid?   SourceId    { get; set; }
            public Guid?   VerticalId  { get; set; }

            // ✅ LeadId — set when converting from Lead page
            public Guid? LeadId { get; set; }
        }

        // ── GET ───────────────────────────────────────────────────────
        public async Task<IActionResult> OnGetAsync(Guid? leadId = null)
        {
            var check = await ValidatePermissionAsync(Actions.Create);
            if (check != null) return check;

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();

                // ✅ Tenant context first
                TenantCurrencyCode   = _currentTenantService.GetCurrencyCode();
                TenantCurrencySymbol = _currentTenantService.GetCurrencySymbol();
                TenantTaxLabel       = _currentTenantService.GetTaxLabel();
                Input.Currency       = TenantCurrencyCode;

                if (leadId.HasValue)
                    Input.LeadId = leadId;

                // Load all dropdowns in parallel
                var contactsTask  = _contactService.GetAllAsync(tenantId);
                var companiesTask = _companyService.GetLookupAsync(tenantId);
                var sourcesTask   = _dealService.GetSourcesAsync(tenantId);
                var usersTask     = _userService.GetLookupAsync(tenantId);

                await Task.WhenAll(contactsTask, companiesTask, sourcesTask, usersTask);

                var contacts  = await contactsTask;
                var companies = await companiesTask;
                var sources   = await sourcesTask;
                var users     = await usersTask;

                // ✅ Build company lookup for enriching contact items
                var companyLookup = companies.ToDictionary(
                    c => c.Id.ToString(),
                    c => c.Name);

                // ✅ Rich ContactDropdownItem — CompanyId + CompanyName for JS auto-fill
                ContactItems = contacts.Items.Select(c => new ContactDropdownItem
                {
                    Id          = c.Id.ToString(),
                    Text        = $"{c.FullName} ({c.Email})",
                    CompanyId   = c.CompanyId.HasValue ? c.CompanyId.Value.ToString() : null,
                    CompanyName = c.CompanyId.HasValue && companyLookup.TryGetValue(
                                      c.CompanyId.Value.ToString(), out var name)
                                  ? name : null
                }).ToList();

                Companies = companies.Select(c => new SelectListItem
                {
                    Value    = c.Id.ToString(),
                    Text     = c.Name,
                    Selected = Input.CompanyId.HasValue && c.Id == Input.CompanyId.Value
                }).ToList();

                Sources = sources.Select(s => new SelectListItem
                {
                    Value = s.Value,
                    Text  = s.Label
                }).ToList();

                SalesTeam = users.Select(u => new SelectListItem
                {
                    Value = u.Id.ToString(),
                    Text  = u.FullName
                }).ToList();

                try
                {
                    var verticals = await _metaService.GetVerticalsAsync();
                    VerticalOptions = verticals.Select(v => new SelectListItem
                    {
                        Value = v.Id.ToString(),
                        Text  = v.Name
                    }).ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load verticals — vertical dropdown will be empty");
                }

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load create deal page");
                ErrorMessage = "Failed to load form. Please try again.";
                return RedirectToPage("/Pipeline/Index");
            }
        }

        // ── POST ──────────────────────────────────────────────────────
        public async Task<IActionResult> OnPostAsync()
        {
            var check = await ValidatePermissionAsync(Actions.Create);
            if (check != null) return check;

            if (!ModelState.IsValid)
            {
                await OnGetAsync();
                return Page();
            }

            try
            {
                var tenantId    = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                if (!DealStages.IsValid(Input.Stage))
                    Input.Stage = DealStages.Discovery;

                var createDto = new CreateDealDto
                {
                    TenantId             = tenantId.ToString(),
                    ContactId            = Input.ContactId,
                    LeadId               = Input.LeadId,
                    CompanyId            = Input.CompanyId,
                    Title                = Input.Title,
                    Description          = Input.Description,
                    Stage                = Input.Stage,
                    ExpectedValue        = Input.ExpectedValue,
                    Currency             = Input.Currency,
                    Probability          = Input.Probability,
                    ExpectedCloseDateUtc = Input.ExpectedCloseDate.ToUniversalTime(),
                    OwnerUserId          = string.IsNullOrEmpty(Input.OwnerUserId)
                                            ? currentUser.UserId.ToString()
                                            : Input.OwnerUserId,
                    SourceId             = Input.SourceId,
                    VerticalId           = Input.VerticalId,
                    Tags                 = Input.Tags,
                    CreatedBy            = currentUser.FullName
                };

                var createdDeal = await _dealService.CreateAsync(createDto);

                SuccessMessage = $"Deal '{Input.Title}' created successfully!";
                return RedirectToPage("/Pipeline/Detail", new { id = createdDeal.Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create deal");
                ErrorMessage = "Failed to create deal. Please try again.";
                await OnGetAsync();
                return Page();
            }
        }

        public int GetStageProbability(string stage) =>
            DealStages.DefaultProbability(stage);
    }
}
