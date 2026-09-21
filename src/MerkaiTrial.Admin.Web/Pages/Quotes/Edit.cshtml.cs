// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Quotes/Edit.cshtml.cs
// PURPOSE: Edit Quote - Multi-Tenant SaaS CRM (Mobile/Tablet Compatible)
// FEATURES: Edit line items, change deal, draft-only editing
// FIXES: Added missing QuoteItemData class, proper item serialization
//
// CHANGES (017 — quote approvals)
//   ✅ Only Draft, Revised and Approved quotes open for editing. Anything
//      else goes back to the Detail page with the reason (the API refuses
//      the save anyway — before, a Sent or Accepted quote could be edited
//      and the customer's copy changed under them).
//   ✅ Editing an Approved quote warns that saving sends it back to draft.
//   ✅ New lines default to the TENANT's tax rate (DefaultTaxRate). The
//      view had 18 hard-coded — Thai (7%), UAE (5%) and Philippine (12%)
//      tenants got Indian GST on every line they added.
//   ✅ Live approval hint, same as Create (ApprovalRulesJson + list prices).
//   ✅ The deal is shown read-only. UpdateQuoteDto has no DealId, so the
//      old "you can change the deal" dropdown never saved anything.
//   ✅ API refusals show their real message.
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
        private readonly IQuoteService _quoteService;
        private readonly IDealService _dealService;
        private readonly IProductService _productService;
        private readonly ICurrentUserService  _currentUserService;
        private readonly ICurrentTenantService  _tenantService;
        private readonly IQuoteApprovalService  _approvals;
        private readonly ILogger<EditModel>      _logger;

        protected override string ModuleName => Modules.Quotes;

        public EditModel(
            IQuoteService quoteService,
            IDealService dealService,
            IProductService productService,
            ICurrentUserService   currentUserService,
            ICurrentTenantService tenantService,
            IQuoteApprovalService approvals,
            IAuthorizationService authorizationService,
            ILogger<EditModel>    logger)
            : base(authorizationService, currentUserService, logger)
        {
            _quoteService = quoteService;
            _dealService = dealService;
            _productService = productService;
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
        public List<SelectListItem> AvailableDeals { get; set; } = new();
        public List<ProductListItem> Products { get; set; } = new();

        [TempData]
        public string? SuccessMessage { get; set; }

        [TempData]
        public string? ErrorMessage { get; set; }

        /// <summary>Same list as the API's QuoteWorkflow.IsEditable.</summary>
        public bool IsEditable => ExistingQuote?.Status is "Draft" or "Revised" or "Approved";

        /// <summary>Tenant default tax, as a percentage (7 for Thai VAT) — for new lines.</summary>
        public decimal DefaultTaxRate { get; private set; }

        /// <summary>JSON for the view's script: { enabled, maxDiscountPercent, maxQuoteTotal, exempt }.</summary>
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
            "PendingApproval" => "This quote is waiting for approval. Recall the request first if you need to change it.",
            "Sent" or "Viewed" => "This quote has already gone to the customer, so it can't be edited. Create a new quote instead.",
            "Accepted" => "This quote has been accepted, so it can't be edited. Create a new quote instead.",
            "Rejected" or "Expired" => "Mark this quote as Revised first, then edit it.",
            _ => "This quote can't be edited."
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

            [Required(ErrorMessage = "Currency is required")]
            [StringLength(3)]
            public string Currency { get; set; } = string.Empty;  // ✅ Set from tenant in OnGetAsync

            // JSON string of line items (populated by JavaScript)
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

                _logger.LogInformation("=== Loading Quote for Editing ===");
                _logger.LogInformation("QuoteId: {QuoteId}", Id);

                // ✅ Load tenant context
                TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
                TenantCurrencyCode   = _tenantService.GetCurrencyCode();

                var tenantId = _currentUserService.GetCurrentTenantId();

                // Load existing quote
                ExistingQuote = await _quoteService.GetByIdAsync(tenantId, Id);

                if (ExistingQuote == null)
                {
                    ErrorMessage = "Quote not found";
                    return RedirectToPage("/Quotes/Index");
                }

                _logger.LogInformation("✅ Quote loaded: {Number} - Status: {Status}",
                    ExistingQuote.Number, ExistingQuote.Status);

                if (!IsEditable)
                {
                    ErrorMessage = NotEditableReason(ExistingQuote.Status);
                    return RedirectToPage("/Quotes/Detail", new { id = Id });
                }

                await LoadTaxAndRulesAsync();
                _logger.LogInformation("✅ Items count: {Count}", ExistingQuote.Items?.Count ?? 0);

                // Populate form with existing data
                Input.DealId = ExistingQuote.DealId;
                Input.IssueDate = ExistingQuote.IssueDateUtc.ToLocalTime();
                Input.ExpiryDate = ExistingQuote.ExpiresAtUtc.ToLocalTime();
                // ✅ Currency from quote; fallback to tenant currency if NULL
                Input.Currency = !string.IsNullOrEmpty(ExistingQuote.Currency)
                    ? ExistingQuote.Currency
                    : TenantCurrencyCode;

                // Load available deals for dropdown
                await LoadAvailableDealsAsync(tenantId);

                // Load products catalog
                await LoadProductsAsync(tenantId);

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to load quote for editing");
                ErrorMessage = "Failed to load quote. Please try again.";
                return RedirectToPage("/Quotes/Index");
            }
        }

        // ==================== ON POST UPDATE ====================

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

                // Load existing quote to check status
                ExistingQuote = await _quoteService.GetByIdAsync(tenantId, Id);

                if (ExistingQuote == null)
                {
                    ErrorMessage = "Quote not found";
                    return RedirectToPage("/Quotes/Index");
                }

                _logger.LogInformation("Updating quote {QuoteId}", Id);

                // Validate items
                if (string.IsNullOrWhiteSpace(itemsJson))
                {
                    ErrorMessage = "Please add at least one item to the quote";
                    await LoadFormDataAsync(tenantId);
                    return Page();
                }

                // ✅ FIX: Case-insensitive JSON deserialization
                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var items = JsonSerializer.Deserialize<List<QuoteItemData>>(itemsJson, jsonOptions);

                if (items == null || items.Count == 0)
                {
                    _logger.LogWarning("No items provided for quote update");
                    ErrorMessage = "Please add at least one item to the quote";
                    await LoadFormDataAsync(tenantId);
                    return Page();
                }

                // Log received items
                _logger.LogInformation("Received {Count} items from form", items.Count);
                foreach (var item in items)
                {
                    _logger.LogInformation("Item: Id={Id}, Name={Name}, Qty={Qty}, Price={Price}",
                        item.Id, item.Name ?? "(null)", item.Quantity, item.UnitPrice);
                }

                // Validate item names
                if (items.Any(i => string.IsNullOrWhiteSpace(i.Name)))
                {
                    var missingNames = items.Count(i => string.IsNullOrWhiteSpace(i.Name));
                    ErrorMessage = $"All items must have a name. {missingNames} item(s) are missing names.";
                    await LoadFormDataAsync(tenantId);
                    return Page();
                }

                // Create update DTO
                var updateDto = new UpdateQuoteDto
                {
                    IssueDateUtc = Input.IssueDate.ToUniversalTime(),
                    ExpiresAtUtc = Input.ExpiryDate.ToUniversalTime(),
                    Currency = Input.Currency,
                    Items = items.Select(i => new UpdateQuoteItemDto
                    {
                        Id = i.Id, // Existing items have Id, new items are null
                        ProductId = i.ProductId,
                        Name = i.Name,
                        Description = i.Description,
                        UnitPrice = i.UnitPrice,
                        Quantity = i.Quantity,
                        LineDiscount = i.LineDiscount,
                        TaxRate = i.TaxRate / 100m // % -> decimal
                    }).ToList()
                };

                await _quoteService.UpdateAsync(tenantId, Id, updateDto);
                _logger.LogInformation("✅ Quote {QuoteId} updated successfully", Id);

                SuccessMessage = ExistingQuote.Status == "Approved"
                    ? $"Quote {ExistingQuote.Number} updated. It's back in draft — send it, or submit it for approval again if it's over the limits."
                    : $"Quote {ExistingQuote.Number} updated successfully!";
                return RedirectToPage("/Quotes/Detail", new { id = Id });
            }
            catch (InvalidOperationException ex)
            {
                // The API refused and said why — e.g. the quote isn't editable any more.
                ErrorMessage = ex.Message;
                var tenantId = _currentUserService.GetCurrentTenantId();
                await LoadFormDataAsync(tenantId);
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to update quote {QuoteId}", Id);
                ErrorMessage = $"Failed to update quote: {ex.Message}";

                // Reload form data
                var tenantId = _currentUserService.GetCurrentTenantId();
                await LoadFormDataAsync(tenantId);
                return Page();
            }
        }

        // ==================== HELPER METHODS ====================

        private async Task LoadFormDataAsync(Guid tenantId)
        {
            try
            {
                ExistingQuote = await _quoteService.GetByIdAsync(tenantId, Id);
                TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
                TenantCurrencyCode   = _tenantService.GetCurrencyCode();
                await LoadTaxAndRulesAsync();
                await LoadAvailableDealsAsync(tenantId);
                await LoadProductsAsync(tenantId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload form data");
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

        private async Task LoadAvailableDealsAsync(Guid tenantId)
        {
            try
            {
                _logger.LogInformation("Loading available deals...");

                var allDeals = await _dealService.GetAllAsync(tenantId, pageSize: 500);

                // Include deals in Proposal, Negotiation, or current deal
                var availableDeals = allDeals.Items
                    .Where(d =>
                        d.Id == ExistingQuote?.DealId || // Current deal
                        string.Equals(d.Stage, "Proposal", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(d.Stage, "Negotiation", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(d => d.ExpectedCloseDateUtc)
                    .ToList();

                AvailableDeals = availableDeals.Select(d => new SelectListItem
                {
                    Value = d.Id.ToString(),
                    Text = $"{d.Title} - {d.CompanyName} ({d.OwnerName} {d.ExpectedValue:N0})",
                    Selected = d.Id == Input.DealId
                }).ToList();

                _logger.LogInformation("Loaded {Count} available deals", AvailableDeals.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load available deals");
                AvailableDeals = new List<SelectListItem>();
            }
        }

        private async Task LoadProductsAsync(Guid tenantId)
        {
            try
            {
                _logger.LogInformation("Loading products...");

                var paginated = await _productService.GetAllAsync(
                    tenantId: tenantId,
                    pageNumber: 1,
                    pageSize: 1000,
                    category: null,
                    isActive: true,
                    searchTerm: null
                );

                Products = paginated?.Items ?? new List<ProductListItem>();

                _logger.LogInformation("✅ Loaded {Count} products", Products.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load products");
                Products = new List<ProductListItem>();
            }
        }

        // ✅ Tenant-aware helpers
        public string GetCurrencySymbol(string? code) => code switch
        {
            "INR" => "₹",
            "THB" => "฿",
            "PHP" => "₱",
            "AED" => "د.إ",
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            _     => _tenantService.GetCurrencySymbol()
        };

        public string FormatDate(DateTime utcDate)     => _tenantService.FormatDate(utcDate);
        public string FormatDateTime(DateTime utcDate) => _tenantService.FormatDateTime(utcDate);
        public string FormatCurrency(decimal amount)   => _tenantService.FormatCurrency(amount);
    }

    // ✅ FIX: Added missing QuoteItemData class
    //public class QuoteItemData
    //{
    //    public Guid? Id { get; set; } // Existing item ID (null for new items)
    //    public Guid? ProductId { get; set; }
    //    public string Name { get; set; } = string.Empty;
    //    public string Description { get; set; } = string.Empty;
    //    public decimal UnitPrice { get; set; }
    //    public int Quantity { get; set; }
    //    public decimal LineDiscount { get; set; }
    //    public decimal TaxRate { get; set; } // As percentage (18 for 18%)
    //}
}
