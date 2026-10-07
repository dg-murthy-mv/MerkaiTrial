// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Invoices/Index.cshtml.cs
// Invoice Index Page Backend - List with filters and statistics
//
// CHANGES (018 — invoice workflow)
//   ✅ "Send" on a draft is now ISSUE (OnPostIssue): it gets its INV number
//      and is locked. A manual invoice over the approval limits comes back
//      with the reason and who can issue it.
//   ✅ Delete is for drafts only (the API refuses the rest).
//   ✅ Removed the page-side "Paid → deal Won" transition: Paid can't be
//      set by hand any more; the API moves the deal when payments cover
//      the invoice. (IDealService no longer injected.)
//   ✅ Labels: Sent → "Issued", Cancelled → "Void"; drafts show "Draft"
//      instead of their DRAFT-XXXX placeholder.
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
// CHANGES (074b)
//   ✅ LoadError — a NEW, non-TempData property for the one message this
//      page shows on a RENDER rather than after a redirect. The catch in
//      OnGetAsync used to set the [TempData] ErrorMessage and return
//      Page(), which saves the value for the NEXT request: the failure
//      was announced on some unrelated page later, and this one showed
//      an empty list under "No invoices found."
//   ✅ The view no longer renders TempData["SuccessMessage"] /
//      ["ErrorMessage"] itself. _Layout does that for every page, so
//      every message was appearing twice.
//   ✅ The view's own 5-second auto-dismiss timer is gone — it was a
//      second timer over the same alerts as the layout's, and the two
//      together closed one Bootstrap Alert instance twice. See the note
//      in Index.cshtml.
//   ✅ GetSymbol delegates to CurrencyConfiguration instead of its own
//      hardcoded table, which disagreed about AED and was case-sensitive.
//
//   ⚠️ DESIGN NOTE unchanged from before: OnPostUpdateStatus auto-transitions
//   the linked Deal to Won with no deals.update check. Treated as a
//   system-level consequence of a legitimate invoice action. Not exploitable
//   under the current matrix. Revisit if reps get invoices.update without
//   deals.update.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Invoices;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Configuration;   // 074b: CurrencyConfiguration
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
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(
            IInvoiceService invoiceService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            IAuthorizationService authorizationService,
            ILogger<IndexModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _invoiceService = invoiceService;
            _currentUserService = currentUserService;
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
        //
        // These two are for POST handlers, which all end in RedirectToPage.
        // [TempData] survives exactly one redirect, which is what a
        // post-redirect-get message needs, and _Layout.cshtml renders both
        // keys for every page — so a handler sets one and says nothing more.
        //
        // ⚠ 074b: the VIEW no longer renders them as well. It used to, and
        // the result was every message appearing twice, one banner above
        // the other.
        [TempData]
        public string? SuccessMessage { get; set; }

        [TempData]
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 074b. The load failure, for THIS response.
        ///
        /// OnGetAsync's catch used to set ErrorMessage and then return
        /// Page(). A [TempData] value set during a request that RENDERS is
        /// saved for the NEXT request — so the message never appeared on
        /// the page that failed. It turned up on whatever page the user
        /// opened afterwards, attached to nothing, while this page showed
        /// an empty list under "No invoices found."
        ///
        /// An ordinary property renders now, in the response that failed.
        /// </summary>
        public string? LoadError { get; private set; }

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
                // 074b: LoadError, not ErrorMessage. This path RENDERS; it
                // does not redirect, and a [TempData] value set on a
                // rendering request is only readable on the next one.
                LoadError = "Couldn't load your invoices. Please try again.";
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

                // (018) Drafts only — the API refuses the rest; this is the
                // friendly early answer.
                var invoice = await _invoiceService.GetByIdAsync(tenantId, id);
                if (invoice != null && invoice.Status != "Draft")
                {
                    ErrorMessage = $"Invoice {invoice.Number} has been issued, so it can't be deleted. Open it and void it instead.";
                    return RedirectToPage();
                }

                await _invoiceService.DeleteAsync(tenantId, id);
                SuccessMessage = "Draft invoice deleted.";
                return RedirectToPage();
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting invoice {Id}", id);
                ErrorMessage = "Failed to delete invoice. Please try again.";
                return RedirectToPage();
            }
        }

        // ==================== ISSUE (018) ====================
        public async Task<IActionResult> OnPostIssue(Guid invoiceId)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var number = await _invoiceService.IssueAsync(invoiceId);
                SuccessMessage = $"Invoice issued as {number}.";
            }
            catch (InvalidOperationException ex)
            {
                // e.g. "This invoice is over your workspace's limits, so … needs to issue it."
                ErrorMessage = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error issuing invoice {Id}", invoiceId);
                ErrorMessage = "Failed to issue the invoice. Please try again.";
            }

            return RedirectToPage();
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

        /// <summary>
        /// 074b. Delegates, like every other page does since 074.
        ///
        /// The switch that was here was the third hand-written copy of the
        /// currency symbol table in this solution. It disagreed with
        /// CurrencyConfiguration about AED — whose symbol is deliberately
        /// the string "AED", not "د.إ", so an Arabic glyph doesn't land in
        /// the middle of a left-to-right figure — and it was
        /// case-SENSITIVE, so "inr" fell through to the workspace's symbol:
        /// a rupee sign on a dollar amount.
        ///
        /// Nothing on this page calls it today. It is kept, and corrected,
        /// because the next person to need a symbol here will find it and
        /// use it.
        /// </summary>
        public string GetSymbol(string? code) =>
            string.IsNullOrWhiteSpace(code)
                ? _tenantService.GetCurrencySymbol()
                : CurrencyConfiguration.GetCurrencySymbol(code);

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
            "Sent" => "Issued",
            "Viewed" => "Viewed",
            "PartiallyPaid" => "Partially Paid",
            "Paid" => "Paid",
            "Overdue" => "Overdue",
            "Cancelled" => "Void",
            _ => status
        };

        /// <summary>A draft's DRAFT-XXXX placeholder isn't a number people should quote.</summary>
        public static string DisplayNumber(string number)
            => number.StartsWith("DRAFT-", StringComparison.Ordinal) ? "Draft" : number;
    }
}
