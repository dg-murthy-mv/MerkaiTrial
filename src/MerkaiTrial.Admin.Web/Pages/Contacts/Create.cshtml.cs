// =====================================================================
// CREATE CONTACT - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Contacts/Create.cshtml.cs
//
// ✅ MIGRATED to AuthorizedPageModel (was plain PageModel — had zero
// permission checks before this).
//
// FIXES (unchanged from before, Bug 1 + Tenant):
//   - ICompanyService removed; company dropdown via GetCompaniesLookupAsync
//   - ICurrentTenantService injected
//   - Input.Country defaults to tenant country code (IN/TH/PH/AE)
// =====================================================================

using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Services.Contacts;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Contacts
{
    public class CreateModel : AuthorizedPageModel
    {
        private readonly IContactService       _contactService;
        private readonly ICurrentTenantService _tenantService;

        protected override string ModuleName => Modules.Contacts;

        public CreateModel(
            IContactService         contactService,
            ICurrentTenantService   tenantService,
            IAuthorizationService   authorizationService,
            ICurrentUserService     currentUserService,
            ILogger<CreateModel>    logger)
            : base(authorizationService, currentUserService, logger)
        {
            _contactService = contactService;
            _tenantService  = tenantService;
        }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        public SelectList CompanyOptions { get; set; } = new SelectList(Enumerable.Empty<SelectListItem>());

        // ✅ TENANT: exposed to view for placeholder hints
        public string TenantCountryCode  { get; private set; } = string.Empty;
        public string TenantCurrencyCode { get; private set; } = string.Empty;

        [TempData]
        public string? ErrorMessage { get; set; }

        public class InputModel
        {
            public Guid? CompanyId { get; set; }

            [Required(ErrorMessage = "First name is required")]
            [StringLength(100)] public string FirstName { get; set; } = string.Empty;
            [StringLength(100)] public string? LastName { get; set; }
            [StringLength(100)] public string? JobTitle { get; set; }

            [EmailAddress][StringLength(320)] public string? Email { get; set; }
            [StringLength(32)] public string? Phone   { get; set; }
            [StringLength(32)] public string? Mobile  { get; set; }
            [StringLength(500)] public string? Address { get; set; }
            [StringLength(100)] public string? City    { get; set; }

            [StringLength(5)]
            public string? Country { get; set; }  // ✅ TENANT: defaulted to tenant country on GET

            [StringLength(20)] public string? PostalCode { get; set; }
            public string? Notes    { get; set; }
            public bool IsPrimary   { get; set; }
        }

        public async Task<IActionResult> OnGetAsync(Guid? companyId = null)
        {
            // ✅ Check CREATE permission
            var permissionCheck = await ValidatePermissionAsync(Actions.Create);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                TenantCountryCode  = _tenantService.GetCountryCode();
                TenantCurrencyCode = _tenantService.GetCurrencyCode();

                // ✅ TENANT: pre-fill country with tenant default
                Input.Country = TenantCountryCode;

                if (companyId.HasValue)
                    Input.CompanyId = companyId;

                await LoadDropdownsAsync();
                return Page();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading Create Contact form");
                ErrorMessage = "Failed to load form. Please try again.";
                return Page();
            }
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

                var currentUser = await CurrentUserService.GetCurrentUserAsync();

                var dto = new CreateContactDto
                {
                    TenantId   = CurrentUserService.GetCurrentTenantId(),
                    CompanyId  = Input.CompanyId,
                    FirstName  = Input.FirstName,
                    LastName   = Input.LastName,
                    JobTitle   = Input.JobTitle,
                    Email      = Input.Email,
                    Phone      = Input.Phone,
                    Mobile     = Input.Mobile,
                    Address    = Input.Address,
                    City       = Input.City,
                    Country    = Input.Country,
                    PostalCode = Input.PostalCode,
                    Notes      = Input.Notes,
                    IsPrimary  = Input.IsPrimary,
                    CreatedBy  = currentUser.FullName
                };

                var contact = await _contactService.CreateAsync(dto);
                TempData["SuccessMessage"] = $"Contact '{contact.DisplayName}' created successfully!";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error creating contact");
                ErrorMessage       = "Failed to create contact. Please try again.";
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
                var tenantId  = CurrentUserService.GetCurrentTenantId();
                var companies = await _contactService.GetCompaniesLookupAsync(tenantId);
                CompanyOptions = new SelectList(
                    companies.OrderBy(c => c.Name),
                    nameof(CompanyListItem.Id),
                    nameof(CompanyListItem.Name),
                    Input.CompanyId);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load companies dropdown");
                CompanyOptions = new SelectList(Enumerable.Empty<SelectListItem>());
            }
        }
    }
}
