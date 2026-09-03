// =====================================================================
// EDIT COMPANY - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Companies/Edit.cshtml.cs
//
// ✅ MIGRATED to AuthorizedPageModel (was plain PageModel — had zero
// permission checks before this).
//
// TENANT FIXES (unchanged from before):
//   - ICurrentTenantService injected
//   - Country falls back to tenant country if blank on existing record
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
    public class EditModel : AuthorizedPageModel
    {
        private readonly ICompanyService       _companyService;
        private readonly ICountryService       _countryService;
        private readonly IMetaService          _metaService;
        private readonly ICurrentTenantService _tenantService;

        protected override string ModuleName => Modules.Companies;

        public EditModel(
            ICompanyService         companyService,
            ICountryService         countryService,
            IMetaService            metaService,
            ICurrentTenantService   tenantService,
            IAuthorizationService   authorizationService,
            ICurrentUserService     currentUserService,
            ILogger<EditModel>      logger)
            : base(authorizationService, currentUserService, logger)
        {
            _companyService = companyService;
            _countryService = countryService;
            _metaService    = metaService;
            _tenantService  = tenantService;
        }

        [BindProperty] public Guid Id { get; set; }
        [BindProperty] public InputModel Input { get; set; } = new();

        public SelectList CountryOptions  { get; set; } = new SelectList(Enumerable.Empty<SelectListItem>());
        public SelectList VerticalOptions { get; set; } = new SelectList(Enumerable.Empty<SelectListItem>());

        // ✅ TENANT: exposed to view
        public string TenantCountryCode  { get; private set; } = string.Empty;
        public string TenantCurrencyCode { get; private set; } = string.Empty;

        [TempData] public string? ErrorMessage { get; set; }

        public class InputModel
        {
            [Required(ErrorMessage = "Company name is required")]
            [StringLength(200)]
            public string Name { get; set; } = string.Empty;

            [Required(ErrorMessage = "Please select a vertical")]
            public string? Vertical { get; set; }

            [Required(ErrorMessage = "Please select a country")]
            [StringLength(5)]
            public string? Country { get; set; }

            [StringLength(64)]
            public string? TaxId { get; set; }
        }

        public async Task<IActionResult> OnGetAsync(Guid id)
        {
            // ✅ Check UPDATE permission
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                Id = id;
                TenantCountryCode  = _tenantService.GetCountryCode();
                TenantCurrencyCode = _tenantService.GetCurrencyCode();

                var tenantId = CurrentUserService.GetCurrentTenantId();
                var company  = await _companyService.GetByIdAsync(tenantId, id);

                Input = new InputModel
                {
                    Name     = company.Name,
                    Vertical = company.Vertical,
                    // ✅ TENANT: if country missing on old record, fall back to tenant default
                    Country  = !string.IsNullOrWhiteSpace(company.Country)
                                   ? company.Country
                                   : TenantCountryCode,
                    TaxId    = company.TaxId
                };

                await LoadDropdownsAsync();
                return Page();
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Company not found.";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading company {Id}", id);
                TempData["ErrorMessage"] = "Failed to load company. Please try again.";
                return RedirectToPage("./Index");
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            // ✅ Check UPDATE permission
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
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

                var dto = new UpdateCompanyDto
                {
                    Id        = Id,
                    TenantId  = tenantId,
                    Name      = Input.Name.Trim(),
                    Vertical  = Input.Vertical!,
                    Country   = Input.Country!,
                    TaxId     = Input.TaxId?.Trim(),
                    UpdatedBy = currentUser.FullName
                };

                await _companyService.UpdateAsync(Id, dto);
                TempData["SuccessMessage"] = "Company updated successfully!";
                return RedirectToPage("./Detail", new { id = Id });
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Company not found.";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error updating company {Id}", Id);
                ErrorMessage       = "Failed to update company. Please try again.";
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
