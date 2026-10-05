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
//
// CHANGES (071 — partial invoicing / milestone billing)
//
//   ✅ A QUOTE CAN NOW HAVE MORE THAN ONE INVOICE, and this page assumed
//      it could not. `Invoice` (singular) is kept — it is still the right
//      answer for a quote with no schedule, which is most of them — but
//      everything that used it as "is this quote invoiced" now reads
//      LiveInvoices instead:
//
//        IsLocked          — was `|| Invoice != null`
//        StatusActions     — the Revise button, same test
//
//      Without that change, a quote billed in three stages would unlock
//      itself the moment the first stage was voided, because `Invoice`
//      would come back null while two live invoices still existed.
//
//   ✅ THE BILLING SCHEDULE PANEL. Loaded as Billing; drawn by
//      _BillingSchedule.cshtml, which is one line in Detail.cshtml the
//      same way the approval panel is. Shows every stage with its
//      computed amount, what has been invoiced against it, and a Create
//      invoice button per stage.
//
//   ✅ OnPostSaveScheduleAsync — the editor posts one scheduleJson
//      field, like the price grid in 069 and the bundle grid in 070.
//
//   ✅ OnPostCreateInvoiceAsync takes an OPTIONAL milestoneId. Null is
//      the whole quote, which is what the existing button posts, so
//      nothing about the unscheduled path changes. The duplicate check
//      in this handler is now per stage, matching
//      CreateInvoiceFromQuoteHandler — and the API has the final say
//      either way, now backed by a unique index.
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
        private readonly IQuoteMilestoneService _milestones;          // 071
        private readonly ILogger<DetailModel>   _logger;

        protected override string ModuleName => Modules.Quotes;

        public DetailModel(
            IQuoteService          quoteService,
            ICurrentUserService    currentUserService,
            ICurrentTenantService  tenantService,
            IInvoiceService        invoiceService,
            IQuoteApprovalService  approvals,
            IQuoteMilestoneService milestones,
            IAuthorizationService  authorizationService,
            ILogger<DetailModel>   logger)
            : base(authorizationService, currentUserService, logger)
        {
            _quoteService       = quoteService;
            _currentUserService = currentUserService;
            _tenantService      = tenantService;
            _invoiceService     = invoiceService;
            _approvals          = approvals;
            _milestones         = milestones;
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

        // ── 071: milestone billing ────────────────────────────────────

        /// <summary>
        /// EVERY invoice on this quote, void ones included, newest first.
        /// A quote with a schedule has one per stage; a quote without has
        /// at most one live and possibly several void.
        /// </summary>
        public List<InvoiceListItem> AllInvoices { get; set; } = new();

        /// <summary>
        /// The live ones — not void. THIS is "has this quote been
        /// invoiced", and it replaces every `Invoice != null` test that
        /// used to mean it. With a schedule, `Invoice` holds only the
        /// first stage's invoice, so the old test would have unlocked a
        /// part-billed quote the moment that one stage was voided.
        /// </summary>
        public List<InvoiceListItem> LiveInvoices =>
            AllInvoices.Where(i => i.Status != "Cancelled").ToList();

        /// <summary>
        /// The billing schedule, with the agreed / invoiced / outstanding
        /// rollup. Never null — see BillingScheduleResult: a failed load
        /// and "no schedule" look identical in the DTO, so the flag comes
        /// back separately and this page shows a warning rather than
        /// pretending there are no payment terms.
        /// </summary>
        public BillingScheduleResult Billing { get; set; } = new();

        /// <summary>True when this quote is billed in stages.</summary>
        public bool HasSchedule => Billing.HasSchedule;

        /// <summary>
        /// True when the schedule panel should offer an Edit button.
        /// Needs quotes.update; the API checks it again. A quote that has
        /// been invoiced can still be opened — names and dates stay
        /// editable — and the editor explains what is frozen.
        /// </summary>
        public bool CanEditSchedule => CanUpdate && Quote != null;

        /// <summary>
        /// Everything _BillingSchedule.cshtml needs, built here so the
        /// partial reads nothing off this page model. One line in the
        /// view:
        ///
        ///     &lt;partial name="_BillingSchedule" model="Model.BillingVm" /&gt;
        ///
        /// Money and Date are passed in as the page's OWN helpers —
        /// amounts on this panel are in the QUOTE's currency and
        /// DetailModel.Money already owns that rule, including appending
        /// the ISO code for a foreign-currency quote. Re-deriving it in
        /// the partial would be a second copy of the thing round 029 was
        /// written to stop having two of.
        /// </summary>
        public BillingScheduleVm BillingVm => new()
        {
            QuoteId          = Id,
            Billing          = Billing,
            CurrencySymbol   = GetSymbol(Quote?.Currency),
            QuoteTotal       = Quote?.GrandTotal ?? 0m,
            Money            = Money,
            Date             = FormatDate,
            CanEdit          = CanEditSchedule,

            // CROSS-MODULE. Raising an invoice from this page needs
            // invoices.create as well as quotes.update, which is exactly
            // what OnPostCreateInvoiceAsync checks before it does
            // anything — so the button and the handler agree.
            CanCreateInvoice = CanUpdate && UserCanCreate(Modules.Invoices),

            Invoices         = AllInvoices,
            SuggestedJson    = System.Text.Json.JsonSerializer.Serialize(
                                   MerkaiTrial.Application.Commands.Quotes.QuoteMilestones
                                       .SuggestedSchedule()
                                       .Select(m => new
                                       {
                                           id           = Guid.Empty,
                                           name         = m.Name,
                                           percent      = m.Percent,
                                           fixedAmount  = m.FixedAmount,
                                           dueCondition = m.DueCondition,
                                           dueDate      = (string?)null
                                       }))
        };

        // ── Level 1: UI lock ──────────────────────────────────────────
        // Accepted quotes with an invoice are part of the financial trail.
        // Accepted quotes without an invoice can still be deleted (edge case).
        //
        // 071: LiveInvoices, not Invoice. See the property's own note.
        public bool IsLocked => Quote?.Status is "Accepted" or "Rejected" || LiveInvoices.Count > 0;

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

                    // 071: the whole list is kept now, not just the first
                    // live one. A quote billed in stages has one invoice
                    // per stage, and the schedule panel has to show all of
                    // them.
                    AllInvoices      = invoiceList?.ToList() ?? new List<InvoiceListItem>();
                    VoidInvoiceCount = AllInvoices.Count(i => i.Status == "Cancelled");

                    // (018) The live invoice — a void one doesn't count.
                    // Still loaded in full, because the existing "there is
                    // an invoice" box on this page shows its totals, and
                    // for a quote with no schedule it is the only invoice
                    // there will ever be.
                    var firstInvoice = AllInvoices.FirstOrDefault(i => i.Status != "Cancelled");

                    if (firstInvoice != null)
                        Invoice = await _invoiceService.GetByIdAsync(tenantId, firstInvoice.Id);
                }
                catch (Exception invEx)
                {
                    _logger.LogWarning(invEx,
                        "Failed to load invoice for quote {QuoteId}", Id);
                    Invoice     = null;
                    AllInvoices = new List<InvoiceListItem>();
                }

                // ── 071: the billing schedule ─────────────────────────
                //
                // Never throws — the service degrades and reports it with
                // a flag. A quote page with one panel missing beats a dead
                // quote page, and _BillingSchedule.cshtml says so on the
                // screen rather than pretending there are no payment
                // terms.
                Billing = await _milestones.GetAsync(Id);

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

        // ── EMAIL THE QUOTE TO THE CUSTOMER (061) ─────────────────────
        //
        // The FIRST email now goes out on its own, queued by
        // UpdateQuoteStatusHandler inside the same transaction as the move
        // to Sent. This handler is the RESEND — "they say it never
        // arrived", "they deleted it" — and deliberately does not touch
        // the status, because sending it again is not a new event in the
        // quote's life.
        public async Task<IActionResult> OnPostSendToCustomerAsync()
        {
            try
            {
                var check = await ValidatePermissionAsync(Actions.Update);
                if (check != null) return check;

                if (Id == Guid.Empty)
                {
                    ErrorMessage = "Invalid quote ID";
                    return RedirectToPage("/Quotes/Index");
                }

                var tenantId = _currentUserService.GetCurrentTenantId();

                // The API host has no idea what hostname this site is
                // served on, so the link in the email would be relative
                // without this. Same value the status call passes.
                var baseUrl = $"{Request.Scheme}://{Request.Host}";

                await _quoteService.SendToCustomerAsync(tenantId, Id, baseUrl);

                SuccessMessage = "Quote emailed to the customer.";
                return RedirectToPage(new { id = Id });
            }
            catch (InvalidOperationException ex)
            {
                // The API's own sentence — "Worapong Thongsuk has no email
                // address on file. Add one to the contact, then send the
                // quote again." Show that, not a second vaguer one.
                ErrorMessage = ex.Message;
                return RedirectToPage(new { id = Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to email quote {QuoteId}", Id);
                ErrorMessage = "Could not email the quote. Please try again.";
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

        // ── SAVE BILLING SCHEDULE (071) ───────────────────────────────
        //
        // One hidden field carrying the whole schedule as JSON, exactly
        // like the price grid in 069 and the bundle grid in 070. The
        // stages only make sense together — they have to add up to the
        // quote — so "save the schedule" is one instruction and not a
        // row-at-a-time API.
        //
        // AN EMPTY LIST IS A REAL INSTRUCTION: it removes the schedule
        // and the quote goes back to being invoiced once, for the whole
        // amount. A MALFORMED payload is NOT treated as empty — it
        // refuses, because "the script failed to run" and "remove the
        // payment terms" must never be the same outcome. That is the 069
        // lesson: the price grid's hidden field originally defaulted to
        // [] and would have deleted every price if its script had not
        // run.
        public async Task<IActionResult> OnPostSaveScheduleAsync(Guid id, string? scheduleJson)
        {
            try
            {
                var check = await ValidatePermissionAsync(Actions.Update);
                if (check != null) return check;

                Id = id;

                if (Id == Guid.Empty)
                {
                    ErrorMessage = "Invalid quote ID";
                    return RedirectToPage("/Quotes/Index");
                }

                List<QuoteMilestoneDto>? stages;
                try
                {
                    stages = string.IsNullOrWhiteSpace(scheduleJson)
                        ? null
                        : System.Text.Json.JsonSerializer.Deserialize<List<QuoteMilestoneDto>>(
                            scheduleJson,
                            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (System.Text.Json.JsonException jex)
                {
                    _logger.LogWarning(jex, "Malformed schedule payload for quote {QuoteId}", Id);
                    stages = null;
                }

                if (stages == null)
                {
                    ErrorMessage = "The payment schedule didn't reach the server properly. " +
                                   "Nothing has been changed — reload the page and try again.";
                    return RedirectToPage(new { id = Id });
                }

                var saved = await _milestones.SaveAsync(Id, stages);

                SuccessMessage = saved.HasSchedule
                    ? $"Payment schedule saved — {saved.Rows.Count} stage{(saved.Rows.Count == 1 ? "" : "s")}."
                    : "Payment schedule removed. This quote will be invoiced once, for the whole amount.";

                return RedirectToPage(new { id = Id });
            }
            catch (InvalidOperationException ex)
            {
                // "The stages come to 90,000.00 of 100,000.00…", or the
                // lock message naming the invoice. Written for the person
                // by QuoteMilestones.Validate.
                ErrorMessage = ex.Message;
                return RedirectToPage(new { id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save the billing schedule for quote {QuoteId}", id);
                ErrorMessage = "Failed to save the payment schedule. Please try again.";
                return RedirectToPage(new { id });
            }
        }

        // ── CREATE INVOICE ────────────────────────────────────────────
        //
        // 071: milestoneId is OPTIONAL. Null means the whole quote, which
        // is what the unscheduled path's button posts — so that path is
        // unchanged. A quote WITH a schedule posts the stage's id from
        // the schedule panel, and the API refuses a null for such a quote
        // rather than quietly billing all of it.
        public async Task<IActionResult> OnPostCreateInvoiceAsync(Guid id, Guid? milestoneId)
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

                // ── 071: which stage, and is it free? ─────────────────
                //
                // The schedule is re-read here rather than trusted from
                // the form. The form was rendered at some point in the
                // past; the schedule may have been re-cut since, and this
                // handler is about to decide how much money to invoice.
                //
                // A FAILED LOAD STOPS THE WHOLE THING. "Could not read
                // the schedule" and "there is no schedule" look the same
                // in the DTO, and acting on the second when it was really
                // the first would invoice the entire quote against a
                // customer who agreed to pay 30% of it now. This is the
                // one place that distinction is worth a hard stop.
                var billing = await _milestones.GetAsync(Id);

                if (billing.LoadFailed)
                {
                    ErrorMessage = "Couldn't read this quote's payment schedule, so no invoice has been created. " +
                                   "Reload the page and try again.";
                    return RedirectToPage("/Quotes/Detail", new { id = Id });
                }

                if (billing.HasSchedule && !milestoneId.HasValue)
                {
                    ErrorMessage = $"This quote is billed in {billing.Schedule.Rows.Count} stages. " +
                                   "Pick a stage in the Payment schedule panel.";
                    return RedirectToPage("/Quotes/Detail", new { id = Id });
                }

                if (!billing.HasSchedule && milestoneId.HasValue)
                {
                    ErrorMessage = "This quote no longer has a payment schedule. Reload the page and try again.";
                    return RedirectToPage("/Quotes/Detail", new { id = Id });
                }

                var stage = milestoneId.HasValue
                    ? billing.Schedule.Rows.FirstOrDefault(r => r.Id == milestoneId.Value)
                    : null;

                if (milestoneId.HasValue && stage == null)
                {
                    ErrorMessage = "That payment stage is no longer on this quote. Reload the page and try again.";
                    return RedirectToPage("/Quotes/Detail", new { id = Id });
                }

                // The duplicate check, now PER STAGE. For a quote with no
                // schedule this is the pre-071 test, word for word: the
                // one live invoice with no stage against it.
                //
                // The API checks this again, and since 071 so does a
                // unique index — which is the only one of the three that
                // can win a race between two people pressing the button
                // at the same moment.
                var existingInvoices = await _invoiceService.GetAllAsync(tenantId, quoteId: Id);

                var live = stage == null
                    ? existingInvoices?.FirstOrDefault(i => i.Status != "Cancelled" && !i.IsMilestoneInvoice)
                    : existingInvoices?.FirstOrDefault(i => i.Status != "Cancelled"
                                                            && i.MilestoneId == stage.Id);

                if (live != null)
                {
                    var what = stage == null ? "this quote" : $"stage {stage.Sequence} ({stage.Name})";

                    ErrorMessage = live.Status == "Draft"
                        ? $"A draft invoice already exists for {what} — open it from the panel above."
                        : $"Invoice {live.Number} already covers {what}. Void it first if it needs replacing.";
                    return RedirectToPage("/Quotes/Detail", new { id = Id });
                }

                var currentUser = await _currentUserService.GetCurrentUserAsync();

                // The stage's own DueDateUtc is a SUGGESTION, so it
                // pre-fills the due date when it has one. Thirty days
                // from today otherwise — the pre-071 default, unchanged.
                var dueDate = stage?.DueDateUtc?.Date ?? DateTime.UtcNow.Date.AddDays(30);

                var invoice = await _invoiceService.CreateFromQuoteAsync(new CreateInvoiceFromQuoteDto
                {
                    TenantId        = tenantId,
                    QuoteId         = Id,
                    MilestoneId     = milestoneId,
                    IssueDateUtc    = DateTime.UtcNow.Date,
                    DueDateUtc      = dueDate,
                    CreatedBy       = currentUser.FullName,
                    SendImmediately = false
                });

                SuccessMessage = stage == null
                    ? "Draft invoice created. Check the dates, then Issue it — it gets its invoice number then."
                    : $"Draft invoice created for stage {stage.Sequence} of {stage.Count} ({stage.Name}). " +
                      "Check the dates, then Issue it — it gets its invoice number then.";

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
                    //
                    // 071: LiveInvoices, not Invoice. A quote billed in
                    // three stages whose FIRST stage was voided would come
                    // back with Invoice == null while two live invoices
                    // still existed, and this page would have offered to
                    // revise it.
                    if (m.Status == "Revised" && LiveInvoices.Count > 0) continue;
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

        // ── 061: can we email this quote? ─────────────────────────────

        /// <summary>
        /// The customer's address, when there is one. Null for a
        /// phone-only contact, which is common enough to be worth saying
        /// out loud rather than discovering after pressing a button.
        /// </summary>
        public string? CustomerEmail => string.IsNullOrWhiteSpace(Quote?.ContactEmail)
            ? null
            : Quote!.ContactEmail!.Trim();

        /// <summary>
        /// True when the Email button should do something. The API checks
        /// all of this again — a hidden button is not a control — but a
        /// button that cannot work should say why instead of failing.
        /// </summary>
        public bool CanEmailCustomer =>
            CanUpdate
            && Quote?.Status is "Sent" or "Viewed"
            && CustomerEmail is not null;

        /// <summary>
        /// Why the Email button is not offered, or null when it is. Only
        /// ever shown on a quote that is out with the customer, so the
        /// draft case needs no sentence here.
        /// </summary>
        public string? EmailBlockedReason
        {
            get
            {
                if (Quote?.Status is not ("Sent" or "Viewed")) return null;
                if (!CanUpdate) return null;
                if (CustomerEmail is not null) return null;

                return string.IsNullOrWhiteSpace(Quote?.ContactName)
                    ? "This deal's contact has no email address, so the quote can't be emailed."
                    : $"{Quote!.ContactName} has no email address, so the quote can't be emailed.";
            }
        }
    }
}
