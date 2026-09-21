// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Quotes/Detail.cshtml.cs
// CHANGES (018 — invoice workflow)
//   ✅ The invoice shown on the quote is the one that isn't void. A voided
//      invoice no longer blocks "Create Invoice" — that's how a mistake on
//      an issued invoice is put right (void, then invoice again).
//   ✅ "Create Invoice" makes a DRAFT (no number yet) and says so.
//
// CHANGES (017 — quote approvals)
//   ✅ Approval state loaded with the quote (Approval). The panel is drawn
//      by _QuoteApprovalPanel.cshtml — one line in Detail.cshtml:
//          <partial name="_QuoteApprovalPanel" model="Model" />
//   ✅ New handlers: SubmitForApproval, Approve, RequestChanges, Recall.
//      Approve / RequestChanges need only Quotes.Read — the API decides
//      whether THIS user may approve THIS quote.
//   ✅ CanChangeStatus follows the new flow: Draft → Sent only when the
//      quote needs no approval (or you're an admin); Approved → Sent;
//      nothing while PendingApproval.
//   ✅ Status / delete refusals from the API now show their real message
//      ("This quote needs approval before it can be sent…") instead of
//      "Failed to update quote status".
//   ✅ Badge classes and descriptions for PendingApproval / Approved.
//
// EARLIER FIXES:
//   ✅ ICurrentTenantService injected
//   ✅ FormatDate() / FormatDateTime() / FormatCurrency() helpers
//   ✅ TenantCurrencySymbol / TenantCurrencyCode for views
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Invoices;
using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Admin.Web.Pages.Quotes
{
    public class DetailModel : AuthorizedPageModel
    {
        private readonly IQuoteService          _quoteService;
        private readonly ICurrentUserService    _currentUserService;
        private readonly ICurrentTenantService  _tenantService;
        private readonly IInvoiceService        _invoiceService;
        private readonly IQuoteApprovalService  _approvals;
        private readonly ILogger<DetailModel>   _logger;

        protected override string ModuleName => Modules.Quotes;

        public DetailModel(
            IQuoteService         quoteService,
            ICurrentUserService   currentUserService,
            ICurrentTenantService tenantService,
            IInvoiceService       invoiceService,
            IQuoteApprovalService approvals,
            IAuthorizationService authorizationService,
            ILogger<DetailModel>  logger)
            : base(authorizationService, currentUserService, logger)
        {
            _quoteService       = quoteService;
            _currentUserService = currentUserService;
            _tenantService      = tenantService;
            _invoiceService     = invoiceService;
            _approvals          = approvals;
            _logger             = logger;
        }

        // ── Properties ────────────────────────────────────────────────
        [BindProperty(SupportsGet = true)] public Guid Id { get; set; }

        [TempData] public string? ErrorMessage   { get; set; }
        [TempData] public string? SuccessMessage { get; set; }

        public QuoteDto?          Quote       { get; set; }
        public InvoiceDto?        Invoice     { get; set; }
        public List<AttachmentDto> Attachments { get; set; } = new();

        /// <summary>(018) Invoices raised from this quote and then voided.</summary>
        public int VoidInvoiceCount { get; set; }

        /// <summary>(018) The live invoice is still a draft (no number yet).</summary>
        public bool InvoiceIsDraft => Invoice?.Status == "Draft";

        /// <summary>Approval panel data. Null if it couldn't be loaded — the page still works.</summary>
        public QuoteApprovalStateDto? Approval { get; set; }

        // ── Level 1: UI lock ──────────────────────────────────────────
        // Accepted quotes with an invoice are part of the financial trail.
        // Accepted quotes without an invoice can still be deleted (edge case).
        public bool IsLocked => Quote?.Status is "Accepted" or "Rejected" || Invoice != null;

        // ── Tenant context ────────────────────────────────────────────
        public string TenantCurrencySymbol { get; private set; } = string.Empty;
        public string TenantCurrencyCode   { get; private set; } = string.Empty;

        // ── GET ───────────────────────────────────────────────────────
        public async Task<IActionResult> OnGetAsync()
        {
            var check = await ValidatePermissionAsync(Actions.Read);
            if (check != null) return check;

            await InitializePermissionsAsync();

            try
            {
                if (Id == Guid.Empty)
                {
                    ErrorMessage = "Invalid quote ID";
                    return RedirectToPage("/Quotes/Index");
                }

                // ✅ Load tenant context
                TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
                TenantCurrencyCode   = _tenantService.GetCurrencyCode();

                var tenantId = _currentUserService.GetCurrentTenantId();

                Quote = await _quoteService.GetByIdAsync(tenantId, Id);

                if (Quote == null)
                {
                    ErrorMessage = "Quote not found";
                    return RedirectToPage("/Quotes/Index");
                }

                _logger.LogInformation("Quote loaded: {QuoteNumber}", Quote.Number);

                try
                {
                    var invoiceList  = await _invoiceService.GetAllAsync(tenantId, quoteId: Id);
                    // (018) The live invoice — a void one doesn't count.
                    var firstInvoice = invoiceList?.FirstOrDefault(i => i.Status != "Cancelled");
                    VoidInvoiceCount = invoiceList?.Count(i => i.Status == "Cancelled") ?? 0;

                    if (firstInvoice != null)
                        Invoice = await _invoiceService.GetByIdAsync(tenantId, firstInvoice.Id);
                }
                catch (Exception invEx)
                {
                    _logger.LogWarning(invEx,
                        "Failed to load invoice for quote {QuoteId}", Id);
                    Invoice = null;
                }

                // ✅ Load attachments
                try
                {
                    Attachments = await _quoteService.GetAttachmentsAsync(tenantId, Id);
                }
                catch (Exception attEx)
                {
                    _logger.LogWarning(attEx, "Failed to load attachments for quote {QuoteId}", Id);
                    Attachments = new();
                }

                // ✅ Approval state (017)
                try
                {
                    Approval = await _approvals.GetStateAsync(Id);
                }
                catch (Exception apEx)
                {
                    _logger.LogWarning(apEx, "Failed to load approval state for quote {QuoteId}", Id);
                    Approval = null;
                }

                return Page();
            }
            catch (KeyNotFoundException)
            {
                ErrorMessage = "Quote not found";
                return RedirectToPage("/Quotes/Index");
            }
            catch (UnauthorizedAccessException)
            {
                ErrorMessage = "You don't have permission to view this quote";
                return RedirectToPage("/Quotes/Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load quote {QuoteId}", Id);
                ErrorMessage = "Failed to load quote details. Please try again.";
                return RedirectToPage("/Quotes/Index");
            }
        }

        // ── UPDATE STATUS ─────────────────────────────────────────────
        public async Task<IActionResult> OnPostUpdateStatusAsync(string newStatus)
        {
            try
            {
                var check = await ValidatePermissionAsync(Actions.Update);
                if (check != null) return check;

                if (string.IsNullOrWhiteSpace(newStatus))
                {
                    ErrorMessage = "Invalid status";
                    return RedirectToPage(new { id = Id });
                }

                var tenantId = _currentUserService.GetCurrentTenantId();

                // ✅ Pass BaseUrl so handler can generate full public link
                // e.g. https://yourapp.com/q/{token}
                var dto = new UpdateQuoteStatusDto
                {
                    Status = newStatus,
                    BaseUrl = $"{Request.Scheme}://{Request.Host}"  // ← ADD THIS LINE
                };

                await _quoteService.UpdateStatusAsync(tenantId, Id, newStatus, dto.BaseUrl);

                SuccessMessage = newStatus == "Sent"
                    ? "Quote sent! A customer link has been generated."
                    : $"Quote status updated to {newStatus} successfully!";

                return RedirectToPage(new { id = Id });
            }
            catch (InvalidOperationException ex)
            {
                // The API refused, and said why — e.g. "This quote needs
                // approval before it can be sent…". Show that, not a generic error.
                ErrorMessage = ex.Message;
                return RedirectToPage(new { id = Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update quote status {QuoteId}", Id);
                ErrorMessage = "Failed to update quote status. Please try again.";
                return RedirectToPage(new { id = Id });
            }
        }

        // ── APPROVALS (017) ───────────────────────────────────────────

        public async Task<IActionResult> OnPostSubmitForApprovalAsync(string? comment)
        {
            var check = await ValidatePermissionAsync(Actions.Update);
            if (check != null) return check;

            return await ApprovalActionAsync(
                () => _approvals.SubmitAsync(Id, comment),
                "Sent for approval. You'll be able to send the quote once it's approved.");
        }

        public async Task<IActionResult> OnPostApproveAsync(string? comment)
        {
            // Read is enough — the API decides whether this user may approve this quote.
            var check = await ValidatePermissionAsync(Actions.Read);
            if (check != null) return check;

            return await ApprovalActionAsync(
                () => _approvals.ApproveAsync(Id, comment),
                "Approved. The quote can now be sent to the customer.");
        }

        public async Task<IActionResult> OnPostRequestChangesAsync(string? comment)
        {
            var check = await ValidatePermissionAsync(Actions.Read);
            if (check != null) return check;

            if (string.IsNullOrWhiteSpace(comment))
            {
                ErrorMessage = "Say what needs to change, so the rep knows what to fix.";
                return RedirectToPage(new { id = Id });
            }

            return await ApprovalActionAsync(
                () => _approvals.RequestChangesAsync(Id, comment),
                "Sent back to draft with your comments.");
        }

        public async Task<IActionResult> OnPostRecallAsync()
        {
            var check = await ValidatePermissionAsync(Actions.Update);
            if (check != null) return check;

            return await ApprovalActionAsync(
                () => _approvals.RecallAsync(Id),
                "Approval request recalled. The quote is back in draft.");
        }

        private async Task<IActionResult> ApprovalActionAsync(Func<Task> action, string success)
        {
            try
            {
                if (Id == Guid.Empty)
                {
                    ErrorMessage = "Invalid quote ID";
                    return RedirectToPage("/Quotes/Index");
                }

                await action();
                SuccessMessage = success;
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Approval action failed for quote {QuoteId}", Id);
                ErrorMessage = "That didn't work. Please try again.";
            }

            return RedirectToPage(new { id = Id });
        }

        // ── UPLOAD ATTACHMENT ─────────────────────────────────────────
        public async Task<IActionResult> OnPostUploadAttachmentAsync(IFormFile file)
        {
            try
            {
                var check = await ValidatePermissionAsync(Actions.Update);
                if (check != null) return check;

                if (file == null || file.Length == 0)
                {
                    ErrorMessage = "Please select a file to upload.";
                    return RedirectToPage(new { id = Id });
                }

                var tenantId = _currentUserService.GetCurrentTenantId();
                await _quoteService.UploadAttachmentAsync(tenantId, Id, file);

                SuccessMessage = $"'{file.FileName}' uploaded successfully.";
                return RedirectToPage(new { id = Id });
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                return RedirectToPage(new { id = Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload attachment for quote {QuoteId}", Id);
                ErrorMessage = "Failed to upload file. Please try again.";
                return RedirectToPage(new { id = Id });
            }
        }

        // ── DELETE ATTACHMENT ──────────────────────────────────────────
        public async Task<IActionResult> OnPostDeleteAttachmentAsync(Guid attachmentId)
        {
            try
            {
                var check = await ValidatePermissionAsync(Actions.Update);
                if (check != null) return check;

                var tenantId = _currentUserService.GetCurrentTenantId();
                await _quoteService.DeleteAttachmentAsync(tenantId, attachmentId);

                SuccessMessage = "Attachment deleted.";
                return RedirectToPage(new { id = Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete attachment {AttachmentId}", attachmentId);
                ErrorMessage = "Failed to delete attachment. Please try again.";
                return RedirectToPage(new { id = Id });
            }
        }

        // ── DELETE ────────────────────────────────────────────────────────
        public async Task<IActionResult> OnPostDeleteAsync()
        {
            try
            {
                var check = await ValidatePermissionAsync(Actions.Delete);
                if (check != null) return check;

                var tenantId = _currentUserService.GetCurrentTenantId();

                // ✅ Level 2: backend guard
                var quote = await _quoteService.GetByIdAsync(tenantId, Id);
                if (quote?.Status is "Accepted")
                {
                    var existingInvoices = await _invoiceService.GetAllAsync(tenantId, quoteId: Id);
                    if (existingInvoices?.Any() == true)
                    {
                        ErrorMessage = "This quote has an invoice — it cannot be deleted.";
                        return RedirectToPage(new { id = Id });
                    }
                }

                await _quoteService.DeleteAsync(tenantId, Id);
                SuccessMessage = "Quote deleted successfully!";
                return RedirectToPage("/Quotes/Index");
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                return RedirectToPage(new { id = Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete quote {QuoteId}", Id);
                ErrorMessage = "Failed to delete quote. Please try again.";
                return RedirectToPage(new { id = Id });
            }
        }

        // ── DOWNLOAD PDF ──────────────────────────────────────────────────────
        public async Task<IActionResult> OnGetDownloadPdfAsync()
        {
            try
            {
                var check = await ValidatePermissionAsync(Actions.Read);
                if (check != null) return check;

                if (Id == Guid.Empty)
                {
                    ErrorMessage = "Invalid quote ID";
                    return RedirectToPage(new { id = Id });
                }

                var tenantId = _currentUserService.GetCurrentTenantId();
                var pdfBytes = await _quoteService.DownloadPdfAsync(tenantId, Id);

                // Resolve quote number for filename (Quote is loaded in OnGetAsync,
                // but this is a separate GET handler so we load it fresh)
                string filename;
                try
                {
                    var quote = await _quoteService.GetByIdAsync(tenantId, Id);
                    filename = $"{quote.Number}.pdf";
                }
                catch
                {
                    filename = $"quote-{Id.ToString()[..8]}.pdf";
                }

                return File(pdfBytes, "application/pdf", filename);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download PDF for quote {QuoteId}", Id);
                ErrorMessage = "Failed to generate quote PDF. Please try again.";
                return RedirectToPage(new { id = Id });
            }
        }

        // ── CREATE INVOICE ────────────────────────────────────────────
        public async Task<IActionResult> OnPostCreateInvoiceAsync(Guid id)
        {
            try
            {
                // Cross-module action: reads/advances the quote AND creates an Invoice.
                // Requires permission on BOTH modules — this page's own module (Quotes)
                // and the target module (Invoices).
                var check = await ValidatePermissionAsync(Actions.Update);
                if (check != null) return check;

                if (!UserCanCreate(Modules.Invoices))
                {
                    Logger.LogWarning(
                        "❌ Access denied (missing permission: {Module}.{Action}) for user {Email}",
                        Modules.Invoices, Actions.Create,
                        User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value);
                    return RedirectToPage("/AccessDenied");
                }

                Id = id;

                if (Id == Guid.Empty)
                {
                    ErrorMessage = "Invalid quote ID";
                    return RedirectToPage("/Quotes/Index");
                }

                var tenantId = _currentUserService.GetCurrentTenantId();
                var quote    = await _quoteService.GetByIdAsync(tenantId, Id);

                if (quote == null)
                {
                    ErrorMessage = "Quote not found";
                    return RedirectToPage("/Quotes/Index");
                }

                if (quote.Status != "Accepted")
                {
                    ErrorMessage = $"Only accepted quotes can be invoiced. Current status: {quote.Status}";
                    return RedirectToPage("/Quotes/Detail", new { id = Id });
                }

                var existingInvoices = await _invoiceService.GetAllAsync(tenantId, quoteId: Id);
                var live = existingInvoices?.FirstOrDefault(i => i.Status != "Cancelled");
                if (live != null)
                {
                    ErrorMessage = live.Status == "Draft"
                        ? "A draft invoice already exists for this quote — open it from the box above."
                        : $"Invoice {live.Number} already exists for this quote. Void it first if it needs replacing.";
                    return RedirectToPage("/Quotes/Detail", new { id = Id });
                }

                var currentUser = await _currentUserService.GetCurrentUserAsync();

                var invoice = await _invoiceService.CreateFromQuoteAsync(new CreateInvoiceFromQuoteDto
                {
                    TenantId        = tenantId,
                    QuoteId         = Id,
                    IssueDateUtc    = DateTime.UtcNow.Date,
                    DueDateUtc      = DateTime.UtcNow.Date.AddDays(30),
                    CreatedBy       = currentUser.FullName,
                    SendImmediately = false
                });

                SuccessMessage = "Draft invoice created. Check the dates, then Issue it — it gets its invoice number then.";
                return RedirectToPage("/Invoices/Detail", new { id = invoice.Id });
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                return RedirectToPage("/Quotes/Detail", new { id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create invoice from quote {QuoteId}", id);
                ErrorMessage = "Failed to create invoice. Please try again.";
                return RedirectToPage("/Quotes/Detail", new { id });
            }
        }

        // ── VIEW HELPERS ──────────────────────────────────────────────

        /// <summary>UTC → tenant local date</summary>
        public string FormatDate(DateTime utcDate)
            => _tenantService.FormatDate(utcDate);

        /// <summary>UTC → tenant local date + time</summary>
        public string FormatDateTime(DateTime utcDate)
            => _tenantService.FormatDateTime(utcDate);

        /// <summary>Amount with tenant currency symbol e.g. ₱9,408.00</summary>
        public string FormatCurrency(decimal amount)
            => _tenantService.FormatCurrency(amount);

        /// <summary>Currency symbol for a specific code — falls back to tenant symbol</summary>
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
            "Draft"           => "bg-secondary",
            "PendingApproval" => "bg-warning text-dark",
            "Approved"        => "bg-success-subtle text-success-emphasis border border-success",
            "Sent"            => "bg-primary",
            "Viewed"          => "bg-info",
            "Accepted"        => "bg-success",
            "Rejected"        => "bg-danger",
            "Expired"         => "bg-warning text-dark",
            "Revised"         => "bg-dark",
            _                 => "bg-secondary"
        };

        /// <summary>"PendingApproval" → "Pending approval" for badges.</summary>
        public static string StatusLabel(string status) => status switch
        {
            "PendingApproval" => "Pending approval",
            _                 => status
        };

        /// <summary>
        /// Same moves the API allows (QuoteWorkflow.CanMove), plus the
        /// approval gate: Draft/Revised → Sent only when Approval.CanSend.
        /// If the approval state couldn't be loaded, the Send button still
        /// shows and the API has the final say.
        /// </summary>
        public bool CanChangeStatus(string currentStatus, string targetStatus) =>
            currentStatus switch
            {
                "Draft"    => targetStatus == "Sent" && (Approval?.CanSend ?? true),
                "Revised"  => targetStatus == "Sent" && (Approval?.CanSend ?? true),
                "Approved" => targetStatus == "Sent",
                "Sent"     => targetStatus is "Viewed" or "Accepted" or "Rejected" or "Expired",
                "Viewed"   => targetStatus is "Accepted" or "Rejected" or "Expired",
                "Rejected" => targetStatus == "Revised",
                "Expired"  => targetStatus == "Revised",
                _          => false   // Accepted, PendingApproval
            };

        public string GetStatusDescription(string status) => status switch
        {
            "Draft"           => "Quote is being prepared",
            "PendingApproval" => "Waiting for a manager to approve it",
            "Approved"        => "Approved — ready to send to the customer",
            "Sent"            => "Quote has been sent to customer",
            "Viewed"          => "Customer has viewed the quote",
            "Accepted"        => "Customer accepted the quote",
            "Rejected"        => "Customer rejected the quote",
            "Expired"         => "Quote has expired",
            "Revised"         => "Quote has been revised",
            _                 => "Unknown status"
        };

        /// <summary>Edit shows for these only — the API refuses the rest.</summary>
        public bool IsEditableStatus => Quote?.Status is "Draft" or "Revised" or "Approved";
    }
}
