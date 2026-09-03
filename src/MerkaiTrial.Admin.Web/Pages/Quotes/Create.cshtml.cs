// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Quotes/Create.cshtml.cs
// FIXES:
//   ✅ ICurrentTenantService for tenant currency — no more hardcoded "INR"
//   ✅ ICurrentTenantService for default tax rate — no more hardcoded 0.11
//   ✅ Qualification stage added to quotable deals (triggers Proposal advance)
//   ✅ Removed IContactService/ITaxRateService dependency for tax lookup
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Deals;
using MerkaiTrial.Admin.Web.Services.Products;
using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.Text.Json;

namespace MerkaiTrial.Admin.Web.Pages.Quotes
{
    public class CreateModel : AuthorizedPageModel
    {
        private readonly IQuoteService _quoteService;
        private readonly IDealService _dealService;
        private readonly IProductService _productService;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _currentTenantService;
        private readonly ILogger<CreateModel> _logger;

        protected override string ModuleName => Modules.Quotes;

        public CreateModel(
            IQuoteService quoteService,
            IDealService dealService,
            IProductService productService,
            ICurrentUserService currentUserService,
            ICurrentTenantService currentTenantService,
            IAuthorizationService authorizationService,
            ILogger<CreateModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _quoteService         = quoteService;
            _dealService          = dealService;
            _productService       = productService;
            _currentUserService   = currentUserService;
            _currentTenantService = currentTenantService;
            _logger               = logger;
        }

        // ── Properties ────────────────────────────────────────────────

        [BindProperty(SupportsGet = true)]
        public Guid? DealId { get; set; }

        [TempData] public string? ErrorMessage  { get; set; }
        [TempData] public string? SuccessMessage { get; set; }

        public DealDetailDto? Deal { get; set; }
        public List<ProductListItem> Products { get; set; } = new();
        public List<SelectListItem>  AvailableDeals { get; set; } = new();
        public bool ShowDealSelection { get; set; }

        // ✅ Tenant-driven — set from ICurrentTenantService
        public string  Currency            { get; set; } = string.Empty;
        public string  CurrencySymbol      { get; set; } = string.Empty;
        public decimal DefaultTaxRate      { get; set; } = 0m;   // percentage e.g. 18
        public string  DefaultTaxRateName  { get; set; } = "Tax";

        [BindProperty] public DateTime IssueDate   { get; set; } = DateTime.Today;
        [BindProperty] public DateTime ExpiryDate  { get; set; } = DateTime.Today.AddDays(30);
        [BindProperty] public Guid?    SelectedDealId { get; set; }

        // ── GET ───────────────────────────────────────────────────────

        public async Task<IActionResult> OnGetAsync()
        {
            var check = await ValidatePermissionAsync(Actions.Create);
            if (check != null) return check;

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();

                // ✅ Load tenant defaults first
                await LoadTenantCurrencyAsync();

                if (!DealId.HasValue || DealId.Value == Guid.Empty)
                {
                    ShowDealSelection = true;
                    await LoadAvailableDealsAsync(tenantId);
                    await LoadProductsAsync(tenantId);
                    return Page();
                }

                ShowDealSelection = false;
                SelectedDealId    = DealId;

                await LoadDealAsync(tenantId, DealId.Value);
                await LoadProductsAsync(tenantId);

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load quote creation page");
                ErrorMessage      = $"An unexpected error occurred: {ex.Message}";
                ShowDealSelection = true;
                return Page();
            }
        }

        // ── SELECT DEAL ───────────────────────────────────────────────

        public async Task<IActionResult> OnPostSelectDealAsync()
        {
            var check = await ValidatePermissionAsync(Actions.Create);
            if (check != null) return check;

            if (!SelectedDealId.HasValue || SelectedDealId.Value == Guid.Empty)
            {
                ErrorMessage = "Please select a deal";
                return RedirectToPage();
            }
            return RedirectToPage(new { DealId = SelectedDealId.Value });
        }

        // ── CREATE QUOTE ──────────────────────────────────────────────

        public async Task<IActionResult> OnPostCreateAsync(string itemsJson, bool sendToCustomer = false)
        {
            var check = await ValidatePermissionAsync(Actions.Create);
            if (check != null) return check;

            try
            {
                if (!DealId.HasValue || DealId.Value == Guid.Empty)
                {
                    ErrorMessage = "Deal ID is required to create a quote";
                    return RedirectToPage();
                }

                if (string.IsNullOrWhiteSpace(itemsJson))
                {
                    ErrorMessage = "Please add at least one item to the quote";
                    return RedirectToPage(new { DealId = DealId.Value });
                }

                var tenantId    = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                // ✅ Guard: JS may send "id":0 (integer) or "id":"" for new items
                // System.Text.Json cannot parse those into Guid? — normalise to null
                itemsJson = System.Text.RegularExpressions.Regex.Replace(
                    itemsJson,
                    @"""id""\s*:\s*(-?\d+|"""")",
                    @"""id"":null",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                var items = JsonSerializer.Deserialize<List<QuoteItemData>>(
                    itemsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (items == null || items.Count == 0)
                {
                    ErrorMessage = "Please add at least one item to the quote";
                    return RedirectToPage(new { DealId = DealId.Value });
                }

                if (items.Any(i => string.IsNullOrWhiteSpace(i.Name)))
                {
                    ErrorMessage = "All items must have a name";
                    return RedirectToPage(new { DealId = DealId.Value });
                }

                // ── BUG FIX 2026-08-03: currency was persisting as '' ───────
                // `Currency` is a plain property, not [BindProperty], and the
                // create form posts only itemsJson + DealId. It is populated by
                // LoadTenantCurrencyAsync()/LoadDealAsync() during OnGet, but a
                // POST gets a FRESH page model instance, so it was still
                // string.Empty here and every UI-created quote stored ''.
                // Confirmed in the DB: QUO-0001..0003 (seeded) = 'INR',
                // QUO-0004/0005 (UI) = LEN 0. Invoices inherited the blank.
                //
                // Same trap as the CanCreate/CanRead properties: values set in
                // OnGet do not survive to a POST handler.
                //
                // Resolved server-side rather than via a hidden field — currency
                // determines what the customer is billed, so it must not be
                // supplied by the browser.
                var currency = await ResolveCurrencyAsync(tenantId, DealId.Value);
                if (string.IsNullOrWhiteSpace(currency))
                {
                    _logger.LogError(
                        "Could not resolve a currency for deal {DealId} on tenant {TenantId}; " +
                        "refusing to create a quote with a blank currency.", DealId.Value, tenantId);
                    ErrorMessage = "Could not determine the currency for this deal. " +
                                   "Check the tenant's country settings and try again.";
                    return RedirectToPage(new { DealId = DealId.Value });
                }

                var createDto = new CreateQuoteDto
                {
                    TenantId     = tenantId,
                    DealId       = DealId.Value,
                    // ── BUG FIX 2026-08-03: date rolled back one day ─────────
                    // These are date-only values. IssueDate arrives with
                    // Kind=Unspecified, so ToUniversalTime() treated midnight as
                    // LOCAL and subtracted the offset — in IST (UTC+5:30)
                    // 03-08 00:00 became 02-08 18:30 UTC. The CRM hid it by
                    // converting back to tenant time; the public quote page,
                    // which has no tenant context, rendered raw UTC and showed
                    // "Issued 02 Aug 2026" for a quote issued on the 3rd.
                    // Storing the calendar date as midnight UTC keeps it stable.
                    IssueDateUtc = DateTime.SpecifyKind(IssueDate.Date, DateTimeKind.Utc),
                    ExpiresAtUtc = DateTime.SpecifyKind(ExpiryDate.Date, DateTimeKind.Utc),
                    Currency     = currency,
                    CreatedBy    = currentUser.FullName,
                    Items        = items.Select(i => new CreateQuoteItemDto
                    {
                        ProductId    = i.ProductId,
                        Name         = i.Name,
                        Description  = i.Description,
                        UnitPrice    = i.UnitPrice,
                        Quantity     = i.Quantity,
                        LineDiscount = i.LineDiscount,
                        TaxRate      = i.TaxRate / 100m  // % → decimal fraction
                    }).ToList()
                };

                var quote = await _quoteService.CreateAsync(createDto);

                _logger.LogInformation("Quote {Number} created — deal auto-advancing to Proposal", quote.Number);

                if (sendToCustomer)
                {
                    await _quoteService.UpdateStatusAsync(tenantId, quote.Id, "Sent");
                    SuccessMessage = $"Quote {quote.Number} created and sent to customer!";
                }
                else
                {
                    SuccessMessage = $"Quote {quote.Number} created as draft. Deal has been moved to Proposal stage.";
                }

                return RedirectToPage("/Quotes/Detail", new { id = quote.Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create quote");
                ErrorMessage = $"Failed to create quote: {ex.Message}";
                return DealId.HasValue
                    ? RedirectToPage(new { DealId = DealId.Value })
                    : RedirectToPage();
            }
        }

        // ── HELPERS ───────────────────────────────────────────────────

        // Authoritative currency for a new quote, resolved on the server.
        // Order matches LoadDealAsync so the POST agrees with what the GET
        // showed the user: the deal's own currency wins, tenant currency is the
        // fallback. Returns "" only if both are missing, which the caller
        // treats as a hard failure rather than writing a blank to the DB.
        private async Task<string> ResolveCurrencyAsync(Guid tenantId, Guid dealId)
        {
            try
            {
                var deal = await _dealService.GetDetailAsync(tenantId, dealId);
                if (!string.IsNullOrWhiteSpace(deal?.Currency))
                    return deal!.Currency!;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not read deal {DealId} while resolving currency; " +
                    "falling back to the tenant currency.", dealId);
            }

            return _currentTenantService.GetCurrencyCode() ?? string.Empty;
        }

        // ✅ Load currency + tax from ICurrentTenantService
        private async Task LoadTenantCurrencyAsync()
        {
            Currency           = _currentTenantService.GetCurrencyCode();
            CurrencySymbol     = _currentTenantService.GetCurrencySymbol();
            DefaultTaxRateName = _currentTenantService.GetTaxLabel();

            // GetDefaultTaxRateAsync may return decimal fraction (0.18) or percentage (18)
            // Normalise to percentage for the JS tax input
            var rawRate = await _currentTenantService.GetDefaultTaxRateAsync();
            DefaultTaxRate = rawRate < 1m ? rawRate * 100m : rawRate;

            _logger.LogInformation(
                "Tenant currency: {Code} {Symbol}, Tax: {Rate}% ({Label})",
                Currency, CurrencySymbol, DefaultTaxRate, DefaultTaxRateName);
        }

        // ✅ Override currency with deal's currency when deal is loaded
        private async Task LoadDealAsync(Guid tenantId, Guid dealId)
        {
            Deal = await _dealService.GetDetailAsync(tenantId, dealId);

            if (Deal == null)
                throw new KeyNotFoundException($"Deal {dealId} not found");

            // Deal's own currency wins; fall back to tenant currency
            if (!string.IsNullOrEmpty(Deal.Currency))
            {
                Currency       = Deal.Currency;
                CurrencySymbol = GetSymbolForCode(Deal.Currency);
            }

            _logger.LogInformation("Deal loaded: {Title}, Currency: {Currency}", Deal.Title, Currency);
        }

        // ✅ Qualification included — creating a quote promotes the deal to Proposal
        private async Task LoadAvailableDealsAsync(Guid tenantId)
        {
            try
            {
                var allDeals = await _dealService.GetAllAsync(tenantId);

                var quotableDeals = allDeals.Items
                    .Where(d =>
                        string.Equals(d.Stage, "Discovery", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(d.Stage, "Qualification", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(d.Stage, "Proposal", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(d => d.ExpectedCloseDateUtc)
                    .ToList();

                AvailableDeals = quotableDeals.Select(d => new SelectListItem
                {
                    Value = d.Id.ToString(),
                    Text  = $"{d.Title} — {d.CompanyName} ({d.Stage})"
                }).ToList();

                if (!AvailableDeals.Any())
                    ErrorMessage = "No deals available for quoting. Deals must be in Discovery, Qualification or Proposal stage.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load deals");
                AvailableDeals = new();
            }
        }

        private async Task LoadProductsAsync(Guid tenantId)
        {
            try
            {
                var paginated = await _productService.GetAllAsync(
                    tenantId: tenantId, pageNumber: 1, pageSize: 1000,
                    category: null, isActive: true, searchTerm: null);

                Products = paginated?.Items ?? new();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load products");
                Products = new();
            }
        }

        public string GetSymbolForCode(string? code) => code switch
        {
            "INR" => "₹",
            "THB" => "฿",
            "PHP" => "₱",
            "AED" => "د.إ",
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            _     => _currentTenantService.GetCurrencySymbol()
        };
    }

    public class QuoteItemData
    {
        public Guid?   Id          { get; set; }
        public Guid?   ProductId   { get; set; }
        public string  Name        { get; set; } = string.Empty;
        public string  Description { get; set; } = string.Empty;
        public decimal UnitPrice   { get; set; }
        public int     Quantity    { get; set; }
        public decimal LineDiscount { get; set; }
        public decimal TaxRate     { get; set; }  // As percentage (18 for 18%)
    }
}
