// =====================================================================
// EDIT CONTACT - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Contacts/Edit.cshtml.cs
//
// ✅ MIGRATED to AuthorizedPageModel (was plain PageModel — had zero
// permission checks before this).
//
// FIXES (unchanged from before, Bug 1 + Tenant):
//   - ICompanyService removed; company dropdown via GetCompaniesLookupAsync
//   - ICurrentTenantService injected
//   - TenantCountryCode exposed (for view placeholder/hint)
//   - Country field preserved from saved record on load
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
    public class EditModel : AuthorizedPageModel
    {
        private readonly IContactService       _contactService;
        private readonly ICurrentTenantService _tenantService;

        protected override string ModuleName => Modules.Contacts;

        public EditModel(
            IContactService         contactService,
            ICurrentTenantService   tenantService,
            IAuthorizationService   authorizationService,
            ICurrentUserService     currentUserService,
            ILogger<EditModel>      logger)
            : base(authorizationService, currentUserService, logger)
        {
            _contactService = contactService;
            _tenantService  = tenantService;
        }

        [BindProperty] public Guid Id { get; set; }
        [BindProperty] public InputModel Input { get; set; } = new();

        public SelectList CompanyOptions { get; set; } = new SelectList(Enumerable.Empty<SelectListItem>());

        // ✅ TENANT: exposed to view
        public string TenantCountryCode  { get; private set; } = string.Empty;
        public string TenantCurrencyCode { get; private set; } = string.Empty;

        [TempData] public string? ErrorMessage { get; set; }

        public class InputModel
        {
            public Guid? CompanyId { get; set; }

            [Required(ErrorMessage = "First name is required")]
            [StringLength(100)] public string FirstName { get; set; } = string.Empty;
            [StringLength(100)] public string? LastName { get; set; }
            [StringLength(100)] public string? JobTitle { get; set; }

            [EmailAddress][StringLength(320)] public string? Email { get; set; }
            [StringLength(32)] public string? Phone    { get; set; }
            [StringLength(32)] public string? Mobile   { get; set; }
            [StringLength(500)] public string? Address { get; set; }
            [StringLength(100)] public string? City    { get; set; }

            [StringLength(5)]
            public string? Country { get; set; }   // preserved from DB on load; tenant default if blank

            [StringLength(20)] public string? PostalCode { get; set; }
            public string? Notes  { get; set; }
            public bool IsPrimary { get; set; }
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
                var contact  = await _contactService.GetByIdAsync(tenantId, id);

                Input = new InputModel
                {
                    CompanyId  = contact.CompanyId,
                    FirstName  = contact.FirstName,
                    LastName   = contact.LastName,
                    JobTitle   = contact.JobTitle,
                    Email      = contact.Email,
                    Phone      = contact.Phone,
                    Mobile     = contact.Mobile,
                    Address    = contact.Address,
                    City       = contact.City,
                    // ✅ TENANT: if contact has no country saved yet, default to tenant country
                    Country    = !string.IsNullOrWhiteSpace(contact.Country)
                                    ? contact.Country
                                    : TenantCountryCode,
                    PostalCode = contact.PostalCode,
                    Notes      = contact.Notes,
                    IsPrimary  = contact.IsPrimary
                };

                await LoadDropdownsAsync();
                return Page();
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Contact not found.";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading contact {Id}", id);
                TempData["ErrorMessage"] = "Failed to load contact. Please try again.";
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

                var currentUser = await CurrentUserService.GetCurrentUserAsync();

                var dto = new UpdateContactDto
                {
                    Id         = Id,
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
                    UpdatedBy  = currentUser.FullName
                };

                await _contactService.UpdateAsync(Id, dto);
                TempData["SuccessMessage"] = "Contact updated successfully!";
                return RedirectToPage("./Detail", new { id = Id });
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Contact not found.";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error updating contact {Id}", Id);
                ErrorMessage       = "Failed to update contact. Please try again.";
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
