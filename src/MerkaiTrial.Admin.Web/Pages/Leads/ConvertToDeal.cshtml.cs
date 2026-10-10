// =====================================================================
// CONVERT TO DEAL — Backend
// Location: MerkaiTrial.Admin.Web/Pages/Leads/ConvertToDeal.cshtml.cs
//
// COMPLETE FILE — replaces the existing one.
//
// CHANGES (031)
//
//   1. ★ THE PIPELINE STAGES WERE HARDCODED. The view offered exactly four
//      options — Discovery, Qualification, Proposal, Negotiation — with
//      invented probabilities baked into the labels, and this file's input
//      model defaulted Stage to the literal "Qualification".
//
//      Rounds 019 and 020 made stages tenant-configurable: a workspace can
//      rename them, reorder them, add "Site Visit" and retire "Discovery".
//      This page ignored all of it, so a tenant whose stages are
//      Enquiry / Site Visit / Quote / Won was shown four stages that do not
//      exist in their pipeline, and the conversion was refused by the
//      server (or landed the deal in a stage nothing else recognises).
//      For a workspace that never renamed anything it happened to work,
//      which is why it survived this long.
//
//      Stages now come from IPipelineStageService, active only, and the
//      default is the tenant's own starting stage — exactly as
//      Pipeline/Create does it. The probability shown beside each stage is
//      the tenant's own figure, not a number we made up.
//
//   2. ★ "Only qualified leads can be converted" MATCHED THE LITERAL STRING
//      "Qualified". Lead statuses are tenant-configurable too, with a
//      CATEGORY (Open / Qualified / Disqualified / Converted) that is the
//      stable thing to test. A tenant who renamed "Qualified" to "Ready to
//      quote", or added a second qualified status, could not convert
//      anything: this page bounced them back to the lead with "Only
//      qualified leads can be converted to deals" on a lead that plainly
//      was. Now checked by category.
//
//   3. ★ THE CLOSE DATE WAS STORED A DAY EARLY.
//      ExpectedCloseDateUtc = Input.ExpectedCloseDate.ToUniversalTime() —
//      the third copy of this bug in the app, after Quotes and Pipeline.
//      <input type="date"> posts midnight with Kind=Unspecified, and
//      ToUniversalTime() treats Unspecified as LOCAL, so on an IST server
//      2026-10-25 00:00 is stored as 2026-10-24 18:30 UTC. Now stored as
//      midnight UTC with SpecifyKind, with no conversion.
//
//   4. THE CURRENCY CAME FROM THE BROWSER. Input.Currency was a hidden
//      field and was passed straight into ConvertToDealAsync, so the deal's
//      currency was whatever the form said. It is now resolved on the
//      server from the lead (falling back to the workspace), the same rule
//      Quotes/Create uses and for the same reason: currency decides what
//      the customer is billed.
//
//   5. A CLOSE DATE BEFORE TODAY was accepted silently.
//
//   6. InitializePermissionsAsync() was never called, so every Can* flag on
//      the base class was false for the whole render.
//
// MIGRATION (earlier pass):
//   • Base class bare PageModel -> AuthorizedPageModel. This page had ZERO
//     permission checks of any kind before that pass. It is the page
//     _RightSidebar.cshtml's "Convert to Deal" button links to, so it is
//     the real, reachable conversion path — not the OnPostConvertToDealAsync
//     handler inside Detail.cshtml.cs, which nothing submits to.
//   • Both handlers gated behind Leads.Update AND Deals.Create
//     (cross-module — converting updates the lead and creates a Deal).
//
// 079 — WHAT CARRIES ACROSS, SHOWN BEFORE IT HAPPENS
//
//   1. "Additional details carried over": every mapped Lead custom field
//      that has a value on THIS lead, with where it will land — "Contact ·
//      Budget", "Deal · Budget". The mapping is set per Lead field in
//      Settings → Custom Fields → Leads, and the API applies exactly this
//      list (LeadFieldMapper). Targets that are switched off are left out
//      here because the API skips them too.
//   2. If the lead has custom values but none are mapped, a one-line hint
//      says so, with a link to set it up for people who can open Settings.
//   3. Deal fields marked REQUIRED that nothing fills are named, so it is
//      no surprise when the deal's Edit page asks for them later.
//   4. SymbolFor uses CurrencyConfiguration — the page's own table drew
//      AED as د.إ, unlike the quote PDF and every other screen.
//   Loading the field lists is non-fatal: if it fails the card is simply
//   not shown and the conversion still works.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages.Shared;
using MerkaiTrial.Admin.Web.Services.CustomFields;
using MerkaiTrial.Admin.Web.Services.Leads;
using MerkaiTrial.Admin.Web.Services.Pipeline;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.LeadStatuses;
using MerkaiTrial.Application.Commands.PipelineStages;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Leads
{
    public class ConvertToDealModel : AuthorizedPageModel
    {
        private readonly ILeadService _leadService;
        private readonly ILeadStatusService _statusService;      // (031)
        private readonly IPipelineStageService _stageService;    // (031)
        private readonly ICustomFieldService _customFields;      // 079
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly ILogger<ConvertToDealModel> _logger;

        protected override string ModuleName => Modules.Leads;

        public ConvertToDealModel(
            ILeadService leadService,
            ILeadStatusService statusService,
            IPipelineStageService stageService,
            ICustomFieldService customFields,
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            ILogger<ConvertToDealModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _leadService = leadService;
            _statusService = statusService;
            _stageService = stageService;
            _customFields = customFields;
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
        public string? LeadCompany { get; set; }
        public Guid? ContactId { get; set; }
        public decimal EstimatedValue { get; set; }

        /// <summary>The lead's status, resolved to the tenant's own name.</summary>
        public string LeadStatusName { get; private set; } = string.Empty;

        /// <summary>
        /// (031) The tenant's OWN pipeline stages, active only — a retired
        /// stage should not be offered for brand new work. Replaces four
        /// hardcoded options in the view.
        /// </summary>
        public List<PipelineStageDto> Stages { get; private set; } = new();

        public string TenantCurrencySymbol { get; private set; } = string.Empty;
        public string TenantCurrency { get; private set; } = string.Empty;

        /// <summary>The currency this deal will be raised in — the lead's, or the workspace's.</summary>
        public string DealCurrency { get; private set; } = string.Empty;

        [TempData] public string? ErrorMessage { get; set; }
        [TempData] public string? SuccessMessage { get; set; }

        // ── 079: what carries across ──────────────────────────────────

        /// <summary>One mapped lead value and where it lands.</summary>
        public sealed record CarryOverItem(string LeadLabel, string Value, string? ToContact, string? ToDeal);

        /// <summary>Mapped Lead fields with a value on this lead (note 1).</summary>
        public List<CarryOverItem> CarryOver { get; private set; } = new();

        /// <summary>Lead fields with a value here but no mapping (note 2).</summary>
        public int UnmappedValueCount { get; private set; }

        /// <summary>Required Deal fields nothing fills (note 3).</summary>
        public List<string> RequiredDealFieldsNotFilled { get; private set; } = new();

        public bool CanConfigureFields => UserCanRead(Modules.Settings);

        public class ConvertInputModel
        {
            [Required(ErrorMessage = "Deal title is required")]
            [StringLength(200)]
            public string DealTitle { get; set; } = string.Empty;

            [StringLength(2000)]
            public string? Description { get; set; }

            /// <summary>
            /// (031) No default. It is filled from the tenant's own starting
            /// stage on GET. A hardcoded "Qualification" here overrode the
            /// workspace's choice of where deals begin — and named a stage
            /// many workspaces do not have.
            /// </summary>
            [Required(ErrorMessage = "Please choose a stage")]
            public string Stage { get; set; } = string.Empty;

            [Required(ErrorMessage = "Expected value is required")]
            [Range(0.01, 999999999)]
            public decimal ExpectedValue { get; set; }

            [Required(ErrorMessage = "Expected close date is required")]
            [DataType(DataType.Date)]
            public DateTime ExpectedCloseDate { get; set; } = DateTime.Today.AddDays(30);
        }

        /// <summary>
        /// Cross-module check: converting a lead both updates the Lead
        /// record and creates a new Deal, so both permissions are required.
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

            // (031) Was missing. Without it every Can* flag stays false.
            await InitializePermissionsAsync();

            try
            {
                LoadTenantContext();

                var tenantId = _currentUserService.GetCurrentTenantId();
                var lead = await _leadService.GetByIdAsync(tenantId, id);

                if (lead == null)
                {
                    ErrorMessage = "Lead not found.";
                    return RedirectToPage("/Leads/Index");
                }

                // ★ (031) By CATEGORY, not by the literal string "Qualified".
                // A tenant who renamed the status, or who has more than one
                // qualified status, could not convert anything.
                if (!await IsQualifiedAsync(lead.Status))
                {
                    ErrorMessage = "Only a qualified lead can be converted to a deal. " +
                                   "Change its status first.";
                    return RedirectToPage("/Leads/Detail", new { id });
                }

                if (lead.DealId.HasValue)
                {
                    ErrorMessage = "This lead has already been converted to a deal.";
                    return RedirectToPage("/Leads/Detail", new { id });
                }

                await LoadStagesAsync();

                LeadId         = lead.Id;
                LeadName       = lead.FullName;
                LeadEmail      = lead.Email;
                LeadPhone      = lead.Phone;
                LeadCompany    = lead.CompanyName;
                ContactId      = lead.ContactId;
                EstimatedValue = lead.ExpectedValue;
                LeadStatusName = await StatusNameAsync(lead.Status);
                DealCurrency   = ResolveCurrency(lead.Currency);
                await LoadCarryOverAsync(lead);                         // 079

                // Start where the tenant says deals start.
                var start = Stages.FirstOrDefault(s => s.IsDefault) ?? Stages.FirstOrDefault();

                Input = new ConvertInputModel
                {
                    DealTitle = string.IsNullOrWhiteSpace(lead.CompanyName)
                        ? $"{lead.FullName}"
                        : $"{lead.CompanyName} — {lead.FullName}",
                    Stage             = start?.Key ?? string.Empty,
                    ExpectedValue     = lead.ExpectedValue,
                    ExpectedCloseDate = DateTime.UtcNow.Date.AddDays(30)
                };

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load convert page for lead {LeadId}", id);
                ErrorMessage = "Failed to load the conversion page. Please try again.";
                return RedirectToPage("/Leads/Index");
            }
        }

        public async Task<IActionResult> OnPostAsync(Guid id)
        {
            var permissionCheck = await ValidateConvertPermissionAsync();
            if (permissionCheck != null) return permissionCheck;

            await InitializePermissionsAsync();

            try
            {
                LoadTenantContext();

                var tenantId    = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();
                var lead        = await _leadService.GetByIdAsync(tenantId, id);

                if (lead == null)
                {
                    ErrorMessage = "Lead not found.";
                    return RedirectToPage("/Leads/Index");
                }

                // Same two guards as the GET — a stale form is otherwise a way
                // straight past them.
                if (!await IsQualifiedAsync(lead.Status))
                {
                    ErrorMessage = "Only a qualified lead can be converted to a deal.";
                    return RedirectToPage("/Leads/Detail", new { id });
                }

                if (lead.DealId.HasValue)
                {
                    ErrorMessage = "This lead has already been converted to a deal.";
                    return RedirectToPage("/Leads/Detail", new { id });
                }

                if (!ModelState.IsValid)
                {
                    await LoadForDisplayAsync(id);
                    return Page();
                }

                // ✅ (031) A close date in the past is almost always a typo.
                if (Input.ExpectedCloseDate.Date < DateTime.UtcNow.Date)
                {
                    ModelState.AddModelError("Input.ExpectedCloseDate",
                        "The expected close date is in the past.");
                    await LoadForDisplayAsync(id);
                    return Page();
                }

                // ✅ (031) The stage must be one of THIS tenant's stages. The
                // server checks again, but this gives the real message.
                await LoadStagesAsync();
                if (!Stages.Any(s => s.Key == Input.Stage))
                {
                    ModelState.AddModelError("Input.Stage",
                        "That stage isn't part of your workspace's pipeline. Pick one from the list.");
                    await LoadForDisplayAsync(id);
                    return Page();
                }

                var result = await _leadService.ConvertToDealAsync(new ConvertLeadToDealDto
                {
                    TenantId      = tenantId,
                    LeadId        = id,
                    DealTitle     = Input.DealTitle,
                    Description   = Input.Description,
                    Stage         = Input.Stage,
                    ExpectedValue = Input.ExpectedValue,
                    // ✅ (031) Server-resolved, never from the hidden field.
                    Currency      = ResolveCurrency(lead.Currency),
                    // ★ (031) SpecifyKind, not ToUniversalTime. See change 3.
                    ExpectedCloseDateUtc = DateTime.SpecifyKind(
                                               Input.ExpectedCloseDate.Date, DateTimeKind.Utc),
                    OwnerUserId   = lead.OwnerUserId,
                    ConvertedBy   = currentUser.FullName
                });

                SuccessMessage =
                    $"{result.Message} Deal '{Input.DealTitle}' is now in the pipeline.";

                return RedirectToPage("/Pipeline/Index");
            }
            catch (InvalidOperationException ex)
            {
                // The API refused and said why — a stage its guard won't take,
                // a lead already converted. Show its wording.
                _logger.LogWarning(ex, "Conversion refused for lead {LeadId}", id);
                ModelState.AddModelError(string.Empty, ex.Message);
                await LoadForDisplayAsync(id);
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to convert lead {LeadId} to deal", id);
                ModelState.AddModelError(string.Empty,
                    "Failed to convert the lead. Please try again.");
                await LoadForDisplayAsync(id);
                return Page();
            }
        }

        // ── helpers ───────────────────────────────────────────────────

        private void LoadTenantContext()
        {
            TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
            TenantCurrency       = _tenantService.GetCurrencyCode();
        }

        /// <summary>The lead's currency if it has one, else the workspace's.</summary>
        private string ResolveCurrency(string? leadCurrency) =>
            !string.IsNullOrWhiteSpace(leadCurrency)
                ? leadCurrency!
                : (_tenantService.GetCurrencyCode() ?? string.Empty);

        private async Task LoadStagesAsync()
        {
            if (Stages.Count > 0) return;
            try
            {
                Stages = await _stageService.GetAsync(activeOnly: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not load pipeline stages for the conversion form");
                Stages = new();
            }
        }

        /// <summary>
        /// True when the lead's status belongs to the tenant's QUALIFIED
        /// category, whatever they have called it. If the status list can't be
        /// loaded, fall back to letting the API decide rather than blocking a
        /// legitimate conversion on a failed lookup.
        /// </summary>
        private async Task<bool> IsQualifiedAsync(string? statusKey)
        {
            try
            {
                var statuses = await _statusService.GetAsync(selectableOnly: false);
                var status = statuses.FirstOrDefault(s => s.Key == statusKey);
                if (status == null) return false;
                return status.Category == LeadStatusCategory.Qualified;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load lead statuses; deferring the qualified check to the API");
                return true;
            }
        }

        private async Task<string> StatusNameAsync(string? statusKey)
        {
            if (string.IsNullOrEmpty(statusKey)) return "—";
            try
            {
                var statuses = await _statusService.GetAsync(selectableOnly: false);
                return statuses.FirstOrDefault(s => s.Key == statusKey)?.Name ?? statusKey;
            }
            catch
            {
                return statusKey;
            }
        }

        /// <summary>Re-fill everything the view needs after a failed post.</summary>
        private async Task LoadForDisplayAsync(Guid id)
        {
            try
            {
                LoadTenantContext();
                await LoadStagesAsync();

                var tenantId = _currentUserService.GetCurrentTenantId();
                var lead = await _leadService.GetByIdAsync(tenantId, id);
                if (lead == null) return;

                LeadId         = lead.Id;
                LeadName       = lead.FullName;
                LeadEmail      = lead.Email;
                LeadPhone      = lead.Phone;
                LeadCompany    = lead.CompanyName;
                ContactId      = lead.ContactId;
                EstimatedValue = lead.ExpectedValue;
                LeadStatusName = await StatusNameAsync(lead.Status);
                DealCurrency   = ResolveCurrency(lead.Currency);
                await LoadCarryOverAsync(lead);                         // 079
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload lead {LeadId} for display", id);
            }
        }

        private async Task<List<CustomFieldDefinitionDto>> TryGetFieldsAsync(string entityType)
        {
            try
            {
                return await _customFields.GetDefinitionsAsync(entityType, includeInactive: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load {Entity} custom fields for the conversion preview", entityType);
                return new List<CustomFieldDefinitionDto>();
            }
        }

        /// <summary>
        /// 079. What LeadFieldMapper will copy for this lead, worked out from
        /// the same three field lists. Never throws — on any failure the card
        /// is simply not shown.
        /// </summary>
        private async Task LoadCarryOverAsync(LeadDetailDto lead)
        {
            CarryOver = new();
            UnmappedValueCount = 0;
            RequiredDealFieldsNotFilled = new();

            try
            {
                var leadFields    = await _customFields.GetDefinitionsAsync(CustomFieldEntityTypes.Lead, includeInactive: true);
                // Each target list on its own: someone who may convert but not
                // read contacts still sees what lands on the deal.
                var contactFields = await TryGetFieldsAsync(CustomFieldEntityTypes.Contact);
                var dealFields    = await TryGetFieldsAsync(CustomFieldEntityTypes.Deal);

                var contactById = contactFields.Where(f => f.IsActive).ToDictionary(f => f.Id);
                var dealById    = dealFields.Where(f => f.IsActive).ToDictionary(f => f.Id);

                var culture    = CustomFieldFormatter.ResolveCulture(CultureName);
                var dateFormat = TenantCtx.GetDateFormat();
                var filledDeal = new HashSet<Guid>();

                foreach (var f in leadFields)
                {
                    if (!lead.CustomFieldValues.TryGetValue(f.Id, out var wire) || string.IsNullOrEmpty(wire))
                        continue;

                    string? toContact = null, toDeal = null;

                    if (f.MapToContactFieldId is { } c && contactById.TryGetValue(c, out var ct) && ct.FieldType == f.FieldType)
                        toContact = ct.Label;

                    if (f.MapToDealFieldId is { } d && dealById.TryGetValue(d, out var dt) && dt.FieldType == f.FieldType)
                    {
                        toDeal = dt.Label;
                        filledDeal.Add(dt.Id);
                    }

                    if (toContact is null && toDeal is null)
                    {
                        UnmappedValueCount++;
                        continue;
                    }

                    CarryOver.Add(new CarryOverItem(f.Label, CustomFieldFormatter.Display(f, wire, culture, dateFormat), toContact, toDeal));
                }

                RequiredDealFieldsNotFilled = dealFields
                    .Where(f => f.IsActive && f.IsRequired && !filledDeal.Contains(f.Id))
                    .OrderBy(f => f.SortOrder)
                    .Select(f => f.Label)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not work out the custom fields carried over for lead {LeadId}", lead.Id);
                CarryOver = new();
                UnmappedValueCount = 0;
                RequiredDealFieldsNotFilled = new();
            }
        }

        // ── view helpers ──────────────────────────────────────────────

        /// <summary>
        /// Symbol for an ISO code, from the app's one currency table (079 —
        /// note 4). Unknown codes come back as the code; no code at all
        /// falls back to the workspace's symbol.
        /// </summary>
        public string SymbolFor(string? code)
            => string.IsNullOrWhiteSpace(code) ? TenantCurrencySymbol : CurrencyConfiguration.GetCurrencySymbol(code);

        public string StageBadgeClass(StageCategory category) => category switch
        {
            StageCategory.Won  => "bg-success",
            StageCategory.Lost => "bg-danger",
            _                  => "bg-secondary"
        };
    }
}
