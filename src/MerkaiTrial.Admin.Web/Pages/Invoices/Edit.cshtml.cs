// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Invoices/Edit.cshtml.cs
// PURPOSE: Edit Invoice - Only Draft invoices can be edited
//
// ✅ SESSION 5 — PERMISSION MIGRATION
//   1. AppPageModel  →  AuthorizedPageModel   (ModuleName = Modules.Invoices)
//   2. BOTH handlers were UNGATED — there was not a single permission check in
//      this file. Any authenticated user including viewer could edit a Draft
//      invoice's due date, notes and LINE ITEMS by direct POST. Now:
//        OnGetAsync  → invoices.update
//        OnPostAsync → invoices.update
//   3. IsEditable (Draft-only business lock) is unchanged and does NOT collide
//      with the base class Can* properties. Both gates apply independently:
//      permission first, then business state.
//   4. ✅ CROSS-MODULE: LoadProductsAsync reads the product catalogue, so
//      OnGetAsync now also requires products.read.
//
//   NOTE: Edit.cshtml itself needs NO changes — page entry is gated here, and
//   the view contains no permission-dependent buttons (only the form itself,
//   which is unreachable without passing OnGetAsync).
//
// CHANGES (081 — custom fields on invoices)
//   1. The invoice's own custom fields, through the shared
//      _CustomFieldInputs partial, pre-filled from InvoiceDto.
//      CustomFieldValues and sent as UpdateInvoiceDto.CustomFields — on a
//      quote's invoice too: its LINES are the quote's, its fields are its
//      own until it is issued. NULL when the field list could not be
//      loaded, so nothing about them changes.
//   2. A REFUSED SAVE KEEPS WHAT WAS TYPED. The old error path re-seeded
//      the lines from the database, put the reason in TempData on a
//      response that renders, and never refilled the permission flags.
//      RedisplayAsync shows the POSTED lines (QuoteItemsSeed, the one seed
//      builder), the reason in PageError, and fills the flags again.
//   3. NEW LINES GOT THE WRONG TAX RATE BELOW 1% — the "raw < 1 → ×100"
//      guess. The stored rate is always a percentage (as Quotes/Create
//      since 056).
//   4. FormatDate / FormatDateTime / FormatCurrency deleted: they hid the
//      AuthorizedPageModel members of the same names (CS0108) and did the
//      same thing. Money() formats an amount in the INVOICE's currency
//      culture — the read-only table printed lakh-currency figures with
//      Western grouping (InvariantCulture).
//
// CHANGES (074 — the catalogue finally reaches manual invoices)
//
//   Everything rounds 052, 055, 067, 069 and 070 built for the catalogue
//   stopped at the quote editor. A MANUAL invoice — raised straight from
//   a deal, with no quote — got none of it. Invoices raised FROM a quote
//   were always fine, because they copy the quote's lines.
//
//   1. ⚠ THE CATALOGUE SHOWED HOME-CURRENCY PRICES. LoadProductsAsync
//      called GetAllAsync without a currency, so on a USD deal from an
//      Indian workspace the picker handed you ₹ figures and the invoice
//      billed them as dollars. This is the exact bug 069 fixed on
//      quotes; it has been live here ever since.
//
//   2. QUANTITY WAS AN int. InvoiceItemData.Quantity, the pre-052 shape
//      — so 12.5 m² became 12 on its way in, silently, and the invoice
//      disagreed with the work done.
//
//   3. NO UNIT OF MEASURE (052), NO TAX CODE (067), NO PERCENTAGE
//      DISCOUNT (055) travelled from the editor. CreateInvoiceLineDto
//      has carried all three for rounds; this page never filled them,
//      so a manual GST invoice went out with no HSN/SAC on any line.
//
//   4. THE LINE EDITOR IS NOW _QuoteItemsEditor.cshtml — the same one
//      the quote pages use, not a fourth hand-rolled copy. It is 1,400
//      lines of bundle handling, per-currency pricing, tax codes and
//      percentage discounts; reimplementing that here would have been a
//      copy guaranteed to drift, which is the problem round 029 solved
//      for the two quote pages and this round solves for the two
//      invoice pages.
//
//      The partial's name still says "Quote". It takes a
//      QuoteItemsEditorVm and nothing else — no page model, nothing
//      quote-specific — and ApprovalRulesJson defaults to disabled, so
//      the approval banner stays off. Renaming it would touch four
//      working pages for a word.
//
//   5. GetSymbol's hardcoded table is gone. It said "AED" => "د.إ",
//      contradicting CurrencyConfiguration, which deliberately uses the
//      string "AED" — so the same invoice showed two different symbols
//      depending on which screen you were on.
//
//   6. InvoiceItemData is declared ONCE, here. Create.cshtml.cs had a
//      nested second copy with its own int Quantity; its own comment
//      said "Consider deleting this one — deferred, out of scope".
//
// CHANGES (018 — invoice workflow)
//   ✅ An invoice raised from a QUOTE: only due date and notes can change —
//      its lines are the accepted (possibly approved) quote's. The API
//      enforces this too.
//   ✅ New lines default to the tenant's tax rate (was 0%); catalog lines
//      take the product's tax rate.
//   ✅ Due date stored as the calendar date (was ToUniversalTime()).
//   ✅ Status messages: "Only draft invoices can be edited" now explains
//      that issued invoices are voided and re-issued.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages.Quotes;          // 074: QuoteItemsEditorVm; 081: QuoteItemsSeed
using MerkaiTrial.Admin.Web.Pages.Shared;          // 081 — CustomFieldFormVm, CustomFieldForm
using MerkaiTrial.Admin.Web.Services.CustomFields; // 081
using MerkaiTrial.Admin.Web.Services.Invoices;
using MerkaiTrial.Admin.Web.Services.Products;
using MerkaiTrial.Application.Configuration;       // 074: CurrencyConfiguration, UnitsOfMeasure, TaxCodes
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

namespace MerkaiTrial.Admin.Web.Pages.Invoices
{
    public class EditModel : AuthorizedPageModel
    {
        private readonly IInvoiceService _invoiceService;
        private readonly IProductService _productService;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly ICustomFieldService _customFields;      // 081
        private readonly ILogger<EditModel> _logger;

        public EditModel(
            IInvoiceService invoiceService,
            IProductService productService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            ICustomFieldService customFields,                    // 081
            IAuthorizationService authorizationService,
            ILogger<EditModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _customFields = customFields;
            _invoiceService = invoiceService;
            _productService = productService;
            _currentUserService = currentUserService;
            _tenantService = tenantService;
            _logger = logger;
        }

        // ✅ Required by AuthorizedPageModel
        protected override string ModuleName => Modules.Invoices;

        // ==================== PROPERTIES ====================

        [BindProperty(SupportsGet = true)]
        public Guid Id { get; set; }

        [BindProperty]
        public InvoiceEditModel Input { get; set; } = new();

        public InvoiceDto? ExistingInvoice { get; set; }
        public List<ProductListItem> Products { get; set; } = new();

        [TempData]
        public string? SuccessMessage { get; set; }

        [TempData]
        public string? ErrorMessage { get; set; }

        // ── 081 ───────────────────────────────────────────────────────

        /// <summary>Why the form came back, on THIS response (not TempData — no redirect).</summary>
        public string? PageError { get; private set; }

        /// <summary>The lines as POSTED, after a refusal. Null on a fresh open — the view seeds from the invoice.</summary>
        public string? PostedItemsJson { get; private set; }

        /// <summary>Posted as CustomFields[&lt;field id&gt;] = value.</summary>
        [BindProperty]
        public Dictionary<string, string?> CustomFields { get; set; } = new();

        /// <summary>Active Invoice custom fields, in order.</summary>
        public List<CustomFieldDefinitionDto> ActiveCustomFields { get; private set; } = new();
        public bool CustomFieldsLoadFailed { get; private set; }

        /// <summary>The model for _CustomFieldInputs (rendered inside the form).</summary>
        public CustomFieldFormVm CustomFieldInputs => new()
        {
            Fields       = ActiveCustomFields,
            Values       = CustomFields,
            LoadFailed   = CustomFieldsLoadFailed,
            CanConfigure = UserCanRead(Modules.Settings),
            EntityType   = CustomFieldEntityTypes.Invoice
        };

        // Business-state lock — distinct from the CanUpdate PERMISSION property
        public bool IsEditable => ExistingInvoice?.Status == "Draft";

        /// <summary>(018) Raised from a quote — lines are the quote's and can't be edited here.</summary>
        public bool IsFromQuote => ExistingInvoice?.QuoteId.HasValue == true;

        /// <summary>Tenant default tax as a percentage (7 for Thai VAT) — for new custom lines.</summary>
        public decimal DefaultTaxRate { get; private set; }

        /// <summary>
        /// 074: the country's word for the tax — "GST", "VAT" — for the
        /// editor's column header. A column headed "Tax" on an Indian
        /// invoice is not wrong, just not what anybody there calls it.
        /// </summary>
        public string DefaultTaxRateName { get; private set; } = "Tax";

        // ✅ Tenant context
        public string TenantCurrencySymbol { get; private set; } = string.Empty;
        public string TenantCurrencyCode { get; private set; } = string.Empty;
        public string StatusWarning => ExistingInvoice?.Status switch
        {
            "Draft" => "",
            "Cancelled" => "This invoice is void.",
            _ => "This invoice has been issued, so it can't be changed. If something is wrong, void it and issue a new one."
        };

        // ==================== INPUT MODEL ====================

        public class InvoiceEditModel
        {
            [Required(ErrorMessage = "Due date is required")]
            public DateTime DueDate { get; set; } = DateTime.Today.AddDays(30);

            public string? Notes { get; set; }

            // JSON string of line items (populated by JavaScript)
            public string? ItemsJson { get; set; }
        }

        // ==================== ON GET ====================

        public async Task<IActionResult> OnGetAsync()
        {
            // ✅ GATE: was completely absent
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            await InitializePermissionsAsync();

            try
            {
                if (Id == Guid.Empty)
                {
                    ErrorMessage = "Invalid invoice ID";
                    return RedirectToPage("/Invoices/Index");
                }

                _logger.LogInformation("=== Loading Invoice for Editing ===");
                _logger.LogInformation("InvoiceId: {InvoiceId}", Id);

                TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
                TenantCurrencyCode = _tenantService.GetCurrencyCode();

                var tenantId = _currentUserService.GetCurrentTenantId();

                // Load existing invoice
                ExistingInvoice = await _invoiceService.GetByIdAsync(tenantId, Id);

                if (ExistingInvoice == null)
                {
                    ErrorMessage = "Invoice not found";
                    return RedirectToPage("/Invoices/Index");
                }

                _logger.LogInformation("✅ Invoice loaded: {Number} - Status: {Status}",
                    ExistingInvoice.Number, ExistingInvoice.Status);

                // Only allow editing Draft invoices (business lock, separate from permission)
                if (!IsEditable)
                {
                    ErrorMessage = StatusWarning;
                    return RedirectToPage("/Invoices/Detail", new { id = Id });
                }

                await LoadDefaultTaxAsync();

                // Populate form with existing data
                // Stored as the calendar date (018) — read it back the same way,
                // or every save would shift it by the server's time zone.
                Input.DueDate = ExistingInvoice.DueDateUtc?.Date ?? DateTime.Today.AddDays(30);
                Input.Notes = ExistingInvoice.Notes;

                // ✅ CROSS-MODULE: the product picker needs products.read.
                // Load only if permitted — an empty catalogue degrades the page
                // gracefully rather than 403-ing the whole edit screen.
                if (UserCanRead(Modules.Products))
                {
                    await LoadProductsAsync(tenantId);
                }
                else
                {
                    _logger.LogWarning(
                        "Skipping product catalogue on invoice edit — user lacks products.read");
                    Products = new List<ProductListItem>();
                }

                // 081 — the invoice's stored custom values, into the inputs.
                CustomFields = CustomFieldForm.FromStored(ExistingInvoice.CustomFieldValues);
                await LoadCustomFieldsAsync();

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to load invoice for editing");
                ErrorMessage = "Failed to load invoice. Please try again.";
                return RedirectToPage("/Invoices/Index");
            }
        }

        // ==================== ON POST UPDATE ====================

        public async Task<IActionResult> OnPostAsync(string itemsJson)
        {
            // ✅ GATE: was completely absent — viewer could edit Draft invoice
            // line items by direct POST
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            if (Id == Guid.Empty)
            {
                ErrorMessage = "Invalid invoice ID";
                return RedirectToPage("/Invoices/Index");
            }

            var tenantId = _currentUserService.GetCurrentTenantId();

            // 081. Read the lines FIRST so a refusal can show them again as
            // posted (the old error path re-seeded them from the database,
            // undoing the person's edits). Null when unreadable.
            List<InvoiceItemData>? items = null;
            if (!string.IsNullOrWhiteSpace(itemsJson))
            {
                try
                {
                    items = JsonSerializer.Deserialize<List<InvoiceItemData>>(
                        itemsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException jex)
                {
                    _logger.LogWarning(jex, "Unreadable itemsJson on invoice {InvoiceId}", Id);
                }
            }

            try
            {
                // Load existing invoice to check status
                ExistingInvoice = await _invoiceService.GetByIdAsync(tenantId, Id);

                if (ExistingInvoice == null)
                {
                    ErrorMessage = "Invoice not found";
                    return RedirectToPage("/Invoices/Index");
                }

                if (!IsEditable)
                {
                    ErrorMessage = StatusWarning;
                    return RedirectToPage("/Invoices/Detail", new { id = Id });
                }

                _logger.LogInformation("Updating invoice {InvoiceId}", Id);

                // 081 — each custom field's message under its own input.
                await LoadCustomFieldsAsync();
                CustomFieldForm.Validate(ActiveCustomFields, CustomFields, ModelState);

                // ── 074: due on or after issue ────────────────────────
                // The issue date is not editable here, so the comparison is
                // against the invoice's own. An invoice due before it was
                // raised turns Overdue on the next refresh, unexplained.
                if (Input.DueDate.Date < ExistingInvoice.IssueDateUtc.Date)
                {
                    return await RedisplayAsync(tenantId, items,
                        $"The due date ({Input.DueDate:dd MMM yyyy}) is before this invoice's " +
                        $"issue date ({ExistingInvoice.IssueDateUtc:dd MMM yyyy}). " +
                        "An invoice cannot fall due before it was raised.");
                }

                // (018) A quote's invoice: due date, notes and (081) its own
                // custom fields — no lines are sent, the API keeps the quote's.
                var lines = new List<InvoiceItemData>();

                if (!IsFromQuote)
                {
                    if (items == null || items.Count == 0)
                        return await RedisplayAsync(tenantId, items, "Please add at least one item to the invoice.");

                    if (items.Any(i => string.IsNullOrWhiteSpace(i.Name)))
                        return await RedisplayAsync(tenantId, items, "All items must have a name.");

                    lines = items;
                }

                // 081 — a custom field is wrong: its message is under its own input.
                if (CustomFieldForm.HasErrors(ModelState))
                    return await RedisplayAsync(tenantId, items, "Please correct the highlighted fields.");

                // A date box that failed to bind (a cleared box posts "") is said
                // plainly rather than saving the default binding left behind. ONLY
                // these keys: with nullable reference types on, an unposted string
                // property also lands in ModelState, and must not block the save.
                if (new[] { "Input.DueDate" }.Any(k => ModelState[k]?.Errors.Count > 0))
                    return await RedisplayAsync(tenantId, items, "Some of the dates could not be read. Please check them and try again.");

                var currentUser = await _currentUserService.GetCurrentUserAsync();

                // Create update DTO
                var updateDto = new UpdateInvoiceDto
                {
                    Id = Id,
                    TenantId = tenantId,
                    DueDateUtc = DateTime.SpecifyKind(Input.DueDate.Date, DateTimeKind.Utc),
                    Notes = Input.Notes,
                    UpdatedBy = currentUser.FullName,

                    // 081 — null when the field list could not be loaded: the
                    // form had no custom inputs, so leave the values alone.
                    CustomFields = CustomFieldsLoadFailed
                        ? null
                        : CustomFieldForm.ToSubmission(CustomFields, ActiveCustomFields),

                    Lines = lines.Select(i => new CreateInvoiceLineDto
                    {
                        ProductId = i.ProductId,
                        Name = i.Name,
                        Description = i.Description,
                        UnitPrice = i.UnitPrice,
                        Quantity = i.Quantity,            // 074: decimal, was int

                        // 052. Sent even though the handler would fill it
                        // from the product: a CUSTOM line has no product
                        // to ask.
                        UnitOfMeasure = i.UnitOfMeasure,

                        LineDiscount = i.LineDiscount,

                        // 055. Authoritative when set — the handler
                        // recomputes the amount from it.
                        DiscountPercent = i.DiscountPercent,

                        TaxRate = i.TaxRate / 100m,       // % -> decimal

                        // 067. "" and not null: null tells the handler to
                        // ask the product, which would refill a code the
                        // person cleared on purpose.
                        TaxCode = i.TaxCode ?? ""
                    }).ToList()
                };

                await _invoiceService.UpdateAsync(tenantId, Id, updateDto);
                _logger.LogInformation("✅ Invoice {InvoiceId} updated successfully", Id);

                // TempData is right here: this response REDIRECTS.
                SuccessMessage = $"Invoice {ExistingInvoice.Number} updated successfully!";
                return RedirectToPage("/Invoices/Detail", new { id = Id });
            }
            catch (InvalidOperationException ex)
            {
                // The API refused and said why (a bad line, no longer a draft,
                // a required custom field).
                _logger.LogWarning(ex, "Invoice update refused");
                return await RedisplayAsync(tenantId, items, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to update invoice {InvoiceId}", Id);
                return await RedisplayAsync(tenantId, items, "Failed to update the invoice. Please try again.");
            }
        }

        // ==================== HELPER METHODS ====================

        /// <summary>
        /// 081 (was LoadFormDataAsync + a TempData message). The form again,
        /// as POSTED — lines, due date, notes, custom fields — with the
        /// reason in PageError. Permissions are filled again (a POST gets a
        /// fresh page model), which the old reload never did.
        /// </summary>
        private async Task<IActionResult> RedisplayAsync(Guid tenantId, List<InvoiceItemData>? items, string message)
        {
            PageError = message;

            try
            {
                await InitializePermissionsAsync();

                ExistingInvoice ??= await _invoiceService.GetByIdAsync(tenantId, Id);
                if (ExistingInvoice == null)
                {
                    ErrorMessage = "Invoice not found";
                    return RedirectToPage("/Invoices/Index");
                }

                TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
                TenantCurrencyCode = _tenantService.GetCurrencyCode();
                await LoadDefaultTaxAsync();
                if (UserCanRead(Modules.Products))
                {
                    await LoadProductsAsync(tenantId);
                }
                if (ActiveCustomFields.Count == 0 && !CustomFieldsLoadFailed)
                    await LoadCustomFieldsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload form data");
            }

            // A quote's invoice has no editable lines; a manual one shows
            // the lines as posted when they could be read.
            PostedItemsJson = !IsFromQuote && items is { Count: > 0 } ? SeedFromPosted(items) : null;
            return Page();
        }

        /// <summary>081. Posted lines → the editor's seed, through QuoteItemsSeed (the one seed builder).</summary>
        private static string SeedFromPosted(IEnumerable<InvoiceItemData> items)
            => QuoteItemsSeed.FromPosted(items.Select(i => new QuoteItemData
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

        /// <summary>
        /// 081. The active Invoice custom fields. A failure is remembered
        /// (CustomFieldsLoadFailed) so the form says why the fields are
        /// missing and the save sends CustomFields = null — every stored
        /// value stays exactly as it was.
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
                _logger.LogWarning(ex, "Custom fields could not be loaded for the Edit Invoice form");
                ActiveCustomFields     = new List<CustomFieldDefinitionDto>();
                CustomFieldsLoadFailed = true;
            }
        }

        private async Task LoadDefaultTaxAsync()
        {
            try
            {
                // 081: ALWAYS a percentage (as Quotes/Create since 056). The
                // old "raw < 1 → ×100" guess made a real 0.5% rate 50%.
                DefaultTaxRate = await _tenantService.GetDefaultTaxRateAsync();

                var label = _tenantService.GetTaxLabel();
                if (!string.IsNullOrWhiteSpace(label)) DefaultTaxRateName = label;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load the tenant's default tax rate");
                DefaultTaxRate = 0m;
            }
        }

        private async Task LoadProductsAsync(Guid tenantId)
        {
            try
            {
                _logger.LogInformation("Loading products...");

                // ⚠ 074: THE CURRENCY. Without it the catalogue comes
                // back priced in the WORKSPACE's currency and this page
                // hands those figures to an invoice denominated in
                // something else — ₹ numbers billed as dollars.
                //
                // A product with no price in this currency comes back
                // with HasPriceInCurrency = false and ListPrice = 0; the
                // editor shows "no USD price" rather than a figure, and
                // its own "needs a unit price above zero" check stops the
                // save until somebody types one. A missing price is a
                // decision for a person, not a number to invent.
                //
                // MUST run after ExistingInvoice is loaded — that is what
                // knows the currency.
                var paginated = await _productService.GetAllAsync(
                    tenantId: tenantId,
                    pageNumber: 1,
                    pageSize: 1000,
                    category: null,
                    isActive: true,
                    searchTerm: null,
                    currency: ExistingInvoice?.Currency
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

        /// <summary>
        /// 074: one source of truth. The table this replaces said
        /// "AED" => "د.إ" while CurrencyConfiguration deliberately uses
        /// the string "AED" (see its 069 note), so the same invoice
        /// showed two different symbols depending on the screen. It was
        /// also case-sensitive, so "inr" fell through to the workspace
        /// symbol — a rupee sign on a dollar figure.
        /// </summary>
        public string GetSymbol(string? code) =>
            string.IsNullOrWhiteSpace(code)
                ? _tenantService.GetCurrencySymbol()
                : CurrencyConfiguration.GetCurrencySymbol(code);

        // 081: FormatDate / FormatDateTime / FormatCurrency removed — the
        // base class declares all three (see the header, note 4).

        /// <summary>
        /// 081. An amount in THIS INVOICE's currency: its symbol and its
        /// grouping (1,00,000.00 for INR). The read-only table used
        /// InvariantCulture, so an Indian invoice printed 100,000.00 here
        /// and 1,00,000.00 on its PDF.
        /// </summary>
        public string Money(decimal amount)
        {
            var code = ExistingInvoice?.Currency;
            // DocumentCulture — the same currency → format choice the PDF makes.
            return GetSymbol(code) + amount.ToString("N2", DocumentCulture.For(code));
        }
    }

    /// <summary>
    /// What _QuoteItemsEditor posts, deserialised. THE ONLY DECLARATION —
    /// Create.cshtml.cs used to nest a second copy of this class with its
    /// own int Quantity, and its own comment admitted it.
    ///
    /// Field names are camelCase on the wire and matched
    /// case-insensitively, which is how the editor's payload lines up
    /// with these properties.
    /// </summary>
    public class InvoiceItemData
    {
        public Guid? ProductId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public decimal UnitPrice { get; set; }

        /// <summary>
        /// 074: DECIMAL, was int. 12.5 m², 3.5 days, 0.75 kg. The int
        /// version rounded every fractional quantity down on its way in,
        /// silently — the 052 bug, still live on this one page.
        /// </summary>
        public decimal Quantity { get; set; }

        /// <summary>052: a code from UnitsOfMeasure. "unit" when nothing was chosen.</summary>
        public string? UnitOfMeasure { get; set; }

        public decimal LineDiscount { get; set; }

        /// <summary>055: set when the discount was agreed as a percentage; null when typed as an amount.</summary>
        public decimal? DiscountPercent { get; set; }

        /// <summary>A PERCENTAGE here (18 for 18%); converted once, where the DTO is built.</summary>
        public decimal TaxRate { get; set; }

        /// <summary>067: HSN / SAC or the local equivalent. "" when none applies.</summary>
        public string? TaxCode { get; set; }
    }
}
