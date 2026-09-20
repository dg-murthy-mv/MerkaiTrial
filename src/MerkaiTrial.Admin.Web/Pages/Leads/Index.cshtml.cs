// =====================================================================
// LEADS INDEX - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Leads/Index.cshtml.cs
//
// COMPLETE FILE — replaces the existing one.
//
// STATUS TABS (this pass)
//   The Status dropdown and the fixed New/Working/Qualified/Converted cards
//   are replaced by one tab per tenant status, with counts. The cards were
//   hardcoded: a tenant that added "Site Visit Booked" got no card for it.
//
//   Tabs, left to right:
//     Active  — default. Every lead still being worked: Open + Qualified
//               categories. Hides Converted and Not-pursuing clutter.
//     <each tenant status in its SortOrder>  — retired ones only if they
//               still hold leads.
//     All
//
//   StatusFilter (in the URL, so it survives paging and bookmarks):
//     empty      → Active
//     "all"      → All
//     <key>      → that status
//
//   The API receives a comma-separated key list for Active
//   (status=New,Working,Qualified). GetLeadsPaginatedHandler must accept
//   that — see the snippet in the notes.
//
//   Counts come from /api/lead-statuses (LeadCount per status) — no new
//   endpoint.
//
// Earlier passes: tenant statuses (ILeadStatusService), StatusName helper,
// category-based badges, IsConverted delete guard, AuthorizedPageModel.
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

namespace MerkaiTrial.Admin.Web.Pages.Leads
{
    public class IndexModel : AuthorizedPageModel
    {
        public const string AllTab = "all";

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

        // Still needed: TotalLeads drives the quota bar and the Add Lead limit.
        public LeadStatsDto Stats { get; set; } = new LeadStatsDto(0, 0, 0, 0, 0, 0, 0, 0);

        /// <summary>All of this tenant's statuses, including retired and Converted.</summary>
        public List<LeadStatusDto> Statuses { get; private set; } = new();

        public string TenantCurrencySymbol { get; private set; } = string.Empty;
        public string TenantCurrency { get; private set; } = string.Empty;

        [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
        [BindProperty(SupportsGet = true)] public string? SearchTerm { get; set; }
        [BindProperty(SupportsGet = true)] public string? StatusFilter { get; set; }   // "", "all" or a status KEY
        [BindProperty(SupportsGet = true)] public string? AssignedToFilter { get; set; }

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

        public int TotalLeads => PaginatedLeads.TotalCount;

        // ── Tabs ──────────────────────────────────────────────────────────

        public bool IsActiveTab => string.IsNullOrEmpty(StatusFilter);
        public bool IsAllTab => string.Equals(StatusFilter, AllTab, StringComparison.OrdinalIgnoreCase);
        public bool IsStatusTab(string key) => StatusFilter == key;

        /// <summary>
        /// Statuses whose leads still need work: Open + Qualified categories.
        /// Retired ones included — a lead sitting in a retired status is still active.
        /// </summary>
        public IEnumerable<LeadStatusDto> ActiveStatuses => Statuses.Where(s =>
            s.Category == LeadStatusCategory.Open ||
            s.Category == LeadStatusCategory.Qualified);

        /// <summary>One tab per status, in the tenant's order. Retired only while they hold leads.</summary>
        public IEnumerable<LeadStatusDto> TabStatuses => Statuses
            .Where(s => s.IsActive || s.IsSystem || s.LeadCount > 0)
            .OrderBy(s => s.SortOrder);

        public int ActiveCount => ActiveStatuses.Sum(s => s.LeadCount);
        public int AllCount => Statuses.Sum(s => s.LeadCount);

        /// <summary>Heading for the empty state and page context.</summary>
        public string CurrentTabName =>
            IsActiveTab ? "Active" :
            IsAllTab    ? "All"    :
            StatusName(StatusFilter);

        public async Task<IActionResult> OnGetAsync()
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Read);
            if (permissionCheck != null) return permissionCheck;

            await InitializePermissionsAsync();

            // Statuses first: the tab decides which keys the list asks for.
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
                    status: ResolveStatusQuery(),
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

        /// <summary>
        /// Turns the selected tab into the API's status parameter.
        ///   All            → null (no filter)
        ///   Active         → "New,Working,Qualified" (comma-separated keys)
        ///   a status tab   → that key
        /// If statuses failed to load, Active falls back to All rather than
        /// showing an empty list that looks like "you have no leads".
        /// </summary>
        private string? ResolveStatusQuery()
        {
            if (IsAllTab) return null;

            if (IsActiveTab)
            {
                var keys = ActiveStatuses.Select(s => s.Key).ToList();
                return keys.Count == 0 ? null : string.Join(",", keys);
            }

            return StatusFilter;
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
                    return RedirectToPage(new { StatusFilter });
                }

                if (lead.IsConverted)
                {
                    TempData["ErrorMessage"] = "Converted leads cannot be deleted. Manage this record via the associated Deal.";
                    return RedirectToPage(new { StatusFilter });
                }

                await _leadService.DeleteAsync(tenantId, id);
                TempData["SuccessMessage"] = "Lead deleted successfully!";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting lead {Id}", id);
                TempData["ErrorMessage"] = "Failed to delete lead. Please try again.";
            }

            // Stay on the tab the user was on
            return RedirectToPage(new { StatusFilter });
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

        /// <summary>Use: @Model.StatusName(lead.Status). Falls back to the key.</summary>
        public string StatusName(string? key)
        {
            if (string.IsNullOrEmpty(key)) return "—";
            return Statuses.FirstOrDefault(s => s.Key == key)?.Name ?? key;
        }

        /// <summary>
        /// True when the key is the tenant's Converted (system) status.
        /// Use instead of lead.Status == "Converted" — the name is editable.
        /// </summary>
        public bool IsConvertedStatus(string? key) =>
            Statuses.FirstOrDefault(s => s.Key == key)?.Category == LeadStatusCategory.Converted;

        /// <summary>Badge colour by CATEGORY, so renamed/new statuses stay correct.</summary>
        public string GetStatusBadgeClass(string? key)
        {
            var status = Statuses.FirstOrDefault(s => s.Key == key);
            if (status == null) return "bg-dark";
            return CategoryBadgeClass(status.Category);
        }

        public static string CategoryBadgeClass(LeadStatusCategory category) => category switch
        {
            LeadStatusCategory.Qualified    => "bg-success",
            LeadStatusCategory.Disqualified => "bg-secondary",
            LeadStatusCategory.Converted    => "bg-warning text-dark",
            _                               => "bg-primary"   // Open
        };

        /// <summary>Small coloured dot for tab labels.</summary>
        public static string CategoryDotClass(LeadStatusCategory category) => category switch
        {
            LeadStatusCategory.Qualified    => "text-success",
            LeadStatusCategory.Disqualified => "text-secondary",
            LeadStatusCategory.Converted    => "text-warning",
            _                               => "text-primary"
        };

        public string FormatDate(DateTime utcDateTime)
            => _tenantService.FormatDate(utcDateTime);

        public string FormatCurrency(decimal amount)
            => _tenantService.FormatCurrency(amount);

        /// <summary>Whole-number money for compact list displays. Same shape as Pipeline/Index.</summary>
        public string FormatCurrency(decimal amount, int decimals)
            => _tenantService.FormatCurrency(amount, decimals);

        public string FormatDateTime(DateTime utcDateTime)
            => _tenantService.FormatDateTime(utcDateTime);
    }
}
