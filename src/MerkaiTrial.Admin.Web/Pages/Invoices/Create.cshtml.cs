// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Invoices/Create.cshtml.cs
//
// CHANGES (081 — custom fields on invoices)
//   1. The workspace's Invoice custom fields, in the Invoice Details card,
//      through the shared _CustomFieldInputs partial. They sit OUTSIDE the
//      post forms (which live in the sticky sidebar), so the partial is
//      given the form's id (CustomFieldFormVm.FormId) and every input
//      carries form="…" — it posts, and the browser checks required
//      fields, as if it were inside.
//   2. FROM A QUOTE, the boxes are PRE-FILLED from the quote's fields
//      that are mapped to invoice fields, using CustomFieldWireMapper —
//      the same conversion the server uses when an invoice is raised
//      without this form. The card says how many came from the quote.
//   3. A REFUSED SAVE KEEPS WHAT WAS TYPED. Both paths used to REDIRECT
//      on every error, which on the manual path threw away every line.
//      They re-render now (RedisplayFromQuoteAsync / RedisplayManualAsync)
//      with the posted lines, dates, notes and fields, and the reason in
//      PageError — a plain property, because TempData is for redirects.
//      The GET-side loading moved into LoadFromQuoteAsync / LoadManualAsync
//      so the re-render loads exactly what the first render did.
//   4. NEW LINES GOT THE WRONG TAX RATE BELOW 1% — the "raw < 1 → ×100"
//      guess Quotes/Create dropped in 056. The stored rate is always a
//      percentage.
//
// CHANGES (074 — the catalogue reaches the manual invoice)
//   The MANUAL path (straight from a deal, no quote) got none of what
//   052, 055, 067, 069 and 070 built. Same four fixes as Edit.cshtml.cs,
//   whose header sets them out in full:
//     1. the catalogue is loaded in the DEAL's currency, not the
//        workspace's — it was handing ₹ figures to a USD invoice;
//     2. quantity is decimal;
//     3. unit of measure, tax code and percentage discount travel;
//     4. the line editor is _QuoteItemsEditor.cshtml, the shared one.
//
//   The nested InvoiceItemData class is GONE. Its own comment said
//   "Consider deleting this one and using the shared type — deferred,
//   out of scope"; it is the top-level one in Edit.cshtml.cs now, which
//   is the copy that gained the decimal quantity.
//
//   The FromQuote path is untouched — those invoices copy the quote's
//   lines and have been correct since 067.
//
// ✅ SESSION 5 — PERMISSION MIGRATION
//   1. AppPageModel  →  AuthorizedPageModel   (ModuleName = Modules.Invoices)
//   2. OnGetAsync was UNGATED — now invoices.create, plus
//      InitializePermissionsAsync() so Can* properties work in the view.
//   3. The two POST gates were already on the CORRECT module. They change from
//      CanCreate("Invoices") (AppPageModel method, case-SENSITIVE claim read,
//      unlogged) to ValidatePermissionAsync(Actions.Create), which routes
//      through PermissionHandler.
//   4. ✅ CROSS-MODULE: the FromQuote path reads a Quote, so it also requires
//      quotes.read. Previously it read quote data with no Quotes permission at
//      all — a user with invoices.create but no quotes.read could pull quote
//      contents (customer, line items, pricing) through this page.
//   5. ✅ RENAMED  public int Page  →  public int PageNumber.
//      `Page` hid PageModel.Page(), which OnGetAsync calls in three places.
//      Renaming removes the ambiguity regardless of how the compiler was
//      resolving it. No .cshtml references Model.Page, so this is safe —
//      verified by grep across Create.cshtml.
//      NOTE: PageNumber/PageSize/CategoryFilter/IsActiveFilter/SearchTerm are
//      all currently UNUSED — the only consumer was the commented-out
//      GetAllAsync block below. Left in place rather than deleted in case you
//      intend to restore product loading for the manual path.
//
// CHANGES (018 — invoice workflow)
//   ✅ MANUAL INVOICES HAD BLANK LINES. The line items were read with
//      JsonSerializer.Deserialize(itemsJson) — case-SENSITIVE — but the
//      page sends camelCase ("name", "unitPrice"), so every property came
//      back empty/0. Now case-insensitive.
//   ✅ The product catalog is loaded again for manual invoices (it had
//      been commented out, so "Add from Product Catalog" never showed).
//   ✅ New custom lines default to the TENANT's tax rate (was 18%).
//   ✅ Dates are stored as the calendar date (was ToUniversalTime(), which
//      moved an IST date back a day — same fix as quotes).
//   ✅ "Create & Issue": creates the draft and issues it. A manual invoice
//      over the approval limits stays a draft and the message says why and
//      who can issue it.
//   ✅ API refusals show their message.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages.Shared;            // 081 — CustomFieldFormVm, CustomFieldForm
using MerkaiTrial.Admin.Web.Services.CustomFields;   // 081
using MerkaiTrial.Admin.Web.Services.Deals;
using MerkaiTrial.Admin.Web.Services.Invoices;
using MerkaiTrial.Admin.Web.Services.Products;
using MerkaiTrial.Admin.Web.Pages.Quotes;          // 074: QuoteItemsEditorVm
using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Configuration;       // 074: CurrencyConfiguration
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace MerkaiTrial.Admin.Web.Pages.Invoices
{
    public class CreateModel : AuthorizedPageModel
    {
        private readonly IInvoiceService _invoiceService;
        private readonly IQuoteService _quoteService;
        private readonly IProductService _productService;
        private readonly IDealService _dealService;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly IQuoteMilestoneService _milestones;          // 071
        private readonly ICustomFieldService _customFields;           // 081
        private readonly ILogger<CreateModel> _logger;

        public CreateModel(
            IInvoiceService invoiceService,
            IQuoteService quoteService,
            IProductService productService,
            IDealService dealService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            IQuoteMilestoneService milestones,
            ICustomFieldService customFields,                         // 081
            IAuthorizationService authorizationService,
            ILogger<CreateModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _customFields = customFields;
            _invoiceService = invoiceService;
            _quoteService = quoteService;
            _productService = productService;
            _dealService = dealService;
            _currentUserService = currentUserService;
            _tenantService = tenantService;
            _milestones = milestones;
            _logger = logger;
        }

        // ✅ Required by AuthorizedPageModel
        protected override string ModuleName => Modules.Invoices;

        [BindProperty(SupportsGet = true)]
        public Guid? QuoteId { get; set; }

        [BindProperty(SupportsGet = true)]
        public Guid? DealId { get; set; }

        public QuoteDto? Quote { get; set; }
        public DealDetailDto? Deal { get; set; }
        public List<ProductListItem> Products { get; set; } = new();

        [BindProperty]
        public DateTime IssueDate { get; set; } = DateTime.Today;

        [BindProperty]
        public DateTime DueDate { get; set; } = DateTime.Today.AddDays(30);

        // ✅ RENAMED from `Page` — see header note 5
        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public int PageSize { get; set; } = 25;

        [BindProperty(SupportsGet = true)]
        public string? CategoryFilter { get; set; }

        [BindProperty(SupportsGet = true)]
        public bool? IsActiveFilter { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? SearchTerm { get; set; }

        [BindProperty]
        public string Currency { get; set; } = string.Empty;

        [BindProperty]
        public string? Notes { get; set; }

        // ── 071: milestone billing ────────────────────────────────────

        /// <summary>
        /// WHICH stage of the quote's schedule to bill. Arrives in the
        /// query string from the schedule panel's link, and is posted
        /// back as a hidden field.
        ///
        /// NULL on a quote with no schedule, which is the default and
        /// most quotes. A quote WITH a schedule and no stage chosen does
        /// NOT silently bill the whole amount — the page refuses and asks
        /// which stage, the same way CreateInvoiceFromQuoteHandler does.
        /// </summary>
        [BindProperty(SupportsGet = true)]
        public Guid? MilestoneId { get; set; }

        /// <summary>
        /// The quote's billing schedule. Never null — a failed read comes
        /// back flagged, and this page STOPS on that flag rather than
        /// treating "could not read the schedule" as "there is no
        /// schedule". The second would invoice a whole project to a
        /// customer who agreed to pay 30% of it now.
        /// </summary>
        public BillingScheduleResult Billing { get; private set; } = new();

        /// <summary>The stage being billed, resolved from MilestoneId. Null for a whole quote.</summary>
        public MilestoneRowDto? Stage { get; private set; }

        /// <summary>True when this quote is billed in stages.</summary>
        public bool HasSchedule => Billing.HasSchedule;

        [TempData]
        public string? SuccessMessage { get; set; }

        [TempData]
        public string? ErrorMessage { get; set; }

        // ── 081 ───────────────────────────────────────────────────────

        /// <summary>Why the form came back, on THIS response (not TempData — no redirect).</summary>
        public string? PageError { get; private set; }

        /// <summary>The manual editor's starting lines: "[]", or the posted lines after a refusal.</summary>
        public string InitialItemsJson { get; private set; } = "[]";

        /// <summary>Posted as CustomFields[&lt;field id&gt;] = value.</summary>
        [BindProperty]
        public Dictionary<string, string?> CustomFields { get; set; } = new();

        /// <summary>Active Invoice custom fields, in order.</summary>
        public List<CustomFieldDefinitionDto> ActiveCustomFields { get; private set; } = new();
        public bool CustomFieldsLoadFailed { get; private set; }

        /// <summary>How many boxes were filled from the quote's mapped fields (fresh form only).</summary>
        public int PrefilledFromQuote { get; private set; }

        /// <summary>
        /// The model for _CustomFieldInputs. FormId: the inputs are rendered
        /// in the left column, outside the sidebar form that posts them.
        /// </summary>
        public CustomFieldFormVm CustomFieldInputs => new()
        {
            Fields       = ActiveCustomFields,
            Values       = CustomFields,
            LoadFailed   = CustomFieldsLoadFailed,
            CanConfigure = UserCanRead(Modules.Settings),
            EntityType   = CustomFieldEntityTypes.Invoice,
            FormId       = Mode == "FromQuote" ? "fromQuoteInvoiceForm" : "manualInvoiceForm"
        };

        public string TenantCurrencySymbol { get; private set; } = string.Empty;
        public string TenantCurrencyCode { get; private set; } = string.Empty;

        /// <summary>Tenant default tax as a percentage (7 for Thai VAT) — for new custom lines.</summary>
        public decimal DefaultTaxRate { get; private set; }

        /// <summary>074: "GST", "VAT" — the editor's tax column header.</summary>
        public string DefaultTaxRateName { get; private set; } = "Tax";
        public string Mode => QuoteId.HasValue ? "FromQuote" : "Manual";

        public async Task<IActionResult> OnGetAsync()
        {
            // ✅ GATE: was completely absent
            var permissionCheck = await ValidatePermissionAsync(Actions.Create);
            if (permissionCheck != null) return permissionCheck;

            await InitializePermissionsAsync();

            try
            {
                TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
                TenantCurrencyCode = _tenantService.GetCurrencyCode();

                var tenantId = _currentUserService.GetCurrentTenantId();

                if (QuoteId.HasValue)
                {
                    var stop = await LoadFromQuoteAsync(tenantId, isPost: false);
                    if (stop != null) return stop;

                    // 081 — the invoice's own fields, pre-filled from the
                    // quote's mapped ones.
                    await LoadCustomFieldsAsync();
                    await PrefillFromQuoteAsync();
                }
                else if (DealId.HasValue)
                {
                    var stop = await LoadManualAsync(tenantId);
                    if (stop != null) return stop;

                    await LoadCustomFieldsAsync();   // 081
                }
                else
                {
                    ErrorMessage = "Either QuoteId or DealId is required";
                    return RedirectToPage("/Pipeline/Index");
                }

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading invoice creation page");
                // 081: PageError, not TempData — this response renders.
                PageError = "The invoice form could not be loaded. Please try again.";
                return Page();
            }
        }

        /// <summary>
        /// Everything the FROM-QUOTE form needs: the quote (accepted, and
        /// readable by this user), the currency, and — 071 — the billing
        /// schedule and the stage. Returns a redirect when the page must
        /// not be shown at all, null otherwise. Used by GET and, 081, by a
        /// refused POST that shows the form again.
        /// </summary>
        private async Task<IActionResult?> LoadFromQuoteAsync(Guid tenantId, bool isPost)
        {
            // ✅ CROSS-MODULE GATE: this page is about to read quote contents.
            // NOTE: UserCanRead reads the claim directly and does NOT go
            // through AuthorizationService, so it produces no log line —
            // same as the Create Invoice button gate on Quotes/Detail.
            if (!UserCanRead(Modules.Quotes))
            {
                _logger.LogWarning(
                    "❌ Access denied (missing permission: quotes.read) while creating invoice from quote {QuoteId}",
                    QuoteId);
                return RedirectToPage("/AccessDenied");
            }

            Quote = await _quoteService.GetByIdAsync(tenantId, QuoteId!.Value);

            if (Quote == null)
            {
                ErrorMessage = "Quote not found";
                return RedirectToPage("/Quotes/Index");
            }

            if (Quote.Status != "Accepted")
            {
                ErrorMessage = "Can only create invoice from accepted quotes";
                return RedirectToPage("/Quotes/Detail", new { id = QuoteId.Value });
            }

            Currency = !string.IsNullOrEmpty(Quote.Currency) ? Quote.Currency : _tenantService.GetCurrencyCode();

            // The dates are the person's on a POST — only a fresh form
            // gets the defaults.
            if (!isPost)
            {
                IssueDate = DateTime.Today;
                DueDate = DateTime.Today.AddDays(30);
            }

            // ── 071: the billing schedule ─────────────────────
            Billing = await _milestones.GetAsync(QuoteId.Value);

            if (Billing.LoadFailed)
            {
                // Hard stop. "Could not read the schedule" and "there is no
                // schedule" look identical in the DTO, and carrying on as if
                // it were the second would bill the whole quote against a
                // customer who agreed to stages.
                ErrorMessage = "Couldn't read this quote's payment schedule, so no invoice has been started. " +
                               "Try again from the quote.";
                return RedirectToPage("/Quotes/Detail", new { id = QuoteId.Value });
            }

            if (HasSchedule)
            {
                if (!MilestoneId.HasValue)
                {
                    ErrorMessage = $"This quote is billed in {Billing.Schedule.Rows.Count} stages. " +
                                   "Pick a stage in the Payment schedule panel.";
                    return RedirectToPage("/Quotes/Detail", new { id = QuoteId.Value });
                }

                Stage = Billing.Schedule.Rows.FirstOrDefault(r => r.Id == MilestoneId.Value);

                if (Stage == null)
                {
                    ErrorMessage = "That payment stage is no longer on this quote.";
                    return RedirectToPage("/Quotes/Detail", new { id = QuoteId.Value });
                }

                if (Stage.IsInvoiced)
                {
                    ErrorMessage = $"Stage {Stage.Sequence} ({Stage.Name}) is already covered by " +
                                   $"{Stage.InvoiceNumber ?? "a draft invoice"}. " +
                                   "Void it first if it needs replacing.";
                    return RedirectToPage("/Quotes/Detail", new { id = QuoteId.Value });
                }

                // The stage's own date is a SUGGESTION, so it pre-fills the
                // due date on a fresh form when it has one.
                if (!isPost && Stage.DueDateUtc.HasValue)
                    DueDate = Stage.DueDateUtc.Value.Date;
            }
            else if (MilestoneId.HasValue)
            {
                // A stale link: the schedule was removed after somebody
                // opened the quote page.
                ErrorMessage = "This quote no longer has a payment schedule.";
                return RedirectToPage("/Quotes/Detail", new { id = QuoteId.Value });
            }

            return null;
        }

        /// <summary>
        /// Everything the MANUAL form needs: the deal, the catalogue in the
        /// deal's currency, and the tax defaults. A redirect when the page
        /// must not be shown, null otherwise. GET and, 081, a refused POST.
        /// </summary>
        private async Task<IActionResult?> LoadManualAsync(Guid tenantId)
        {
            // ✅ CROSS-MODULE GATE: about to read deal contents
            if (!UserCanRead(Modules.Deals))
            {
                _logger.LogWarning(
                    "❌ Access denied (missing permission: deals.read) while creating invoice from deal {DealId}",
                    DealId);
                return RedirectToPage("/AccessDenied");
            }

            Deal = await _dealService.GetDetailAsync(tenantId, DealId!.Value);

            if (Deal == null)
            {
                ErrorMessage = "Deal not found";
                return RedirectToPage("/Pipeline/Index");
            }

            // (018) Catalog for "Add from Product Catalog" — only with products.read.
            if (UserCanRead(Modules.Products))
            {
                try
                {
                    // ⚠ 074: THE DEAL'S CURRENCY. Without it the catalogue
                    // comes back priced in the WORKSPACE's currency and this
                    // page hands those figures to an invoice denominated in
                    // something else. MUST run after Deal is loaded.
                    var paginated = await _productService.GetAllAsync(
                        tenantId: tenantId, pageNumber: 1, pageSize: 1000,
                        category: null, isActive: true, searchTerm: null,
                        currency: Deal?.Currency ?? _tenantService.GetCurrencyCode());
                    Products = paginated?.Items ?? new List<ProductListItem>();
                }
                catch (Exception pex)
                {
                    _logger.LogWarning(pex, "Could not load products for manual invoice");
                    Products = new List<ProductListItem>();
                }
            }

            try
            {
                // 081 (as Quotes/Create since 056): the stored rate is ALWAYS
                // a percentage. The old "raw < 1 → ×100" guess turned a real
                // 0.5% rate into 50% on every new line.
                DefaultTaxRate = await _tenantService.GetDefaultTaxRateAsync();

                // 074: the country's word for it — "GST", "VAT" — for the
                // editor's column header.
                var label = _tenantService.GetTaxLabel();
                if (!string.IsNullOrWhiteSpace(label)) DefaultTaxRateName = label;
            }
            catch (Exception tex)
            {
                _logger.LogWarning(tex, "Could not load the tenant's default tax rate");
            }

            Currency = Deal.Currency ?? _tenantService.GetCurrencyCode();
            return null;
        }

        public async Task<IActionResult> OnPostCreateFromQuoteAsync(bool sendImmediately = false)
        {
            // ✅ Was: if (!CanCreate("Invoices")) return Forbid();
            var permissionCheck = await ValidatePermissionAsync(Actions.Create);
            if (permissionCheck != null) return permissionCheck;

            if (!QuoteId.HasValue)
            {
                ErrorMessage = "Quote ID is required";
                return RedirectToPage();
            }

            // 081 — each custom field's message under its own input.
            await LoadCustomFieldsAsync();
            CustomFieldForm.Validate(ActiveCustomFields, CustomFields, ModelState);

            try
            {
                // 074. Before anything is read or written. 081: shown on the
                // form again rather than redirecting away from what was typed.
                var dateProblem = DateProblem();
                if (dateProblem != null)
                    return await RedisplayFromQuoteAsync(dateProblem);

                // 081 — a custom field is wrong: its message is under its own input.
                if (CustomFieldForm.HasErrors(ModelState))
                    return await RedisplayFromQuoteAsync("Please correct the highlighted fields.");

                // A date box that failed to bind (a cleared box posts "") is said
                // plainly rather than saving the default binding left behind. ONLY
                // these keys: with nullable reference types on, an unposted string
                // property also lands in ModelState, and must not block the save.
                if (new[] { nameof(IssueDate), nameof(DueDate) }.Any(k => ModelState[k]?.Errors.Count > 0))
                    return await RedisplayFromQuoteAsync("Some of the dates could not be read. Please check them and try again.");

                var tenantId = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                // ── 071: re-read the schedule. ────────────────────────
                //
                // Not trusted from the form. The schedule may have been
                // re-cut since the form was rendered, and this handler is
                // about to decide how much money to invoice. A failed read
                // stops everything for the reason the GET path gives.
                var billing = await _milestones.GetAsync(QuoteId.Value);

                if (billing.LoadFailed)
                {
                    ErrorMessage = "Couldn't read this quote's payment schedule, so no invoice has been created. " +
                                   "Reload the page and try again.";
                    return RedirectToPage(new { QuoteId });
                }

                if (billing.HasSchedule && !MilestoneId.HasValue)
                {
                    ErrorMessage = $"This quote is billed in {billing.Schedule.Rows.Count} stages. " +
                                   "Pick a stage in the Payment schedule panel on the quote.";
                    return RedirectToPage("/Quotes/Detail", new { id = QuoteId.Value });
                }

                if (!billing.HasSchedule && MilestoneId.HasValue)
                {
                    ErrorMessage = "This quote no longer has a payment schedule. Reload the page and try again.";
                    return RedirectToPage("/Quotes/Detail", new { id = QuoteId.Value });
                }

                if (MilestoneId.HasValue &&
                    billing.Schedule.Rows.All(r => r.Id != MilestoneId.Value))
                {
                    ErrorMessage = "That payment stage is no longer on this quote. Reload the page and try again.";
                    return RedirectToPage("/Quotes/Detail", new { id = QuoteId.Value });
                }

                var dto = new CreateInvoiceFromQuoteDto
                {
                    TenantId = tenantId,
                    QuoteId = QuoteId.Value,
                    MilestoneId = MilestoneId,          // 071 — null = the whole quote
                    IssueDateUtc = AsUtcDate(IssueDate),
                    DueDateUtc = AsUtcDate(DueDate),
                    Notes = Notes,
                    CreatedBy = currentUser.FullName,
                    SendImmediately = sendImmediately,

                    // 081 — the form states the invoice's fields (pre-filled
                    // from the quote, then possibly changed). Null when the
                    // field list could not be loaded: the server then copies
                    // the quote's mapped values itself.
                    CustomFields = CustomFieldsLoadFailed
                        ? null
                        : CustomFieldForm.ToSubmission(CustomFields, ActiveCustomFields)
                };

                var invoice = await _invoiceService.CreateFromQuoteAsync(dto);

                // TempData is right from here: this response REDIRECTS.
                SuccessMessage = ResultMessage(invoice, sendImmediately);
                return RedirectToPage("/Invoices/Detail", new { id = invoice.Id });
            }
            catch (InvalidOperationException ex)
            {
                // The API's own sentence — a stage already billed, a required
                // custom field. The form comes back as typed.
                return await RedisplayFromQuoteAsync(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating invoice from quote");
                return await RedisplayFromQuoteAsync("Failed to create the invoice. Please try again.");
            }
        }

        public async Task<IActionResult> OnPostCreateManualAsync(string itemsJson, bool sendImmediately = false)
        {
            // ✅ Was: if (!CanCreate("Invoices")) return Forbid();
            var permissionCheck = await ValidatePermissionAsync(Actions.Create);
            if (permissionCheck != null) return permissionCheck;

            if (!DealId.HasValue)
            {
                ErrorMessage = "Deal ID is required";
                return RedirectToPage();
            }

            // (018) Case-insensitive — the page sends camelCase. 081: read
            // FIRST, so every refusal can put the lines back on the form.
            List<InvoiceItemData>? items = null;
            try
            {
                items = JsonSerializer.Deserialize<List<InvoiceItemData>>(
                    string.IsNullOrWhiteSpace(itemsJson) ? "[]" : itemsJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException jex)
            {
                _logger.LogWarning(jex, "Unreadable itemsJson on the manual invoice form");
            }

            // 081 — each custom field's message under its own input.
            await LoadCustomFieldsAsync();
            CustomFieldForm.Validate(ActiveCustomFields, CustomFields, ModelState);

            try
            {
                // 074. Before anything is written.
                var dateProblem = DateProblem();
                if (dateProblem != null)
                    return await RedisplayManualAsync(items, dateProblem);

                if (items == null || items.Count == 0)
                    return await RedisplayManualAsync(items, "Please add at least one item to the invoice.");

                // 081 — a custom field is wrong: its message is under its own input.
                if (CustomFieldForm.HasErrors(ModelState))
                    return await RedisplayManualAsync(items, "Please correct the highlighted fields.");

                // A date box that failed to bind (a cleared box posts "") is said
                // plainly rather than saving the default binding left behind. ONLY
                // these keys: with nullable reference types on, an unposted string
                // property also lands in ModelState, and must not block the save.
                if (new[] { nameof(IssueDate), nameof(DueDate) }.Any(k => ModelState[k]?.Errors.Count > 0))
                    return await RedisplayManualAsync(items, "Some of the dates could not be read. Please check them and try again.");

                var tenantId = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                var dto = new CreateInvoiceDto
                {
                    TenantId = tenantId,
                    DealId = DealId.Value,
                    IssueDateUtc = AsUtcDate(IssueDate),
                    DueDateUtc = AsUtcDate(DueDate),
                    Currency = Currency,
                    Notes = Notes,
                    CreatedBy = currentUser.FullName,
                    SendImmediately = sendImmediately,

                    // 081 — null when the field list could not be loaded.
                    CustomFields = CustomFieldsLoadFailed
                        ? null
                        : CustomFieldForm.ToSubmission(CustomFields, ActiveCustomFields),

                    Lines = items.Select(i => new CreateInvoiceLineDto
                    {
                        ProductId = i.ProductId,
                        Name = i.Name,
                        Description = i.Description,
                        UnitPrice = i.UnitPrice,
                        Quantity = i.Quantity,            // 074: decimal, was int

                        // 052 / 055 / 067 — all three travel. TaxCode is the
                        // one a GST invoice is legally incomplete without.
                        UnitOfMeasure = i.UnitOfMeasure,
                        LineDiscount = i.LineDiscount,
                        DiscountPercent = i.DiscountPercent,
                        TaxRate = i.TaxRate / 100m,
                        TaxCode = i.TaxCode ?? ""
                    }).ToList()
                };

                var invoice = await _invoiceService.CreateAsync(dto);

                // TempData is right from here: this response REDIRECTS.
                SuccessMessage = ResultMessage(invoice, sendImmediately);
                return RedirectToPage("/Invoices/Detail", new { id = invoice.Id });
            }
            catch (InvalidOperationException ex)
            {
                // e.g. a bad line, a deal this user can't see, a required field
                return await RedisplayManualAsync(items, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating invoice manually");
                return await RedisplayManualAsync(items, "Failed to create invoice. Please try again.");
            }
        }

        // ── 081: showing the form again, as posted ────────────────────

        /// <summary>
        /// A refused FROM-QUOTE save, shown again: the dates and notes are
        /// bound properties, the custom field values are the posted ones,
        /// and the reason is in PageError (this response renders).
        /// </summary>
        private async Task<IActionResult> RedisplayFromQuoteAsync(string message)
        {
            PageError = message;
            await InitializePermissionsAsync();

            TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
            TenantCurrencyCode = _tenantService.GetCurrencyCode();

            try
            {
                var stop = await LoadFromQuoteAsync(_currentUserService.GetCurrentTenantId(), isPost: true);
                if (stop != null) return stop;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not redisplay the invoice-from-quote form for {QuoteId}", QuoteId);
                ErrorMessage = message;
                return RedirectToPage("/Quotes/Detail", new { id = QuoteId });
            }

            return Page();
        }

        /// <summary>A refused MANUAL save, shown again with the posted lines.</summary>
        private async Task<IActionResult> RedisplayManualAsync(List<InvoiceItemData>? items, string message)
        {
            PageError = message;
            await InitializePermissionsAsync();

            TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
            TenantCurrencyCode = _tenantService.GetCurrencyCode();

            try
            {
                var stop = await LoadManualAsync(_currentUserService.GetCurrentTenantId());
                if (stop != null) return stop;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not redisplay the manual invoice form for deal {DealId}", DealId);
                ErrorMessage = message;
                return RedirectToPage("/Pipeline/Index");
            }

            InitialItemsJson = InvoiceLinesSeed.FromPosted(items);
            return Page();
        }

        /// <summary>
        /// 081. The active Invoice custom fields. A failure is remembered
        /// (CustomFieldsLoadFailed) so the form says why the fields are
        /// missing and the save sends CustomFields = null.
        /// </summary>
        private async Task LoadCustomFieldsAsync()
        {
            try
            {
                ActiveCustomFields = (await _customFields.GetDefinitionsAsync(CustomFieldEntityTypes.Invoice, includeInactive: false))
                    .Where(f => f.IsActive)
                    .OrderBy(f => f.SortOrder)
                    .ToList();
                CustomFieldsLoadFailed = false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Custom fields could not be loaded for the New Invoice form");
                ActiveCustomFields     = new List<CustomFieldDefinitionDto>();
                CustomFieldsLoadFailed = true;
            }
        }

        /// <summary>
        /// 081. Fill the invoice's fields from the quote's MAPPED ones
        /// (Settings → Custom Fields → Quotes → "Copy to invoice field"),
        /// through CustomFieldWireMapper — the same conversion the server
        /// uses when an invoice is raised without this form, so the two can
        /// never disagree. Only on a fresh form; a POST keeps what was typed.
        /// Never throws: a failure just leaves the boxes empty.
        /// </summary>
        private async Task PrefillFromQuoteAsync()
        {
            if (Quote == null || CustomFieldsLoadFailed || ActiveCustomFields.Count == 0) return;
            if (Quote.CustomFieldValues.Count == 0) return;

            try
            {
                var quoteFields = await _customFields.GetDefinitionsAsync(CustomFieldEntityTypes.Quote, includeInactive: true);
                var targets = ActiveCustomFields.ToDictionary(f => f.Id);

                foreach (var source in quoteFields.Where(f => f.MapToInvoiceFieldId.HasValue))
                {
                    if (!targets.TryGetValue(source.MapToInvoiceFieldId!.Value, out var target)) continue;
                    if (!Quote.CustomFieldValues.TryGetValue(source.Id, out var wire)) continue;

                    var converted = CustomFieldWireMapper.Convert(
                        source.FieldType, source.Options, wire,
                        target.FieldType, target.DecimalPlaces, target.Options);
                    if (converted is null) continue;

                    var key = target.Id.ToString();
                    if (CustomFields.ContainsKey(key)) continue;

                    CustomFields[key] = converted;
                    PrefilledFromQuote++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not pre-fill invoice custom fields from quote {QuoteId}", QuoteId);
            }
        }

        // ── helpers (018) ─────────────────────────────────────────────

        /// <summary>A date picked on the page is a calendar date — keep it, don't shift it by the server's time zone.</summary>
        private static DateTime AsUtcDate(DateTime d) => DateTime.SpecifyKind(d.Date, DateTimeKind.Utc);

        /// <summary>
        /// 074. Due on or after issue, or the reason it isn't.
        ///
        /// The page checks this too, and the page's check is the one the
        /// user sees — inline, next to the box, before anything is posted.
        /// This is the one that counts. Both invoice dates arrive in hidden
        /// fields, and a hidden field is not a control: it can be edited,
        /// replayed, or posted by something that never rendered the page.
        ///
        /// It matters because of what the rest of the system does with the
        /// due date: an invoice whose due date has passed is Overdue, so an
        /// invoice issued due-before-issued is overdue the moment it is
        /// sent, and the customer's first sight of it is a demand that was
        /// already late.
        /// </summary>
        private string? DateProblem()
            => DueDate.Date < IssueDate.Date
                ? $"The due date ({DueDate:dd MMM yyyy}) is before the issue date " +
                  $"({IssueDate:dd MMM yyyy}). An invoice cannot fall due before it is raised."
                : null;

        /// <summary>
        /// Says whether it was issued, and if not, why.
        ///
        /// 071: a milestone invoice says WHICH stage, from the snapshot on
        /// the invoice itself rather than from the schedule, so the
        /// sentence matches the document that was just created even if
        /// the schedule changes a minute later.
        /// </summary>
        private static string ResultMessage(InvoiceDto invoice, bool wantedToIssue)
        {
            var stage = invoice.IsMilestoneInvoice && invoice.MilestoneSequence.HasValue
                ? $" for stage {invoice.MilestoneSequence} of {invoice.MilestoneCount} ({invoice.MilestoneName})"
                : string.Empty;

            if (invoice.Status != "Draft")
                return $"Invoice {invoice.Number} created and issued{stage}.";

            return wantedToIssue
                ? "Invoice saved as a draft but NOT issued — it's over your workspace's limits, so a manager of this deal's team (or an admin) needs to issue it. Open it to see why."
                : $"Draft invoice created{stage}. Issue it when it's ready — it gets its number then.";
        }

        /// <summary>
        /// 081. The manual editor's seed from posted lines. InvoiceItemData
        /// has no line id (a manual invoice's lines are replaced on save),
        /// so it is turned into the quote shape and QuoteItemsSeed — the
        /// one seed builder — does the rest.
        /// </summary>
        private static class InvoiceLinesSeed
        {
            public static string FromPosted(IEnumerable<InvoiceItemData>? items)
                => items == null
                    ? "[]"
                    : QuoteItemsSeed.FromPosted(items.Select(i => new QuoteItemData
                    {
                        ProductId       = i.ProductId,
                        Name            = i.Name ?? string.Empty,
                        Description     = i.Description ?? string.Empty,
                        UnitPrice       = i.UnitPrice,
                        Quantity        = i.Quantity,
                        UnitOfMeasure   = i.UnitOfMeasure,
                        LineDiscount    = i.LineDiscount,
                        DiscountPercent = i.DiscountPercent,
                        TaxRate         = i.TaxRate,
                        TaxCode         = i.TaxCode
                    }));
        }

        // 074: the nested InvoiceItemData is GONE. It duplicated the
        // top-level one in Edit.cshtml.cs (same namespace) with its own
        // int Quantity, so the two invoice pages rounded quantities
        // differently — and the comment that used to sit here said as
        // much and deferred it. Both pages deserialise the one declared
        // in Edit.cshtml.cs, which is the copy that now carries the
        // decimal quantity, the unit, the tax code and the percentage.
    }
}
