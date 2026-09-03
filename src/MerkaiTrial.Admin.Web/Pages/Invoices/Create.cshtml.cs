// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Invoices/Create.cshtml.cs
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
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Deals;
using MerkaiTrial.Admin.Web.Services.Invoices;
using MerkaiTrial.Admin.Web.Services.Products;
using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Application.Authorization;
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
        private readonly ILogger<CreateModel> _logger;

        public CreateModel(
            IInvoiceService invoiceService,
            IQuoteService quoteService,
            IProductService productService,
            IDealService dealService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
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

        [TempData]
        public string? SuccessMessage { get; set; }

        [TempData]
        public string? ErrorMessage { get; set; }

        public string TenantCurrencySymbol { get; private set; } = string.Empty;
        public string TenantCurrencyCode { get; private set; } = string.Empty;
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

                    //Products = await _productService.GetAllAsync(
                    //tenantId,
                    //PageNumber,
                    //PageSize,
                    //CategoryFilter,
                    //IsActiveFilter,
                    //SearchTerm);

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

                var tenantId = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                var dto = new CreateInvoiceFromQuoteDto
                {
                    TenantId = tenantId,
                    QuoteId = QuoteId.Value,
                    IssueDateUtc = IssueDate.ToUniversalTime(),
                    DueDateUtc = DueDate.ToUniversalTime(),
                    Notes = Notes,
                    CreatedBy = currentUser.FullName,
                    SendImmediately = sendImmediately
                };

                var invoice = await _invoiceService.CreateFromQuoteAsync(dto);

                SuccessMessage = sendImmediately
                    ? $"Invoice {invoice.Number} created and sent successfully!"
                    : $"Invoice {invoice.Number} created as draft!";

                return RedirectToPage("/Invoices/Detail", new { id = invoice.Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating invoice from quote");
                ErrorMessage = ex.Message;
                return RedirectToPage();
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

                var items = JsonSerializer.Deserialize<List<InvoiceItemData>>(itemsJson);

                if (items == null || items.Count == 0)
                {
                    ErrorMessage = "Please add at least one item to the invoice";
                    return RedirectToPage();
                }

                var tenantId = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                var dto = new CreateInvoiceDto
                {
                    TenantId = tenantId,
                    DealId = DealId.Value,
                    IssueDateUtc = IssueDate.ToUniversalTime(),
                    DueDateUtc = DueDate.ToUniversalTime(),
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
                        Quantity = i.Quantity,
                        LineDiscount = i.LineDiscount,
                        TaxRate = i.TaxRate / 100m
                    }).ToList()
                };

                var invoice = await _invoiceService.CreateAsync(dto);

                SuccessMessage = sendImmediately
                    ? $"Invoice {invoice.Number} created and sent successfully!"
                    : $"Invoice {invoice.Number} created as draft!";

                return RedirectToPage("/Invoices/Detail", new { id = invoice.Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating invoice manually");
                ErrorMessage = "Failed to create invoice. Please try again.";
                return RedirectToPage();
            }
        }

        // NOTE: this nested InvoiceItemData duplicates the top-level
        // InvoiceItemData declared in Edit.cshtml.cs (same namespace). They do not
        // collide because this one is nested, but it is confusing. Consider
        // deleting this one and using the shared type — deferred, out of scope.
        public class InvoiceItemData
        {
            public Guid? ProductId { get; set; }
            public string Name { get; set; } = string.Empty;
            public string? Description { get; set; }
            public decimal UnitPrice { get; set; }
            public int Quantity { get; set; }
            public decimal LineDiscount { get; set; }
            public decimal TaxRate { get; set; }
        }
    }
}
