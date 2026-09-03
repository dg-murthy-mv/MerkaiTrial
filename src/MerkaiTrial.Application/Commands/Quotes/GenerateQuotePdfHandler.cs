// =====================================================================
// FILE: MerkaiTrial.Application/Commands/Quotes/GenerateQuotePdfHandler.cs
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Pdf;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Quotes
{
    public class GenerateQuotePdfHandler : ICommandHandler
    {
        private readonly FlowDbContext             _db;
        private readonly IQuotePdfService          _pdfService;
        private readonly ICurrentTenantService     _tenantService;
        private readonly ILogger<GenerateQuotePdfHandler> _logger;

        public GenerateQuotePdfHandler(
            FlowDbContext                      db,
            IQuotePdfService                   pdfService,
            ICurrentTenantService              tenantService,
            ILogger<GenerateQuotePdfHandler>   logger)
        {
            _db            = db;
            _pdfService    = pdfService;
            _tenantService = tenantService;
            _logger        = logger;
        }

        /// <summary>Returns raw PDF bytes ready to stream as application/pdf.</summary>
        public async Task<byte[]> HandleAsync(Guid tenantId, Guid quoteId, CancellationToken ct = default)
        {
            _logger.LogInformation("Generating PDF for quote {QuoteId}", quoteId);

            // ── Load quote with all required nav properties ────────────
            var quote = await _db.Quotes
                .AsNoTracking()
                .Where(q => q.Id == quoteId && q.TenantId == tenantId && !q.IsDeleted)
                .Include(q => q.Items.Where(i => !i.IsDeleted))
                .Include(q => q.Deal)
                    .ThenInclude(d => d.Contact)
                .Include(q => q.Deal)
                    .ThenInclude(d => d.Vertical)
                .FirstOrDefaultAsync(ct);

            if (quote == null)
                throw new KeyNotFoundException($"Quote {quoteId} not found for tenant {tenantId}");

            // ── Load tenant contact info ───────────────────────────────
            var tenantContact = await _db.Tenants
                .AsNoTracking()
                .Where(t => t.Id == tenantId && !t.IsDeleted)
                .Select(t => new { t.Phone, t.FromEmail })
                .FirstOrDefaultAsync(ct);

            // ── Build QuoteDto from entity (mirrors GetQuoteByIdHandler) ─
            var contactName = quote.Deal?.Contact != null
                ? $"{quote.Deal.Contact.FirstName} {quote.Deal.Contact.LastName}".Trim()
                : null;

            var dto = new QuoteDto
            {
                Id            = quote.Id,
                TenantId      = quote.TenantId,
                DealId        = quote.DealId,
                DealTitle     = quote.Deal?.Title ?? string.Empty,
                CompanyName   = contactName ?? "Unknown",
                Number        = quote.Number,
                IssueDateUtc  = quote.IssueDateUtc,
                ExpiresAtUtc  = quote.ExpiresAtUtc,
                Currency      = quote.Currency,
                Status        = quote.Status.ToString(),
                Subtotal      = quote.Subtotal,
                DiscountTotal = quote.DiscountTotal,
                TaxTotal      = quote.TaxTotal,
                GrandTotal    = quote.GrandTotal,
                VerticalName  = quote.Deal?.Vertical?.Name,
                Items = quote.Items.Select(i => new QuoteItemDto
                {
                    Id             = i.Id,
                    QuoteId        = i.QuoteId,
                    ProductId      = i.ProductId,
                    Name           = i.Name,
                    Description    = i.Description,
                    UnitPrice      = i.UnitPrice,
                    Quantity       = i.Quantity,
                    LineDiscount   = i.LineDiscount,
                    TaxRate        = i.TaxRate,
                    LineTotal      = (i.UnitPrice * i.Quantity) - i.LineDiscount,
                    LineTax        = ((i.UnitPrice * i.Quantity) - i.LineDiscount) * i.TaxRate,
                    LineGrandTotal = ((i.UnitPrice * i.Quantity) - i.LineDiscount) +
                                    (((i.UnitPrice * i.Quantity) - i.LineDiscount) * i.TaxRate)
                }).ToList()
            };

            // ── Build PDF model with tenant locale ────────────────────
            var model = new QuotePdfModel
            {
                Quote          = dto,
                CurrencySymbol = _tenantService.GetCurrencySymbol(),
                CurrencyCode   = _tenantService.GetCurrencyCode(),
                DateFormat     = _tenantService.GetDateFormat(),
                TaxLabel       = _tenantService.GetTaxLabel(),
                NumberFormat   = _tenantService.GetNumberFormat(),
                Tenant = new TenantPdfInfo
                {
                    Name    = _tenantService.GetTenantName(),
                    Phone   = tenantContact?.Phone,
                    Email   = tenantContact?.FromEmail,
                    Country = _tenantService.GetCountryName()
                }
            };

            var pdfBytes = _pdfService.Generate(model);

            _logger.LogInformation(
                "PDF generated for quote {QuoteId} — {Bytes} bytes", quoteId, pdfBytes.Length);

            return pdfBytes;
        }
    }
}
