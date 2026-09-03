// =====================================================================
// CREATE COMPANY - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Companies/Create.cshtml.cs
//
// ✅ MIGRATED to AuthorizedPageModel (was plain PageModel — had zero
// permission checks before this).
//
// TENANT FIXES (unchanged from before):
//   - ICurrentTenantService injected
//   - Input.Country defaults to tenant country code on GET
//   - TenantCountryCode / TenantCurrencyCode exposed to view
// =====================================================================

using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Services.Companies;
using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Admin.Web.Services.Meta;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Meta;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Companies
{
    public class CreateModel : AuthorizedPageModel
    {
        private readonly ICompanyService       _companyService;
        private readonly ICountryService       _countryService;
        private readonly IMetaService          _metaService;
        private readonly ICurrentTenantService _tenantService;

        protected override string ModuleName => Modules.Companies;

        public CreateModel(
            ICompanyService         companyService,
            ICountryService         countryService,
            IMetaService            metaService,
            ICurrentTenantService   tenantService,
            IAuthorizationService   authorizationService,
            ICurrentUserService     currentUserService,
            ILogger<CreateModel>    logger)
            : base(authorizationService, currentUserService, logger)
        {
            _companyService = companyService;
            _countryService = countryService;
            _metaService    = metaService;
            _tenantService  = tenantService;
        }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        public SelectList CountryOptions  { get; set; } = new SelectList(Enumerable.Empty<SelectListItem>());
        public SelectList VerticalOptions { get; set; } = new SelectList(Enumerable.Empty<SelectListItem>());

        // ✅ TENANT: exposed to view
        public string TenantCountryCode  { get; private set; } = string.Empty;
        public string TenantCurrencyCode { get; private set; } = string.Empty;

        [TempData]
        public string? ErrorMessage { get; set; }

        public class InputModel
        {
            [Required(ErrorMessage = "Company name is required")]
            [StringLength(200)]
            public string Name { get; set; } = string.Empty;

            [Required(ErrorMessage = "Please select a vertical")]
            public string? Vertical { get; set; }

            [Required(ErrorMessage = "Please select a country")]
            [StringLength(5)]
            public string? Country { get; set; }  // ✅ TENANT: defaulted in OnGetAsync

            [StringLength(64)]
            public string? TaxId { get; set; }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            // ✅ Check CREATE permission
            var permissionCheck = await ValidatePermissionAsync(Actions.Create);
            if (permissionCheck != null) return permissionCheck;

            // ✅ TENANT: pre-fill country with tenant default
            TenantCountryCode  = _tenantService.GetCountryCode();
            TenantCurrencyCode = _tenantService.GetCurrencyCode();
            Input.Country      = TenantCountryCode;

            await LoadDropdownsAsync();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            // ✅ Check CREATE permission
            var permissionCheck = await ValidatePermissionAsync(Actions.Create);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                if (!ModelState.IsValid)
                {
                    TenantCountryCode  = _tenantService.GetCountryCode();
                    TenantCurrencyCode = _tenantService.GetCurrencyCode();
                    await LoadDropdownsAsync();
                    return Page();
                }

                var tenantId    = CurrentUserService.GetCurrentTenantId();
                var currentUser = await CurrentUserService.GetCurrentUserAsync();

                var dto = new CreateCompanyDto
                {
                    TenantId  = tenantId,
                    Name      = Input.Name.Trim(),
                    Vertical  = Input.Vertical!,
                    Country   = Input.Country!,
                    TaxId     = Input.TaxId?.Trim(),
                    CreatedBy = currentUser.FullName
                };

                var company = await _companyService.CreateAsync(dto);
                TempData["SuccessMessage"] = $"Company '{company.Name}' created successfully!";
                return RedirectToPage("./Index");
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                TenantCountryCode  = _tenantService.GetCountryCode();
                TenantCurrencyCode = _tenantService.GetCurrencyCode();
                await LoadDropdownsAsync();
                return Page();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error creating company");
                ErrorMessage = "Failed to create company. Please try again.";
                TenantCountryCode  = _tenantService.GetCountryCode();
                TenantCurrencyCode = _tenantService.GetCurrencyCode();
                await LoadDropdownsAsync();
                return Page();
            }
        }

        private async Task LoadDropdownsAsync()
        {
            try
            {
                var countries = await _countryService.GetActiveAsync();
                CountryOptions = new SelectList(
                    countries.OrderBy(c => c.Name),
                    nameof(CountryListItem.Code),
                    nameof(CountryListItem.Name),
                    Input.Country);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load countries");
                CountryOptions = new SelectList(Enumerable.Empty<SelectListItem>());
            }

            try
            {
                var verticals = await _metaService.GetVerticalsAsync();
                VerticalOptions = new SelectList(
                    verticals.OrderBy(v => v.Name),
                    nameof(VerticalDto.Name),
                    nameof(VerticalDto.Name),
                    Input.Vertical);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load verticals");
                VerticalOptions = new SelectList(Enumerable.Empty<SelectListItem>());
            }
        }
    }
}
