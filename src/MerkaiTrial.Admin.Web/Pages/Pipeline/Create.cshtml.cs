// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Pipeline/Create.cshtml.cs
//
// COMPLETE FILE — replaces the existing one.
//
// CHANGES (030)
//
//   1. THE CLOSE DATE WAS STORED A DAY EARLY. The old line was
//
//          ExpectedCloseDateUtc = Input.ExpectedCloseDate.ToUniversalTime(),
//
//      and carried a comment admitting it: "ToUniversalTime() converts
//      using the SERVER's timezone... left as-is here to keep this change
//      to stages only." It is not a no-op on an Indian or Thai server: an
//      <input type="date"> posts midnight with Kind=Unspecified, and
//      ToUniversalTime() treats Unspecified as LOCAL, so on IST
//      (UTC+5:30) 2026-10-25 00:00 is stored as 2026-10-24 18:30 UTC.
//      Every new deal's close date lands a day before the one that was
//      typed. This is the same bug the Quotes pages were fixed for, and
//      the fix is the same: a calendar date is stored as midnight UTC with
//      SpecifyKind, with no conversion in either direction.
//
//   2. OnGetAsync NEVER CALLED InitializePermissionsAsync(). Every other
//      page does. Without it CanCreate / CanUpdate / CanRead / CanDelete
//      are all false for the whole render, so any permission gate in the
//      view silently hides what it guards.
//
//   3. THE LINE VALIDATION ONLY CHECKED THE MODEL ATTRIBUTES. A close date
//      before today now warns in the view; the value and stage are still
//      validated by the API, which is the authority.
//
//   See Create.cshtml for the browser-side half: its script used to
//   overwrite the tenant's starting stage with the literal "Discovery" and
//   the probability with 20 on every page load, undoing the work this file
//   does below to resolve them properly.
//
// WHAT CHANGED EARLIER
//   The stage dropdown, its default and its probability all come from the
//   tenant's PipelineStages now. Previously a tenant could add "Site
//   Visit" in Settings and then find it nowhere on the form that creates
//   deals — which made the settings page a promise the product did not
//   keep.
//
//   GetStageProbability also read the hardcoded table, so the JS that
//   auto-fills probability on stage change was showing figures the tenant
//   had not chosen.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Companies;
using MerkaiTrial.Admin.Web.Services.Contacts;
using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Admin.Web.Services.Deals;
using MerkaiTrial.Admin.Web.Services.Meta;
using MerkaiTrial.Admin.Web.Services.Pipeline;
using MerkaiTrial.Admin.Web.Services.Users;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.PipelineStages;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Domain.Entities;
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
        private readonly IPipelineStageService    _stageService;
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
            IPipelineStageService stageService,
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
            _stageService         = stageService;
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

        /// <summary>
        /// The tenant's own stages, active only — a retired stage should
        /// not be offered for brand new work.
        /// </summary>
        public List<PipelineStageDto> Stages { get; set; } = new();

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

            /// <summary>
            /// Empty by default, filled from the tenant's starting stage on
            /// GET. A hardcoded default here would override the tenant's own
            /// choice of where deals begin.
            /// </summary>
            [Required(ErrorMessage = "Please choose a stage")]
            public string Stage { get; set; } = string.Empty;

            [Range(0, 100)]
            public int? Probability { get; set; }

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

            // ✅ (030) Was missing. Without it every Can* flag on the base
            // class stays false for the whole render.
            await InitializePermissionsAsync();

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
                var stagesTask    = _stageService.GetAsync(activeOnly: true);

                await Task.WhenAll(contactsTask, companiesTask, sourcesTask, usersTask, stagesTask);

                var contacts  = await contactsTask;
                var companies = await companiesTask;
                var sources   = await sourcesTask;
                var users     = await usersTask;
                Stages        = await stagesTask;

                // Start where the tenant says deals start, unless the user
                // has already chosen (a validation round-trip keeps theirs).
                if (string.IsNullOrEmpty(Input.Stage))
                {
                    var start = Stages.FirstOrDefault(s => s.IsDefault) ?? Stages.FirstOrDefault();
                    if (start is not null)
                    {
                        Input.Stage = start.Key;
                        Input.Probability ??= start.Probability;
                    }
                }

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

                // The stage is validated server-side by CreateDealHandler
                // against this tenant's own stages, so nothing is checked
                // against a hardcoded list here. An empty value resolves to
                // the tenant's starting stage.

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
                    // ★ (030) SpecifyKind, not ToUniversalTime. This is a
                    // CALENDAR DATE from <input type="date">: it arrives as
                    // midnight with Kind=Unspecified, and ToUniversalTime()
                    // treats Unspecified as LOCAL, so on an IST server
                    // 2026-10-25 00:00 was stored as 2026-10-24 18:30 UTC —
                    // every close date a day early. Store the date as midnight
                    // UTC and never convert it.
                    ExpectedCloseDateUtc = DateTime.SpecifyKind(Input.ExpectedCloseDate.Date, DateTimeKind.Utc),
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
            catch (InvalidOperationException ex)
            {
                // A refused stage or a missing pipeline — the message is
                // written for the user, so show it rather than swallowing it.
                _logger.LogWarning(ex, "Deal creation refused");
                ErrorMessage = ex.Message;
                await OnGetAsync();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create deal");
                ErrorMessage = "Failed to create deal. Please try again.";
                await OnGetAsync();
                return Page();
            }
        }

        /// <summary>
        /// The tenant's probability for a stage, used by the JS that
        /// auto-fills the field when the stage dropdown changes. Was the
        /// hardcoded table, so it showed figures the tenant never chose.
        /// </summary>
        public int GetStageProbability(string stageKey) =>
            Stages.FirstOrDefault(s => s.Key == stageKey)?.Probability ?? 0;
    }
}
