// =====================================================================
// FILE: MerkaiTrial.Application/Commands/Invoices/GenerateInvoicePdfHandler.cs
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Pdf;
using MerkaiTrial.Application.Services.Tenants;
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
                .Include(i => i.Deal)
                    .ThenInclude(d => d.Contact)
                .FirstOrDefaultAsync(ct);

            if (invoice == null)
                throw new KeyNotFoundException($"Invoice {invoiceId} not found for tenant {tenantId}");

            // ── Load tenant info ─────────────────────────────────────────
            var tenantName  = _tenantService.GetTenantName();

            // Phone + FromEmail not on ICurrentTenantService yet — load directly
            var tenantContact = await _db.Tenants
                .AsNoTracking()
                .Where(t => t.Id == tenantId && !t.IsDeleted)
                .Select(t => new { t.Phone, t.FromEmail })
                .FirstOrDefaultAsync(ct);

            var tenantPhone = tenantContact?.Phone;
            var tenantEmail = tenantContact?.FromEmail;

            // ── Build InvoiceDto from entity (re-use query handler logic) ─
            var contactName = invoice.Deal?.Contact != null
                ? $"{invoice.Deal.Contact.FirstName} {invoice.Deal.Contact.LastName}".Trim()
                : invoice.Quote?.Deal?.Contact != null
                    ? $"{invoice.Quote.Deal.Contact.FirstName} {invoice.Quote.Deal.Contact.LastName}".Trim()
                    : null;

            var dto = new InvoiceDto
            {
                Id            = invoice.Id,
                Number        = invoice.Number,
                IssueDateUtc  = invoice.IssueDateUtc,
                DueDateUtc    = invoice.DueDateUtc,
                Currency      = invoice.Currency,
                Status        = invoice.Status.ToString(),
                Subtotal      = invoice.Subtotal,
                DiscountTotal = invoice.DiscountTotal,
                TaxTotal      = invoice.TaxTotal,
                GrandTotal    = invoice.Subtotal + invoice.TaxTotal - invoice.DiscountTotal,
                Balance       = invoice.Balance,
                TotalPaid     = invoice.Payments.Where(p => !p.IsDeleted).Sum(p => p.Amount),
                DealTitle     = invoice.Deal?.Title ?? invoice.Quote?.Deal?.Title,
                CompanyName   = invoice.Deal?.Contact?.FirstName ?? invoice.Quote?.Deal?.Contact?.FirstName,
                ContactName   = contactName,
                QuoteNumber   = invoice.Quote?.Number,
                Notes         = invoice.Notes,
                IsOverdue     = invoice.DueDateUtc.HasValue
                                && invoice.DueDateUtc.Value < DateTime.UtcNow
                                && invoice.Balance > 0,
                Lines = invoice.Lines.Select(l => new InvoiceLineDto
                {
                    Id             = l.Id,
                    Name           = l.Name,
                    Description    = l.Description,
                    Quantity       = l.Quantity,
                    UnitPrice      = l.UnitPrice,
                    LineDiscount   = l.LineDiscount,
                    TaxRate        = l.TaxRate,
                    LineTotal      = (l.UnitPrice * l.Quantity) - l.LineDiscount,
                    LineTax        = ((l.UnitPrice * l.Quantity) - l.LineDiscount) * l.TaxRate,
                    LineGrandTotal = ((l.UnitPrice * l.Quantity) - l.LineDiscount) * (1 + l.TaxRate)
                }).ToList(),
                Payments = invoice.Payments.Select(p => new PaymentDto
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
                DateFormat     = _tenantService.GetDateFormat(),
                TaxLabel       = _tenantService.GetTaxLabel(),
                NumberFormat   = _tenantService.GetNumberFormat(),
                Tenant         = new TenantPdfInfo
                {
                    Name    = tenantName,
                    Phone   = tenantPhone,
                    Email   = tenantEmail,
                    Country = _tenantService.GetCountryName()
                }
            };

            var pdfBytes = _pdfService.Generate(model);

            _logger.LogInformation(
                "PDF generated for invoice {InvoiceId} — {Bytes} bytes", invoiceId, pdfBytes.Length);

            return pdfBytes;
        }
    }
}
