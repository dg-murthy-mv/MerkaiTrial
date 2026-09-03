// =====================================================================
// LEADS INDEX - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Leads/Index.cshtml.cs
//
// MIGRATION (this pass):
//   1. Base class AppPageModel -> AuthorizedPageModel
//   2. OnGetAsync now enforces Leads.Read via ValidatePermissionAsync
//      before loading any data (previously: no permission check on GET at all)
//   3. InitializePermissionsAsync() called so the view can gate
//      Add/Edit/Delete buttons via Model.CanCreate/CanUpdate/CanDelete
//   4. OnPostDeleteAsync now uses ValidatePermissionAsync(Actions.Delete)
//      instead of the old hand-rolled CanDelete("Leads") claim check
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Leads;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MerkaiTrial.Admin.Web.Pages.Leads
{
    public class IndexModel : AuthorizedPageModel
    {
        private readonly ILeadService _leadService;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly ILogger<IndexModel> _logger;

        protected override string ModuleName => Modules.Leads;

        public IndexModel(
            ILeadService leadService,
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            ILogger<IndexModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _leadService = leadService;
            _currentUserService = currentUserService;
            _tenantService = tenantService;
            _logger = logger;
        }

        public PaginatedResult<LeadListItem> PaginatedLeads { get; set; }
            = new PaginatedResult<LeadListItem>();

        public LeadStatsDto Stats { get; set; } = new LeadStatsDto(0, 0, 0, 0, 0, 0, 0, 0);

        // Tenant context — use these in the view, never hardcode.
        // ✅ Defaults were "₹" / "INR". The comment said "never hardcode" while the
        // initialisers did exactly that: on any failure path that returns before
        // OnGetAsync sets them, a Thai tenant would render Indian currency. Empty
        // is visibly wrong rather than plausibly wrong.
        public string TenantCurrencySymbol { get; private set; } = string.Empty;
        public string TenantCurrency { get; private set; } = string.Empty;

        [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
        [BindProperty(SupportsGet = true)] public string? SearchTerm { get; set; }
        [BindProperty(SupportsGet = true)] public string? StatusFilter { get; set; }
        [BindProperty(SupportsGet = true)] public string? AssignedToFilter { get; set; }

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

        public int TotalLeads => PaginatedLeads.TotalCount;

        public SelectList StatusOptions => new SelectList(
            Enum.GetValues<LeadStatus>().Select(s => new SelectListItem
            {
                Value = s.ToString(),
                Text = s.ToString(),
                Selected = s.ToString() == StatusFilter
            }),
            "Value", "Text", StatusFilter);

        public async Task<IActionResult> OnGetAsync()
        {
            // ✅ Check READ permission before loading anything
            var permissionCheck = await ValidatePermissionAsync(Actions.Read);
            if (permissionCheck != null) return permissionCheck;

            // Initialize permissions for UI buttons (Add/Edit/Delete gating)
            await InitializePermissionsAsync();

            try
            {
                // Load tenant context first — needed by view helpers
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
            // ✅ Check DELETE permission via the real (case-fixed) pipeline
            var permissionCheck = await ValidatePermissionAsync(Actions.Delete);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();

                // Guard: fetch lead first, block delete if Converted
                var lead = await _leadService.GetByIdAsync(tenantId, id);
                if (lead == null)
                {
                    TempData["ErrorMessage"] = "Lead not found.";
                    return RedirectToPage();
                }
                if (lead.Status == "Converted")
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

        // ── View helpers — call these in cshtml, never format inline ──

        /// <summary>Use: @Model.FormatDate(item.CreatedAtUtc)</summary>
        public string FormatDate(DateTime utcDateTime)
            => _tenantService.FormatDate(utcDateTime);

        /// <summary>Use: @Model.FormatCurrency(item.EstimatedValue)</summary>
        public string FormatCurrency(decimal amount)
            => _tenantService.FormatCurrency(amount);

        /// <summary>
        /// Use: @Model.FormatCurrency(item.EstimatedValue, 0) — whole-number money
        /// for compact list/tile displays, without hardcoding "N0" in the view.
        ///
        /// ✅ Added because the Leads list previously rendered
        ///      @@lead.Currency @@lead.EstimatedValue.ToString("N0")
        /// which printed the ISO CODE instead of the symbol ("INR 1,40,000") and,
        /// worse, used ToString("N0") with NO culture — i.e. the SERVER's
        /// CurrentCulture. On an en-IN dev box that is the same leak that put
        /// Indian lakh grouping on Thai tenants in Pipeline/Index.
        ///
        /// Same shape as Pipeline/Index.cshtml.cs — keep the two in step.
        /// </summary>
        public string FormatCurrency(decimal amount, int decimals)
            => _tenantService.FormatCurrency(amount, decimals);

        /// <summary>Use: @Model.FormatDateTime(item.CreatedAtUtc)</summary>
        public string FormatDateTime(DateTime utcDateTime)
            => _tenantService.FormatDateTime(utcDateTime);

        public string GetStatusBadgeClass(string status) => status switch
        {
            "New" => "bg-primary",
            "Contacted" => "bg-info",
            "Qualified" => "bg-success",
            "Unqualified" => "bg-secondary",
            "Converted" => "bg-warning text-dark",
            _ => "bg-dark"
        };
    }
}
