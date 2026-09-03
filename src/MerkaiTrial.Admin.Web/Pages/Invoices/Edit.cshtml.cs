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
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Invoices;
using MerkaiTrial.Admin.Web.Services.Products;
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
        private readonly ILogger<EditModel> _logger;

        public EditModel(
            IInvoiceService invoiceService,
            IProductService productService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            IAuthorizationService authorizationService,
            ILogger<EditModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
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

        // Business-state lock — distinct from the CanUpdate PERMISSION property
        public bool IsEditable => ExistingInvoice?.Status == "Draft";

        // ✅ Tenant context
        public string TenantCurrencySymbol { get; private set; } = string.Empty;
        public string TenantCurrencyCode { get; private set; } = string.Empty;
        public string StatusWarning => ExistingInvoice?.Status switch
        {
            "Sent" => "This invoice has been sent. Only draft invoices can be edited.",
            "Paid" => "This invoice has been paid. Cannot edit paid invoices.",
            "PartiallyPaid" => "This invoice has payments. Cannot edit after payments received.",
            _ => ""
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
                    ErrorMessage = $"Cannot edit invoice with status '{ExistingInvoice.Status}'. Only draft invoices can be edited.";
                    return RedirectToPage("/Invoices/Detail", new { id = Id });
                }

                // Populate form with existing data
                Input.DueDate = ExistingInvoice.DueDateUtc?.ToLocalTime() ?? DateTime.Today.AddDays(30);
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

            try
            {
                if (Id == Guid.Empty)
                {
                    ErrorMessage = "Invalid invoice ID";
                    return RedirectToPage("/Invoices/Index");
                }

                var tenantId = _currentUserService.GetCurrentTenantId();

                // Load existing invoice to check status
                ExistingInvoice = await _invoiceService.GetByIdAsync(tenantId, Id);

                if (ExistingInvoice == null)
                {
                    ErrorMessage = "Invoice not found";
                    return RedirectToPage("/Invoices/Index");
                }

                if (!IsEditable)
                {
                    ErrorMessage = "Only draft invoices can be edited";
                    return RedirectToPage("/Invoices/Detail", new { id = Id });
                }

                _logger.LogInformation("Updating invoice {InvoiceId}", Id);

                // Validate items
                if (string.IsNullOrWhiteSpace(itemsJson))
                {
                    ErrorMessage = "Please add at least one item to the invoice";
                    await LoadFormDataAsync(tenantId);
                    return Page();
                }

                // ✅ Case-insensitive JSON deserialization
                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var items = JsonSerializer.Deserialize<List<InvoiceItemData>>(itemsJson, jsonOptions);

                if (items == null || items.Count == 0)
                {
                    _logger.LogWarning("No items provided for invoice update");
                    ErrorMessage = "Please add at least one item to the invoice";
                    await LoadFormDataAsync(tenantId);
                    return Page();
                }

                // Validate item names
                if (items.Any(i => string.IsNullOrWhiteSpace(i.Name)))
                {
                    ErrorMessage = "All items must have a name";
                    await LoadFormDataAsync(tenantId);
                    return Page();
                }

                var currentUser = await _currentUserService.GetCurrentUserAsync();

                // Create update DTO
                var updateDto = new UpdateInvoiceDto
                {
                    Id = Id,
                    TenantId = tenantId,
                    DueDateUtc = Input.DueDate.ToUniversalTime(),
                    Notes = Input.Notes,
                    UpdatedBy = currentUser.FullName,
                    Lines = items.Select(i => new CreateInvoiceLineDto
                    {
                        ProductId = i.ProductId,
                        Name = i.Name,
                        Description = i.Description,
                        UnitPrice = i.UnitPrice,
                        Quantity = i.Quantity,
                        LineDiscount = i.LineDiscount,
                        TaxRate = i.TaxRate / 100m // % -> decimal
                    }).ToList()
                };

                await _invoiceService.UpdateAsync(tenantId, Id, updateDto);
                _logger.LogInformation("✅ Invoice {InvoiceId} updated successfully", Id);

                SuccessMessage = $"Invoice {ExistingInvoice.Number} updated successfully!";
                return RedirectToPage("/Invoices/Detail", new { id = Id });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "❌ Cannot update invoice");
                ErrorMessage = ex.Message;
                return RedirectToPage("/Invoices/Detail", new { id = Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to update invoice {InvoiceId}", Id);
                ErrorMessage = $"Failed to update invoice: {ex.Message}";

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
                ExistingInvoice = await _invoiceService.GetByIdAsync(tenantId, Id);
                if (UserCanRead(Modules.Products))
                {
                    await LoadProductsAsync(tenantId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload form data");
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

        public string GetSymbol(string? code) => code switch
        {
            "INR" => "₹", "THB" => "฿", "PHP" => "₱", "AED" => "د.إ",
            "USD" => "$", "EUR" => "€", "GBP" => "£",
            _ => _tenantService.GetCurrencySymbol()
        };
        public string FormatDate(DateTime utcDate) => _tenantService.FormatDate(utcDate);
        public string FormatDateTime(DateTime utcDate) => _tenantService.FormatDateTime(utcDate);
        public string FormatCurrency(decimal amount) => _tenantService.FormatCurrency(amount);
    }

    // Helper class for JSON deserialization
    public class InvoiceItemData
    {
        public Guid? ProductId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public decimal UnitPrice { get; set; }
        public int Quantity { get; set; }
        public decimal LineDiscount { get; set; }
        public decimal TaxRate { get; set; } // As percentage (18 for 18%)
    }
}
