// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Invoices/Create.cshtml.cs
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
        private readonly ILogger<CreateModel> _logger;

        public CreateModel(
            IInvoiceService invoiceService,
            IQuoteService quoteService,
            IProductService productService,
            IDealService dealService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            IQuoteMilestoneService milestones,
            IAuthorizationService authorizationService,
            ILogger<CreateModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
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

                    Quote = await _quoteService.GetByIdAsync(tenantId, QuoteId.Value);

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
                    IssueDate = DateTime.Today;
                    DueDate = DateTime.Today.AddDays(30);

                    // ── 071: the billing schedule ─────────────────────
                    Billing = await _milestones.GetAsync(QuoteId.Value);

                    if (Billing.LoadFailed)
                    {
                        // Hard stop. "Could not read the schedule" and
                        // "there is no schedule" look identical in the
                        // DTO, and carrying on as if it were the second
                        // would bill the whole quote against a customer
                        // who agreed to stages. Back to the quote, with a
                        // sentence that says what happened.
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

                        // The stage's own date is a SUGGESTION, so it
                        // pre-fills the due date when it has one. Thirty
                        // days from today otherwise — the pre-071 default.
                        if (Stage.DueDateUtc.HasValue)
                            DueDate = Stage.DueDateUtc.Value.Date;
                    }
                    else if (MilestoneId.HasValue)
                    {
                        // A stale link: the schedule was removed after
                        // somebody opened the quote page.
                        ErrorMessage = "This quote no longer has a payment schedule.";
                        return RedirectToPage("/Quotes/Detail", new { id = QuoteId.Value });
                    }
                }
                else if (DealId.HasValue)
                {
                    // ✅ CROSS-MODULE GATE: about to read deal contents
                    if (!UserCanRead(Modules.Deals))
                    {
                        _logger.LogWarning(
                            "❌ Access denied (missing permission: deals.read) while creating invoice from deal {DealId}",
                            DealId);
                        return RedirectToPage("/AccessDenied");
                    }

                    Deal = await _dealService.GetDetailAsync(tenantId, DealId.Value);

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
                            // ⚠ 074: THE DEAL'S CURRENCY. Without it the
                            // catalogue comes back priced in the
                            // WORKSPACE's currency and this page hands
                            // those figures to an invoice denominated in
                            // something else — the 069 bug, which was
                            // fixed on quotes and left standing here.
                            //
                            // MUST run after Deal is loaded; that is what
                            // knows the currency.
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
                        var raw = await _tenantService.GetDefaultTaxRateAsync();
                        DefaultTaxRate = raw < 1m ? raw * 100m : raw;

                        // 074: the country's word for it — "GST", "VAT" —
                        // for the editor's column header.
                        var label = _tenantService.GetTaxLabel();
                        if (!string.IsNullOrWhiteSpace(label)) DefaultTaxRateName = label;
                    }
                    catch (Exception tex)
                    {
                        _logger.LogWarning(tex, "Could not load the tenant's default tax rate");
                    }

                    Currency = Deal.Currency ?? _tenantService.GetCurrencyCode();
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
                ErrorMessage = $"Error: {ex.Message}";
                return Page();
            }
        }

        public async Task<IActionResult> OnPostCreateFromQuoteAsync(bool sendImmediately = false)
        {
            // ✅ Was: if (!CanCreate("Invoices")) return Forbid();
            var permissionCheck = await ValidatePermissionAsync(Actions.Create);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                if (!QuoteId.HasValue)
                {
                    ErrorMessage = "Quote ID is required";
                    return RedirectToPage();
                }

                // 074. Before anything is read or written.
                var dateProblem = DateProblem();
                if (dateProblem != null)
                {
                    ErrorMessage = dateProblem;
                    return RedirectToPage(new { QuoteId, MilestoneId });
                }

                var tenantId = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                // ── 071: re-read the schedule. ────────────────────────
                //
                // Not trusted from the form. The form was rendered at
                // some point in the past, the schedule may have been
                // re-cut since, and this handler is about to decide how
                // much money to invoice. A failed read stops everything
                // for the reason the OnGet path gives at length.
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
                    SendImmediately = sendImmediately
                };

                var invoice = await _invoiceService.CreateFromQuoteAsync(dto);

                SuccessMessage = ResultMessage(invoice, sendImmediately);
                return RedirectToPage("/Invoices/Detail", new { id = invoice.Id });
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                return RedirectToPage(new { QuoteId, MilestoneId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating invoice from quote");
                ErrorMessage = "Failed to create the invoice. Please try again.";
                return RedirectToPage(new { QuoteId, MilestoneId });
            }
        }

        public async Task<IActionResult> OnPostCreateManualAsync(string itemsJson, bool sendImmediately = false)
        {
            // ✅ Was: if (!CanCreate("Invoices")) return Forbid();
            var permissionCheck = await ValidatePermissionAsync(Actions.Create);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                if (!DealId.HasValue)
                {
                    ErrorMessage = "Deal ID is required";
                    return RedirectToPage();
                }

                // 074. Before anything is parsed or written.
                var dateProblem = DateProblem();
                if (dateProblem != null)
                {
                    ErrorMessage = dateProblem;
                    return RedirectToPage(new { DealId });
                }

                // (018) Case-insensitive — the page sends camelCase. Without this
                // every line arrived with an empty name and zero price.
                var items = JsonSerializer.Deserialize<List<InvoiceItemData>>(
                    itemsJson ?? "[]", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (items == null || items.Count == 0)
                {
                    ErrorMessage = "Please add at least one item to the invoice";
                    return RedirectToPage(new { DealId });
                }

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
                    Lines = items.Select(i => new CreateInvoiceLineDto
                    {
                        ProductId = i.ProductId,
                        Name = i.Name,
                        Description = i.Description,
                        UnitPrice = i.UnitPrice,
                        Quantity = i.Quantity,            // 074: decimal, was int

                        // 052 / 055 / 067 — all three now travel. See the
                        // matching block in Edit.cshtml.cs for why each
                        // one matters; TaxCode is the one a GST invoice
                        // is legally incomplete without.
                        UnitOfMeasure = i.UnitOfMeasure,
                        LineDiscount = i.LineDiscount,
                        DiscountPercent = i.DiscountPercent,
                        TaxRate = i.TaxRate / 100m,
                        TaxCode = i.TaxCode ?? ""
                    }).ToList()
                };

                var invoice = await _invoiceService.CreateAsync(dto);

                SuccessMessage = ResultMessage(invoice, sendImmediately);
                return RedirectToPage("/Invoices/Detail", new { id = invoice.Id });
            }
            catch (InvalidOperationException ex)
            {
                // e.g. a bad line, or a deal this user can't see
                ErrorMessage = ex.Message;
                return RedirectToPage(new { DealId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating invoice manually");
                ErrorMessage = "Failed to create invoice. Please try again.";
                return RedirectToPage(new { DealId });
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

        // 074: the nested InvoiceItemData is GONE. It duplicated the
        // top-level one in Edit.cshtml.cs (same namespace) with its own
        // int Quantity, so the two invoice pages rounded quantities
        // differently — and the comment that used to sit here said as
        // much and deferred it. Both pages deserialise the one declared
        // in Edit.cshtml.cs, which is the copy that now carries the
        // decimal quantity, the unit, the tax code and the percentage.
    }
}
