// =====================================================================
// LEADS INDEX - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Leads/Index.cshtml.cs
//
// COMPLETE FILE — replaces the existing one.
//
// LEAD STATUSES (this pass):
//   1. Status filter options come from the tenant's lead statuses
//      (ILeadStatusService), not Enum.GetValues<LeadStatus>(). Value = Key
//      (what's stored on Lead.Status), Text = Name (what the tenant calls it).
//      Retired and system (Converted) statuses are INCLUDED — you still need
//      to filter old leads sitting in a retired status.
//   2. LeadListItem.Status is now the KEY. The view must show
//      @Model.StatusName(lead.Status), never lead.Status directly, or a tenant
//      that renamed "Working" to "กำลังติดต่อ" still sees "Working".
//   3. Badge colour is decided by CATEGORY, not name — renaming a status
//      never breaks its colour, and a new tenant status gets the right one.
//   4. Delete guard uses lead.IsConverted instead of Status == "Converted"
//      (the Converted key's display name is tenant-editable).
//   5. `using MerkaiTrial.Domain.Enums` removed — the LeadStatus enum is gone.
//
// Earlier passes: AuthorizedPageModel, Leads.Read on GET, Leads.Delete on POST.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Leads;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.LeadStatuses;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MerkaiTrial.Admin.Web.Pages.Leads
{
    public class IndexModel : AuthorizedPageModel
    {
        private readonly ILeadService _leadService;
        private readonly ILeadStatusService _statusService;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly ILogger<IndexModel> _logger;

        protected override string ModuleName => Modules.Leads;

        public IndexModel(
            ILeadService leadService,
            ILeadStatusService statusService,
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            ILogger<IndexModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _leadService = leadService;
            _statusService = statusService;
            _currentUserService = currentUserService;
            _tenantService = tenantService;
            _logger = logger;
        }

        public PaginatedResult<LeadListItem> PaginatedLeads { get; set; }
            = new PaginatedResult<LeadListItem>();

        public LeadStatsDto Stats { get; set; } = new LeadStatsDto(0, 0, 0, 0, 0, 0, 0, 0);

        /// <summary>
        /// All of this tenant's lead statuses (including retired and Converted),
        /// used for the filter dropdown, display names and badge colours.
        /// </summary>
        public List<LeadStatusDto> Statuses { get; private set; } = new();

        // Tenant context — use these in the view, never hardcode.
        public string TenantCurrencySymbol { get; private set; } = string.Empty;
        public string TenantCurrency { get; private set; } = string.Empty;

        [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
        [BindProperty(SupportsGet = true)] public string? SearchTerm { get; set; }
        [BindProperty(SupportsGet = true)] public string? StatusFilter { get; set; }   // status KEY
        [BindProperty(SupportsGet = true)] public string? AssignedToFilter { get; set; }

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

        public int TotalLeads => PaginatedLeads.TotalCount;

        public SelectList StatusOptions => new SelectList(
            Statuses
                .OrderBy(s => s.SortOrder)
                .Select(s => new SelectListItem
                {
                    Value = s.Key,
                    Text = s.IsActive ? s.Name : $"{s.Name} (retired)",
                    Selected = s.Key == StatusFilter
                }),
            "Value", "Text", StatusFilter);

        public async Task<IActionResult> OnGetAsync()
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Read);
            if (permissionCheck != null) return permissionCheck;

            await InitializePermissionsAsync();

            // Statuses first and outside the main try: a failure here must not
            // blank the lead list — the helpers fall back to showing the key.
            await LoadStatusesAsync();

            try
            {
                TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
                TenantCurrency = _tenantService.GetCurrencyCode();

                var tenantId = _currentUserService.GetCurrentTenantId();

                PaginatedLeads = await _leadService.GetPaginatedAsync(
                    tenantId: tenantId,
                    pageNumber: PageNumber,
                    pageSize: 10,
                    searchTerm: SearchTerm,
                    status: StatusFilter,
                    assignedTo: AssignedToFilter
                );

                try
                {
                    Stats = await _leadService.GetStatsAsync(tenantId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not load lead stats");
                }

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading leads");
                ErrorMessage = "Failed to load leads. Please try again.";
                PaginatedLeads = new PaginatedResult<LeadListItem>
                {
                    Items = new List<LeadListItem>(),
                    Page = 1,
                    PageSize = 10,
                    TotalCount = 0
                };
                return Page();
            }
        }

        public async Task<IActionResult> OnPostDeleteAsync(Guid id)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Delete);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();

                var lead = await _leadService.GetByIdAsync(tenantId, id);
                if (lead == null)
                {
                    TempData["ErrorMessage"] = "Lead not found.";
                    return RedirectToPage();
                }

                // By flag, not by name — the Converted status can be renamed.
                if (lead.IsConverted)
                {
                    TempData["ErrorMessage"] = "Converted leads cannot be deleted. Manage this record via the associated Deal.";
                    return RedirectToPage();
                }

                await _leadService.DeleteAsync(tenantId, id);
                TempData["SuccessMessage"] = "Lead deleted successfully!";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting lead {Id}", id);
                TempData["ErrorMessage"] = "Failed to delete lead. Please try again.";
            }
            return RedirectToPage();
        }

        private async Task LoadStatusesAsync()
        {
            try
            {
                Statuses = await _statusService.GetAsync(selectableOnly: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load lead statuses");
                Statuses = new();
            }
        }

        // ── View helpers — call these in cshtml, never format inline ──

        /// <summary>
        /// Use: @Model.StatusName(lead.Status)
        /// Lead.Status holds the KEY; this returns the tenant's display name.
        /// Falls back to the key if statuses failed to load.
        /// </summary>
        public string StatusName(string? key)
        {
            if (string.IsNullOrEmpty(key)) return "—";
            return Statuses.FirstOrDefault(s => s.Key == key)?.Name ?? key;
        }

        /// <summary>
        /// Use: class="badge @Model.GetStatusBadgeClass(lead.Status)"
        /// Colour follows the status CATEGORY, so renamed or tenant-added
        /// statuses still get the right colour.
        /// </summary>
        public string GetStatusBadgeClass(string? key)
        {
            var status = Statuses.FirstOrDefault(s => s.Key == key);
            if (status == null) return "bg-dark";

            return status.Category switch
            {
                LeadStatusCategory.Qualified => "bg-success",
                LeadStatusCategory.Disqualified => "bg-secondary",
                LeadStatusCategory.Converted => "bg-warning text-dark",
                _ => "bg-primary"   // Open
            };
        }

        /// <summary>Use: @Model.FormatDate(item.CreatedAtUtc)</summary>
        public string FormatDate(DateTime utcDateTime)
            => _tenantService.FormatDate(utcDateTime);

        /// <summary>Use: @Model.FormatCurrency(item.EstimatedValue)</summary>
        public string FormatCurrency(decimal amount)
            => _tenantService.FormatCurrency(amount);

        /// <summary>
        /// Use: @Model.FormatCurrency(item.EstimatedValue, 0) — whole-number money
        /// for compact list/tile displays. Same shape as Pipeline/Index.cshtml.cs.
        /// </summary>
        public string FormatCurrency(decimal amount, int decimals)
            => _tenantService.FormatCurrency(amount, decimals);

        /// <summary>Use: @Model.FormatDateTime(item.CreatedAtUtc)</summary>
        public string FormatDateTime(DateTime utcDateTime)
            => _tenantService.FormatDateTime(utcDateTime);
    }
}
