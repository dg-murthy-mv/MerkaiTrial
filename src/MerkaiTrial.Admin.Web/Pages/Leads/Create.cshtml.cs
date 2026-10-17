// =====================================================================
// CREATE LEAD - Backend
// Location: MerkaiTrial.Admin.Web/Pages/Leads/Create.cshtml.cs
//
// MIGRATION (this pass):
//   1. Base class AppPageModel -> AuthorizedPageModel
//   2. OnGetAsync now enforces Leads.Create via ValidatePermissionAsync
//      before rendering the form (previously: anyone could open this page
//      by URL regardless of role; only the final POST was blocked)
//   3. OnPostAsync's defense-in-depth check now uses
//      ValidatePermissionAsync(Actions.Create) instead of the old
//      hand-rolled CanCreate("Leads") claim check, so a denied user gets
//      the same AccessDenied redirect as everywhere else in the app
//      instead of a bare Forbid()
//
// CHANGES (031)
//   ✅ InitializePermissionsAsync() on both handlers — it was never called, so
//      every Can* flag on the base class was false for the whole render.
//   ✅ The view no longer renders Model.ErrorMessage itself: it is [TempData]
//      and _Layout renders TempData alerts globally, so a failure gave two
//      identical banners.
//
// CHANGES (083 — create a lead from an email)
//   ✅ ?fromEmail={id} — the Inbox's "Create a lead from this email". The
//      form opens with the sender's name and address filled in, a banner
//      says which email it came from, and after saving the email is FILED
//      ON THE NEW LEAD and the page goes to that lead's Emails tab.
//   ✅ The email is loaded through the API, which applies its own access
//      rules; one this person may not see simply leaves the form blank.
//      Filing afterwards needs inbox.update, checked by the API again. If
//      filing fails the lead is still created and the message says so —
//      a lead must never be lost because the email could not be moved.
//   ✅ FromEmailId is a NULLABLE Guid on purpose: OnPostAsync still uses
//      ModelState.IsValid, and a non-nullable hidden field would add an
//      implicit Required error to every ordinary lead.
//
// CHANGES (092 — custom fields on Create)
//   ✅ The workspace's Lead custom fields are on this form, through the
//      same _CustomFieldInputs partial the Edit page has used since 079.
//      Before this, a lead could only get its custom values AFTER it was
//      created — and a REQUIRED field could not be filled in at all until
//      someone opened Edit, which is exactly where the form then refused
//      to save without it.
//   ✅ Checked here first (CustomFieldForm.Validate), so each message sits
//      under its own input, and checked again by the API, which is the
//      rule that counts. If the field list cannot be loaded, CustomFields
//      is sent as NULL and the lead is created with its standard fields
//      only — the same "say nothing" rule every other caller uses.
//   ✅ A refusal from the API (InvalidOperationException — for example a
//      required field emptied in another tab) is shown in the summary on
//      THIS response, not stored for the next page.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages.Shared;             // 092
using MerkaiTrial.Admin.Web.Services.CustomFields;    // 092
using MerkaiTrial.Admin.Web.Services.InboundEmails;   // 083
using MerkaiTrial.Admin.Web.Services.Leads;
using MerkaiTrial.Admin.Web.Services.Meta;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Meta;
using MerkaiTrial.Application.Configuration;          // 092
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
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly ILogger<CreateModel> _logger;
        private readonly IMetaService _metaService;
        private readonly IInboundEmailService _emails;      // 083
        private readonly ICustomFieldService _customFields; // 092

        protected override string ModuleName => Modules.Leads;

        public CreateModel(
            ILeadService leadService,
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            IMetaService metaService,
            IInboundEmailService emails,
            ICustomFieldService customFields,
            ILogger<CreateModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _emails = emails;
            _customFields = customFields;
            _leadService = leadService;
            _currentUserService = currentUserService;
            _tenantService = tenantService;
            _metaService = metaService;
            _logger = logger;
        }

        [BindProperty]
        public InputModel Input { get; set; } = new();
        public string CountryCodesJson { get; set; } = "{}";

        // ── 092: custom fields ────────────────────────────────────────
        /// <summary>Posted as CustomFields[&lt;field id&gt;] = value, exactly like Edit.</summary>
        [BindProperty]
        public Dictionary<string, string?> CustomFields { get; set; } = new();

        /// <summary>Active custom fields for Leads, in order.</summary>
        public List<CustomFieldDefinitionDto> ActiveCustomFields { get; private set; } = new();
        public bool CustomFieldsLoadFailed { get; private set; }

        /// <summary>The model for _CustomFieldInputs.</summary>
        public CustomFieldFormVm CustomFieldInputs => new()
        {
            Fields       = ActiveCustomFields,
            Values       = CustomFields,
            LoadFailed   = CustomFieldsLoadFailed,
            CanConfigure = UserCanRead(Modules.Settings),
            EntityType   = CustomFieldEntityTypes.Lead
        };

        public List<SelectListItem> VerticalOptions { get; set; } = new();
        public List<SelectListItem> ChannelOptions { get; set; } = new();
        public List<SelectListItem> SourceOptions { get; set; } = new();
        public List<SelectListItem> SalesTeamOptions { get; set; } = new();
        public List<SelectListItem> CountryOptions { get; set; } = new();
        public List<SelectListItem> CurrencyOptions { get; set; } = new();

        public string TenantCurrencySymbol { get; private set; } = "₹";
        public string TenantCurrency { get; private set; } = "INR";
        public string TenantCountryCode { get; private set; } = "IN";

        [TempData] public string? ErrorMessage { get; set; }

        // ── 083: from an email ────────────────────────────────────────
        /// <summary>The email this lead is being created from. Posted back as a hidden field.</summary>
        [BindProperty] public Guid? FromEmailId { get; set; }

        /// <summary>"Priya Shah — Quotation for 3 units", for the banner. Display only.</summary>
        public string? FromEmailLabel { get; private set; }

        public class InputModel
        {
            [Required(ErrorMessage = "Full name is required")]
            [StringLength(200)]
            public string FullName { get; set; } = string.Empty;

            [EmailAddress]
            [StringLength(320)]
            public string? Email { get; set; }

            [StringLength(32)]
            public string? Phone { get; set; }

            [StringLength(200)]
            public string? CompanyName { get; set; }

            [StringLength(500)]
            public string? Address { get; set; }

            [Required(ErrorMessage = "Country is required")]
            public Guid? CountryId { get; set; }

            [Required(ErrorMessage = "Currency is required")]
            public string? CurrencyId { get; set; }

            [Required(ErrorMessage = "Channel is required")]
            public Guid? ChannelId { get; set; }

            public Guid? SourceId { get; set; }

            [Range(0, double.MaxValue)]
            public decimal? EstimatedValue { get; set; }
            public Guid? VerticalId { get; set; }
            public string? OwnerUserId { get; set; }
        }

        public async Task<IActionResult> OnGetAsync(Guid? fromEmail = null)
        {
            // ✅ Check CREATE permission before rendering the form
            var permissionCheck = await ValidatePermissionAsync(Actions.Create);
            if (permissionCheck != null) return permissionCheck;

            // ✅ (031) Was missing. Without it every Can* flag on the base class
            // stays false for the whole render, so any permission gate in the
            // view silently hides what it guards.
            await InitializePermissionsAsync();

            try
            {
                LoadTenantContext();
                await LoadDropdownsAsync();
                await LoadCustomFieldsAsync();      // 092

                // Set tenant defaults on the form — user can override
                Input.CurrencyId = TenantCurrency;
                // CountryId will be selected via dropdown match on TenantCountryCode

                // 083 — prefill from the email, when there is one.
                if (fromEmail.HasValue && fromEmail.Value != Guid.Empty)
                    await PrefillFromEmailAsync(fromEmail.Value);

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading form");
                ErrorMessage = $"Failed to load form: {ex.Message}";
                return Page();
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            try
            {
                // Defense in depth — the button that links here is already
                // hidden for roles without Leads.Create, but a direct POST
                // (bypassing the UI) must be blocked here too, since this
                // ultimately calls a WebApi endpoint that trusts the caller.
                var permissionCheck = await ValidatePermissionAsync(Actions.Create);
                if (permissionCheck != null) return permissionCheck;

                await InitializePermissionsAsync();
                LoadTenantContext();
                await LoadCustomFieldsAsync();      // 092

                // 092 — each custom field's message under its own input.
                CustomFieldForm.Validate(ActiveCustomFields, CustomFields, ModelState);

                if (!ModelState.IsValid)
                {
                    await LoadDropdownsAsync();
                    return Page();
                }

                var currentUserId = _currentUserService.GetCurrentUserId();

                var dto = new CreateLeadDto(
                    TenantId: _currentUserService.GetCurrentTenantId(),
                    FullName: Input.FullName,
                    Email: Input.Email,
                    Phone: Input.Phone,
                    CompanyName: Input.CompanyName,
                    Address: Input.Address,
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
                    // 092 — null when the field list could not be loaded:
                    // the lead is still created, with its standard fields.
                    CustomFields = CustomFieldsLoadFailed
                        ? null
                        : CustomFieldForm.ToSubmission(CustomFields, ActiveCustomFields)
                };

                var lead = await _leadService.CreateAsync(dto);

                // 083 — file the email on the new lead. The lead exists
                // already; a failure here is reported, never undone.
                if (FromEmailId.HasValue && FromEmailId.Value != Guid.Empty && lead is not null)
                {
                    try
                    {
                        await _emails.LinkAsync(FromEmailId.Value, "Lead", lead.Id);
                        TempData["SuccessMessage"] = $"Lead '{lead.FullName}' created, and the email is filed on it.";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Lead {LeadId} created but email {EmailId} could not be filed on it",
                            lead.Id, FromEmailId);
                        TempData["SuccessMessage"] = $"Lead '{lead.FullName}' created.";
                        TempData["ErrorMessage"] = "The email could not be filed on it automatically — open it from the Inbox and file it by hand.";
                    }

                    return RedirectToPage("./Detail", null, new { id = lead.Id }, "emails");
                }

                TempData["SuccessMessage"] = $"Lead '{lead.FullName}' created successfully!";
                return RedirectToPage("./Index");
            }
            // 092 — the API's own sentence ("Renewal date is required."),
            // shown once, in the summary on this response.
            catch (InvalidOperationException ex)
            {
                _logger.LogInformation("Lead create refused: {Message}", ex.Message);
                ModelState.AddModelError(string.Empty, ex.Message);
                LoadTenantContext();
                await LoadDropdownsAsync();
                if (ActiveCustomFields.Count == 0 && !CustomFieldsLoadFailed) await LoadCustomFieldsAsync();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating lead");
                ErrorMessage = $"Failed to create lead: {ex.Message}";
                LoadTenantContext();
                await LoadDropdownsAsync();
                if (ActiveCustomFields.Count == 0 && !CustomFieldsLoadFailed) await LoadCustomFieldsAsync();
                return Page();
            }
        }

        /// <summary>
        /// 092. The active Lead fields. Non-fatal: on failure the form shows
        /// the partial's "couldn't load" note and the lead is created
        /// without custom values (CustomFields sent as null).
        /// </summary>
        private async Task LoadCustomFieldsAsync()
        {
            try
            {
                ActiveCustomFields = (await _customFields.GetDefinitionsAsync(CustomFieldEntityTypes.Lead, includeInactive: false))
                    .Where(f => f.IsActive)
                    .OrderBy(f => f.SortOrder)
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

        /// <summary>
        /// 083. Name and address from the email. Silent on failure — the
        /// form simply stays blank, which is the same page they would have
        /// had without the button.
        /// </summary>
        private async Task PrefillFromEmailAsync(Guid emailId)
        {
            try
            {
                var email = await _emails.GetAsync(emailId);

                // The CUSTOMER — for a forward, the person quoted inside it.
                var address = email.CounterpartAddress ?? email.FromAddress;
                var name = email.Direction == "Received" && !string.IsNullOrWhiteSpace(email.FromName)
                    ? email.FromName!
                    : NameFromAddress(address);

                if (string.IsNullOrWhiteSpace(Input.FullName)) Input.FullName = name;
                if (string.IsNullOrWhiteSpace(Input.Email)) Input.Email = address;

                FromEmailId = email.Id;
                FromEmailLabel = $"{name} — {email.Subject}";
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Could not prefill a lead from email {EmailId}", emailId);
            }
        }

        /// <summary>"priya.shah@acme.com" → "Priya Shah". A starting point the person can correct.</summary>
        private static string NameFromAddress(string? address)
        {
            var local = (address ?? string.Empty).Split('@')[0];
            var words = local.Split(new[] { '.', '_', '-', '+' }, StringSplitOptions.RemoveEmptyEntries)
                             .Where(w => w.Any(char.IsLetter))
                             .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());
            var name = string.Join(" ", words);
            return name.Length > 0 ? name : (address ?? string.Empty);
        }

        private void LoadTenantContext()
        {
            TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
            TenantCurrency = _tenantService.GetCurrencyCode();
            TenantCountryCode = _tenantService.GetCountryCode();
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
                    Text = v.Name
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load verticals");
            }
            try
            {
                var countries = await _leadService.GetCountriesAsync();

                CountryOptions = countries.Select(c => new SelectListItem
                {
                    Value = c.Id.ToString(),
                    Text = c.Name,
                    Selected = c.Id == Input.CountryId ||
                               (!Input.CountryId.HasValue && c.Code == TenantCountryCode)
                }).ToList();

                // Auto-set CountryId default from tenant if not already set
                if (!Input.CountryId.HasValue)
                {
                    var tenantCountry = countries.FirstOrDefault(c => c.Code == TenantCountryCode);
                    if (tenantCountry != null)
                        Input.CountryId = tenantCountry.Id;
                }

                CountryCodesJson = System.Text.Json.JsonSerializer.Serialize(
                    countries.ToDictionary(
                        c => c.Id.ToString(),
                        c => c.Code ?? ""
                    )
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load countries");
                CountryCodesJson = "{}";
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
            }
            catch (Exception ex) { _logger.LogError(ex, "Failed to load currencies"); }

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
