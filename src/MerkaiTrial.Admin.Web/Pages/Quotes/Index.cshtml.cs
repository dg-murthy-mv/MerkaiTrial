// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Quotes/Index.cshtml.cs
// FIXES:
//   ✅ ICurrentTenantService injected — tenant currency/symbol/dates
//   ✅ FormatDate() / FormatCurrency() helpers for views
//   ✅ TenantCurrencySymbol / TenantCurrencyCode exposed as properties
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Admin.Web.Pages.Quotes
{
    public class IndexModel : AuthorizedPageModel
    {
        private readonly IQuoteService            _quoteService;
        private readonly ICurrentUserService      _currentUserService;
        private readonly ICurrentTenantService    _tenantService;
        private readonly ILogger<IndexModel>      _logger;

        protected override string ModuleName => Modules.Quotes;

        public IndexModel(
            IQuoteService         quoteService,
            ICurrentUserService   currentUserService,
            ICurrentTenantService tenantService,
            IAuthorizationService authorizationService,
            ILogger<IndexModel>   logger)
            : base(authorizationService, currentUserService, logger)
        {
            _quoteService       = quoteService;
            _currentUserService = currentUserService;
            _tenantService      = tenantService;
            _logger             = logger;
        }

        // ── Data ──────────────────────────────────────────────────────
        public List<QuoteListItem>  Quotes     { get; set; } = new();
        public QuoteStatisticsDto   Statistics { get; set; } = new();

        // ── Tenant context (use in view, never hardcode ₹ / $ / INR) ─
        public string TenantCurrencySymbol { get; private set; } = string.Empty;
        public string TenantCurrencyCode   { get; private set; } = string.Empty;

        // ── Filters ───────────────────────────────────────────────────
        [BindProperty(SupportsGet = true)] public string?   StatusFilter { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? FromDate     { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? ToDate       { get; set; }

        [TempData] public string? ErrorMessage   { get; set; }
        [TempData] public string? SuccessMessage { get; set; }

        // ── GET ───────────────────────────────────────────────────────
        public async Task<IActionResult> OnGet()
        {
            var check = await ValidatePermissionAsync(Actions.Read);
            if (check != null) return check;

            await InitializePermissionsAsync();

            try
            {
                // ✅ Load tenant context before rendering
                TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
                TenantCurrencyCode   = _tenantService.GetCurrencyCode();

                var tenantId = _currentUserService.GetCurrentTenantId();

                Statistics = await _quoteService.GetStatisticsAsync(tenantId);

                Quotes = await _quoteService.GetAllAsync(
                    tenantId, null, StatusFilter, FromDate, ToDate);

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load quotes");
                ErrorMessage = "Failed to load quotes. Please try again.";
                return Page();
            }
        }

        // ── DELETE ────────────────────────────────────────────────────
        public async Task<IActionResult> OnPostDelete(Guid quoteId)
        {
            try
            {
                var check = await ValidatePermissionAsync(Actions.Delete);
                if (check != null) return check;

                var tenantId = _currentUserService.GetCurrentTenantId();
                await _quoteService.DeleteAsync(tenantId, quoteId);
                SuccessMessage = "Quote deleted successfully!";
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete quote {QuoteId}", quoteId);
                ErrorMessage = "Failed to delete quote. Please try again.";
                return RedirectToPage();
            }
        }

        // ── UPDATE STATUS ─────────────────────────────────────────────
        public async Task<IActionResult> OnPostUpdateStatus(Guid quoteId, string status)
        {
            try
            {
                var check = await ValidatePermissionAsync(Actions.Update);
                if (check != null) return check;

                var tenantId = _currentUserService.GetCurrentTenantId();
                await _quoteService.UpdateStatusAsync(tenantId, quoteId, status);
                SuccessMessage = $"Quote status updated to {status}!";
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update quote status {QuoteId}", quoteId);
                ErrorMessage = "Failed to update quote status. Please try again.";
                return RedirectToPage();
            }
        }

        // ── VIEW HELPERS ──────────────────────────────────────────────

        /// <summary>UTC → tenant local date (e.g. 16/03/2026 for India)</summary>
        public string FormatDate(DateTime utcDate)
            => _tenantService.FormatDate(utcDate);

        /// <summary>UTC → tenant local date + time</summary>
        public string FormatDateTime(DateTime utcDate)
            => _tenantService.FormatDateTime(utcDate);

        /// <summary>Amount with tenant currency symbol e.g. ₹9,408.00</summary>
        public string FormatCurrency(decimal amount)
            => _tenantService.FormatCurrency(amount);

        /// <summary>Currency symbol for a specific code e.g. "PHP" → "₱"</summary>
        public string GetSymbol(string? code) => code switch
        {
            "INR" => "₹",
            "THB" => "฿",
            "PHP" => "₱",
            "AED" => "د.إ",
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            _     => _tenantService.GetCurrencySymbol()
        };

        public string GetStatusBadgeClass(string status) => status switch
        {
            "Draft"    => "bg-secondary",
            "Sent"     => "bg-primary",
            "Viewed"   => "bg-info",
            "Accepted" => "bg-success",
            "Rejected" => "bg-danger",
            "Expired"  => "bg-warning",
            "Revised"  => "bg-dark",
            _          => "bg-secondary"
        };

        public List<QuoteListItem> GetQuotesByStatus(string status)
            => Quotes.Where(q => q.Status == status).ToList();
    }
}
