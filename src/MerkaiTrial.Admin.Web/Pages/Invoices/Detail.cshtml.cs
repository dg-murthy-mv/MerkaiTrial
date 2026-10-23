// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Invoices/Detail.cshtml.cs
//
// CHANGES (100 — a payment is counted once)
//   ✅ OnPostRecordPayment takes the form's paymentKey and passes it on as
//      CreatePaymentDto.IdempotencyKey. The same form sent twice records
//      one payment; the second gets the same "recorded" message.
//
// CHANGES (085 — emailing invoices)
//   ✅ "Email invoice" button and dialog (To, Cc, a personal message). The
//      email carries the PDF as an attachment AND a secure link
//      (/i/{token}) where the customer can view the invoice and download
//      it again. Replies to it are filed on the deal automatically when
//      receiving is set up.
//        • Gate: invoices.update (ValidatePermissionAsync(Actions.Update)),
//          and the API checks it again plus that this invoice is visible.
//        • Only an ISSUED invoice: a draft has no number and is not a tax
//          invoice; a void one is not owed. The button says why when it is
//          not available instead of disappearing.
//        • A refused send ("not a valid address") comes back with the
//          dialog re-opened and what was typed still in it.
//   ✅ "Email history" card (_SentEmailsCard, shared with the quote page):
//      each email, to whom, by whom, and Delivered / Bounced / Spam /
//      Failed with the reason; "Send again" for one that never left.
//   Both load like every other optional panel — a failure costs the
//   dialog's prefill or the card, never the page.
//
// CHANGES (081 — custom fields on invoices)
//   ✅ The invoice's custom fields, in the shared _CustomFieldValues card —
//      Edit link only while it is a draft (the API's rule: an issued tax
//      invoice does not change).
//   ✅ "Printed on the invoice" (_DocumentFieldsCard, shared with the
//      quote page): exactly what the PDF prints — the copy fixed when the
//      invoice was issued, or a draft's preview.
//   ✅ FormatDate / FormatDateTime / FormatCurrency deleted: AuthorizedPage-
//      Model declares all three (CS0108 — these hid them, doing the same
//      thing). GetSymbol's private table — "AED" => "د.إ", contradicting
//      CurrencyConfiguration — is CurrencyConfiguration now, as on Edit
//      since 074.
//
// CHANGES (018 — invoice workflow)
//   ✅ Workflow (from api/invoices/{id}/workflow) drives the buttons:
//        Draft  → Edit, Issue, Delete
//        Issued → Record payment, Reverse payment, Void
//        Void   → nothing but PDF
//   ✅ New handlers: Issue, Void (reason required), ReversePayment (reason
//      required). Manual invoices over the approval limits can only be
//      issued by a manager of the deal owner's team or an admin — the page
//      says who.
//   ✅ Delete is for drafts only; issued invoices are voided.
//   ✅ Status buttons: only "Mark as Viewed" is left — Paid/Partly paid come
//      from payments, Void has its own button.
//   ✅ API refusals show their real message instead of "Failed to …".
//   ✅ Removed the page-side deal moves (UpdateStatus AND RecordPayment):
//      the API's AddPayment moves the deal to ClosedWon when the invoice is
//      fully paid. The page also moved it — to "Won" (a different stage
//      name), or back to "Negotiation" on a partial payment.
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

using MerkaiTrial.Admin.Web.Pages.Shared;            // 081 — CustomFieldDisplayVm, DocumentFieldsVm
using MerkaiTrial.Admin.Web.Services.CustomFields;   // 081
using MerkaiTrial.Admin.Web.Services.Emails;         // 085
using MerkaiTrial.Admin.Web.Services.Invoices;
using MerkaiTrial.Admin.Web.Services.Deals;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Configuration;        // 081 — CustomFieldEntityTypes, CurrencyConfiguration
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Text.Json;                               // 085 — the dialog's draft after a refusal

namespace MerkaiTrial.Admin.Web.Pages.Invoices
{
    public class DetailModel : AuthorizedPageModel
    {
        private readonly IInvoiceService _invoiceService;
        private readonly IDealService _dealService;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly ICustomFieldService _customFields;      // 081
        private readonly ISentEmailService _sentEmails;          // 085
        private readonly ILogger<DetailModel> _logger;

        public DetailModel(
            IInvoiceService invoiceService,
            IDealService dealService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            ICustomFieldService customFields,                    // 081
            ISentEmailService sentEmails,                        // 085
            IAuthorizationService authorizationService,
            ILogger<DetailModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _customFields = customFields;
            _sentEmails = sentEmails;
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

        /// <summary>What can happen next. Null if it couldn't be loaded — buttons then fall back to status checks.</summary>
        public InvoiceWorkflowDto? Workflow { get; set; }

        public string TenantCurrencySymbol { get; private set; } = string.Empty;
        public string TenantCurrencyCode { get; private set; } = string.Empty;

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

        // ── 085: emailing the invoice ────────────────────────────────────

        /// <summary>What the dialog starts from. Null when it could not be loaded.</summary>
        public InvoiceEmailPrefillDto? EmailPrefill { get; private set; }

        /// <summary>The Email history card. Never null; LoadFailed says when it could not load.</summary>
        public SentEmailsCardVm EmailHistory { get; private set; } = new();

        /// <summary>What was typed before a refused send — so the dialog re-opens with it.</summary>
        public InvoiceEmailDraft? EmailDraft { get; private set; }

        /// <summary>Re-open the dialog on load (after a refused send).</summary>
        public bool ReopenEmailDialog => EmailDraft is not null;

        [TempData] public string? InvoiceEmailDraftJson { get; set; }

        public sealed record InvoiceEmailDraft(string? To, string? Cc, string? Message);

        /// <summary>The button is drawn at all: this person can send invoices.</summary>
        public bool ShowEmailButton => CanUpdate && Invoice != null;

        /// <summary>Why the button is disabled, or null when it can be used.</summary>
        public string? EmailBlockedReason
        {
            get
            {
                if (Invoice == null) return "";
                if (IsDraft) return "Issue the invoice first — a draft has no invoice number and isn't a tax invoice yet.";
                if (IsVoid) return "This invoice is void, so it can't be emailed.";
                if (EmailPrefill is null) return "The email details could not be loaded just now. Refresh the page to try again.";
                return EmailPrefill.CanSend ? null : EmailPrefill.BlockedReason ?? "This invoice can't be emailed right now.";
            }
        }

        /// <summary>The file name the customer will receive — shown in the dialog.</summary>
        public string AttachmentName => Invoice == null ? "invoice.pdf"
            : new string(Invoice.Number.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()) + ".pdf";

        // ── 081: custom fields ───────────────────────────────────────────────

        /// <summary>Every Invoice field, switched-off ones included (a retired field still shows its value).</summary>
        public List<CustomFieldDefinitionDto> CustomFieldDefinitions { get; private set; } = new();
        public bool CustomFieldsLoadFailed { get; private set; }

        /// <summary>081. The model for _CustomFieldValues.</summary>
        public CustomFieldDisplayVm CustomFieldDisplay => new()
        {
            Fields       = CustomFieldDefinitions,
            Values       = Invoice?.CustomFieldValues ?? new Dictionary<Guid, string>(),
            Culture      = CustomFieldFormatter.ResolveCulture(CultureName),
            DateFormat   = TenantCtx.GetDateFormat(),
            LoadFailed   = CustomFieldsLoadFailed,
            CanConfigure = UserCanRead(Modules.Settings),
            // Drafts only — the API's rule. An issued invoice is filed.
            CanEdit      = CanUpdate && IsDraft,
            RecordId     = Id,
            EntityType   = CustomFieldEntityTypes.Invoice,
            EditPage     = "/Invoices/Edit"
        };

        /// <summary>081. The model for _DocumentFieldsCard — what the invoice prints.</summary>
        public DocumentFieldsVm DocumentFieldsCard => new()
        {
            Fields          = Invoice?.DocumentFields ?? new List<DocumentFieldEntry>(),
            FrozenAtUtc     = Invoice?.DocumentFieldsFrozenAtUtc,
            FrozenAtDisplay = Invoice?.DocumentFieldsFrozenAtUtc is { } at ? FormatDateTime(at) : null,
            IsOut           = Invoice != null && !IsDraft,
            Noun            = "invoice",
            FreezeVerb      = "issued",
            // The PDF's number format — by the invoice's currency.
            Culture         = DocumentCulture.For(Invoice?.Currency),
            DateFormat      = TenantCtx.GetDateFormat(),
            CanConfigure    = UserCanRead(Modules.Settings),
            EntityType      = CustomFieldEntityTypes.Invoice
        };

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
        // (018) Only a draft can be deleted — an issued invoice is voided.
        public bool IsDeletableState => Workflow?.CanDelete ?? Invoice?.Status == "Draft";

        /// <summary>Why deletion is blocked, or null when it isn't. Drives the tooltip.</summary>
        public string? DeleteBlockedReason => IsDeletableState ? null
            : Invoice?.Status == "Cancelled"
                ? "A void invoice stays on record."
                : "Issued invoices can't be deleted — void it instead.";

        public bool IsDraft => Workflow?.IsDraft ?? Invoice?.Status == "Draft";
        public bool IsVoid => Workflow?.IsVoid ?? Invoice?.Status == "Cancelled";

        /// <summary>A draft's placeholder number isn't shown to people — "Draft" is.</summary>
        public string DisplayNumber => Invoice == null ? ""
            : Invoice.Number.StartsWith("DRAFT-", StringComparison.Ordinal) ? "Draft invoice" : Invoice.Number;

        public bool CanReversePayment(Guid paymentId) =>
            Workflow?.ReversiblePaymentIds.Contains(paymentId) == true;

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

                try
                {
                    Workflow = await _invoiceService.GetWorkflowAsync(Id);
                }
                catch (Exception wfEx)
                {
                    _logger.LogWarning(wfEx, "Could not load workflow for invoice {Id}", Id);
                }

                // 081 — never blocks the page; the card says so on failure.
                try
                {
                    CustomFieldDefinitions = await _customFields.GetDefinitionsAsync(
                        CustomFieldEntityTypes.Invoice, includeInactive: true);
                }
                catch (Exception cfEx)
                {
                    _logger.LogWarning(cfEx, "Custom fields could not be loaded for invoice {Id}", Id);
                    CustomFieldDefinitions = new();
                    CustomFieldsLoadFailed = true;
                }

                // ── 085: the email dialog and the history card ──────────────
                // Neither ever blocks the page.
                if (CanUpdate && !IsDraft && !IsVoid)
                {
                    try
                    {
                        EmailPrefill = await _invoiceService.GetEmailPrefillAsync(Id);
                    }
                    catch (Exception epEx)
                    {
                        _logger.LogWarning(epEx, "Could not load the email prefill for invoice {Id}", Id);
                    }
                }

                EmailDraft = ReadEmailDraft();

                EmailHistory = await SentEmailsCardVm.LoadAsync(
                    _sentEmails, "invoice", Id, "invoice",
                    canRetry:       CanUpdate,
                    returnUrl:      $"/Invoices/Detail?id={Id}#email-history",
                    formatDateTime: d => FormatDateTime(d),
                    logger:         _logger,
                    emptyHint:      IsDraft
                        ? "Issue it first; then use Email invoice to send it with the PDF attached."
                        : CanUpdate ? "Use Email invoice to send it with the PDF attached and a link to view it online." : null);

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
            string? notes,
            Guid? paymentKey)        // 100 — one payment per key, see the .cshtml header
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
                    CreatedBy = currentUser.FullName,
                    IdempotencyKey = paymentKey is Guid k && k != Guid.Empty ? k : null   // 100
                };

                await _invoiceService.AddPaymentAsync(dto);

                // (018) No deal moves from here any more. The API moves the
                // deal to ClosedWon when the invoice is fully paid; the page
                // used to ALSO move it — to "Won" (a second, different stage
                // name) or, on a partial payment, back to "Negotiation", which
                // could drag a deal that was further along backwards.

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

                SuccessMessage = $"Invoice marked as {StatusLabel(newStatus).ToLowerInvariant()}.";
                return RedirectToPage(new { id = Id });
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
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
                // (018) The API refuses anything but a draft; this is the
                // friendly early answer.
                var invoice = await _invoiceService.GetByIdAsync(tenantId, Id);
                if (invoice != null && invoice.Status != "Draft")
                {
                    ErrorMessage = invoice.Status == "Cancelled"
                        ? "A void invoice stays on record and can't be deleted."
                        : $"Invoice {invoice.Number} has been issued, so it can't be deleted. Void it instead.";
                    return RedirectToPage(new { id = Id });
                }

                await _invoiceService.DeleteAsync(tenantId, Id);
                SuccessMessage = "Draft invoice deleted.";
                return RedirectToPage("/Invoices/Index");
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                return RedirectToPage(new { id = Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting invoice {Id}", Id);
                ErrorMessage = "Failed to delete invoice. Please try again.";
                return RedirectToPage(new { id = Id });
            }
        }

        // ── ISSUE / VOID / REVERSE (018) ─────────────────────────────────────

        public async Task<IActionResult> OnPostIssueAsync()
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            return await WorkflowActionAsync(async () =>
            {
                var number = await _invoiceService.IssueAsync(Id);
                return $"Invoice issued as {number}. It can no longer be edited — void it if something is wrong.";
            });
        }

        public async Task<IActionResult> OnPostVoidAsync(string? reason)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            if (string.IsNullOrWhiteSpace(reason))
            {
                ErrorMessage = "Give a reason for voiding the invoice.";
                return RedirectToPage(new { id = Id });
            }

            return await WorkflowActionAsync(async () =>
            {
                await _invoiceService.VoidAsync(Id, reason.Trim());
                return "Invoice voided. It stays on record; raise a new invoice if the customer still owes something.";
            });
        }

        public async Task<IActionResult> OnPostReversePaymentAsync(Guid paymentId, string? reason)
        {
            var permissionCheck = await ValidateRecordPaymentPermissionAsync();
            if (permissionCheck != null) return permissionCheck;

            if (string.IsNullOrWhiteSpace(reason))
            {
                ErrorMessage = "Give a reason for reversing the payment.";
                return RedirectToPage(new { id = Id });
            }

            return await WorkflowActionAsync(async () =>
            {
                await _invoiceService.ReversePaymentAsync(Id, paymentId, reason.Trim());
                return "Payment reversed. The balance has been updated.";
            });
        }

        private async Task<IActionResult> WorkflowActionAsync(Func<Task<string>> action)
        {
            try
            {
                if (Id == Guid.Empty)
                {
                    ErrorMessage = "Invalid invoice ID";
                    return RedirectToPage("/Invoices/Index");
                }

                SuccessMessage = await action();
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Invoice action failed for {Id}", Id);
                ErrorMessage = "That didn't work. Please try again.";
            }

            return RedirectToPage(new { id = Id });
        }

        // ── EMAIL INVOICE (085) ──────────────────────────────────────────────

        public async Task<IActionResult> OnPostEmailAsync(string? toAddress, string? ccAddresses, string? message)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            if (Id == Guid.Empty)
            {
                ErrorMessage = "Invalid invoice ID";
                return RedirectToPage("/Invoices/Index");
            }

            // Cheap checks first, with the same wording as the API — the API
            // checks everything again regardless.
            if (string.IsNullOrWhiteSpace(toAddress))
                return EmailRefused("Enter the customer's email address in To.", toAddress, ccAddresses, message);

            if (message is { Length: > 2000 })
                return EmailRefused("Keep the message under 2000 characters.", toAddress, ccAddresses, message);

            try
            {
                var result = await _invoiceService.SendEmailAsync(Id, new SendInvoiceEmailDto(
                    ToAddress:   toAddress.Trim(),
                    ToName:      null,          // the API names the contact when the address is theirs
                    CcAddresses: string.IsNullOrWhiteSpace(ccAddresses) ? null : ccAddresses.Trim(),
                    Message:     string.IsNullOrWhiteSpace(message) ? null : message.Trim(),
                    BaseUrl:     $"{Request.Scheme}://{Request.Host}{Request.PathBase}"));

                SuccessMessage = result.CcCount > 0
                    ? $"Invoice emailed to {result.To} (and {result.CcCount} in copy). Delivery shows under Email history in a minute or so."
                    : $"Invoice emailed to {result.To}. Delivery shows under Email history in a minute or so.";

                return Redirect($"/Invoices/Detail?id={Id}#email-history");
            }
            catch (InvalidOperationException ex)
            {
                // The API's own sentence ("…isn't a valid email address").
                return EmailRefused(ex.Message, toAddress, ccAddresses, message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to email invoice {Id}", Id);
                return EmailRefused("The invoice could not be emailed just now. Please try again.", toAddress, ccAddresses, message);
            }
        }

        /// <summary>Back to the page with the reason, and the dialog re-opened with what was typed.</summary>
        private IActionResult EmailRefused(string reason, string? to, string? cc, string? message)
        {
            ErrorMessage = reason;
            try
            {
                InvoiceEmailDraftJson = JsonSerializer.Serialize(new InvoiceEmailDraft(
                    Clip(to, 254), Clip(cc, 1700), Clip(message, 2000)));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not keep the email draft for invoice {Id}", Id);
            }

            return RedirectToPage(new { id = Id });
        }

        private InvoiceEmailDraft? ReadEmailDraft()
        {
            if (string.IsNullOrWhiteSpace(InvoiceEmailDraftJson)) return null;
            try
            {
                return JsonSerializer.Deserialize<InvoiceEmailDraft>(InvoiceEmailDraftJson);
            }
            catch
            {
                return null;
            }
            finally
            {
                InvoiceEmailDraftJson = null;   // one use
            }
        }

        private static string? Clip(string? value, int max)
            => string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];

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

        // ── VIEW HELPERS ──────────────────────────────────────────────────────

        // 081: FormatDate / FormatDateTime / FormatCurrency come from
        // AuthorizedPageModel (see the header).

        /// <summary>081: CurrencyConfiguration, the one symbol table (as Edit since 074).</summary>
        public string GetSymbol(string? code) =>
            string.IsNullOrWhiteSpace(code)
                ? _tenantService.GetCurrencySymbol()
                : CurrencyConfiguration.GetCurrencySymbol(code);

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

        /// <summary>What people read: "Cancelled" is shown as "Void", "Sent" as "Issued".</summary>
        public static string StatusLabel(string? status) => status switch
        {
            "Sent" => "Issued",
            "PartiallyPaid" => "Partially paid",
            "Cancelled" => "Void",
            null => "",
            _ => status
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
            "Reversed" => "bg-light text-muted border text-decoration-line-through",
            "Cancelled" => "bg-dark",
            _ => "bg-secondary"
        };

        // ── BUSINESS-STATE HELPERS (unchanged — no collision with base Can* props) ──

        // (018) Payments only against an ISSUED, not-void invoice with money owed.
        public bool CanRecordPayment()
        {
            if (Invoice == null) return false;
            if (Workflow != null) return Workflow.CanRecordPayment;
            return Invoice.Status is not ("Draft" or "Paid" or "Cancelled") && Invoice.Balance > 0;
        }

        public bool CanChangeStatus() => GetAvailableStatuses().Count > 0;

        /// <summary>
        /// (018) The only hand-set move left is "Viewed". Issue and Void have
        /// their own buttons; Paid/Partly paid come from payments.
        /// </summary>
        public List<string> GetAvailableStatuses()
        {
            if (Invoice == null) return new List<string>();
            return Invoice.Status is "Sent" or "Unpaid" or "Overdue"
                ? new List<string> { "Viewed" }
                : new List<string>();
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
