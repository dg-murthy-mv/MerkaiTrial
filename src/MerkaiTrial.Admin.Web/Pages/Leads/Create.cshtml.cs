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
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Leads;
using MerkaiTrial.Admin.Web.Services.Meta;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Meta;
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

        protected override string ModuleName => Modules.Leads;

        public CreateModel(
            ILeadService leadService,
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            IMetaService metaService,
            ILogger<CreateModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _leadService = leadService;
            _currentUserService = currentUserService;
            _tenantService = tenantService;
            _metaService = metaService;
            _logger = logger;
        }

        [BindProperty]
        public InputModel Input { get; set; } = new();
        public string CountryCodesJson { get; set; } = "{}";

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

        public async Task<IActionResult> OnGetAsync()
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

                // Set tenant defaults on the form — user can override
                Input.CurrencyId = TenantCurrency;
                // CountryId will be selected via dropdown match on TenantCountryCode

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
                );

                var lead = await _leadService.CreateAsync(dto);

                TempData["SuccessMessage"] = $"Lead '{lead.FullName}' created successfully!";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating lead");
                ErrorMessage = $"Failed to create lead: {ex.Message}";
                LoadTenantContext();
                await LoadDropdownsAsync();
                return Page();
            }
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
