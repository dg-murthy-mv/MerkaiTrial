// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Invoices/Index.cshtml.cs
// Invoice Index Page Backend - List with filters and statistics
//
// ✅ SESSION 5 — PERMISSION MIGRATION
//   1. AppPageModel  →  AuthorizedPageModel   (ModuleName = Modules.Invoices)
//   2. OnGet was UNGATED — now invoices.read, plus InitializePermissionsAsync()
//      so the Can* properties the view reads are actually populated.
//   3. The two existing gates were already on the CORRECT module (unlike
//      Detail.cshtml). They only change shape: CanDelete("Invoices") /
//      CanUpdate("Invoices") were AppPageModel METHODS with a case-SENSITIVE
//      claim read; they are now ValidatePermissionAsync(...) calls which route
//      through PermissionHandler (case-insensitive, and logged).
//   4. ⚠️ IMPORTANT PATTERN: POST handlers must use the ...Async() methods /
//      ValidatePermissionAsync, NEVER the CanCreate/CanUpdate/CanDelete
//      PROPERTIES. Those properties are only populated by
//      InitializePermissionsAsync(), which runs in OnGet. In a POST they are
//      all false, so a property-based gate would silently refuse everything.
//      (Properties in views, Async in handlers.)
//
//   ⚠️ DESIGN NOTE unchanged from before: OnPostUpdateStatus auto-transitions
//   the linked Deal to Won with no deals.update check. Treated as a
//   system-level consequence of a legitimate invoice action. Not exploitable
//   under the current matrix. Revisit if reps get invoices.update without
//   deals.update.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Deals;        // injected for deal transition
using MerkaiTrial.Admin.Web.Services.Invoices;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MerkaiTrial.Admin.Web.Pages.Invoices
{
    public class IndexModel : AuthorizedPageModel
    {
        private readonly IInvoiceService _invoiceService;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly IDealService _dealService;
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(
            IInvoiceService invoiceService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            IDealService dealService,
            IAuthorizationService authorizationService,
            ILogger<IndexModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _invoiceService = invoiceService;
            _currentUserService = currentUserService;
            _dealService = dealService;
            _tenantService = tenantService;
            _logger = logger;
        }

        // ✅ Required by AuthorizedPageModel
        protected override string ModuleName => Modules.Invoices;

        // Properties for View
        public List<InvoiceListItem> Invoices { get; set; } = new();
        public InvoiceStatisticsDto Statistics { get; set; } = new();

        // ✅ Tenant context
        public string TenantCurrencySymbol { get; private set; } = string.Empty;
        public string TenantCurrencyCode { get; private set; } = string.Empty;

        // Filters
        [BindProperty(SupportsGet = true)]
        public string? StatusFilter { get; set; }

        [BindProperty(SupportsGet = true)]
        public DateTime? FromDate { get; set; }

        [BindProperty(SupportsGet = true)]
        public DateTime? ToDate { get; set; }

        // Messages
        [TempData]
        public string? SuccessMessage { get; set; }

        [TempData]
        public string? ErrorMessage { get; set; }

        // ==================== ON GET ====================
        // ✅ Signature changed from `Task OnGet()` to `Task<IActionResult> OnGetAsync()`
        //    so the permission gate can return a redirect.
        public async Task<IActionResult> OnGetAsync()
        {
            // ✅ GATE: was completely absent
            var permissionCheck = await ValidatePermissionAsync(Actions.Read);
            if (permissionCheck != null) return permissionCheck;

            // ✅ Populates CanCreate/CanRead/CanUpdate/CanDelete for Index.cshtml
            await InitializePermissionsAsync();

            try
            {
                TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
                TenantCurrencyCode = _tenantService.GetCurrencyCode();

                var tenantId = _currentUserService.GetCurrentTenantId();

                // Load statistics
                Statistics = await _invoiceService.GetStatisticsAsync(tenantId);

                // Load invoices with filters
                Invoices = await _invoiceService.GetAllAsync(
                    tenantId,
                    status: StatusFilter,
                    fromDate: FromDate,
                    toDate: ToDate);

                _logger.LogInformation("Loaded {Count} invoices", Invoices.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading invoices");
                ErrorMessage = "Failed to load invoices. Please try again.";
                Invoices = new List<InvoiceListItem>();
                Statistics = new InvoiceStatisticsDto();
            }

            return Page();
        }

        // ==================== DELETE INVOICE ====================
        public async Task<IActionResult> OnPostDelete(Guid id)
        {
            // ✅ Was: if (!CanDelete("Invoices")) return Forbid();
            //    Correct module, but a case-sensitive claim read that bypassed
            //    PermissionHandler and logged nothing.
            var permissionCheck = await ValidatePermissionAsync(Actions.Delete);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();

                // Guard: block delete if payments have been recorded
                var invoice = await _invoiceService.GetByIdAsync(tenantId, id);
                if (invoice?.Status is "Paid" or "PartiallyPaid")
                {
                    ErrorMessage = $"Invoice {invoice.Number} cannot be deleted — payments have been recorded against it.";
                    return RedirectToPage();
                }

                await _invoiceService.DeleteAsync(tenantId, id);
                SuccessMessage = "Invoice deleted successfully!";
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting invoice {Id}", id);
                ErrorMessage = "Failed to delete invoice. Please try again.";
                return RedirectToPage();
            }
        }

        // ==================== UPDATE STATUS ====================
        public async Task<IActionResult> OnPostUpdateStatus(Guid invoiceId, string newStatus)
        {
            // ✅ Was: if (!CanUpdate("Invoices")) return Forbid();
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                await _invoiceService.UpdateStatusAsync(tenantId, invoiceId, newStatus);

                // BUG 2 FIX: When invoice is marked Paid, auto-transition
                //    the linked deal to Won.
                //    Path: Invoice → QuoteId → Quote.DealId → Deal.Stage = Won
                if (string.Equals(newStatus, "Paid", StringComparison.OrdinalIgnoreCase))
                {
                    await TransitionLinkedDealToWonAsync(tenantId, invoiceId);
                }

                SuccessMessage = $"Invoice status updated to {newStatus}!";
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating invoice status");
                ErrorMessage = "Failed to update status. Please try again.";
                return RedirectToPage();
            }
        }

        private async Task TransitionLinkedDealToWonAsync(Guid tenantId, Guid invoiceId)
        {
            try
            {
                var invoice = await _invoiceService.GetByIdAsync(tenantId, invoiceId);
                if (invoice == null)
                {
                    _logger.LogWarning("TransitionLinkedDeal: invoice {Id} not found", invoiceId);
                    return;
                }

                Guid? dealId = invoice.DealId;
                if (!dealId.HasValue || dealId == Guid.Empty)
                {
                    _logger.LogInformation(
                        "Invoice {Id} has no linked Deal — skipping Won transition", invoiceId);
                    return;
                }

                var deal = await _dealService.GetByIdAsync(tenantId, dealId.Value);
                if (deal == null)
                {
                    _logger.LogWarning("TransitionLinkedDeal: deal {DealId} not found", dealId);
                    return;
                }

                if (deal.Stage is "Won" or "Lost" or "ClosedWon" or "ClosedLost")
                {
                    _logger.LogInformation(
                        "Deal {DealId} already in terminal stage {Stage} — skipping",
                        dealId, deal.Stage);
                    return;
                }

                await _dealService.TransitionStageAsync(
                    tenantId.ToString(), dealId.Value, "Won", probability: 100);

                _logger.LogInformation(
                    "✅ Invoice Paid — Deal {DealId} auto-transitioned {From} → Won",
                    dealId, deal.Stage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to auto-transition deal to Won after invoice {Id} paid", invoiceId);
            }
        }

        // ==================== HELPER METHODS ====================

        public string GetStatusBadgeClass(string status)
        {
            return status switch
            {
                "Draft" => "bg-secondary",
                "Sent" => "bg-primary",
                "Viewed" => "bg-info",
                "PartiallyPaid" => "bg-warning text-dark",
                "Paid" => "bg-success",
                "Overdue" => "bg-danger",
                "Cancelled" => "bg-dark",
                _ => "bg-secondary"
            };
        }

        public string GetStatusIcon(string status)
        {
            return status switch
            {
                "Draft" => "file-earmark",
                "Sent" => "send",
                "Viewed" => "eye",
                "PartiallyPaid" => "coin",
                "Paid" => "check-circle-fill",
                "Overdue" => "exclamation-triangle-fill",
                "Cancelled" => "x-circle",
                _ => "file-earmark"
            };
        }

        public string FormatDate(DateTime utcDate) => _tenantService.FormatDate(utcDate);
        public string FormatDateTime(DateTime utcDate) => _tenantService.FormatDateTime(utcDate);
        public string FormatCurrency(decimal amount) => _tenantService.FormatCurrency(amount);

        public string GetSymbol(string? code) => code switch
        {
            "INR" => "₹", "THB" => "฿", "PHP" => "₱", "AED" => "د.إ",
            "USD" => "$", "EUR" => "€", "GBP" => "£",
            _ => _tenantService.GetCurrencySymbol()
        };

        public string GetRelativeDate(DateTime? date)
        {
            if (!date.HasValue) return "No due date";

            var days = (date.Value - DateTime.UtcNow).Days;

            if (days < 0)
                return $"Overdue by {Math.Abs(days)} days";
            else if (days == 0)
                return "Due today";
            else if (days == 1)
                return "Due tomorrow";
            else if (days <= 7)
                return $"Due in {days} days";
            else
                return _tenantService.FormatDate(date.Value);
        }

        public bool IsOverdue(InvoiceListItem invoice)
        {
            return invoice.IsOverdue;
        }

        public string GetStatusDisplay(string status) => status switch
        {
            "Draft" => "Draft",
            "Sent" => "Sent",
            "Viewed" => "Viewed",
            "PartiallyPaid" => "Partially Paid",
            "Paid" => "Paid",
            "Overdue" => "Overdue",
            "Cancelled" => "Cancelled",
            _ => status
        };
    }
}
