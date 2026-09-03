// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Invoices/Detail.cshtml.cs
//
// ✅ SESSION 5 — PERMISSION MIGRATION
//   1. AppPageModel  →  AuthorizedPageModel   (ModuleName = Modules.Invoices)
//   2. ALL FIVE handlers were previously UNGATED. Every one now has a gate:
//        OnGet                  → invoices.read
//        OnGetDownloadPdfAsync  → invoices.read
//        OnPostRecordPayment    → invoices.update  (see RecordPayment note)
//        OnPostUpdateStatus     → invoices.update
//        OnPostDelete           → invoices.delete
//      Before this, ANY authenticated user — including viewer — could delete
//      an invoice, record a payment, or change status by direct POST.
//   3. ✅ DATA-INTEGRITY FIX: OnPostDelete now refuses Paid / PartiallyPaid
//      invoices, matching the guard Index.OnPostDelete already had. The two
//      pages previously disagreed: the list refused, the detail page allowed,
//      and deleting from here destroyed the payment records with it.
//   4. OnGet null-check added — Invoice.Number was dereferenced immediately
//      after GetByIdAsync with no null test.
//
// STAGE TRANSITION — complete CRM flow (unchanged):
//
//   OnPostRecordPayment:
//     Balance == 0 after payment → Deal → Won
//     Balance  > 0 after payment → Deal → Negotiation
//
//   OnPostUpdateStatus:
//     Status = "Paid"          → Deal → Won
//     Status = "PartiallyPaid" → Deal → Negotiation
//
//   Both paths call TransitionLinkedDealAsync(invoice, toStage)
//   which reads InvoiceDto.DealId directly.
//   Non-fatal: invoice save already succeeded before transition attempt.
//
//   ⚠️ KNOWN DESIGN NOTE: this deal transition performs NO deals.update check.
//   It is treated as a system-level consequence of a legitimate invoice action
//   rather than a user-initiated deal edit. Not exploitable under the current
//   matrix (everyone with invoices.update also has deals.update). Revisit if
//   that stops being true.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Invoices;
using MerkaiTrial.Admin.Web.Services.Deals;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Admin.Web.Pages.Invoices
{
    public class DetailModel : AuthorizedPageModel
    {
        private readonly IInvoiceService _invoiceService;
        private readonly IDealService _dealService;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly ILogger<DetailModel> _logger;

        public DetailModel(
            IInvoiceService invoiceService,
            IDealService dealService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            IAuthorizationService authorizationService,
            ILogger<DetailModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _invoiceService = invoiceService;
            _dealService = dealService;
            _currentUserService = currentUserService;
            _tenantService = tenantService;
            _logger = logger;
        }

        // ✅ Required by AuthorizedPageModel — drives every policy name on this page
        protected override string ModuleName => Modules.Invoices;

        [BindProperty(SupportsGet = true)]
        public Guid Id { get; set; }

        public InvoiceDto? Invoice { get; set; }

        public string TenantCurrencySymbol { get; private set; } = string.Empty;
        public string TenantCurrencyCode { get; private set; } = string.Empty;

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

        // ── ✅ RECORD PAYMENT PERMISSION — SINGLE SOURCE OF TRUTH ────────────
        //
        // Recording a payment currently maps to invoices.update.
        //
        // Market note: accounting-side tools (Xero, QuickBooks) and CRM-side
        // tools (HubSpot, Zoho) generally treat "apply a payment" as its own
        // capability, separate from "edit the invoice", because it touches the
        // ledger. We deliberately fold it into invoices.update for now because
        // the role matrix is strictly CRUD-per-module and, under the CURRENT
        // matrix, the outcome is identical either way:
        //     sales_rep     → Invoices read-only  → blocked
        //     sales_manager → Invoices update     → allowed
        //
        // THE DAY reps are granted invoices.update but should NOT be applying
        // payments, change ONLY these two members to Actions.Payment and add
        // "invoices.payment" to the role JSON. No other call site needs edits.
        private Task<IActionResult?> ValidateRecordPaymentPermissionAsync()
            => ValidatePermissionAsync(Actions.Update);

        /// <summary>View-side twin of the payment gate. Use in Detail.cshtml.</summary>
        public bool HasRecordPaymentPermission => CanUpdate;

        // ── ✅ BUSINESS-STATE LOCK (naming follows Pipeline/Detail convention) ──
        // Distinct from the CanDelete PERMISSION property on the base class.
        // Both must be true to delete.
        public bool IsDeletableState =>
            Invoice != null &&
            Invoice.Status != "Paid" &&
            Invoice.Status != "PartiallyPaid";

        /// <summary>Why deletion is blocked, or null when it isn't. Drives the modal copy.</summary>
        public string? DeleteBlockedReason => Invoice?.Status switch
        {
            "Paid" => "Paid invoices cannot be deleted.",
            "PartiallyPaid" => "This invoice has payments recorded against it and cannot be deleted.",
            _ => null
        };

        // ── GET ──────────────────────────────────────────────────────────────

        public async Task<IActionResult> OnGet()
        {
            // ✅ GATE: was completely absent
            var permissionCheck = await ValidatePermissionAsync(Actions.Read);
            if (permissionCheck != null) return permissionCheck;

            // ✅ Populates CanCreate / CanRead / CanUpdate / CanDelete for the view.
            // Without this every Can* property in Detail.cshtml is silently false.
            await InitializePermissionsAsync();

            try
            {
                if (Id == Guid.Empty)
                {
                    ErrorMessage = "Invalid invoice ID";
                    return RedirectToPage("/Invoices/Index");
                }

                TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
                TenantCurrencyCode = _tenantService.GetCurrencyCode();

                var tenantId = _currentUserService.GetCurrentTenantId();
                Invoice = await _invoiceService.GetByIdAsync(tenantId, Id);

                // ✅ NULL CHECK: previously dereferenced Invoice.Number directly.
                // The catch below only helps if the service THROWS; it returns
                // null in at least some paths.
                if (Invoice == null)
                {
                    ErrorMessage = "Invoice not found";
                    return RedirectToPage("/Invoices/Index");
                }

                _logger.LogInformation("Loaded invoice {Number}", Invoice.Number);
                return Page();
            }
            catch (KeyNotFoundException)
            {
                ErrorMessage = "Invoice not found";
                return RedirectToPage("/Invoices/Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading invoice {Id}", Id);
                ErrorMessage = "Failed to load invoice. Please try again.";
                return RedirectToPage("/Invoices/Index");
            }
        }

        // ── RECORD PAYMENT ───────────────────────────────────────────────────

        public async Task<IActionResult> OnPostRecordPayment(
            decimal amount,
            DateTime paymentDate,
            string paymentMethod,
            string? notes)
        {
            // ✅ GATE: was completely absent — viewer could record payments by direct POST
            var permissionCheck = await ValidateRecordPaymentPermissionAsync();
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                var invoiceBefore = await _invoiceService.GetByIdAsync(tenantId, Id);

                var dto = new CreatePaymentDto
                {
                    TenantId = tenantId,
                    InvoiceId = Id,
                    Amount = amount,
                    Currency = invoiceBefore?.Currency ?? _tenantService.GetCurrencyCode(),
                    Method = paymentMethod,
                    PaidAtUtc = paymentDate.ToUniversalTime(),
                    Notes = notes,
                    CreatedBy = currentUser.FullName
                };

                await _invoiceService.AddPaymentAsync(dto);

                // ── Re-fetch to get updated Balance / IsFullyPaid ────────────
                var invoiceAfter = await _invoiceService.GetByIdAsync(tenantId, Id);

                if (invoiceAfter != null)
                {
                    if (invoiceAfter.IsFullyPaid || invoiceAfter.Balance <= 0)
                    {
                        // ✅ Fully paid → Won
                        await TransitionLinkedDealAsync(
                            tenantId, invoiceAfter, "Won", probability: 100);
                    }
                    else
                    {
                        // ✅ Partial payment → Negotiation
                        await TransitionLinkedDealAsync(
                            tenantId, invoiceAfter, "Negotiation", probability: 80);
                    }
                }

                SuccessMessage =
                    $"Payment of {_tenantService.FormatCurrency(amount)} recorded successfully!";
                return RedirectToPage(new { id = Id });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Invalid payment for invoice {Id}", Id);
                ErrorMessage = ex.Message;
                return RedirectToPage(new { id = Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recording payment for invoice {Id}", Id);
                ErrorMessage = "Failed to record payment. Please try again.";
                return RedirectToPage(new { id = Id });
            }
        }

        // ── UPDATE STATUS ─────────────────────────────────────────────────────

        public async Task<IActionResult> OnPostUpdateStatus(string newStatus)
        {
            // ✅ GATE: was completely absent
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                await _invoiceService.UpdateStatusAsync(tenantId, Id, newStatus);

                // ── Stage transition based on new invoice status ──────────────
                if (string.Equals(newStatus, "Paid", StringComparison.OrdinalIgnoreCase))
                {
                    // ✅ Fully paid → Won
                    var invoice = await _invoiceService.GetByIdAsync(tenantId, Id);
                    await TransitionLinkedDealAsync(tenantId, invoice, "Won", probability: 100);
                }
                else if (string.Equals(newStatus, "PartiallyPaid",
                             StringComparison.OrdinalIgnoreCase))
                {
                    // ✅ Partially paid → Negotiation
                    var invoice = await _invoiceService.GetByIdAsync(tenantId, Id);
                    await TransitionLinkedDealAsync(
                        tenantId, invoice, "Negotiation", probability: 80);
                }

                SuccessMessage = $"Invoice status updated to {newStatus}!";
                return RedirectToPage(new { id = Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating status for invoice {Id}", Id);
                ErrorMessage = "Failed to update status. Please try again.";
                return RedirectToPage(new { id = Id });
            }
        }

        // ── DELETE ────────────────────────────────────────────────────────────

        public async Task<IActionResult> OnPostDelete()
        {
            // ✅ GATE: was completely absent — viewer could delete invoices by direct POST
            var permissionCheck = await ValidatePermissionAsync(Actions.Delete);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();

                // ✅ DATA-INTEGRITY GUARD — ported from Index.OnPostDelete, which
                // already had it. This page did not, so deleting from here wiped
                // the invoice AND its payment rows. Server-side, so a hand-crafted
                // POST can't skip it.
                var invoice = await _invoiceService.GetByIdAsync(tenantId, Id);
                if (invoice?.Status is "Paid" or "PartiallyPaid")
                {
                    _logger.LogWarning(
                        "Blocked delete of invoice {Number} — status {Status} has payments recorded",
                        invoice.Number, invoice.Status);
                    ErrorMessage =
                        $"Invoice {invoice.Number} cannot be deleted — payments have been recorded against it.";
                    return RedirectToPage(new { id = Id });
                }

                await _invoiceService.DeleteAsync(tenantId, Id);
                SuccessMessage = "Invoice deleted successfully!";
                return RedirectToPage("/Invoices/Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting invoice {Id}", Id);
                ErrorMessage = "Failed to delete invoice. Please try again.";
                return RedirectToPage(new { id = Id });
            }
        }

        // ── DOWNLOAD PDF ──────────────────────────────────────────────────────

        public async Task<IActionResult> OnGetDownloadPdfAsync()
        {
            // ✅ GATE: was completely absent. Read is enough — the PDF contains
            // nothing the Detail page doesn't already show — but it must still
            // be gated, or it becomes an unauthenticated-by-omission data export.
            var permissionCheck = await ValidatePermissionAsync(Actions.Read);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                if (Id == Guid.Empty)
                {
                    ErrorMessage = "Invalid invoice ID";
                    return RedirectToPage(new { id = Id });
                }
                var tenantId = _currentUserService.GetCurrentTenantId();
                var invoice = await _invoiceService.GetByIdAsync(tenantId, Id);
                var filename = $"{invoice?.Number ?? "invoice"}.pdf";
                var pdfBytes = await _invoiceService.DownloadPdfAsync(tenantId, Id);
                return File(pdfBytes, "application/pdf", filename);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download PDF for invoice {Id}", Id);
                ErrorMessage = "Failed to generate PDF. Please try again.";
                return RedirectToPage(new { id = Id });
            }
        }

        // ── PRIVATE: STAGE TRANSITION ─────────────────────────────────────────

        /// <summary>
        /// Transitions the deal linked to this invoice.
        /// Uses InvoiceDto.DealId directly — no chain through QuoteId needed.
        /// Non-fatal — invoice operation already succeeded before this is called.
        /// TransitionStageAsync already no-ops if deal is already Won/Lost.
        /// </summary>
        private async Task TransitionLinkedDealAsync(
            Guid tenantId, InvoiceDto? invoice, string toStage, int probability)
        {
            try
            {
                if (invoice == null)
                {
                    _logger.LogWarning(
                        "TransitionLinkedDeal: invoice null — skipping {Stage}", toStage);
                    return;
                }

                if (!invoice.DealId.HasValue || invoice.DealId == Guid.Empty)
                {
                    _logger.LogInformation(
                        "Invoice {Id} has no linked Deal — skipping {Stage}", invoice.Id, toStage);
                    return;
                }

                await _dealService.TransitionStageAsync(
                    tenantId.ToString(), invoice.DealId.Value, toStage, probability);

                _logger.LogInformation(
                    "✅ Invoice {InvoiceId} → Deal {DealId} → {Stage}",
                    invoice.Id, invoice.DealId, toStage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to transition Deal to {Stage} after invoice {Id} update",
                    toStage, invoice?.Id);
            }
        }

        // ── VIEW HELPERS ──────────────────────────────────────────────────────

        public string FormatDate(DateTime utcDate) => _tenantService.FormatDate(utcDate);
        public string FormatDateTime(DateTime utcDate) => _tenantService.FormatDateTime(utcDate);
        public string FormatCurrency(decimal amount) => _tenantService.FormatCurrency(amount);

        public string GetSymbol(string? code) => code switch
        {
            "INR" => "₹",
            "THB" => "฿",
            "PHP" => "₱",
            "AED" => "د.إ",
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            _ => _tenantService.GetCurrencySymbol()
        };

        public string GetStatusBadgeClass(string status) => status switch
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

        public string GetPaymentMethodIcon(string method) => method switch
        {
            "BankTransfer" => "bank",
            "CreditCard" => "credit-card",
            "DebitCard" => "credit-card-2-front",
            "Cash" => "cash-coin",
            "Check" => "journal-check",
            "PromptPay" => "phone",
            "PayPal" => "paypal",
            "Stripe" => "stripe",
            _ => "cash"
        };

        public string GetPaymentStatusBadge(string status) => status switch
        {
            "Pending" => "bg-warning text-dark",
            "Captured" => "bg-success",
            "Failed" => "bg-danger",
            "Refunded" => "bg-secondary",
            "Cancelled" => "bg-dark",
            _ => "bg-secondary"
        };

        // ── BUSINESS-STATE HELPERS (unchanged — no collision with base Can* props) ──

        public bool CanRecordPayment()
        {
            if (Invoice == null) return false;
            return Invoice.Status != "Paid" &&
                   Invoice.Status != "Cancelled" &&
                   Invoice.Balance > 0;
        }

        public bool CanChangeStatus()
        {
            if (Invoice == null) return false;
            return Invoice.Status != "Paid" &&
                   Invoice.Status != "Cancelled";
        }

        public List<string> GetAvailableStatuses()
        {
            if (Invoice == null) return new List<string>();
            return Invoice.Status switch
            {
                "Draft" => new List<string> { "Sent" },
                "Sent" => new List<string> { "Viewed", "Cancelled" },
                "Viewed" => new List<string> { "Cancelled" },
                "PartiallyPaid" => new List<string> { "Cancelled" },
                _ => new List<string>()
            };
        }

        public string GetRelativeDate(DateTime? date)
        {
            if (!date.HasValue) return "No due date";
            var days = (date.Value - DateTime.UtcNow).Days;
            if (days < 0) return $"Overdue by {Math.Abs(days)} days";
            if (days == 0) return "Due today";
            if (days == 1) return "Due tomorrow";
            if (days <= 7) return $"Due in {days} days";
            return _tenantService.FormatDate(date.Value);
        }

        public bool IsOverdue() => Invoice?.IsOverdue ?? false;

        public string GetPaymentMethodName(string method) => method switch
        {
            "BankTransfer" => "Bank Transfer",
            "CreditCard" => "Credit Card",
            "DebitCard" => "Debit Card",
            "Cash" => "Cash",
            "Check" => "Check",
            "PromptPay" => "PromptPay",
            "PayPal" => "PayPal",
            "Stripe" => "Stripe",
            _ => method
        };
    }
}
