// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Quotes/Detail.cshtml.cs
// FIXES:
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
        private readonly ILogger<DetailModel>   _logger;

        protected override string ModuleName => Modules.Quotes;

        public DetailModel(
            IQuoteService         quoteService,
            ICurrentUserService   currentUserService,
            ICurrentTenantService tenantService,
            IInvoiceService       invoiceService,
            IAuthorizationService authorizationService,
            ILogger<DetailModel>  logger)
            : base(authorizationService, currentUserService, logger)
        {
            _quoteService       = quoteService;
            _currentUserService = currentUserService;
            _tenantService      = tenantService;
            _invoiceService     = invoiceService;
            _logger             = logger;
        }

        // ── Properties ────────────────────────────────────────────────
        [BindProperty(SupportsGet = true)] public Guid Id { get; set; }

        [TempData] public string? ErrorMessage   { get; set; }
        [TempData] public string? SuccessMessage { get; set; }

        public QuoteDto?          Quote       { get; set; }
        public InvoiceDto?        Invoice     { get; set; }
        public List<AttachmentDto> Attachments { get; set; } = new();

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
                    var firstInvoice = invoiceList?.FirstOrDefault();

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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update quote status {QuoteId}", Id);
                ErrorMessage = "Failed to update quote status. Please try again.";
                return RedirectToPage(new { id = Id });
            }
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
                if (existingInvoices != null && existingInvoices.Any())
                {
                    ErrorMessage = $"Invoice {existingInvoices.First().Number} already exists for this quote";
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

                SuccessMessage = $"Invoice {invoice.Number} created successfully!";
                return RedirectToPage("/Invoices/Detail", new { id = invoice.Id });
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
            "Draft"    => "bg-secondary",
            "Sent"     => "bg-primary",
            "Viewed"   => "bg-info",
            "Accepted" => "bg-success",
            "Rejected" => "bg-danger",
            "Expired"  => "bg-warning text-dark",
            "Revised"  => "bg-dark",
            _          => "bg-secondary"
        };

        public bool CanChangeStatus(string currentStatus, string targetStatus) =>
            currentStatus switch
            {
                "Draft"    => targetStatus == "Sent",
                "Sent"     => targetStatus is "Viewed" or "Accepted" or "Rejected" or "Expired",
                "Viewed"   => targetStatus is "Accepted" or "Rejected" or "Expired",
                "Accepted" => false,
                "Rejected" => targetStatus == "Revised",
                "Expired"  => targetStatus == "Revised",
                "Revised"  => targetStatus == "Sent",
                _          => false
            };

        public string GetStatusDescription(string status) => status switch
        {
            "Draft"    => "Quote is being prepared",
            "Sent"     => "Quote has been sent to customer",
            "Viewed"   => "Customer has viewed the quote",
            "Accepted" => "Customer accepted the quote",
            "Rejected" => "Customer rejected the quote",
            "Expired"  => "Quote has expired",
            "Revised"  => "Quote has been revised",
            _          => "Unknown status"
        };
    }
}
