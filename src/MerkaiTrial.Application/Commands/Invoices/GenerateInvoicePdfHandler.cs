// =====================================================================
// FILE: MerkaiTrial.Application/Commands/Invoices/GenerateInvoicePdfHandler.cs
//
// CHANGES (018 — invoice workflow)
//   ✅ A DRAFT prints "DRAFT – not a tax invoice" where the number goes,
//      instead of its internal placeholder (DRAFT-3F9A1C2B). A customer
//      must never receive a draft that looks like a real invoice.
//   ✅ A VOID invoice prints its number with "(VOID)" after it.
//   ✅ Reversed payments are left off the PDF and don't count towards
//      "Paid" — only captured payments do, same as the rest of the app.
//   ✅ Overdue is never printed on a draft or a void invoice.
//   ✅ GrandTotal = Subtotal − Discount + Tax (same formula as before,
//      written in the same order as everywhere else).
//
// CHANGES (052c) — three lines, two of them defects that predate 052
//   ✅ UnitOfMeasure is carried into InvoiceLineDto. This handler builds
//      its own InvoiceDto instead of going through InvoicesQueries, so it
//      has to be kept in step by hand; without it the unit would sit at
//      its default and every invoice PDF would print "12.5" with no unit.
//   ✅ CurrencyCode is SET ON THE PDF MODEL. It never was. InvoicePdfService
//      derives its CultureInfo from exactly that field, so with it blank
//      every invoice PDF ever produced fell through to InvariantCulture:
//      an Indian invoice printed 100,000.00 where the CRM, the quote PDF
//      and the customer's accountant all say 1,00,000.00. The quote
//      handler has always set it; this one was missed.
//   ✅ CompanyName was invoice.Deal?.Contact?.FirstName — the contact's
//      FIRST NAME, under a heading called "BILL TO".
//
// CHANGES (053)
//   ✅ BILL TO is the customer's COMPANY now, with the contact underneath,
//      through CustomerNaming — the same rule the invoice list, the
//      invoice detail screen and both quote paths use. 052c put the
//      contact's FULL name there as a stopgap; this is the real answer,
//      and the Include chain gains Deal.Company and Deal.Contact.Company
//      (on the invoice's own deal AND on the quote's) to feed it.
// =====================================================================

using MerkaiTrial.Application.Common;          // 053: CustomerNaming
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Pdf;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Invoices
{
    public class GenerateInvoicePdfHandler : ICommandHandler
    {
        private readonly FlowDbContext      _db;
        private readonly IInvoicePdfService _pdfService;
        private readonly ICurrentTenantService _tenantService;
        private readonly ILogger<GenerateInvoicePdfHandler> _logger;

        public GenerateInvoicePdfHandler(
            FlowDbContext          db,
            IInvoicePdfService     pdfService,
            ICurrentTenantService  tenantService,
            ILogger<GenerateInvoicePdfHandler> logger)
        {
            _db            = db;
            _pdfService    = pdfService;
            _tenantService = tenantService;
            _logger        = logger;
        }

        /// <summary>Returns raw PDF bytes ready to stream as application/pdf.</summary>
        public async Task<byte[]> HandleAsync(Guid tenantId, Guid invoiceId, CancellationToken ct = default)
        {
            _logger.LogInformation("Generating PDF for invoice {InvoiceId}", invoiceId);

            // ── Load invoice with all required nav properties ─────────
            var invoice = await _db.Invoices
                .AsNoTracking()
                .Where(i => i.Id == invoiceId && i.TenantId == tenantId && !i.IsDeleted)
                .Include(i => i.Lines.Where(l => !l.IsDeleted))
                .Include(i => i.Payments.Where(p => !p.IsDeleted))
                .Include(i => i.Quote)
                    .ThenInclude(q => q.Deal)
                        .ThenInclude(d => d.Contact)
                            .ThenInclude(c => c!.Company)    // 053
                .Include(i => i.Quote)
                    .ThenInclude(q => q.Deal)
                        .ThenInclude(d => d!.Company)        // 053
                .Include(i => i.Deal)
                    .ThenInclude(d => d.Contact)
                        .ThenInclude(c => c!.Company)        // 053
                .Include(i => i.Deal)
                    .ThenInclude(d => d!.Company)            // 053
                .FirstOrDefaultAsync(ct);

            if (invoice == null)
                throw new KeyNotFoundException($"Invoice {invoiceId} not found for tenant {tenantId}");

            // ── Load tenant info ─────────────────────────────────────────
            var tenantName  = _tenantService.GetTenantName();

            // 065: was { Phone, FromEmail }. The company profile columns
            // 064 added are what let an invoice name the seller and carry a
            // tax registration number — which is the difference between a
            // receipt and a document somebody can file.
            var tenantInfo = await _db.Tenants
                .AsNoTracking()
                .Where(t => t.Id == tenantId && !t.IsDeleted)
                .Select(t => new
                {
                    t.Phone,
                    t.FromEmail,
                    t.ReplyToEmail,
                    t.LegalName,
                    t.AddressLine1,
                    t.AddressLine2,
                    t.City,
                    t.State,
                    t.PostalCode,
                    t.Website,
                    t.TaxNumber,
                    t.TaxNumberLabel
                })
                .FirstOrDefaultAsync(ct);

            var tenantPhone = tenantInfo?.Phone;

            // ReplyToEmail first — FromEmail is the sending identity, often
            // a no-reply address, and an invoice is something people answer.
            var tenantEmail = !string.IsNullOrWhiteSpace(tenantInfo?.ReplyToEmail)
                ? tenantInfo!.ReplyToEmail
                : tenantInfo?.FromEmail;

            // ── Build InvoiceDto from entity (re-use query handler logic) ─
            // 053. An invoice reaches its customer either directly (DealId)
            // or through the quote it was raised from. Pick the deal once,
            // here, so the company and the contact can never come from two
            // different places.
            var customerDeal = invoice.Deal ?? invoice.Quote?.Deal;

            // (018) What goes where the invoice number is printed.
            var isDraft = invoice.Status == InvoiceStatus.Draft;
            var isVoid  = invoice.Status == InvoiceStatus.Cancelled;
            var printedNumber = isDraft
                ? "DRAFT – not a tax invoice"
                : isVoid ? $"{invoice.Number} (VOID)" : invoice.Number;

            // Only captured payments count and are listed; reversed ones don't.
            var captured = invoice.Payments
                .Where(p => !p.IsDeleted && p.Status == PaymentStatusNames.Captured)
                .ToList();

            var dto = new InvoiceDto
            {
                Id            = invoice.Id,
                Number        = printedNumber,
                IssueDateUtc  = invoice.IssueDateUtc,
                DueDateUtc    = invoice.DueDateUtc,
                Currency      = invoice.Currency,
                Status        = invoice.Status.ToString(),
                Subtotal      = invoice.Subtotal,
                DiscountTotal = invoice.DiscountTotal,
                TaxTotal      = invoice.TaxTotal,
                GrandTotal    = invoice.Subtotal - invoice.DiscountTotal + invoice.TaxTotal,
                Balance       = invoice.Balance,
                TotalPaid     = captured.Sum(p => p.Amount),
                DealTitle     = customerDeal?.Title,
                // 053: BILL TO is the company; the contact is a line under it.
                CompanyName   = CustomerNaming.CompanyOf(customerDeal),
                ContactName   = CustomerNaming.PersonOf(customerDeal),
                QuoteNumber   = invoice.Quote?.Number,
                Notes         = invoice.Notes,
                IsOverdue     = !isDraft && !isVoid
                                && invoice.DueDateUtc.HasValue
                                && invoice.DueDateUtc.Value < DateTime.UtcNow
                                && invoice.Balance > 0,
                Lines = invoice.Lines.Select(l => new InvoiceLineDto
                {
                    Id             = l.Id,
                    Name           = l.Name,
                    Description    = l.Description,
                    Quantity       = l.Quantity,
                    UnitOfMeasure  = l.UnitOfMeasure,          // 052c
                    UnitPrice      = l.UnitPrice,

                    LineDiscount   = l.LineDiscount,

                    // 065 — THE SAME DEFECT FROM 055 AS THE QUOTE HANDLER
                    // HAD. InvoicePdfService prints line.DiscountLabel,
                    // which is computed from DiscountPercent, and this
                    // handler builds its own InvoiceDto and never set it.
                    // So every invoice PDF printed the discount AMOUNT even
                    // on lines agreed as a percentage — and the quote the
                    // invoice was raised from said "-10%".
                    //
                    // Exactly what this file's own 052c note warns about:
                    // it mirrors InvoicesQueries by hand, so a field added
                    // there has to be added here as well.
                    DiscountPercent = l.DiscountPercent,       // 065

                    TaxRate        = l.TaxRate,

                    // 067. Same hazard again. The PDF turns the HSN / SAC
                    // column on by asking whether any line has a code, so
                    // omitting this line would silently print every Indian
                    // tax invoice without the classification it is required
                    // to carry — no error, nothing to notice.
                    TaxCode        = l.TaxCode,                // 067
                    LineTotal      = (l.UnitPrice * l.Quantity) - l.LineDiscount,
                    LineTax        = ((l.UnitPrice * l.Quantity) - l.LineDiscount) * l.TaxRate,
                    LineGrandTotal = ((l.UnitPrice * l.Quantity) - l.LineDiscount) * (1 + l.TaxRate)
                }).ToList(),
                Payments = captured.Select(p => new PaymentDto
                {
                    Id            = p.Id,
                    Amount        = p.Amount,
                    Currency      = p.Currency,
                    Method        = p.Method,
                    PaidAtUtc     = p.PaidAtUtc,
                    Notes         = p.Notes,
                    ProviderTxnId = p.ProviderTxnId
                }).ToList()
            };

            // ── Build PDF model with tenant locale ────────────────────
            var model = new InvoicePdfModel
            {
                Invoice        = dto,
                CurrencySymbol = _tenantService.GetCurrencySymbol(),
                // 052c: InvoicePdfService maps this code to a CultureInfo and
                // formats every amount on the page with it. Left unset it was
                // null, so the switch fell to InvariantCulture and an INR
                // invoice grouped its digits the American way.
                CurrencyCode   = _tenantService.GetCurrencyCode(),
                DateFormat     = _tenantService.GetDateFormat(),
                TaxLabel       = _tenantService.GetTaxLabel(),
                NumberFormat   = _tenantService.GetNumberFormat(),
                // 065: the full letterhead — see TenantPdfInfo, where the
                // layout rules live so this and the quote PDF assemble the
                // block with the same code.
                Tenant         = new TenantPdfInfo
                {
                    Name    = tenantName,
                    Phone   = tenantPhone,
                    Email   = tenantEmail,
                    Country = _tenantService.GetCountryName(),

                    LegalName      = tenantInfo?.LegalName,
                    AddressLine1   = tenantInfo?.AddressLine1,
                    AddressLine2   = tenantInfo?.AddressLine2,
                    City           = tenantInfo?.City,
                    State          = tenantInfo?.State,
                    PostalCode     = tenantInfo?.PostalCode,
                    Website        = tenantInfo?.Website,
                    TaxNumber      = tenantInfo?.TaxNumber,
                    TaxNumberLabel = tenantInfo?.TaxNumberLabel
                }
            };

            var pdfBytes = _pdfService.Generate(model);

            _logger.LogInformation(
                "PDF generated for invoice {InvoiceId} — {Bytes} bytes", invoiceId, pdfBytes.Length);

            return pdfBytes;
        }
    }
}
