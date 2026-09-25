// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Quotes/Edit.cshtml.cs
//
// COMPLETE FILE — replaces the 017 version.
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

using MerkaiTrial.Admin.Web.Services.Products;
using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Application.Authorization;
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
            IAuthorizationService authorizationService,
            ILogger<EditModel>    logger)
            : base(authorizationService, currentUserService, logger)
        {
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

        /// <summary>Same list as the API's QuoteWorkflow.IsEditable.</summary>
        public bool IsEditable => ExistingQuote?.Status is "Draft" or "Revised" or "Approved";

        /// <summary>Tenant default tax, as a percentage (7 for Thai VAT) — for new lines.</summary>
        public decimal DefaultTaxRate { get; private set; }

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

            try
            {
                if (Id == Guid.Empty)
                {
                    ErrorMessage = "Invalid quote ID";
                    return RedirectToPage("/Quotes/Index");
                }

                var tenantId = _currentUserService.GetCurrentTenantId();

                ExistingQuote = await _quoteService.GetByIdAsync(tenantId, Id);

                if (ExistingQuote == null)
                {
                    ErrorMessage = "Quote not found";
                    return RedirectToPage("/Quotes/Index");
                }

                // ✅ Level 2 guard, matching OnGetAsync. The old POST checked
                // only that the quote existed, so a status that OnGet refuses
                // to open reached the API and the user got the API's wording
                // instead of this page's.
                if (!IsEditable)
                {
                    ErrorMessage = NotEditableReason(ExistingQuote.Status);
                    return RedirectToPage("/Quotes/Detail", new { id = Id });
                }

                if (Input.ExpiryDate.Date < Input.IssueDate.Date)
                {
                    ErrorMessage = "The expiry date can't be before the issue date.";
                    await LoadFormDataAsync(tenantId);
                    return Page();
                }

                if (string.IsNullOrWhiteSpace(itemsJson))
                {
                    ErrorMessage = "Please add at least one item to the quote";
                    await LoadFormDataAsync(tenantId);
                    return Page();
                }

                // The editor sends "id":null for a new line, but guard the shapes
                // System.Text.Json cannot turn into a Guid? anyway — the Create
                // page has carried this normalisation since 017 and this one
                // never did.
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
                    await LoadFormDataAsync(tenantId);
                    return Page();
                }

                if (items.Any(i => string.IsNullOrWhiteSpace(i.Name)))
                {
                    var missing = items.Count(i => string.IsNullOrWhiteSpace(i.Name));
                    ErrorMessage = $"All items must have a name. {missing} item(s) are missing names.";
                    await LoadFormDataAsync(tenantId);
                    return Page();
                }

                if (items.Any(i => i.Quantity < 1))
                {
                    ErrorMessage = "Every line needs a quantity of 1 or more.";
                    await LoadFormDataAsync(tenantId);
                    return Page();
                }

                if (items.Any(i => i.UnitPrice <= 0))
                {
                    ErrorMessage = "Every line needs a unit price above zero.";
                    await LoadFormDataAsync(tenantId);
                    return Page();
                }

                if (items.Any(i => i.TaxRate < 0 || i.TaxRate > 100))
                {
                    ErrorMessage = "Tax rates must be between 0 and 100%.";
                    await LoadFormDataAsync(tenantId);
                    return Page();
                }

                if (items.Any(i => i.LineDiscount < 0))
                {
                    ErrorMessage = "A line discount can't be negative.";
                    await LoadFormDataAsync(tenantId);
                    return Page();
                }

                // ✅ Currency from the STORED quote, never from the post. The
                // hidden field exists so the form is self-describing; trusting
                // it would let anyone re-denominate a quote from dev tools.
                var currency = !string.IsNullOrWhiteSpace(ExistingQuote.Currency)
                    ? ExistingQuote.Currency
                    : _tenantService.GetCurrencyCode();

                var updateDto = new UpdateQuoteDto
                {
                    // ★ SpecifyKind, not ToUniversalTime. See change 1 in the
                    // header: these are calendar dates stored as midnight UTC,
                    // and ToUniversalTime() on an Unspecified midnight subtracts
                    // the server's offset, moving the date back a day on every
                    // single save.
                    IssueDateUtc = DateTime.SpecifyKind(Input.IssueDate.Date, DateTimeKind.Utc),
                    ExpiresAtUtc = DateTime.SpecifyKind(Input.ExpiryDate.Date, DateTimeKind.Utc),
                    Currency     = currency,
                    Items        = items.Select(i => new UpdateQuoteItemDto
                    {
                        Id           = i.Id,          // existing line, or null for a new one
                        ProductId    = i.ProductId,
                        Name         = i.Name,
                        Description  = i.Description,
                        UnitPrice    = i.UnitPrice,
                        Quantity     = i.Quantity,
                        LineDiscount = i.LineDiscount,
                        TaxRate      = i.TaxRate / 100m   // % → decimal fraction
                    }).ToList()
                };

                await _quoteService.UpdateAsync(tenantId, Id, updateDto);

                _logger.LogInformation("Quote {QuoteId} updated with {Count} lines", Id, items.Count);

                SuccessMessage = ExistingQuote.Status == "Approved"
                    ? $"Quote {ExistingQuote.Number} updated. It's back in draft — send it, or submit it for approval again if it's over the limits."
                    : $"Quote {ExistingQuote.Number} updated.";

                return RedirectToPage("/Quotes/Detail", new { id = Id });
            }
            catch (InvalidOperationException ex)
            {
                // The API refused and said why — e.g. the quote isn't editable any more.
                ErrorMessage = ex.Message;
                await LoadFormDataAsync(_currentUserService.GetCurrentTenantId());
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update quote {QuoteId}", Id);
                ErrorMessage = "Failed to update quote. Please try again.";
                await LoadFormDataAsync(_currentUserService.GetCurrentTenantId());
                return Page();
            }
        }

        // ==================== HELPERS ====================

        /// <summary>
        /// Re-fill everything the view needs after a validation failure, so the
        /// page can be re-rendered instead of redirecting and losing the edit.
        /// </summary>
        private async Task LoadFormDataAsync(Guid tenantId)
        {
            try
            {
                // Permissions do not survive a POST — the page model is a fresh
                // instance — so CanUpdate and friends must be filled again or the
                // re-rendered page hides its own Save button.
                await InitializePermissionsAsync();

                ExistingQuote        ??= await _quoteService.GetByIdAsync(tenantId, Id);
                TenantCurrencySymbol  = _tenantService.GetCurrencySymbol();
                TenantCurrencyCode    = _tenantService.GetCurrencyCode();

                await LoadTaxAndRulesAsync();
                await LoadProductsAsync(tenantId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload form data for quote {QuoteId}", Id);
            }
        }

        private async Task LoadTaxAndRulesAsync()
        {
            try
            {
                // GetDefaultTaxRateAsync may return a fraction (0.07) or a percentage (7).
                var raw = await _tenantService.GetDefaultTaxRateAsync();
                DefaultTaxRate = raw < 1m ? raw * 100m : raw;
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
