// =====================================================================
// FILE: MerkaiTrial.Application/Commands/Quotes/GenerateQuotePdfHandler.cs
//
// 053: the QUOTE TO block is addressed to the customer's COMPANY, with
// the contact underneath, instead of putting the contact's name where the
// company belongs. The rule is CustomerNaming; the Include chain below
// gains Deal.Company and Deal.Contact.Company to feed it.
//
// 052a: ONE LINE — UnitOfMeasure is carried into QuoteItemDto.
//
// This handler builds its own QuoteDto rather than going through
// GetQuoteByIdHandler, so it has to be kept in step by hand. Without the
// line, QuoteItemDto.UnitOfMeasure would sit at its default "unit" and
// QuantityDisplay would print "12.5" with no unit on the PDF — the one
// document the customer actually keeps.
//
// STILL OUTSTANDING, and it is not in this file: whatever implements
// IQuotePdfService renders the line table, and if it prints
// item.Quantity directly it will now show "3.0000". The fix is
// item.Quantity -> item.QuantityDisplay. Send me that file.
// =====================================================================

using MerkaiTrial.Application.Common;          // 053: CustomerNaming
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
                        .ThenInclude(c => c!.Company)    // 053
                .Include(q => q.Deal)
                    .ThenInclude(d => d!.Company)        // 053
                .Include(q => q.Deal)
                    .ThenInclude(d => d.Vertical)
                .FirstOrDefaultAsync(ct);

            if (quote == null)
                throw new KeyNotFoundException($"Quote {quoteId} not found for tenant {tenantId}");

            // ── Load the seller's letterhead ───────────────────────────
            // 065: was { Phone, FromEmail }. The company profile columns
            // 064 added are what let this document name the seller and
            // carry a tax registration number. ReplyToEmail first — a
            // customer replying to a quote should reach the people who
            // sent it, not the no-reply address the system sends from.
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

            // ── Build QuoteDto from entity (mirrors GetQuoteByIdHandler) ─
            var dto = new QuoteDto
            {
                Id            = quote.Id,
                TenantId      = quote.TenantId,
                DealId        = quote.DealId,
                DealTitle     = quote.Deal?.Title ?? string.Empty,
                // 053: the company; the person goes in ContactName below.
                CompanyName   = CustomerNaming.CompanyOf(quote.Deal) ?? string.Empty,
                ContactName   = CustomerNaming.PersonOf(quote.Deal),
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
                    UnitOfMeasure  = i.UnitOfMeasure,          // 052a

                    LineDiscount   = i.LineDiscount,

                    // 065 — A DEFECT FROM 055, FOUND HERE RATHER THAN
                    // REPORTED. 055 added DiscountPercent so a line agreed
                    // as "-10%" reads back as a percentage. QuotePdfService
                    // prints item.DiscountLabel, which is computed from
                    // DiscountPercent — and this handler builds its own DTO
                    // and never set it. So DiscountLabel was always null and
                    // the PDF has been printing the AMOUNT on every line,
                    // including the ones the customer was told a percentage
                    // for. The quote page showed "-10%" and the PDF beside
                    // it showed "-฿590.00".
                    //
                    // This is the hazard the header warns about: the handler
                    // mirrors GetQuoteByIdHandler by hand, so every field
                    // added there has to be added here too, and 055 added
                    // one and stopped.
                    DiscountPercent = i.DiscountPercent,       // 065

                    TaxRate        = i.TaxRate,

                    // 067. THE SAME HAZARD, THE THIRD TIME. The PDF decides
                    // whether to print the HSN / SAC column by asking
                    // whether any line has a code — so forgetting this one
                    // line would not throw, would not look broken, and
                    // would simply print every Indian quote without the
                    // classification column while the quote page showed it.
                    TaxCode        = i.TaxCode,                // 067
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
                // 065: the full letterhead. Everything below Country is
                // new — see TenantPdfInfo, where the layout rules live so
                // this PDF, the invoice PDF and the public quote page
                // assemble the block identically.
                Tenant = new TenantPdfInfo
                {
                    Name    = _tenantService.GetTenantName(),
                    Country = _tenantService.GetCountryName(),

                    Phone   = tenantInfo?.Phone,

                    // ReplyToEmail before FromEmail: FromEmail is the
                    // sending identity (often noreply@), and a quote is a
                    // document somebody is meant to answer.
                    Email   = !string.IsNullOrWhiteSpace(tenantInfo?.ReplyToEmail)
                                ? tenantInfo!.ReplyToEmail
                                : tenantInfo?.FromEmail,

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
                "PDF generated for quote {QuoteId} — {Bytes} bytes", quoteId, pdfBytes.Length);

            return pdfBytes;
        }
    }
}
