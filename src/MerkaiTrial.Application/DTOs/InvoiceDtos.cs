// =====================================================================
// InvoiceDtos.cs
// Location: MerkaiTrial.Application/DTOs/InvoiceDtos.cs
//
// COMPLETE FILE — 052 (Phase B of the catalogue).
//
// InvoiceLineDto and CreateInvoiceLineDto move Quantity from int to
// decimal and gain UnitOfMeasure, matching QuoteItemDto exactly. They
// have to move in the same round: CreateInvoiceFromQuoteHandler copies
// every quote item into a CreateInvoiceLineDto, so a decimal quantity on
// the quote and an int on the invoice would round 12.5 m² down to 12 the
// moment the invoice was raised — silently, with the money changing.
//
// InvoiceLineDto.QuantityDisplay ("12.5 m²") is what the views print.
//
// 067 — TAX CLASSIFICATION ON THE LINE
//
// Both line shapes gain TaxCode, for the same "they have to move in the
// same round" reason as above: CreateInvoiceFromQuoteHandler copies every
// quote item into a CreateInvoiceLineDto, so a code on the quote line
// with nowhere to land on the invoice line would be dropped exactly at
// the point it starts to matter — the tax invoice is the document that
// legally has to carry it.
//
// Nullable. NULL is the normal state outside the markets that ask for a
// code, and every screen and both PDFs show the column only when at least
// one line on the document has one.
// =====================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MerkaiTrial.Application.Common;          // 055: LineDiscounts
using MerkaiTrial.Application.Configuration;   // 052: UnitsOfMeasure

namespace MerkaiTrial.Application.DTOs
{
    public class InvoiceDto
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid? QuoteId { get; set; }
        public Guid? DealId { get; set; }

        public string Number { get; set; } = string.Empty;
        public DateTime IssueDateUtc { get; set; }
        public DateTime? DueDateUtc { get; set; }
        public string Currency { get; set; } = string.Empty;  // ✅ Set from tenant
        public string Status { get; set; } = "Draft";
        public string? VerticalName { get; set; }

        // Breakdown amounts
        public decimal Subtotal { get; set; }
        public decimal DiscountTotal { get; set; }
        public decimal TaxTotal { get; set; }
        public decimal GrandTotal { get; set; }
        public decimal Balance { get; set; }
        public decimal TotalPaid { get; set; }

        // Related data
        public string? QuoteNumber { get; set; }
        public string? DealTitle { get; set; }
        public string? CompanyName { get; set; }
        public string? ContactName { get; set; }

        public string? PdfUrl { get; set; }
        public string? Notes { get; set; }

        // Payment gateway
        public string? PromptPayQrImageUrl { get; set; }
        public string? CheckoutUrl { get; set; }

        // Items and payments
        public List<InvoiceLineDto> Lines { get; set; } = new();
        public List<PaymentDto> Payments { get; set; } = new();

        // Audit
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }

        // Calculated
        public bool IsOverdue { get; set; }
        public bool IsFullyPaid { get; set; }
        public int DaysUntilDue { get; set; }
        public int ItemCount => Lines.Count;
    }

    public class InvoiceListItem
    {
        public Guid Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public DateTime IssueDateUtc { get; set; }
        public DateTime? DueDateUtc { get; set; }
        public string Currency { get; set; } = string.Empty;  // ✅ Set from tenant
        public string Status { get; set; } = "Draft";

        public decimal GrandTotal { get; set; }
        public decimal Balance { get; set; }

        public string CompanyName { get; set; } = string.Empty;
        public string? DealTitle { get; set; }
        public string? QuoteNumber { get; set; }

        public int ItemCount { get; set; }
        public bool IsOverdue { get; set; }

        public DateTime CreatedAtUtc { get; set; }
    }

    public class CreateInvoiceDto
    {
        public Guid TenantId { get; set; }
        public Guid? QuoteId { get; set; }
        public Guid? DealId { get; set; }

        public DateTime IssueDateUtc { get; set; }
        public DateTime? DueDateUtc { get; set; }
        public string Currency { get; set; } = string.Empty;  // ✅ Set from tenant
        public string? Notes { get; set; }

        public List<CreateInvoiceLineDto> Lines { get; set; } = new();

        public string CreatedBy { get; set; } = string.Empty;

        // Optional: Send immediately after creation
        public bool SendImmediately { get; set; }
    }

    public class CreateInvoiceFromQuoteDto
    {
        public Guid TenantId { get; set; }
        public Guid QuoteId { get; set; }

        public DateTime IssueDateUtc { get; set; }
        public DateTime? DueDateUtc { get; set; }
        public string? Notes { get; set; }

        public string CreatedBy { get; set; } = string.Empty;
        public bool SendImmediately { get; set; }
    }

    public class UpdateInvoiceDto
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }

        public DateTime? DueDateUtc { get; set; }
        public string? Notes { get; set; }

        public List<CreateInvoiceLineDto> Lines { get; set; } = new();

        public string UpdatedBy { get; set; } = string.Empty;
    }

    public class UpdateInvoiceStatusDto
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string UpdatedBy { get; set; } = string.Empty;
    }

    public class InvoiceLineDto
    {
        public Guid Id { get; set; }
        public Guid? ProductId { get; set; }

        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public decimal UnitPrice { get; set; }

        /// <summary>052: decimal, was int.</summary>
        public decimal Quantity { get; set; }

        /// <summary>052: code from UnitsOfMeasure. Copied from the quote line.</summary>
        public string UnitOfMeasure { get; set; } = UnitsOfMeasure.Unit;

        /// <summary>
        /// 052: the quantity as it should be PRINTED — "12.5 m²", "3",
        /// "2 days". Display only.
        /// </summary>
        public string QuantityDisplay => UnitsOfMeasure.Describe(Quantity, UnitOfMeasure);

        public decimal LineDiscount { get; set; }

        /// <summary>055: set when the discount was agreed as a percentage.</summary>
        public decimal? DiscountPercent { get; set; }

        /// <summary>055: "-10%", or null when the discount was an amount.</summary>
        public string? DiscountLabel => LineDiscounts.PercentLabel(DiscountPercent);

        public decimal TaxRate { get; set; }

        /// <summary>
        /// 067: the tax classification this line was INVOICED under — HSN /
        /// SAC in India, the local equivalent elsewhere, NULL where none
        /// applies. Copied from the quote line when the invoice was raised
        /// from a quote; snapshotted, not joined to the product.
        /// </summary>
        public string? TaxCode { get; set; }

        /// <summary>067: true when this line has a code to print.</summary>
        public bool HasTaxCode => !string.IsNullOrWhiteSpace(TaxCode);

        // Calculated
        public decimal LineTotal { get; set; }
        public decimal LineTax { get; set; }
        public decimal LineGrandTotal { get; set; }

        public string? ProductName { get; set; }
        public string? ProductSku { get; set; }
    }

    public class CreateInvoiceLineDto
    {
        public Guid? ProductId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public decimal UnitPrice { get; set; }

        /// <summary>052: decimal, was int.</summary>
        public decimal Quantity { get; set; }

        /// <summary>
        /// 052. Blank with a ProductId set → the handler snapshots the
        /// product's unit, so an existing API caller keeps working.
        /// </summary>
        public string? UnitOfMeasure { get; set; }

        public decimal LineDiscount { get; set; }

        /// <summary>
        /// 055. Authoritative when set: the handler recomputes LineDiscount
        /// from it. See LineDiscounts.cs.
        /// </summary>
        public decimal? DiscountPercent { get; set; }

        public decimal TaxRate { get; set; }  // Decimal format (0.18 for 18%)

        /// <summary>
        /// 067. Blank with a ProductId set → the handler snapshots the
        /// product's own code, so an existing API caller keeps working.
        /// CreateInvoiceFromQuoteHandler fills it from the quote line, which
        /// takes priority over the product: the invoice must say what the
        /// customer accepted, not what the catalogue says today.
        /// </summary>
        public string? TaxCode { get; set; }
    }

    public class InvoiceStatisticsDto
    {
        public int TotalInvoices { get; set; }
        public int DraftInvoices { get; set; }
        public int SentInvoices { get; set; }
        public int PaidInvoices { get; set; }
        public int OverdueInvoices { get; set; }

        public decimal TotalAmount { get; set; }
        public decimal PaidAmount { get; set; }
        public decimal UnpaidAmount { get; set; }
        public decimal OverdueAmount { get; set; }

        public string Currency { get; set; } = string.Empty;  // ✅ Set from tenant
    }

    public class PaymentDto
    {
        public Guid Id { get; set; }
        public Guid InvoiceId { get; set; }

        public decimal Amount { get; set; }
        public string Currency { get; set; } = string.Empty;  // ✅ Set from tenant
        public string Method { get; set; } = "BankTransfer";
        public string Status { get; set; } = "Captured";

        public DateTime PaidAtUtc { get; set; }
        public string? Notes { get; set; }
        public string? ProviderTxnId { get; set; }

        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
    }

    public class CreatePaymentDto
    {
        public Guid TenantId { get; set; }
        public Guid InvoiceId { get; set; }

        public decimal Amount { get; set; }
        public string Currency { get; set; } = string.Empty;  // ✅ Set from tenant
        public string Method { get; set; } = "BankTransfer";
        public DateTime PaidAtUtc { get; set; }
        public string? Notes { get; set; }
        public string? ProviderTxnId { get; set; }

        public string CreatedBy { get; set; } = string.Empty;
    }
}
