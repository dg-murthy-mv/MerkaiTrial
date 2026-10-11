// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Quotes/Edit.cshtml.cs
//
// COMPLETE FILE — replaces the 017 version.
//
// CHANGES (081 — custom fields on quotes)
//
//   1. The quote's own custom fields, through the shared _CustomFieldInputs
//      partial, pre-filled from QuoteDto.CustomFieldValues, checked here
//      first and sent as UpdateQuoteDto.CustomFields. Only ACTIVE fields
//      are sent, so a switched-off field's stored value is untouched. If
//      the field list could not be loaded, CustomFields is sent as NULL and
//      nothing about them changes.
//
//   2. A REFUSED SAVE KEEPS WHAT WAS TYPED. The old error path re-seeded
//      the editor from the DATABASE, so a single mistake quietly undid
//      every line change the person had just made, and it put the reason
//      in TempData on a response that renders (TempData is for redirects).
//      RedisplayAsync now shows the POSTED lines (QuoteItemsSeed) and the
//      reason in PageError.
//
//   3. NEW LINES GOT THE WRONG TAX RATE BELOW 1%. LoadTaxAndRulesAsync
//      still guessed "fraction or percentage?" (raw < 1 → ×100), the guess
//      Create dropped in 056: the stored rate is ALWAYS a percentage, and a
//      genuine 0.5% rate became 50% on every line added here. Now the same
//      as Create.
//
//   4. The tax column said "Tax" on this page and "GST" / "VAT" on Create.
//      TaxLabel comes from the workspace now, as on Create.
//
// CHANGES (029)
//
//   1. ★ EVERY SAVE MOVED THE DATES BACK A DAY. This is the one to care
//      about. The old code was:
//
//          // GET
//          Input.IssueDate  = ExistingQuote.IssueDateUtc.ToLocalTime();
//          // POST
//          IssueDateUtc = Input.IssueDate.ToUniversalTime(),
//
//      Quotes store a CALENDAR DATE as midnight UTC — the Create page was
//      fixed to do that on 2026-08-03 and says so in its own comments.
//      Round-tripping it through ToLocalTime()/ToUniversalTime() is not
//      symmetric for a date-only value:
//
//        • ToLocalTime() converts by the SERVER's timezone. On a UTC−5
//          server, midnight UTC on the 25th becomes 19:00 on the 24th, and
//          the date box opens showing the wrong day.
//        • On the way back, Input.IssueDate arrives from <input type="date">
//          as midnight with Kind=Unspecified. ToUniversalTime() treats an
//          Unspecified value as LOCAL and subtracts the offset. On an IST
//          (UTC+5:30) server, 2026-09-25 00:00 becomes 2026-09-24 18:30 UTC.
//
//      So: open a quote, press Save without touching anything, and both
//      dates move back one day. Do it three times and the quote was issued
//      three days earlier than it was. The CRM hid it by converting back
//      for display, but the public customer-facing quote page has no tenant
//      context and rendered the raw UTC date — which is how the same bug
//      was originally caught on Create.
//
//      Now, exactly as Create does it: read .Date on the way in, and store
//      the calendar date as midnight UTC with SpecifyKind on the way out.
//      No conversion in either direction.
//
//   2. THE POST NEVER RE-CHECKED THAT THE QUOTE IS EDITABLE. OnGetAsync
//      redirects a Sent, Accepted or PendingApproval quote away with a
//      reason; OnPostAsync only checked that the quote existed. The API
//      refuses the save, so nothing was corrupted, but the user got the
//      API's message instead of this page's, and the house pattern is two
//      levels of guard. The status check now runs on both.
//
//   3. THE BROWSER SUPPLIED THE CURRENCY. UpdateQuoteDto.Currency came
//      from Input.Currency, a hidden field, so editing it in dev tools
//      re-denominated the quote. Create resolves currency on the server for
//      exactly this reason ("currency determines what the customer is
//      billed, so it must not be supplied by the browser"). Edit now takes
//      it from the stored quote and ignores what was posted.
//
//   4. AN EXPIRY DATE BEFORE THE ISSUE DATE WAS ACCEPTED. Nothing checked,
//      on the client or the server.
//
//   5. IT FETCHED 500 DEALS ON EVERY OPEN FOR A DROPDOWN IT NEVER DREW.
//      LoadAvailableDealsAsync + AvailableDeals + IDealService are gone: the
//      deal is read-only on this page because UpdateQuoteDto has no DealId,
//      so the old editable dropdown posted a value the API discarded.
//
//   6. FormatDate / FormatDateTime / FormatCurrency deleted —
//      AuthorizedPageModel declares all three with the same signatures, so
//      these were hiding the base members (CS0108). GetCurrencySymbol went
//      with them; it was unused and fell back to the tenant symbol, which
//      is the mislabelling bug the Detail page had.
//
// CHANGES (017 — quote approvals)
//   ✅ Only Draft, Revised and Approved quotes open for editing.
//   ✅ Editing an Approved quote warns that saving sends it back to draft.
//   ✅ New lines default to the TENANT's tax rate (DefaultTaxRate). The
//      view had 18 hard-coded — Thai (7%), UAE (5%) and Philippine (12%)
//      tenants got Indian GST on every line they added.
//   ✅ Live approval hint, same as Create (ApprovalRulesJson + list prices).
//   ✅ API refusals show their real message.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages.Shared;            // 081 — CustomFieldFormVm, CustomFieldForm
using MerkaiTrial.Admin.Web.Services.CustomFields;   // 081
using MerkaiTrial.Admin.Web.Services.Products;
using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Configuration;   // 067: TaxCodes
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace MerkaiTrial.Admin.Web.Pages.Quotes
{
    public class EditModel : AuthorizedPageModel
    {
        private readonly IQuoteService          _quoteService;
        private readonly IProductService        _productService;
        private readonly ICurrentUserService    _currentUserService;
        private readonly ICurrentTenantService  _tenantService;
        private readonly IQuoteApprovalService  _approvals;
        private readonly ICustomFieldService    _customFields;   // 081
        private readonly ILogger<EditModel>     _logger;

        protected override string ModuleName => Modules.Quotes;

        // IDealService is NOT injected any more. Its only use was
        // LoadAvailableDealsAsync, which fetched 500 deals to fill a dropdown
        // this page does not draw — the deal is read-only on an existing quote
        // because UpdateQuoteDto has no DealId.
        public EditModel(
            IQuoteService         quoteService,
            IProductService       productService,
            ICurrentUserService   currentUserService,
            ICurrentTenantService tenantService,
            IQuoteApprovalService approvals,
            ICustomFieldService   customFields,                 // 081
            IAuthorizationService authorizationService,
            ILogger<EditModel>    logger)
            : base(authorizationService, currentUserService, logger)
        {
            _customFields       = customFields;
            _quoteService       = quoteService;
            _productService     = productService;
            _currentUserService = currentUserService;
            _tenantService      = tenantService;
            _approvals          = approvals;
            _logger             = logger;
        }

        // ==================== PROPERTIES ====================

        [BindProperty(SupportsGet = true)]
        public Guid Id { get; set; }

        [BindProperty]
        public QuoteEditModel Input { get; set; } = new();

        public QuoteDto? ExistingQuote { get; set; }
        public List<ProductListItem> Products { get; set; } = new();

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage   { get; set; }

        /// <summary>081. Why the form came back, on THIS response (not TempData — no redirect).</summary>
        public string? PageError { get; private set; }

        /// <summary>
        /// 081. The lines as POSTED, for the editor, when the form is shown
        /// again after a refusal. Null on a fresh open — the view then seeds
        /// from the stored quote.
        /// </summary>
        public string? PostedItemsJson { get; private set; }

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

        /// <summary>Same list as the API's QuoteWorkflow.IsEditable.</summary>
        public bool IsEditable => ExistingQuote?.Status is "Draft" or "Revised" or "Approved";

        /// <summary>Tenant default tax, as a percentage (7 for Thai VAT) — for new lines.</summary>
        public decimal DefaultTaxRate { get; private set; }

        /// <summary>081. The workspace's word for the tax — "GST", "VAT". Was hard-coded "Tax".</summary>
        public string TaxLabel { get; private set; } = "Tax";

        /// <summary>JSON for the editor: { enabled, maxDiscountPercent, maxQuoteTotal, exempt }.</summary>
        public string ApprovalRulesJson { get; private set; } = "{\"enabled\":false}";

        /// <summary>The deal's title for the read-only deal field.</summary>
        public string DealDisplay => ExistingQuote == null ? "" : ExistingQuote.DealTitle;

        // ✅ Tenant context for views
        public string TenantCurrencySymbol { get; private set; } = string.Empty;
        public string TenantCurrencyCode   { get; private set; } = string.Empty;

        public string StatusWarning => ExistingQuote?.Status switch
        {
            "Approved" => "This quote is approved. Saving changes sends it back to draft — if it's still over your workspace's limits it will need approval again.",
            "Revised"  => "You're revising a quote the customer rejected or let expire. Send it again when you're done.",
            _ => ""
        };

        /// <summary>Why a quote can't be edited — shown on the Detail page.</summary>
        private static string NotEditableReason(string? status) => status switch
        {
            "PendingApproval"       => "This quote is waiting for approval. Recall the request first if you need to change it.",
            "Sent" or "Viewed"      => "This quote has already gone to the customer, so it can't be edited. Create a new quote instead.",
            "Accepted"              => "This quote has been accepted, so it can't be edited. Create a new quote instead.",
            "Rejected" or "Expired" => "Mark this quote as Revised first, then edit it.",
            _                       => "This quote can't be edited."
        };

        // ==================== INPUT MODEL ====================

        public class QuoteEditModel
        {
            [Required(ErrorMessage = "Please select a deal")]
            public Guid DealId { get; set; }

            [Required(ErrorMessage = "Issue date is required")]
            public DateTime IssueDate { get; set; } = DateTime.Today;

            [Required(ErrorMessage = "Expiry date is required")]
            public DateTime ExpiryDate { get; set; } = DateTime.Today.AddDays(30);

            /// <summary>
            /// Shown read-only and posted as a hidden field so the form is
            /// self-describing, but NOT trusted: OnPostAsync takes the currency
            /// from the stored quote. See change 3 in the header.
            /// </summary>
            [Required(ErrorMessage = "Currency is required")]
            [StringLength(3)]
            public string Currency { get; set; } = string.Empty;

            // JSON string of line items, built by _QuoteItemsEditor.cshtml
            public string? ItemsJson { get; set; }
        }

        // ==================== ON GET ====================

        public async Task<IActionResult> OnGetAsync()
        {
            var check = await ValidatePermissionAsync(Actions.Update);
            if (check != null) return check;

            await InitializePermissionsAsync();

            try
            {
                if (Id == Guid.Empty)
                {
                    ErrorMessage = "Invalid quote ID";
                    return RedirectToPage("/Quotes/Index");
                }

                TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
                TenantCurrencyCode   = _tenantService.GetCurrencyCode();

                var tenantId = _currentUserService.GetCurrentTenantId();

                ExistingQuote = await _quoteService.GetByIdAsync(tenantId, Id);

                if (ExistingQuote == null)
                {
                    ErrorMessage = "Quote not found";
                    return RedirectToPage("/Quotes/Index");
                }

                if (!IsEditable)
                {
                    ErrorMessage = NotEditableReason(ExistingQuote.Status);
                    return RedirectToPage("/Quotes/Detail", new { id = Id });
                }

                await LoadTaxAndRulesAsync();

                // ── The dates ───────────────────────────────────────────
                // .Date, NOT .ToLocalTime(). These are calendar dates stored as
                // midnight UTC; converting by the server's timezone shows the
                // wrong day on any server west of UTC. See change 1 in the
                // header for the full story.
                Input.DealId     = ExistingQuote.DealId;
                Input.IssueDate  = ExistingQuote.IssueDateUtc.Date;
                Input.ExpiryDate = ExistingQuote.ExpiresAtUtc.Date;

                // A quote saved without an expiry carries default(DateTime),
                // which <input type="date"> silently refuses — the box would
                // look empty and then post as 01-01-0001. Give it something
                // sensible to show.
                if (Input.ExpiryDate.Year <= 1900)
                    Input.ExpiryDate = Input.IssueDate.AddDays(30);
                if (Input.IssueDate.Year <= 1900)
                    Input.IssueDate = DateTime.UtcNow.Date;

                Input.Currency = !string.IsNullOrEmpty(ExistingQuote.Currency)
                    ? ExistingQuote.Currency
                    : TenantCurrencyCode;

                await LoadProductsAsync(tenantId);

                // 081 — the quote's stored custom values, into the inputs.
                CustomFields = CustomFieldForm.FromStored(ExistingQuote.CustomFieldValues);
                await LoadCustomFieldsAsync();

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load quote {QuoteId} for editing", Id);
                ErrorMessage = "Failed to load quote. Please try again.";
                return RedirectToPage("/Quotes/Index");
            }
        }

        // ==================== ON POST ====================

        public async Task<IActionResult> OnPostAsync(string itemsJson)
        {
            var check = await ValidatePermissionAsync(Actions.Update);
            if (check != null) return check;

            if (Id == Guid.Empty)
            {
                ErrorMessage = "Invalid quote ID";
                return RedirectToPage("/Quotes/Index");
            }

            var tenantId = _currentUserService.GetCurrentTenantId();

            // 081. Read the lines FIRST so every refusal below can show them
            // again exactly as posted. The old page re-seeded from the
            // database on an error, silently undoing the person's changes.
            var items = QuoteItemsSeed.TryParse(itemsJson);

            try
            {
                ExistingQuote = await _quoteService.GetByIdAsync(tenantId, Id);

                if (ExistingQuote == null)
                {
                    ErrorMessage = "Quote not found";
                    return RedirectToPage("/Quotes/Index");
                }

                // ✅ Level 2 guard, matching OnGetAsync. A status OnGet refuses
                // to open must not reach the API through a stale form.
                if (!IsEditable)
                {
                    ErrorMessage = NotEditableReason(ExistingQuote.Status);
                    return RedirectToPage("/Quotes/Detail", new { id = Id });
                }

                // 081 — each custom field's message under its own input.
                await LoadCustomFieldsAsync();
                CustomFieldForm.Validate(ActiveCustomFields, CustomFields, ModelState);

                if (Input.ExpiryDate.Date < Input.IssueDate.Date)
                    return await RedisplayAsync(tenantId, items, "The expiry date can't be before the issue date.");

                if (items == null || items.Count == 0)
                    return await RedisplayAsync(tenantId, items, "Please add at least one item to the quote.");

                if (items.Any(i => string.IsNullOrWhiteSpace(i.Name)))
                {
                    var missing = items.Count(i => string.IsNullOrWhiteSpace(i.Name));
                    return await RedisplayAsync(tenantId, items,
                        $"All items must have a name. {missing} item(s) are missing names.");
                }

                // 052 — see Create.cshtml.cs. Decimal quantities are legal now.
                if (items.Any(i => i.Quantity <= 0))
                    return await RedisplayAsync(tenantId, items, "Every line needs a quantity above zero.");

                if (items.Any(i => i.UnitPrice <= 0))
                    return await RedisplayAsync(tenantId, items, "Every line needs a unit price above zero.");

                if (items.Any(i => i.TaxRate < 0 || i.TaxRate > 100))
                    return await RedisplayAsync(tenantId, items, "Tax rates must be between 0 and 100%.");

                // 055 — see Create.cshtml.cs.
                if (items.Any(i => i.DiscountPercent is < 0 or > 100))
                    return await RedisplayAsync(tenantId, items, "A discount percentage must be between 0 and 100.");

                if (items.Any(i => i.LineDiscount < 0))
                    return await RedisplayAsync(tenantId, items, "A line discount can't be negative.");

                // 067 — see Create.cshtml.cs. TaxCodes.Normalise would
                // silently shorten an over-long code.
                if (items.Any(i => (i.TaxCode ?? string.Empty).Trim().Length > TaxCodes.MaxLength))
                    return await RedisplayAsync(tenantId, items, $"A tax code can be at most {TaxCodes.MaxLength} characters.");

                // 081 — a custom field problem; each message is under its input.
                // 081 — a custom field is wrong: its message is under its own input.
                if (CustomFieldForm.HasErrors(ModelState))
                    return await RedisplayAsync(tenantId, items, "Please correct the highlighted fields.");

                // A date box that failed to bind (a cleared box posts "") is said
                // plainly rather than saving the default binding left behind. ONLY
                // these keys: with nullable reference types on, an unposted string
                // property also lands in ModelState, and must not block the save.
                if (new[] { "Input.IssueDate", "Input.ExpiryDate" }.Any(k => ModelState[k]?.Errors.Count > 0))
                    return await RedisplayAsync(tenantId, items, "Some of the dates could not be read. Please check them and try again.");

                // ✅ Currency from the STORED quote, never from the post. The
                // hidden field exists so the form is self-describing; trusting
                // it would let anyone re-denominate a quote from dev tools.
                var currency = !string.IsNullOrWhiteSpace(ExistingQuote.Currency)
                    ? ExistingQuote.Currency
                    : _tenantService.GetCurrencyCode();

                var updateDto = new UpdateQuoteDto
                {
                    // ★ SpecifyKind, not ToUniversalTime. See change 1 in the
                    // header: calendar dates stored as midnight UTC.
                    IssueDateUtc = DateTime.SpecifyKind(Input.IssueDate.Date, DateTimeKind.Utc),
                    ExpiresAtUtc = DateTime.SpecifyKind(Input.ExpiryDate.Date, DateTimeKind.Utc),
                    Currency     = currency,

                    // 081 — null when the field list could not be loaded: the
                    // form had no custom inputs, so leave the values alone.
                    CustomFields = CustomFieldsLoadFailed
                        ? null
                        : CustomFieldForm.ToSubmission(CustomFields, ActiveCustomFields),

                    Items        = items.Select(i => new UpdateQuoteItemDto
                    {
                        Id           = i.Id,          // existing line, or null for a new one
                        ProductId    = i.ProductId,
                        Name         = i.Name,
                        Description  = i.Description,
                        UnitPrice    = i.UnitPrice,
                        Quantity     = i.Quantity,
                        UnitOfMeasure = i.UnitOfMeasure,   // 052
                        LineDiscount = i.LineDiscount,
                        DiscountPercent = i.DiscountPercent,   // 055
                        TaxRate      = i.TaxRate / 100m,  // % → decimal fraction
                        // 067. `?? string.Empty`, never a bare null — see
                        // QuoteItemData.TaxCode.
                        TaxCode      = i.TaxCode ?? string.Empty
                    }).ToList()
                };

                await _quoteService.UpdateAsync(tenantId, Id, updateDto);

                _logger.LogInformation("Quote {QuoteId} updated with {Count} lines", Id, items.Count);

                // TempData is right here: this response REDIRECTS.
                SuccessMessage = ExistingQuote.Status == "Approved"
                    ? $"Quote {ExistingQuote.Number} updated. It's back in draft — send it, or submit it for approval again if it's over the limits."
                    : $"Quote {ExistingQuote.Number} updated.";

                return RedirectToPage("/Quotes/Detail", new { id = Id });
            }
            catch (InvalidOperationException ex)
            {
                // The API refused and said why — the quote isn't editable any
                // more, a line is wrong, a required custom field is empty.
                return await RedisplayAsync(tenantId, items, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update quote {QuoteId}", Id);
                return await RedisplayAsync(tenantId, items, "Failed to update quote. Please try again.");
            }
        }

        // ==================== HELPERS ====================

        /// <summary>
        /// 081 (was LoadFormDataAsync + a TempData message). Show the form
        /// again with what was POSTED — lines, dates, custom fields — and
        /// one sentence in PageError saying what is wrong. If the quote
        /// itself cannot be read any more there is no form to show.
        /// </summary>
        private async Task<IActionResult> RedisplayAsync(Guid tenantId, List<QuoteItemData>? items, string message)
        {
            PageError = message;

            try
            {
                // Permissions do not survive a POST — the page model is a fresh
                // instance — so CanUpdate and friends must be filled again or the
                // re-rendered page hides its own Save button.
                await InitializePermissionsAsync();

                ExistingQuote        ??= await _quoteService.GetByIdAsync(tenantId, Id);
                TenantCurrencySymbol  = _tenantService.GetCurrencySymbol();
                TenantCurrencyCode    = _tenantService.GetCurrencyCode();

                if (ExistingQuote == null)
                {
                    ErrorMessage = "Quote not found";
                    return RedirectToPage("/Quotes/Index");
                }

                // The currency box shows the stored currency, as on GET.
                Input.Currency = !string.IsNullOrEmpty(ExistingQuote.Currency)
                    ? ExistingQuote.Currency
                    : TenantCurrencyCode;

                await LoadTaxAndRulesAsync();
                await LoadProductsAsync(tenantId);
                if (ActiveCustomFields.Count == 0 && !CustomFieldsLoadFailed)
                    await LoadCustomFieldsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload form data for quote {QuoteId}", Id);
            }

            // The posted lines when they could be read; otherwise the stored
            // ones (null here means the view seeds from ExistingQuote).
            PostedItemsJson = items is { Count: > 0 } ? QuoteItemsSeed.FromPosted(items) : null;
            return Page();
        }

        /// <summary>
        /// 081. The active Quote custom fields. A failure is remembered
        /// (CustomFieldsLoadFailed) so the form says why the fields are
        /// missing and the save sends CustomFields = null — leaving every
        /// stored value exactly as it was.
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
                _logger.LogWarning(ex, "Custom fields could not be loaded for the Edit Quote form");
                ActiveCustomFields     = new List<CustomFieldDefinitionDto>();
                CustomFieldsLoadFailed = true;
            }
        }

        private async Task LoadTaxAndRulesAsync()
        {
            try
            {
                // 081 (as Create since 056): the stored rate is ALWAYS a
                // percentage. The old "raw < 1 → ×100" guess turned a real
                // 0.5% rate into 50% on every new line.
                DefaultTaxRate = await _tenantService.GetDefaultTaxRateAsync();
                TaxLabel       = _tenantService.GetTaxLabel();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load the tenant's default tax rate");
                DefaultTaxRate = 0m;
            }

            try
            {
                var rules = await _approvals.GetSettingsAsync();
                var me    = await _currentUserService.GetCurrentUserAsync();

                ApprovalRulesJson = JsonSerializer.Serialize(new
                {
                    enabled            = rules.IsEnabled,
                    maxDiscountPercent = rules.MaxDiscountPercent,
                    maxQuoteTotal      = rules.MaxQuoteTotal,
                    exempt             = me.IsTenantAdmin
                });
            }
            catch (Exception ex)
            {
                // The hint is a convenience — the API still enforces the rules.
                _logger.LogWarning(ex, "Could not load quote approval rules for the live hint");
            }
        }

        private async Task LoadProductsAsync(Guid tenantId)
        {
            try
            {
                var paginated = await _productService.GetAllAsync(
                    tenantId: tenantId,
                    pageNumber: 1,
                    pageSize: 1000,
                    category: null,
                    isActive: true,
                    searchTerm: null);

                Products = paginated?.Items ?? new List<ProductListItem>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load products");
                Products = new List<ProductListItem>();
            }
        }
    }
}
