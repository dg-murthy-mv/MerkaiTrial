// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Quotes/Create.cshtml.cs
//
// COMPLETE FILE — replaces the 017 version.
//
// CHANGES (029)
//
//   1. OnGetAsync NEVER CALLED InitializePermissionsAsync(). Every other
//      page in the app does. Without it CanCreate / CanUpdate / CanRead /
//      CanDelete are all false for the whole render, so any permission gate
//      the view puts on a control silently hides it — and the next person
//      to add one here would have spent an afternoon on that.
//
//   2. A QUOTE COULD EXPIRE BEFORE IT WAS ISSUED. Nothing checked, on the
//      client or the server. The view checks it now; this is the half that
//      still holds when the form is posted by hand.
//
//   3. THE LINE VALIDATION STOPPED AT "has a name". The old page relied on
//      required / min="1" / max="100" attributes in the markup, but its
//      buttons called form.submit(), which by specification skips HTML5
//      constraint validation — so none of those ever ran. A quantity of 0,
//      a negative discount and a 500% tax rate all reached the API. The
//      shared editor validates in the browser; these are the server-side
//      equivalents, and they match Edit's exactly.
//
//   4. CurrencySymbol was hiding AuthorizedPageModel.CurrencySymbol
//      (CS0108). Marked `new`, since this one deliberately holds the DEAL's
//      symbol rather than the workspace's.
//
//   Not changed: the currency is still resolved server-side by
//   ResolveCurrencyAsync and never taken from the browser, and the dates are
//   still stored as midnight UTC via SpecifyKind. Both were right already —
//   and both were the bugs still present on the Edit page, now fixed there.
//
// CHANGES (017 — quote approvals)
//   ✅ "Send to Customer" on a quote that breaks an approval rule now
//      creates it and SUBMITS IT FOR APPROVAL instead (the API would
//      refuse to send it). The message says so, and why.
//   ✅ The page shows the approval limits live while you type: the Send
//      button turns into "Save & submit for approval" and a banner lists
//      the lines over the limit (ApprovalRulesJson → the view's script).
//   ✅ API refusals (bad line, deal not visible) show their real message.
//   ✅ Deal list loads with pageSize 500 (was the first 20 deals).
//
// EARLIER FIXES:
//   ✅ ICurrentTenantService for tenant currency — no more hardcoded "INR"
//   ✅ ICurrentTenantService for default tax rate — no more hardcoded 0.11
//   ✅ Qualification stage added to quotable deals (triggers Proposal advance)
//   ✅ Removed IContactService/ITaxRateService dependency for tax lookup
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Deals;
using MerkaiTrial.Admin.Web.Services.Products;
using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Configuration;   // 067: TaxCodes
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
        private readonly IQuoteApprovalService _approvals;
        private readonly ILogger<CreateModel> _logger;

        protected override string ModuleName => Modules.Quotes;

        public CreateModel(
            IQuoteService quoteService,
            IDealService dealService,
            IProductService productService,
            ICurrentUserService currentUserService,
            ICurrentTenantService currentTenantService,
            IQuoteApprovalService approvals,
            IAuthorizationService authorizationService,
            ILogger<CreateModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _quoteService         = quoteService;
            _dealService          = dealService;
            _productService       = productService;
            _currentUserService   = currentUserService;
            _currentTenantService = currentTenantService;
            _approvals            = approvals;
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

        /// <summary>
        /// The DEAL's currency symbol, which is not necessarily the
        /// workspace's. `new` because AuthorizedPageModel also has a
        /// CurrencySymbol (the workspace one) and hiding it without saying so
        /// is a CS0108 warning.
        /// </summary>
        public new string CurrencySymbol  { get; set; } = string.Empty;
        public decimal DefaultTaxRate      { get; set; } = 0m;   // percentage e.g. 18
        public string  DefaultTaxRateName  { get; set; } = "Tax";

        [BindProperty] public DateTime IssueDate   { get; set; } = DateTime.Today;
        [BindProperty] public DateTime ExpiryDate  { get; set; } = DateTime.Today.AddDays(30);
        [BindProperty] public Guid?    SelectedDealId { get; set; }

        // ── Approval rules for the live hint (017) ────────────────────
        /// <summary>JSON for the view's script: { enabled, maxDiscountPercent, maxQuoteTotal, exempt }.</summary>
        public string ApprovalRulesJson { get; private set; } = "{\"enabled\":false}";

        // ── GET ───────────────────────────────────────────────────────

        public async Task<IActionResult> OnGetAsync()
        {
            var check = await ValidatePermissionAsync(Actions.Create);
            if (check != null) return check;

            // ✅ (029) Was missing entirely. Without it every Can* flag on the
            // base class stays false for the whole render, so any permission
            // gate in the view hides the control it guards.
            await InitializePermissionsAsync();

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();

                // ✅ Load tenant defaults first
                await LoadTenantCurrencyAsync();
                await LoadApprovalRulesAsync();

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

                // ✅ (029) The rest of the line validation. The markup carried
                // required / min="1" / max="100", but the old page submitted with
                // form.submit(), which skips HTML5 validation — so a quantity of
                // 0, a negative discount and a 500% tax rate all got this far.
                // These match the checks in Edit.cshtml.cs one for one.
                // 052: was `< 1`, with the whole-number rule in the editor to
                // match. Both had to go — 12.5 m² and 3.5 days are the point of
                // this round. What is left is the rule that still holds.
                if (items.Any(i => i.Quantity <= 0))
                {
                    ErrorMessage = "Every line needs a quantity above zero.";
                    return RedirectToPage(new { DealId = DealId.Value });
                }

                if (items.Any(i => i.UnitPrice <= 0))
                {
                    ErrorMessage = "Every line needs a unit price above zero.";
                    return RedirectToPage(new { DealId = DealId.Value });
                }

                if (items.Any(i => i.TaxRate < 0 || i.TaxRate > 100))
                {
                    ErrorMessage = "Tax rates must be between 0 and 100%.";
                    return RedirectToPage(new { DealId = DealId.Value });
                }

                // 055: the percentage has its own range, and the API enforces
                // both. This is the early, friendlier copy.
                if (items.Any(i => i.DiscountPercent is < 0 or > 100))
                {
                    ErrorMessage = "A discount percentage must be between 0 and 100.";
                    return RedirectToPage(new { DealId = DealId.Value });
                }

                if (items.Any(i => i.LineDiscount < 0))
                {
                    ErrorMessage = "A line discount can't be negative.";
                    return RedirectToPage(new { DealId = DealId.Value });
                }

                // 067: the column is NVARCHAR(20). TaxCodes.Normalise would
                // quietly SHORTEN anything longer, which is the right thing
                // for an API caller and the wrong thing for somebody sitting
                // in front of the form — they would save, reopen, and find a
                // code that is not the one they typed. Say so instead. The
                // input carries maxlength=, so this only fires on a paste
                // that bypassed it or a hand-made post.
                if (items.Any(i => (i.TaxCode ?? string.Empty).Trim().Length > TaxCodes.MaxLength))
                {
                    // Deliberately NOT using TaxCodes.ColumnHeaderFor here:
                    // the currency is resolved further down this method, and
                    // reading the page's Currency property on a POST would
                    // depend on whether the model binder happened to round-
                    // trip it. A plain "tax code" is right in every market.
                    ErrorMessage = $"A tax code can be at most {TaxCodes.MaxLength} characters.";
                    return RedirectToPage(new { DealId = DealId.Value });
                }

                // ✅ (029) A quote that expires before it is issued was accepted
                // by both the page and the API.
                if (ExpiryDate.Date < IssueDate.Date)
                {
                    ErrorMessage = "The expiry date can't be before the issue date.";
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
                        // 052. Sent even though the handler would fill it in
                        // from the product: a CUSTOM line has no product to
                        // ask, and "3.5 days of consulting" typed by hand is
                        // exactly the case units were added for.
                        UnitOfMeasure = i.UnitOfMeasure,
                        LineDiscount = i.LineDiscount,
                        DiscountPercent = i.DiscountPercent,   // 055
                        TaxRate      = i.TaxRate / 100m,  // % → decimal fraction
                        // 067. Sent for the same reason the unit is: a CUSTOM
                        // line has no product to ask, and on an Indian
                        // invoice it still needs a code.
                        //
                        // ?? "" rather than passing null: the editor always
                        // posts this field, so an empty value means the rep
                        // left it empty — and null would tell the handler to
                        // go and fetch the product's code instead, which
                        // would quietly undo clearing the box.
                        TaxCode      = i.TaxCode ?? string.Empty
                    }).ToList()
                };

                var quote = await _quoteService.CreateAsync(createDto);

                _logger.LogInformation("Quote {Number} created — deal auto-advancing to Proposal", quote.Number);

                if (sendToCustomer)
                {
                    SuccessMessage = await SendOrSubmitAsync(tenantId, quote.Id, quote.Number);
                }
                else
                {
                    SuccessMessage = $"Quote {quote.Number} created as draft. Deal has been moved to Proposal stage.";
                }

                return RedirectToPage("/Quotes/Detail", new { id = quote.Id });
            }
            catch (InvalidOperationException ex)
            {
                // The API refused and said why (a bad line, a deal you can't see…).
                ErrorMessage = ex.Message;
                return DealId.HasValue
                    ? RedirectToPage(new { DealId = DealId.Value })
                    : RedirectToPage();
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

        /// <summary>
        /// "Send to Customer" after create. Within the rules (or an admin) →
        /// sent. Over a limit → submitted for approval, and the message says
        /// why. The quote exists either way, so a failure here is reported
        /// but never loses it.
        /// </summary>
        private async Task<string> SendOrSubmitAsync(Guid tenantId, Guid quoteId, string number)
        {
            try
            {
                var state = await _approvals.GetStateAsync(quoteId);

                if (state.CanSend)
                {
                    await _quoteService.UpdateStatusAsync(tenantId, quoteId, "Sent", $"{Request.Scheme}://{Request.Host}");
                    return $"Quote {number} created and sent to customer!";
                }

                if (state.CanSubmit)
                {
                    await _approvals.SubmitAsync(quoteId, null);
                    return $"Quote {number} created and sent for approval — {string.Join(" ", state.Reasons)} " +
                           "You can send it once it's approved.";
                }

                return $"Quote {number} created as draft. It needs approval before it can be sent.";
            }
            catch (InvalidOperationException ex)
            {
                return $"Quote {number} created as draft, but it wasn't sent: {ex.Message}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Quote {Number} created but send/submit failed", number);
                return $"Quote {number} created as draft, but it couldn't be sent. Open it and try again.";
            }
        }

        private async Task LoadApprovalRulesAsync()
        {
            try
            {
                var rules = await _approvals.GetSettingsAsync();
                var me = await _currentUserService.GetCurrentUserAsync();

                ApprovalRulesJson = JsonSerializer.Serialize(new
                {
                    enabled = rules.IsEnabled,
                    maxDiscountPercent = rules.MaxDiscountPercent,
                    maxQuoteTotal = rules.MaxQuoteTotal,
                    exempt = me.IsTenantAdmin
                });
            }
            catch (Exception ex)
            {
                // The hint is a convenience — the API still enforces the rules.
                _logger.LogWarning(ex, "Could not load quote approval rules for the live hint");
            }
        }

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

            // 056. WAS: `rawRate < 1m ? rawRate * 100m : rawRate` — a guess
            // at whether the stored number was a fraction or a percentage.
            // It is always a PERCENTAGE: TaxRate.Rate and
            // Country.DefaultTaxRate both store it that way, and Create/Edit
            // validate 0–100. The guess was harmless for 18 and wrong for
            // every rate below 1% — a genuine 0.5% rate became 50%.
            // GetDefaultTaxRateAsync's summary now says so explicitly.
            // Normalise to percentage for the JS tax input
            DefaultTaxRate = await _currentTenantService.GetDefaultTaxRateAsync();

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
                var allDeals = await _dealService.GetAllAsync(tenantId, pageSize: 500);

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

    /// <summary>
    /// The shape _QuoteItemsEditor.cshtml posts as itemsJson. Declared here
    /// and used by Edit.cshtml.cs too, so the two pages cannot disagree about
    /// what a line is.
    ///
    /// 052: Quantity is DECIMAL and UnitOfMeasure is new. TaxRate stays a
    /// PERCENTAGE here (18 for 18%) while the DTOs use a fraction (0.18) —
    /// that asymmetry is deliberate and long-standing, and the conversion
    /// happens once, where the DTO is built.
    /// </summary>
    public class QuoteItemData
    {
        public Guid?   Id          { get; set; }
        public Guid?   ProductId   { get; set; }
        public string  Name        { get; set; } = string.Empty;
        public string  Description { get; set; } = string.Empty;
        public decimal UnitPrice   { get; set; }

        /// <summary>052: decimal — 12.5, 3.5, 0.75.</summary>
        public decimal Quantity    { get; set; }

        /// <summary>
        /// 052: a code from UnitsOfMeasure. Nullable because a quote saved by
        /// an older page, or a hand-made post, simply will not have one — the
        /// handler then falls back to the product's unit, or "unit".
        /// </summary>
        public string? UnitOfMeasure { get; set; }

        public decimal LineDiscount { get; set; }

        /// <summary>
        /// 055: null when the discount was typed as an amount. When it is
        /// set, the API recomputes LineDiscount from it and ignores whatever
        /// amount came with it — LineDiscounts.Resolve.
        /// </summary>
        public decimal? DiscountPercent { get; set; }

        public decimal TaxRate     { get; set; }  // As percentage (18 for 18%)

        /// <summary>
        /// 067: the tax classification code for this line — HSN / SAC in
        /// India, the local equivalent elsewhere.
        ///
        /// The editor ALWAYS posts this field, empty string included, and
        /// that matters: LineTaxCodes.Resolve reads "" as "this line has no
        /// classification" and null as "nothing was said, ask the product".
        /// Both pages forward it as `?? string.Empty` so clearing the box
        /// actually clears the code instead of being helpfully refilled from
        /// the catalogue.
        /// </summary>
        public string? TaxCode { get; set; }
    }
}
