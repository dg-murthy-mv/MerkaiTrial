// =====================================================================
// EDIT LEAD - Backend
// Location: MerkaiTrial.Admin.Web/Pages/Leads/Edit.cshtml.cs
//
// MIGRATION (this pass):
//   1. Base class AppPageModel -> AuthorizedPageModel
//   2. OnGetAsync now enforces Leads.Update via ValidatePermissionAsync
//      before loading the lead / rendering the form (previously: anyone
//      could open this page and see the pre-filled form regardless of
//      role; only the final POST was blocked)
//   3. OnPostAsync's defense-in-depth check now uses
//      ValidatePermissionAsync(Actions.Update) instead of the old
//      hand-rolled CanUpdate("Leads") claim check
//
// CHANGES (031)
//   1. THE COUNTRY DEFAULT BLOCK COMPARED A COUNTRY CODE AGAINST A NULL
//      GUID's ToString(). It read:
//
//          Selected = c.Id == Input.CountryId ||
//                     (!Input.CountryId.HasValue && c.Code == Input.CountryId.ToString())
//          ...
//          var tenantCountry = countries.FirstOrDefault(c => c.Code == Input.CountryId.ToString());
//
//      Input.CountryId is a Guid?; on the branch where it has NO value,
//      .ToString() is "" — so it looked for a country whose Code is the empty
//      string and never found one. It was copy-pasted from Create.cshtml.cs,
//      where the same lines correctly compare against TenantCountryCode. Dead
//      code that did nothing; replaced with the tenant's country code, which
//      is what it was meant to be.
//
//   2. InitializePermissionsAsync() was never called on either handler, so
//      every Can* flag on the base class was false for the whole render.
//
//   3. CurrencySymbolFor(code) added, so the Estimated Value box shows the
//      symbol for the LEAD's currency rather than the workspace's. The view's
//      span also had no id, so the script that keeps it in step with the
//      country never found it — see Edit.cshtml.
//
//   4. A null lead on GET now redirects with "Lead not found." instead of
//      falling into the generic catch via an NRE on lead.FullName.
//
// CHANGES (041 — the owner, on purpose rather than by accident)
//   ★ A LEAD COULD LOSE ITS OWNER WITHOUT ANYONE CHOOSING THAT. The API's
//     update handler read `lead.OwnerUserId = dto.OwnerUserId;`, so any
//     caller that did not send the field unassigned the lead — and a rep
//     whose record scope is "Own" then stopped seeing it at all. Deals had
//     the opposite bug: an empty value was ignored, so a deal assigned by
//     mistake could not be un-assigned. Both now share one rule, in
//     OwnerAssignment.
//
//     This page supplies the three pieces the UI side needs:
//
//       1. "— Unassigned —" at the top of the owner list, so removing an
//          owner is a thing a person can ask for.
//       2. [DisplayFormat(ConvertEmptyStringToNull = false)] on
//          Input.OwnerUserId, so choosing it posts "" rather than null.
//          Null means "field not sent, leave the owner alone" — the exact
//          opposite of the request.
//       3. catch (InvalidOperationException), so a refused owner shows the
//          server's sentence instead of "Failed to update lead."
//
//     An owner is only re-validated when it CHANGES, so a lead owned by
//     someone since deactivated can still have its phone number fixed.
//
// 079 — CUSTOM FIELDS, AND THE TEMPDATA TRAP
//
//   1. CUSTOM FIELDS. Active Lead fields render through the shared
//      _CustomFieldInputs partial, pre-filled from the lead's stored values
//      (LeadDetailDto.CustomFieldValues), checked here first, and sent as
//      UpdateLeadDto.CustomFields. Only ACTIVE fields are on the form and
//      only active fields are sent, so a RETIRED field's value is absent
//      from the map — and absent means "leave alone". If the field list
//      cannot be loaded, CustomFields is sent as NULL and only the
//      standard fields are saved.
//
//   2. ★ A REFUSED SAVE SHOWED ITS MESSAGE TWICE — once now, once later.
//      The InvalidOperationException catch put the sentence in ModelState
//      (shown on this page) AND in [TempData] ErrorMessage, which the
//      layout then showed AGAIN on the next page. The generic catch set
//      only the TempData one, so that message appeared one page late and
//      never here. Both now use ModelState only; the property is gone.
//
//   3. THE COUNTRY → CURRENCY HELPER USES REAL DATA (same as Create): each
//      country's own currency, and symbols from CurrencyConfiguration
//      instead of the page's hardcoded table (which drew AED as د.إ, unlike
//      the quote PDF). CurrencySymbolFor now delegates to it.
//
//   4. A converted lead opened by URL is sent back to its page with a
//      reason, as the Edit buttons are already hidden for it. The API
//      refuses nothing here today, so the page is the guard that matters.
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
    public class EditModel : AuthorizedPageModel
    {
        private readonly ILeadService _leadService;
        private readonly ICustomFieldService _customFields;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly ILogger<EditModel> _logger;
        private readonly IMetaService _metaService;

        protected override string ModuleName => Modules.Leads;

        public EditModel(
            ILeadService leadService,
            ICustomFieldService customFields,
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            IMetaService metaService,
            ILogger<EditModel> logger)
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
        public Guid Id { get; set; }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        /// <summary>079. Posted as CustomFields[&lt;field id&gt;] = value.</summary>
        [BindProperty]
        public Dictionary<string, string?> CustomFields { get; set; } = new();

        /// <summary>079. Country id → its currency code, for the sync script.</summary>
        public string CountryCurrencyJson { get; private set; } = "{}";

        /// <summary>079. Currency code → symbol, from CurrencyConfiguration.</summary>
        public string SymbolsJson { get; private set; } = "{}";

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
        public List<SelectListItem> ChannelOptions { get; set; } = new();
        public List<SelectListItem> SourceOptions { get; set; } = new();
        public List<SelectListItem> SalesTeamOptions { get; set; } = new();
        public List<SelectListItem> CountryOptions { get; set; } = new();
        public List<SelectListItem> CurrencyOptions { get; set; } = new();
        public List<SelectListItem> VerticalOptions { get; set; } = new();

        public string TenantCurrency { get; private set; } = string.Empty;

        /// <summary>(031) The workspace's ISO country code, for the country default.</summary>
        public string TenantCountryCode { get; private set; } = string.Empty;

        public class InputModel
        {
            [Required]
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

            [Range(0, 100)]
            public int Score { get; set; }

            [Range(0, double.MaxValue)]
            public decimal? EstimatedValue { get; set; }

            /// <summary>
            /// 041. THE ANNOTATION IS LOAD-BEARING. MVC binding turns an empty
            /// posted value into null for a string property by default, and the
            /// API now reads the three cases apart:
            ///
            ///     null  → the field was not sent; leave the owner alone
            ///     ""    → sent empty; unassign
            ///     an id → assign, after checking the user is active here
            ///
            /// Without ConvertEmptyStringToNull = false, choosing
            /// "— Unassigned —" would arrive as null and the lead would keep
            /// its existing owner with no error to show for it.
            /// </summary>
            [Display(Name = "Owner")]
            [DisplayFormat(ConvertEmptyStringToNull = false)]
            public string? OwnerUserId { get; set; }

            public Guid? VerticalId { get; set; }
        }

        public async Task<IActionResult> OnGetAsync(Guid id)
        {
            // ✅ Check UPDATE permission before loading the lead / form
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            // ✅ (031) Was missing on both handlers.
            await InitializePermissionsAsync();

            try
            {
                Id = id;
                LoadTenantContext();

                var tenantId = _currentUserService.GetCurrentTenantId();
                var lead = await _leadService.GetByIdAsync(tenantId, id);

                // ✅ (031) A null here used to NRE on lead.FullName and land in
                // the generic catch as "Failed to load lead".
                if (lead == null)
                {
                    TempData["ErrorMessage"] = "Lead not found.";
                    return RedirectToPage("./Index");
                }

                // 079 — note 4. Redirect, so TempData is the right carrier.
                if (lead.IsConverted)
                {
                    TempData["ErrorMessage"] = "This lead has been converted, so it can't be edited. Make changes on its deal.";
                    return RedirectToPage("./Detail", new { id });
                }

                Input = new InputModel
                {
                    FullName = lead.FullName,
                    Email = lead.Email,
                    Phone = lead.Phone,
                    CompanyName = lead.CompanyName,
                    Address = lead.Address,
                    CountryId = lead.CountryId,
                    CurrencyId = !string.IsNullOrEmpty(lead.Currency)
                                       ? lead.Currency
                                       : TenantCurrency,
                    ChannelId = lead.ChannelId,
                    SourceId = lead.SourceId,
                    // ✅ FIX: was never set — Edit form always showed
                    // "-- Select Industry --" regardless of what was saved
                    // on create, since Input.VerticalId silently defaulted
                    // to null every time this page loaded.
                    VerticalId = lead.VerticalId,
                    Score = lead.Score,
                    EstimatedValue = lead.ExpectedValue,
                    OwnerUserId = lead.OwnerUserId
                };

                // 079 — what the custom inputs start with.
                CustomFields = CustomFieldForm.FromStored(lead.CustomFieldValues);

                await LoadDropdownsAsync();
                await LoadCustomFieldsAsync();
                return Page();
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Lead not found.";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading lead {Id}", id);
                TempData["ErrorMessage"] = "Failed to load lead. Please try again.";
                return RedirectToPage("./Index");
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            // Defense in depth — see same note in Leads/Create.cshtml.cs
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            await InitializePermissionsAsync();
            LoadTenantContext();
            await LoadDropdownsAsync();
            await LoadCustomFieldsAsync();

            // 079 — each custom field's message under its own input.
            CustomFieldForm.Validate(ActiveCustomFields, CustomFields, ModelState);

            if (!ModelState.IsValid)
                return Page();

            try
            {
                var dto = new UpdateLeadDto(
                    TenantId: _currentUserService.GetCurrentTenantId(),
                    LeadId: Id,
                    FullName: Input.FullName,
                    Email: Input.Email,
                    Phone: Input.Phone,
                    CompanyName: Input.CompanyName,
                    Address: Input.Address,
                    CountryId: Input.CountryId,
                    Currency: Input.CurrencyId,
                    ChannelId: Input.ChannelId,
                    VerticalId: Input.VerticalId,
                    SourceId: Input.SourceId,
                    Score: Input.Score,
                    EstimatedValue: Input.EstimatedValue,
                    OwnerUserId: Input.OwnerUserId
                )
                {
                    // 079 — null when the field list could not be loaded.
                    CustomFields = CustomFieldsLoadFailed
                        ? null
                        : CustomFieldForm.ToSubmission(CustomFields, ActiveCustomFields)
                };

                await _leadService.UpdateAsync(dto);

                TempData["SuccessMessage"] = "Lead updated successfully!";
                return RedirectToPage("./Detail", new { id = Id });
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Lead not found.";
                return RedirectToPage("./Index");
            }
            // 041. IApiService turns the API's 400/403 { "error": ... } into
            // this, carrying the server's own wording. Without this catch the
            // owner checks added in this round would have surfaced as "Failed
            // to update lead. Please try again." — which tells the person
            // nothing, and hides the one sentence that would have helped
            // ("Somchai's account is deactivated, so it cannot be given new
            // work."). The deal edit page has had this catch since 020; the
            // lead page never did.
            catch (InvalidOperationException ex)
            {
                _logger.LogInformation("Lead {Id} update refused: {Message}", Id, ex.Message);

                // 079 — ModelState only (note 2). Shown once, here.
                ModelState.AddModelError(string.Empty, ex.Message);
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating lead {Id}", Id);
                ModelState.AddModelError(string.Empty, "Failed to update the lead. Please try again.");
                return Page();
            }
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
                _logger.LogWarning(ex, "Custom fields could not be loaded for the Edit Lead form");
                ActiveCustomFields = new List<CustomFieldDefinitionDto>();
                CustomFieldsLoadFailed = true;
            }
        }

        private void LoadTenantContext()
        {
            TenantCurrency    = _tenantService.GetCurrencyCode();
            TenantCountryCode = _tenantService.GetCountryCode();
        }

        /// <summary>
        /// (031) Symbol for an ISO code, so the value box carries the LEAD's
        /// currency rather than the workspace's. An unknown code comes back as
        /// the code — printing ₹ beside a dollar figure is worse than printing
        /// "USD".
        /// </summary>
        public string CurrencySymbolFor(string? code)
            => CurrencyConfiguration.GetCurrencySymbol(string.IsNullOrWhiteSpace(code) ? TenantCurrency : code);   // 079

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
                var verticals = await _metaService.GetVerticalsAsync();
                VerticalOptions = verticals.Select(v => new SelectListItem
                {
                    Value = v.Id.ToString(),
                    Text = v.Name,
                    // ✅ FIX: was missing — every other dropdown here
                    // (Channel/Source/Country/Currency) marks Selected,
                    // Vertical never did, so even with Input.VerticalId
                    // now populated the <select> would still render with
                    // nothing chosen.
                    Selected = v.Id == Input.VerticalId
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load verticals");
            }
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
                var countries = await _leadService.GetCountriesAsync();

                // ★ (031) Was comparing a country Code against
                // Input.CountryId.ToString() on the branch where CountryId has
                // NO value — i.e. against "" — so it never matched anything.
                // Copy-pasted from Create.cshtml.cs, where the same lines
                // correctly use TenantCountryCode. Now they do here too.
                CountryOptions = countries.Select(c => new SelectListItem
                {
                    Value    = c.Id.ToString(),
                    Text     = c.Name,
                    Selected = c.Id == Input.CountryId ||
                               (!Input.CountryId.HasValue && c.Code == TenantCountryCode)
                }).ToList();

                if (!Input.CountryId.HasValue)
                {
                    var tenantCountry = countries.FirstOrDefault(c => c.Code == TenantCountryCode);
                    if (tenantCountry != null)
                        Input.CountryId = tenantCountry.Id;
                }

                // 079 — each country's own currency, not a hardcoded table.
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
                    Selected = string.Equals(c.Code, Input.CurrencyId, StringComparison.OrdinalIgnoreCase)
                }).ToList();

                // 079 — symbols from the one currency table.
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

                SalesTeamOptions = WithUnassignedOption(          // 041
                    salesTeam.Select(u => new SelectListItem
                    {
                        Value = u.Id.ToString(),
                        Text = u.FullName,
                        Selected = u.Id.ToString() == Input.OwnerUserId
                    }).ToList(),
                    Input.OwnerUserId);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to load sales team"); }
        }

        /// <summary>
        /// 041. Puts "— Unassigned —" at the top of the owner list. Taking a
        /// lead off someone was always possible by accident — a DTO with no
        /// owner field wiped it — and never possible on purpose. Now it is the
        /// other way round, which is the correct way round.
        /// </summary>
        private static List<SelectListItem> WithUnassignedOption(
            List<SelectListItem> team, string? selectedOwnerUserId)
        {
            team.Insert(0, new SelectListItem
            {
                Value    = string.Empty,
                Text     = "— Unassigned —",
                Selected = string.IsNullOrEmpty(selectedOwnerUserId)
            });

            return team;
        }
    }
}
