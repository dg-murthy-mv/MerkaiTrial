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
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Leads;
using MerkaiTrial.Admin.Web.Services.Meta;
using MerkaiTrial.Application.Authorization;
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
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly ILogger<EditModel> _logger;
        private readonly IMetaService _metaService;

        protected override string ModuleName => Modules.Leads;

        public EditModel(
            ILeadService leadService,
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            IMetaService metaService,
            ILogger<EditModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _leadService = leadService;
            _currentUserService = currentUserService;
            _tenantService = tenantService;
            _metaService = metaService;
            _logger = logger;
        }

        [BindProperty]
        public Guid Id { get; set; }

        [BindProperty]
        public InputModel Input { get; set; } = new();
        public string CountryCodesJson { get; set; } = "{}";
        public List<SelectListItem> ChannelOptions { get; set; } = new();
        public List<SelectListItem> SourceOptions { get; set; } = new();
        public List<SelectListItem> SalesTeamOptions { get; set; } = new();
        public List<SelectListItem> CountryOptions { get; set; } = new();
        public List<SelectListItem> CurrencyOptions { get; set; } = new();
        public List<SelectListItem> VerticalOptions { get; set; } = new();

        public string TenantCurrencySymbol { get; private set; } = "₹";
        public string TenantCurrency { get; private set; } = "INR";

        /// <summary>(031) The workspace's ISO country code, for the country default.</summary>
        public string TenantCountryCode { get; private set; } = "IN";

        [TempData] public string? ErrorMessage { get; set; }

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

                await LoadDropdownsAsync();
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
            try
            {
                // Defense in depth — see same note in Leads/Create.cshtml.cs
                var permissionCheck = await ValidatePermissionAsync(Actions.Update);
                if (permissionCheck != null) return permissionCheck;

                await InitializePermissionsAsync();
                LoadTenantContext();

                if (!ModelState.IsValid)
                {
                    await LoadDropdownsAsync();
                    return Page();
                }

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
                );

                await _leadService.UpdateAsync(dto);

                TempData["SuccessMessage"] = "Lead updated successfully!";
                return RedirectToPage("./Detail", new { id = Id });
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Lead not found.";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating lead {Id}", Id);
                ErrorMessage = "Failed to update lead. Please try again.";
                LoadTenantContext();
                await LoadDropdownsAsync();
                return Page();
            }
        }

        private void LoadTenantContext()
        {
            TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
            TenantCurrency       = _tenantService.GetCurrencyCode();
            TenantCountryCode    = _tenantService.GetCountryCode();
        }

        /// <summary>
        /// (031) Symbol for an ISO code, so the value box carries the LEAD's
        /// currency rather than the workspace's. An unknown code comes back as
        /// the code — printing ₹ beside a dollar figure is worse than printing
        /// "USD".
        /// </summary>
        public string CurrencySymbolFor(string? code) => (code ?? string.Empty).ToUpperInvariant() switch
        {
            "INR" => "₹",
            "THB" => "฿",
            "PHP" => "₱",
            "AED" => "د.إ",
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            ""    => TenantCurrencySymbol,
            _     => code!.ToUpperInvariant()
        };

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
                    Selected = string.Equals(c.Code, Input.CurrencyId, StringComparison.OrdinalIgnoreCase)
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
