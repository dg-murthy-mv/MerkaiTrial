// =====================================================================
// CREATE LEAD - Backend
// Location: MerkaiTrial.Admin.Web/Pages/Leads/Create.cshtml.cs
//
// MIGRATION (earlier pass):
//   1. Base class AppPageModel -> AuthorizedPageModel
//   2. OnGetAsync enforces Leads.Create via ValidatePermissionAsync
//   3. OnPostAsync's defense-in-depth check uses ValidatePermissionAsync
//
// CHANGES (031)
//   ✅ InitializePermissionsAsync() on both handlers.
//
// 079 — CUSTOM FIELDS, AND THE TEMPDATA TRAP
//
//   1. CUSTOM FIELDS. Active Lead fields render through the shared
//      _CustomFieldInputs partial (the same one Contacts, Deals and
//      Companies use), are checked here first so each message sits under
//      its own input, and are sent as CreateLeadDto.CustomFields. The API
//      checks them again. If the field list cannot be loaded, CustomFields
//      is sent as NULL ("say nothing") and the form says why the extra
//      fields are missing.
//
//   2. ★ ERRORS SHOWED ON THE WRONG PAGE. ErrorMessage was [TempData], set
//      in the catch blocks, and then the page RENDERED. TempData set on a
//      response that renders is shown on the NEXT request — so "You have
//      reached your plan's lead limit" appeared one click late, on
//      whatever page came next, while this form showed nothing at all.
//      Errors now go into ModelState (string.Empty), which the view's
//      validation summary shows on THIS response. The property is gone.
//
//   3. RAW EXCEPTION TEXT NO LONGER REACHES THE PAGE. The catch-all wrote
//      $"Failed to create lead: {ex.Message}" — for an unexpected failure
//      that is an HTTP or SQL message, not something to show a customer.
//      An InvalidOperationException (the API's sentence: plan limit, a
//      refused custom field, an owner who cannot take work) is shown
//      as-is; anything else gets a plain "please try again" and the
//      detail goes to the log.
//
//   4. AFTER CREATING, the person lands on the NEW LEAD'S page, not the
//      list — where they can log the first call or plan the next step.
//      (Contacts, Deals and Companies do the same.)
//
//   5. THE COUNTRY → CURRENCY HELPER USES REAL DATA. The script held two
//      hardcoded tables (country → currency, currency → symbol) with
//      twenty-odd entries each, including the AED glyph the quote PDF
//      deliberately does not use. The countries this page already loads
//      carry their own currency (CountryDropdownDto.CurrencyCode), and
//      symbols come from CurrencyConfiguration — one source for both, so
//      a country added to the database works here with no code change.
//      CountryCodesJson is replaced by CountryCurrencyJson + SymbolsJson.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages.Shared;
using MerkaiTrial.Admin.Web.Services.CustomFields;
using MerkaiTrial.Admin.Web.Services.Leads;
using MerkaiTrial.Admin.Web.Services.Meta;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Leads
{
    public class CreateModel : AuthorizedPageModel
    {
        private readonly ILeadService _leadService;
        private readonly ICustomFieldService _customFields;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly ILogger<CreateModel> _logger;
        private readonly IMetaService _metaService;

        protected override string ModuleName => Modules.Leads;

        public CreateModel(
            ILeadService leadService,
            ICustomFieldService customFields,
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            IMetaService metaService,
            ILogger<CreateModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _leadService = leadService;
            _customFields = customFields;
            _currentUserService = currentUserService;
            _tenantService = tenantService;
            _metaService = metaService;
            _logger = logger;
        }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        /// <summary>079. Posted as CustomFields[&lt;field id&gt;] = value.</summary>
        [BindProperty]
        public Dictionary<string, string?> CustomFields { get; set; } = new();

        /// <summary>079. Country id → its currency code, for the country/currency sync script.</summary>
        public string CountryCurrencyJson { get; private set; } = "{}";

        /// <summary>079. Currency code → symbol, from CurrencyConfiguration.</summary>
        public string SymbolsJson { get; private set; } = "{}";

        public List<SelectListItem> VerticalOptions { get; set; } = new();
        public List<SelectListItem> ChannelOptions { get; set; } = new();
        public List<SelectListItem> SourceOptions { get; set; } = new();
        public List<SelectListItem> SalesTeamOptions { get; set; } = new();
        public List<SelectListItem> CountryOptions { get; set; } = new();
        public List<SelectListItem> CurrencyOptions { get; set; } = new();

        public string TenantCurrency { get; private set; } = string.Empty;
        public string TenantCountryCode { get; private set; } = string.Empty;

        /// <summary>079. Active custom fields for Leads, in order.</summary>
        public List<CustomFieldDefinitionDto> ActiveCustomFields { get; private set; } = new();
        public bool CustomFieldsLoadFailed { get; private set; }

        /// <summary>079. The model for _CustomFieldInputs.</summary>
        public CustomFieldFormVm CustomFieldInputs => new()
        {
            Fields       = ActiveCustomFields,
            Values       = CustomFields,
            LoadFailed   = CustomFieldsLoadFailed,
            CanConfigure = UserCanRead(Modules.Settings),
            EntityType   = CustomFieldEntityTypes.Lead
        };

        /// <summary>The symbol beside Estimated Value, for the lead's currency.</summary>
        public string CurrencySymbol => CurrencyConfiguration.GetCurrencySymbol(
            string.IsNullOrWhiteSpace(Input.CurrencyId) ? TenantCurrency : Input.CurrencyId);

        public class InputModel
        {
            [Required(ErrorMessage = "Full name is required")]
            [StringLength(200)]
            [Display(Name = "Full name")]
            public string FullName { get; set; } = string.Empty;

            [EmailAddress]
            [StringLength(320)]
            public string? Email { get; set; }

            [StringLength(32)]
            public string? Phone { get; set; }

            [StringLength(200)]
            [Display(Name = "Company name")]
            public string? CompanyName { get; set; }

            [StringLength(500)]
            public string? Address { get; set; }

            [Required(ErrorMessage = "Country is required")]
            [Display(Name = "Country")]
            public Guid? CountryId { get; set; }

            [Required(ErrorMessage = "Currency is required")]
            [Display(Name = "Currency")]
            public string? CurrencyId { get; set; }

            [Required(ErrorMessage = "Channel is required")]
            [Display(Name = "Channel")]
            public Guid? ChannelId { get; set; }

            [Display(Name = "Source")]
            public Guid? SourceId { get; set; }

            [Range(0, double.MaxValue, ErrorMessage = "Estimated value cannot be negative")]
            [Display(Name = "Estimated value")]
            public decimal? EstimatedValue { get; set; }

            [Display(Name = "Industry")]
            public Guid? VerticalId { get; set; }

            [Display(Name = "Owner")]
            public string? OwnerUserId { get; set; }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            // ✅ Check CREATE permission before rendering the form
            var permissionCheck = await ValidatePermissionAsync(Actions.Create);
            if (permissionCheck != null) return permissionCheck;

            await InitializePermissionsAsync();

            LoadTenantContext();

            // Tenant defaults on the form — the person can change them.
            Input.CurrencyId = TenantCurrency;

            await LoadFormDataAsync();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            // Defense in depth — a direct POST must be blocked here too.
            var permissionCheck = await ValidatePermissionAsync(Actions.Create);
            if (permissionCheck != null) return permissionCheck;

            await InitializePermissionsAsync();
            LoadTenantContext();
            await LoadFormDataAsync();

            // 079 — each custom field's message under its own input.
            CustomFieldForm.Validate(ActiveCustomFields, CustomFields, ModelState);

            if (!ModelState.IsValid)
                return Page();

            try
            {
                var currentUserId = _currentUserService.GetCurrentUserId();

                var dto = new CreateLeadDto(
                    TenantId: _currentUserService.GetCurrentTenantId(),
                    FullName: Input.FullName.Trim(),
                    Email: string.IsNullOrWhiteSpace(Input.Email) ? null : Input.Email.Trim(),
                    Phone: string.IsNullOrWhiteSpace(Input.Phone) ? null : Input.Phone.Trim(),
                    CompanyName: string.IsNullOrWhiteSpace(Input.CompanyName) ? null : Input.CompanyName.Trim(),
                    Address: string.IsNullOrWhiteSpace(Input.Address) ? null : Input.Address.Trim(),
                    CountryId: Input.CountryId,
                    Currency: Input.CurrencyId,
                    ChannelId: Input.ChannelId,
                    SourceId: Input.SourceId,
                    VerticalId: Input.VerticalId,
                    EstimatedValue: Input.EstimatedValue,
                    OwnerUserId: Input.OwnerUserId,
                    CreatedBy: currentUserId.ToString()
                )
                {
                    // 079 — null when the field list could not be loaded:
                    // the form had no custom inputs, so say nothing about them.
                    CustomFields = CustomFieldsLoadFailed
                        ? null
                        : CustomFieldForm.ToSubmission(CustomFields, ActiveCustomFields)
                };

                var lead = await _leadService.CreateAsync(dto);

                // TempData is right HERE: this response redirects, and the
                // layout shows the message once on the page it lands on.
                TempData["SuccessMessage"] = $"Lead '{lead.FullName}' created successfully!";
                return RedirectToPage("./Detail", new { id = lead.Id });     // note 4
            }
            catch (InvalidOperationException ex)
            {
                // The API's own sentence — plan limit, a refused custom
                // field, an owner who cannot take work. Note 3.
                ModelState.AddModelError(string.Empty, ex.Message);
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating lead");
                ModelState.AddModelError(string.Empty, "Failed to create the lead. Please try again.");
                return Page();
            }
        }

        // =============================================================
        // Helpers
        // =============================================================

        private void LoadTenantContext()
        {
            TenantCurrency    = _tenantService.GetCurrencyCode();
            TenantCountryCode = _tenantService.GetCountryCode();
        }

        private async Task LoadFormDataAsync()
        {
            await LoadDropdownsAsync();
            await LoadCustomFieldsAsync();
        }

        private async Task LoadCustomFieldsAsync()
        {
            try
            {
                ActiveCustomFields = (await _customFields.GetDefinitionsAsync(CustomFieldEntityTypes.Lead, includeInactive: false))
                    .Where(f => f.IsActive)
                    .ToList();
                CustomFieldsLoadFailed = false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Custom fields could not be loaded for the Create Lead form");
                ActiveCustomFields = new List<CustomFieldDefinitionDto>();
                CustomFieldsLoadFailed = true;
            }
        }

        private async Task LoadDropdownsAsync()
        {
            var tenantId = _currentUserService.GetCurrentTenantId();

            try
            {
                var channels = await _leadService.GetChannelsAsync(tenantId);
                ChannelOptions = channels.Select(c => new SelectListItem
                {
                    Value = c.Id.ToString(),
                    Text = c.Name,
                    Selected = c.Id == Input.ChannelId
                }).ToList();
            }
            catch (Exception ex) { _logger.LogError(ex, "Failed to load channels"); }

            try
            {
                var sources = await _leadService.GetSourcesAsync(tenantId);
                SourceOptions = sources.Select(s => new SelectListItem
                {
                    Value = s.Id.ToString(),
                    Text = s.Name,
                    Selected = s.Id == Input.SourceId
                }).ToList();
            }
            catch (Exception ex) { _logger.LogError(ex, "Failed to load sources"); }

            try
            {
                var verticals = await _metaService.GetVerticalsAsync();
                VerticalOptions = verticals.Select(v => new SelectListItem
                {
                    Value = v.Id.ToString(),
                    Text = v.Name,
                    Selected = v.Id == Input.VerticalId
                }).ToList();
            }
            catch (Exception ex) { _logger.LogError(ex, "Failed to load verticals"); }

            try
            {
                var countries = await _leadService.GetCountriesAsync();

                // Default the country to the workspace's, if none chosen yet.
                if (!Input.CountryId.HasValue)
                {
                    var tenantCountry = countries.FirstOrDefault(c =>
                        string.Equals(c.Code, TenantCountryCode, StringComparison.OrdinalIgnoreCase));
                    if (tenantCountry != null)
                        Input.CountryId = tenantCountry.Id;
                }

                CountryOptions = countries.Select(c => new SelectListItem
                {
                    Value = c.Id.ToString(),
                    Text = c.Name,
                    Selected = c.Id == Input.CountryId
                }).ToList();

                // Note 5 — the countries' own currencies, not a hardcoded table.
                CountryCurrencyJson = System.Text.Json.JsonSerializer.Serialize(
                    countries
                        .Where(c => !string.IsNullOrWhiteSpace(c.CurrencyCode))
                        .ToDictionary(c => c.Id.ToString(), c => c.CurrencyCode.Trim().ToUpperInvariant()));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load countries");
                CountryCurrencyJson = "{}";
            }

            try
            {
                var currencies = await _leadService.GetCurrenciesAsync();
                CurrencyOptions = currencies.Select(c => new SelectListItem
                {
                    Value = c.Code,
                    Text = c.Code,
                    Selected = string.Equals(c.Code, Input.CurrencyId ?? TenantCurrency,
                                             StringComparison.OrdinalIgnoreCase)
                }).ToList();

                // Note 5 — symbols from the one currency table.
                SymbolsJson = System.Text.Json.JsonSerializer.Serialize(
                    currencies
                        .Where(c => !string.IsNullOrWhiteSpace(c.Code))
                        .GroupBy(c => c.Code.Trim().ToUpperInvariant())
                        .ToDictionary(g => g.Key, g => CurrencyConfiguration.GetCurrencySymbol(g.Key)));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load currencies");
                SymbolsJson = "{}";
            }

            try
            {
                var salesTeam = await _leadService.GetSalesTeamAsync(tenantId);
                SalesTeamOptions = salesTeam.Select(u => new SelectListItem
                {
                    Value = u.Id.ToString(),
                    Text = u.FullName,
                    Selected = u.Id.ToString() == Input.OwnerUserId
                }).ToList();
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to load sales team"); }
        }
    }
}
