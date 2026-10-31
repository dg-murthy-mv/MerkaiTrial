// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Quotes/Create.cshtml.cs
//
// COMPLETE FILE — replaces the 017 version.
//
// CHANGES (108 — choosing the deal)
//
//   1. ONLY THE NEWEST 500 DEALS COULD BE QUOTED. The deal drop-down
//      loaded 500 deals and kept those in Discovery, Qualification or
//      Proposal. In a workspace with more deals than that, older open
//      deals could not be chosen here at all.
//
//   2. THE STAGES WERE HARD-CODED NAMES. Since 104 each workspace names
//      its own stages; a workspace whose open stages are, say, "Prospect"
//      and "Demo" saw "Nothing to quote yet" with open deals in the
//      pipeline. A deal can be quoted whenever its stage is an OPEN one
//      (not Won or Lost), which the API now answers directly
//      (GET /api/deals?openOnly=true).
//
//   The drop-down starts with the 20 newest open deals and has a search
//   box (FindDeals below, JSON for wwwroot/js/record-search.js) that looks
//   through every open deal. The sentence about moving the deal to
//   "Proposal" is gone too: since 036 that move happens only if the
//   workspace set one up in its pipeline rules.
//
// CHANGES (081 — custom fields on quotes)
//
//   1. The workspace's Quote custom fields, through the shared
//      _CustomFieldInputs partial (the one Contacts, Deals, Companies,
//      Leads and Products use), checked here first and sent as
//      CreateQuoteDto.CustomFields. If the field list could not be loaded,
//      CustomFields is sent as NULL ("say nothing").
//
//   2. A REFUSED SAVE NO LONGER THROWS AWAY THE FORM. Every check below
//      used to REDIRECT back to the page — which started it again from an
//      empty line, so one mistake cost the rep every line they had typed.
//      With required custom fields that would be an everyday event. The
//      checks now RE-RENDER (RedisplayAsync) with the posted lines
//      (QuoteItemsSeed.FromPosted), the dates and the field values, and
//      the reason in PageError — a plain property, because TempData is
//      for a message that survives a redirect, and this response does not
//      redirect.
//
//   3. "No deals available…" was put in TempData on a page that renders,
//      so it appeared on the NEXT page the user opened. The view's own
//      "Nothing to quote yet" panel already says it, so it is gone; the
//      GET's load-failure message is PageError for the same reason.
//
//   4. The success message no longer promises "Deal has been moved to
//      Proposal stage". Since 036 the move is the workspace's own setting
//      and is often switched off; the message could not know.
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

using MerkaiTrial.Admin.Web.Pages.Shared;            // 081 — CustomFieldFormVm, CustomFieldForm
using MerkaiTrial.Admin.Web.Services.CustomFields;   // 081
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
        private readonly ICustomFieldService _customFields;      // 081
        private readonly ILogger<CreateModel> _logger;

        protected override string ModuleName => Modules.Quotes;

        public CreateModel(
            IQuoteService quoteService,
            IDealService dealService,
            IProductService productService,
            ICurrentUserService currentUserService,
            ICurrentTenantService currentTenantService,
            IQuoteApprovalService approvals,
            ICustomFieldService customFields,                    // 081
            IAuthorizationService authorizationService,
            ILogger<CreateModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _customFields         = customFields;
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

        /// <summary>
        /// 081. Why the form came back, on THIS response. Not TempData:
        /// TempData is for a message that has to survive a redirect.
        /// </summary>
        public string? PageError { get; private set; }

        /// <summary>
        /// 081. The editor's starting lines — "[]" on a fresh form, the posted
        /// lines when the form is shown again after a refusal.
        /// </summary>
        public string InitialItemsJson { get; private set; } = "[]";

        /// <summary>081. Posted as CustomFields[&lt;field id&gt;] = value.</summary>
        [BindProperty]
        public Dictionary<string, string?> CustomFields { get; set; } = new();

        /// <summary>081. Active Quote custom fields, in order.</summary>
        public List<CustomFieldDefinitionDto> ActiveCustomFields { get; private set; } = new();
        public bool CustomFieldsLoadFailed { get; private set; }

        /// <summary>081. The model for _CustomFieldInputs.</summary>
        public CustomFieldFormVm CustomFieldInputs => new()
        {
            Fields       = ActiveCustomFields,
            Values       = CustomFields,
            LoadFailed   = CustomFieldsLoadFailed,
            CanConfigure = UserCanRead(Modules.Settings),
            EntityType   = CustomFieldEntityTypes.Quote
        };

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
                await LoadCustomFieldsAsync();     // 081

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load quote creation page");
                // 081: PageError — this response renders, and TempData set
                // here would surface on the NEXT page instead. No raw
                // exception text on screen either.
                PageError         = "The quote form could not be loaded. Please try again.";
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

            if (!DealId.HasValue || DealId.Value == Guid.Empty)
            {
                // Nothing typed can be lost here — there was no deal, so no
                // form. Redirecting to the deal picker is right.
                ErrorMessage = "Deal ID is required to create a quote";
                return RedirectToPage();
            }

            var tenantId = _currentUserService.GetCurrentTenantId();

            // 081. Read the lines FIRST, so every refusal below can put them
            // straight back on the form (RedisplayAsync) instead of
            // redirecting and throwing away everything that was typed.
            var items = QuoteItemsSeed.TryParse(itemsJson);

            // 081 — each custom field's message under its own input.
            await LoadCustomFieldsAsync();
            CustomFieldForm.Validate(ActiveCustomFields, CustomFields, ModelState);

            try
            {
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                if (items == null || items.Count == 0)
                    return await RedisplayAsync(tenantId, items, "Please add at least one item to the quote.");

                if (items.Any(i => string.IsNullOrWhiteSpace(i.Name)))
                    return await RedisplayAsync(tenantId, items, "All items must have a name.");

                // ✅ (029) The rest of the line validation. The markup carried
                // required / min="1" / max="100", but the old page submitted with
                // form.submit(), which skips HTML5 validation — so a quantity of
                // 0, a negative discount and a 500% tax rate all got this far.
                // These match the checks in Edit.cshtml.cs one for one.
                // 052: was `< 1` — 12.5 m² and 3.5 days are legal.
                if (items.Any(i => i.Quantity <= 0))
                    return await RedisplayAsync(tenantId, items, "Every line needs a quantity above zero.");

                if (items.Any(i => i.UnitPrice <= 0))
                    return await RedisplayAsync(tenantId, items, "Every line needs a unit price above zero.");

                if (items.Any(i => i.TaxRate < 0 || i.TaxRate > 100))
                    return await RedisplayAsync(tenantId, items, "Tax rates must be between 0 and 100%.");

                // 055: the percentage has its own range, and the API enforces
                // both. This is the early, friendlier copy.
                if (items.Any(i => i.DiscountPercent is < 0 or > 100))
                    return await RedisplayAsync(tenantId, items, "A discount percentage must be between 0 and 100.");

                if (items.Any(i => i.LineDiscount < 0))
                    return await RedisplayAsync(tenantId, items, "A line discount can't be negative.");

                // 067: the column is NVARCHAR(20). TaxCodes.Normalise would
                // quietly SHORTEN anything longer — right for an API caller,
                // wrong for somebody at the form, who would reopen the quote
                // and find a code that is not the one they typed. Plain "tax
                // code" wording: the currency is only resolved further down.
                if (items.Any(i => (i.TaxCode ?? string.Empty).Trim().Length > TaxCodes.MaxLength))
                    return await RedisplayAsync(tenantId, items, $"A tax code can be at most {TaxCodes.MaxLength} characters.");

                // ✅ (029) A quote that expires before it is issued was accepted
                // by both the page and the API.
                if (ExpiryDate.Date < IssueDate.Date)
                    return await RedisplayAsync(tenantId, items, "The expiry date can't be before the issue date.");

                // 081 — a custom field problem (each message is under its
                // own input; this is the sentence at the top).
                // 081 — a custom field is wrong: its message is under its own input.
                if (CustomFieldForm.HasErrors(ModelState))
                    return await RedisplayAsync(tenantId, items, "Please correct the highlighted fields.");

                // A date box that failed to bind (a cleared box posts "") is said
                // plainly rather than saving the default binding left behind. ONLY
                // these keys: with nullable reference types on, an unposted string
                // property also lands in ModelState, and must not block the save.
                if (new[] { nameof(IssueDate), nameof(ExpiryDate) }.Any(k => ModelState[k]?.Errors.Count > 0))
                    return await RedisplayAsync(tenantId, items, "Some of the dates could not be read. Please check them and try again.");

                // ── BUG FIX 2026-08-03: currency was persisting as '' ───────
                // `Currency` is a plain property, not [BindProperty], and a POST
                // gets a FRESH page model instance, so it is resolved again here
                // — server-side, because currency determines what the customer
                // is billed and must not be supplied by the browser.
                var currency = await ResolveCurrencyAsync(tenantId, DealId.Value);
                if (string.IsNullOrWhiteSpace(currency))
                {
                    _logger.LogError(
                        "Could not resolve a currency for deal {DealId} on tenant {TenantId}; " +
                        "refusing to create a quote with a blank currency.", DealId.Value, tenantId);
                    return await RedisplayAsync(tenantId, items,
                        "Could not determine the currency for this deal. Check the workspace's country settings and try again.");
                }

                var createDto = new CreateQuoteDto
                {
                    TenantId     = tenantId,
                    DealId       = DealId.Value,
                    // ── BUG FIX 2026-08-03: date rolled back one day ─────────
                    // Date-only values, stored as midnight UTC with SpecifyKind.
                    // ToUniversalTime() treated midnight as LOCAL and moved the
                    // date back a day in IST.
                    IssueDateUtc = DateTime.SpecifyKind(IssueDate.Date, DateTimeKind.Utc),
                    ExpiresAtUtc = DateTime.SpecifyKind(ExpiryDate.Date, DateTimeKind.Utc),
                    Currency     = currency,
                    CreatedBy    = currentUser.FullName,

                    // 081 — null when the field list could not be loaded: the
                    // form had no custom inputs, so say nothing about them.
                    CustomFields = CustomFieldsLoadFailed
                        ? null
                        : CustomFieldForm.ToSubmission(CustomFields, ActiveCustomFields),

                    Items        = items.Select(i => new CreateQuoteItemDto
                    {
                        ProductId    = i.ProductId,
                        Name         = i.Name,
                        Description  = i.Description,
                        UnitPrice    = i.UnitPrice,
                        Quantity     = i.Quantity,
                        // 052. Sent even though the handler would fill it in
                        // from the product: a CUSTOM line has no product to ask.
                        UnitOfMeasure = i.UnitOfMeasure,
                        LineDiscount = i.LineDiscount,
                        DiscountPercent = i.DiscountPercent,   // 055
                        TaxRate      = i.TaxRate / 100m,  // % → decimal fraction
                        // 067. ?? "" rather than null: an empty value means the
                        // rep left it empty, and null would tell the handler to
                        // fetch the product's code — undoing a cleared box.
                        TaxCode      = i.TaxCode ?? string.Empty
                    }).ToList()
                };

                var quote = await _quoteService.CreateAsync(createDto);

                _logger.LogInformation("Quote {Number} created", quote.Number);

                // TempData is right from here on: this response REDIRECTS.
                if (sendToCustomer)
                {
                    SuccessMessage = await SendOrSubmitAsync(tenantId, quote.Id, quote.Number);
                }
                else
                {
                    SuccessMessage = $"Quote {quote.Number} created as draft.";
                }

                return RedirectToPage("/Quotes/Detail", new { id = quote.Id });
            }
            catch (InvalidOperationException ex)
            {
                // The API refused and said why — a bad line, a deal you can't
                // see, a required custom field. The form comes back as typed.
                return await RedisplayAsync(tenantId, items, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create quote");
                return await RedisplayAsync(tenantId, items, "The quote could not be created. Please try again.");
            }
        }

        /// <summary>
        /// 081. Show the form again with what was posted: the lines, the
        /// dates (bound properties) and the custom field values, plus one
        /// sentence saying what is wrong (PageError — a plain property,
        /// because this response renders rather than redirects).
        /// </summary>
        private async Task<IActionResult> RedisplayAsync(Guid tenantId, List<QuoteItemData>? items, string message)
        {
            PageError = message;

            // Permissions do not survive a POST — fresh page model — so the
            // Can* flags must be filled again before the view reads them.
            await InitializePermissionsAsync();

            try
            {
                await LoadTenantCurrencyAsync();
                await LoadApprovalRulesAsync();

                ShowDealSelection = false;
                SelectedDealId    = DealId;

                await LoadDealAsync(tenantId, DealId!.Value);
                await LoadProductsAsync(tenantId);
            }
            catch (Exception ex)
            {
                // The deal itself could not be read (deleted, or no longer
                // visible). There is no form to show — go back to the picker.
                _logger.LogError(ex, "Could not redisplay the quote form for deal {DealId}", DealId);
                ErrorMessage = message;
                return RedirectToPage();
            }

            InitialItemsJson = items is { Count: > 0 } ? QuoteItemsSeed.FromPosted(items) : "[]";
            return Page();
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

        /// <summary>
        /// 081. The active Quote custom fields. A failure is remembered
        /// (CustomFieldsLoadFailed) so the form says why the fields are
        /// missing and the save sends CustomFields = null.
        /// </summary>
        private async Task LoadCustomFieldsAsync()
        {
            try
            {
                ActiveCustomFields = (await _customFields.GetDefinitionsAsync(CustomFieldEntityTypes.Quote, includeInactive: false))
                    .Where(f => f.IsActive)
                    .OrderBy(f => f.SortOrder)
                    .ToList();
                CustomFieldsLoadFailed = false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Custom fields could not be loaded for the New Quote form");
                ActiveCustomFields     = new List<CustomFieldDefinitionDto>();
                CustomFieldsLoadFailed = true;
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

        // 108 — every OPEN deal can be quoted (the workspace's own stages,
        // not three hard-coded names). The newest 20 to start with; the
        // search box reaches the rest (FindDeals).
        private async Task LoadAvailableDealsAsync(Guid tenantId)
        {
            try
            {
                var newest = await _dealService.GetAllAsync(tenantId, pageSize: RecordSearch.Take,
                                                            sort: "created", dir: "desc", openOnly: true);

                AvailableDeals = newest.Items.Select(d => new SelectListItem
                {
                    Value = d.Id.ToString(),
                    Text  = DealText(d)
                }).ToList();

                // 081: no TempData message here. This runs on a page that
                // RENDERS, so it surfaced on the user's NEXT page, unrelated;
                // and the view already draws "Nothing to quote yet" with what
                // to do about it.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load deals");
                AvailableDeals = new();
            }
        }

        /// <summary>108. "Metro line — Acme Ltd (Proposal)".</summary>
        private static string DealText(DealListItem d) =>
            string.IsNullOrWhiteSpace(d.CompanyName) ? $"{d.Title} ({d.Stage})" : $"{d.Title} — {d.CompanyName} ({d.Stage})";

        /// <summary>
        /// 108. GET ?handler=FindDeals&amp;q=… — open deals whose title or
        /// company matches, from the whole workspace (the API applies the
        /// person's record visibility). JSON for record-search.js.
        /// </summary>
        public async Task<IActionResult> OnGetFindDealsAsync(string? q)
        {
            if (!await CanCreateAsync()) return new JsonResult(Array.Empty<RecordSearchItem>()) { StatusCode = 403 };
            var term = RecordSearch.Clean(q);
            if (term is null) return new JsonResult(Array.Empty<RecordSearchItem>());

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                var found = await _dealService.GetAllAsync(tenantId, search: term, pageSize: RecordSearch.Take,
                                                           sort: "created", dir: "desc", openOnly: true);
                return new JsonResult(found.Items.Select(d => new RecordSearchItem(d.Id.ToString(), DealText(d))).ToList());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Deal search on the New Quote page failed");
                return new JsonResult(Array.Empty<RecordSearchItem>()) { StatusCode = 500 };
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
