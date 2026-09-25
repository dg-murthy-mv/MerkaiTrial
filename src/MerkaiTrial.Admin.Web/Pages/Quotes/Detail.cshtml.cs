// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Quotes/Detail.cshtml.cs
//
// COMPLETE FILE — replaces the 018 version.
//
// CHANGES (029 — page rebuild)
//
//   1. FormatDate / FormatDateTime DELETED. AuthorizedPageModel already
//      declares both with exactly these signatures, so the copies here
//      were hiding the base members (CS0108) and doing the same thing —
//      both route through ICurrentTenantService. FormatCurrency(decimal)
//      went too: the base has FormatCurrency(decimal, int? decimals = null)
//      and honours the tenant's configured decimal places, which this
//      page's two-line version did not.
//
//   2. MONEY IS SHOWN IN THE QUOTE'S OWN CURRENCY. The page formatted
//      every amount with the TENANT's symbol. A quote raised in USD on an
//      Indian workspace printed "₹1,200.00" against dollar figures —
//      wrong by a factor of about 85. GetSymbol() was already here for
//      exactly this and was never called. Money() now uses it, and
//      appends the ISO code whenever the quote is not in the workspace
//      currency so there is no doubt what the number means.
//
//   3. A MISSING EXPIRY DATE PRINTED "01-01-0001". ExpiresAtUtc is a
//      non-nullable DateTime, so a quote saved without one carries
//      default(DateTime). HasExpiry() is the same guard the Quotes list
//      got in round 028.
//
//   4. StatusHint replaces the nine-branch if/else chain that lived in
//      the view, so the wording for a status is written once. It is
//      approval-aware: a Draft that is over the workspace's limits says
//      so instead of "send this quote to get started".
//
//   5. StatusActions lists the status moves to offer as buttons, each one
//      filtered through CanChangeStatus. The view used to hand-roll
//      "Sent || Viewed" beside a CanChangeStatus call, so the two could
//      drift — and they had: the flow allows Sent/Viewed → Rejected and
//      → Expired, but the page offered no way to record either. A rep
//      whose customer said no had nowhere to put that.
//
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
//   ✅ Handlers: SubmitForApproval, Approve, RequestChanges, Recall.
//      Approve / RequestChanges need only Quotes.Read — the API decides
//      whether THIS user may approve THIS quote.
//   ✅ CanChangeStatus follows the new flow: Draft → Sent only when the
//      quote needs no approval (or you're an admin); Approved → Sent;
//      nothing while PendingApproval.
//   ✅ Status / delete refusals from the API show their real message
//      ("This quote needs approval before it can be sent…") instead of
//      "Failed to update quote status".
// =====================================================================

using System.Globalization;
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

        public QuoteDto?           Quote       { get; set; }
        public InvoiceDto?         Invoice     { get; set; }
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

                // BaseUrl so the handler can build the full public link,
                // e.g. https://yourapp.com/q/{token}
                var baseUrl = $"{Request.Scheme}://{Request.Host}";

                await _quoteService.UpdateStatusAsync(tenantId, Id, newStatus, baseUrl);

                SuccessMessage = newStatus switch
                {
                    "Sent"     => "Quote sent. A customer link has been generated.",
                    "Accepted" => "Marked as accepted. You can raise an invoice from it now.",
                    "Rejected" => "Marked as declined. You can still revise it and send it again.",
                    "Expired"  => "Marked as expired. Revise it to send a fresh version.",
                    "Revised"  => "Back in revision. Edit it, then send it again.",
                    _          => $"Status updated to {StatusLabel(newStatus)}."
                };

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
        // NOTE the permission: Actions.Update, not Actions.Delete. An
        // attachment is part of the quote, so changing which files hang off
        // it is an update to the quote. The view gates the button on
        // CanUpdate to match — it used to gate on CanDelete, which meant a
        // user with Update but not Delete never saw a button the server
        // would have accepted, and a user with Delete but not Update saw
        // one that always bounced to Access Denied.
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

                // Resolve the quote number for the filename. This is a separate
                // GET handler, so OnGetAsync has not run and Quote is null here.
                string filename;
                try
                {
                    var quote = await _quoteService.GetByIdAsync(tenantId, Id);
                    // A null quote here would have thrown an NRE on .Number and
                    // been swallowed by the catch below, which worked but by
                    // accident. Be explicit.
                    filename = string.IsNullOrWhiteSpace(quote?.Number)
                        ? $"quote-{Id.ToString()[..8]}.pdf"
                        : $"{quote!.Number}.pdf";
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
                    ErrorMessage = $"Only accepted quotes can be invoiced. Current status: {StatusLabel(quote.Status)}";
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

        // =============================================================
        // VIEW HELPERS
        //
        // FormatDate, FormatDateTime and FormatCurrency are NOT declared
        // here. AuthorizedPageModel provides all three, routed through
        // ICurrentTenantService. The copies that used to sit here hid the
        // base members (CS0108) and the FormatCurrency one ignored the
        // tenant's configured decimal places.
        // =============================================================

        /// <summary>
        /// Currency symbol for a specific ISO code. Unlike the old version
        /// this does NOT fall back to the tenant symbol: printing ₹ next to
        /// a dollar figure is worse than printing the code, so an unknown
        /// code comes back as the code itself.
        /// </summary>
        public string GetSymbol(string? code) => (code ?? string.Empty).ToUpperInvariant() switch
        {
            "INR" => "₹",
            "THB" => "฿",
            "PHP" => "₱",
            "AED" => "د.إ",
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            ""    => CurrencySymbol,
            _     => code!.ToUpperInvariant() + " "
        };

        /// <summary>
        /// True when this quote is priced in something other than the
        /// workspace currency — the case the old page got wrong.
        /// </summary>
        public bool QuoteInForeignCurrency =>
            !string.IsNullOrWhiteSpace(Quote?.Currency)
            && !string.Equals(Quote!.Currency, CurrencyCode, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// An amount on THIS quote, in THIS quote's currency. Use this for
        /// every figure that came off the quote; FormatCurrency is for
        /// workspace-level figures.
        /// </summary>
        public string Money(decimal amount)
        {
            if (!QuoteInForeignCurrency) return FormatCurrency(amount);

            // Deliberately invariant grouping for a foreign currency: Indian
            // lakh grouping on a US dollar figure reads as a mistake. The ISO
            // code is appended by the view next to the totals.
            return GetSymbol(Quote!.Currency) + amount.ToString("N2", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// ExpiresAtUtc is a non-nullable DateTime, so "no expiry" arrives as
        /// default(DateTime) and formatted straight out as 01-01-0001. Same
        /// guard the Quotes list uses.
        /// </summary>
        public static bool HasExpiry(DateTime d) => d != default && d.Year > 1900;

        /// <summary>Two instants that fall on the same day in the TENANT's timezone.</summary>
        private bool SameTenantDay(DateTime a, DateTime b)
            => string.Equals(FormatDate(a), FormatDate(b), StringComparison.Ordinal);

        /// <summary>
        /// Whole days until the quote expires. 0 means it expires today.
        /// Negative once it has passed. Only meaningful when HasExpiry.
        /// Same helper as the Quotes list (028) — comparing against
        /// DateTime.UtcNow.Date on its own is wrong for five and a half hours
        /// of every Indian day.
        /// </summary>
        public int DaysUntilExpiry(DateTime expiresUtc)
        {
            if (SameTenantDay(expiresUtc, DateTime.UtcNow)) return 0;

            var raw = (int)Math.Round((expiresUtc - DateTime.UtcNow).TotalDays,
                                      MidpointRounding.AwayFromZero);

            // Different tenant-local days, so the answer must not be 0 — the
            // UTC arithmetic can land there inside the timezone offset.
            if (raw == 0) return expiresUtc > DateTime.UtcNow ? 1 : -1;
            return raw;
        }

        /// <summary>
        /// "Expires in 3 days" / "Expired 2 days ago" — or nothing at all when
        /// the quote is not out with a customer, because a draft's expiry date
        /// is not news.
        /// </summary>
        public string ExpiryNote(DateTime expiresUtc)
        {
            if (!HasExpiry(expiresUtc)) return "No expiry set";

            var d = DaysUntilExpiry(expiresUtc);
            if (d < 0)  return d == -1 ? "Expired yesterday" : $"Expired {-d} days ago";
            if (d == 0) return "Expires today";
            if (d == 1) return "Expires tomorrow";
            return $"Expires in {d} days";
        }

        public string GetStatusBadgeClass(string status) => status switch
        {
            "Draft"           => "bg-secondary",
            "PendingApproval" => "bg-warning text-dark",
            "Approved"        => "bg-success-subtle text-success-emphasis border border-success",
            "Sent"            => "bg-primary",
            "Viewed"          => "bg-info text-dark",
            "Accepted"        => "bg-success",
            "Rejected"        => "bg-danger",
            "Expired"         => "bg-warning text-dark",
            "Revised"         => "bg-dark",
            _                 => "bg-secondary"
        };

        public static string StatusIcon(string status) => status switch
        {
            "Draft"           => "bi-file-earmark",
            "PendingApproval" => "bi-hourglass-split",
            "Approved"        => "bi-patch-check",
            "Sent"            => "bi-send",
            "Viewed"          => "bi-eye",
            "Accepted"        => "bi-check-circle",
            "Rejected"        => "bi-x-circle",
            "Expired"         => "bi-clock-history",
            "Revised"         => "bi-arrow-repeat",
            _                 => "bi-question-circle"
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

        /// <param name="Status">The status to move to.</param>
        /// <param name="Label">The button text.</param>
        /// <param name="Icon">Bootstrap icon name.</param>
        /// <param name="Css">Bootstrap button classes.</param>
        /// <param name="Confirm">Confirmation text, or null for no prompt.</param>
        public record StatusMove(string Status, string Label, string Icon, string Css, string? Confirm);

        /// <summary>
        /// The status buttons to draw, already filtered through
        /// CanChangeStatus so the view and the flow cannot drift apart.
        /// Empty when the user can't update, or the quote has nowhere to go.
        /// </summary>
        public List<StatusMove> StatusActions
        {
            get
            {
                var moves = new List<StatusMove>();
                if (Quote == null || !CanUpdate) return moves;

                // "Mark as expired" is left off deliberately: a quote expires
                // on its own date and the API can set it. Offering a button to
                // expire a live quote by hand invites mistakes, and Revise
                // covers the real need.
                var all = new[]
                {
                    new StatusMove("Sent",     "Send to customer",  "bi-send",         "btn-primary",          null),
                    new StatusMove("Accepted", "Customer accepted", "bi-check-circle", "btn-success",          null),
                    new StatusMove("Rejected", "Customer declined", "bi-x-circle",     "btn-outline-danger",
                        "Mark this quote as declined by the customer?"),
                    new StatusMove("Revised",  "Revise",            "bi-arrow-repeat", "btn-outline-primary",  null)
                };

                foreach (var m in all)
                {
                    if (!CanChangeStatus(Quote.Status, m.Status)) continue;
                    // A quote that has been invoiced is part of the financial
                    // trail; don't offer to reopen it.
                    if (m.Status == "Revised" && Invoice != null) continue;
                    moves.Add(m);
                }

                return moves;
            }
        }

        /// <summary>
        /// One sentence explaining where the quote stands. Written once here
        /// rather than as a nine-branch if/else in the view, and
        /// approval-aware for a Draft that is over the workspace's limits.
        /// </summary>
        public string StatusHint
        {
            get
            {
                var status = Quote?.Status ?? string.Empty;

                if (status == "Draft")
                {
                    return Approval?.RequiresApproval == true && Approval?.IsExempt != true
                        ? "This quote is over your workspace's limits, so it needs approval before it can be sent."
                        : "Still a draft. Send it to the customer when you're ready.";
                }

                return status switch
                {
                    "PendingApproval" => "Waiting for a manager to approve it before it can be sent.",
                    "Approved"        => "Approved — ready to send to the customer.",
                    "Sent"            => "Sent. Waiting for the customer to respond.",
                    "Viewed"          => "The customer has opened it. Waiting for a decision.",
                    "Accepted"        => "The customer accepted this quote.",
                    "Rejected"        => "The customer declined this quote. You can revise it and send it again.",
                    "Expired"         => "This quote has expired. Revise it to send a fresh version.",
                    "Revised"         => "Being revised. Edit it, then send it again.",
                    _                 => string.Empty
                };
            }
        }

        /// <summary>Edit shows for these only — the API refuses the rest.</summary>
        public bool IsEditableStatus => Quote?.Status is "Draft" or "Revised" or "Approved";
    }
}
