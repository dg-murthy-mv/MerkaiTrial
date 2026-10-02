// =====================================================================
// InvoicesQueries.cs
// Location: MerkaiTrial.Application/Queries/Invoices/InvoicesQueries.cs
//
// COMPLETE FILE — replaces the existing one.
//
// 053: both read paths address the invoice to the customer's COMPANY.
// They set CompanyName from Deal.Contact.FirstName — a person's first
// name, in the column the list headed "Company" and the detail screen
// showed beside a building icon. The rule now lives in CustomerNaming
// and every path uses it.
//
//   The LIST is a server-side EF projection, so it cannot call a C#
//   method: it selects the four raw strings and resolves them in memory
//   straight afterwards. Same rule, same implementation — not a second
//   copy of the logic, which is how these drifted apart to begin with.
//
// 052: the line projection carries UnitOfMeasure through to
// InvoiceLineDto, so the invoice page can print "12.5 m²" instead of a
// bare "12.5". One line changed; everything else below is untouched.
//
// RECORD VISIBILITY (016) — READ SIDE ONLY, on purpose.
//   Invoices follow their deal (directly, or through the quote they were
//   raised from). The invoice LIST and STATISTICS now only include
//   invoices on deals the user can see. By-id is guarded in
//   InvoicesController (InvoiceAccessHandler).
//
// INVOICE WORKFLOW (018)
//   ✅ Reversed payments don't count: TotalPaid on the detail page sums
//      captured payments only (reversed ones still show in the history).
//   ✅ Overdue never includes drafts or void invoices (list, detail, stats).
//   ✅ Status filter "Overdue" is worked out from the due date — the stored
//      Overdue status is rarely set, so filtering on it showed almost
//      nothing. "Void" is accepted as another name for Cancelled.
//   ✅ Statistics count ISSUED invoices: TotalInvoices / TotalAmount /
//      Unpaid no longer include drafts (not owed yet) or void invoices
//      (never owed). Drafts are still counted in DraftInvoices.
// =====================================================================

using MerkaiTrial.Application.Common;          // 053: CustomerNaming
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Domain.Entities;
using Microsoft.Extensions.Logging;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MerkaiTrial.Application.Queries.Invoices
{
    // ==================== GET INVOICES ====================
    public class GetInvoicesQuery : ICommandHandler
    {
        public Guid TenantId { get; set; }
        public Guid? QuoteId { get; set; }
        public Guid? DealId { get; set; }
        public string? Status { get; set; }
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
    }

    public class GetInvoicesHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetInvoicesHandler> _logger;
        private readonly IRecordScopeService _scope;

        public GetInvoicesHandler(
            FlowDbContext context,
            IRecordScopeService scope,
            ILogger<GetInvoicesHandler> logger)
        {
            _context = context;
            _scope = scope;
            _logger = logger;
        }

        public async Task<List<InvoiceListItem>> Handle(
            GetInvoicesQuery request,
            CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Getting invoices for tenant {TenantId}", request.TenantId);

                var dealAccess = await _scope.GetAsync(RecordModules.Deals, cancellationToken);

                var query = _context.Invoices
                    .AsNoTracking()
                    .Where(i => i.TenantId == request.TenantId && !i.IsDeleted)
                    .WithVisibleDeal(_context, dealAccess);

                // Apply filters
                if (request.QuoteId.HasValue)
                    query = query.Where(i => i.QuoteId == request.QuoteId.Value);

                if (request.DealId.HasValue)
                    query = query.Where(i => i.DealId == request.DealId.Value);

                if (!string.IsNullOrEmpty(request.Status))
                {
                    var draftStatus = Domain.Enums.InvoiceStatus.Draft;
                    var voidStatus  = Domain.Enums.InvoiceStatus.Cancelled;

                    if (string.Equals(request.Status, "Overdue", StringComparison.OrdinalIgnoreCase))
                    {
                        // Computed, like everywhere else — not the stored status.
                        var now = DateTime.UtcNow;
                        query = query.Where(i => i.DueDateUtc.HasValue && i.DueDateUtc.Value < now && i.Balance > 0 &&
                                                 i.Status != draftStatus && i.Status != voidStatus);
                    }
                    else
                    {
                        var wanted = string.Equals(request.Status, "Void", StringComparison.OrdinalIgnoreCase)
                            ? "Cancelled"
                            : request.Status;

                        if (Enum.TryParse<Domain.Enums.InvoiceStatus>(wanted, ignoreCase: true, out var st))
                            query = query.Where(i => i.Status == st);
                    }
                }

                if (request.FromDate.HasValue)
                    query = query.Where(i => i.IssueDateUtc >= request.FromDate.Value);

                if (request.ToDate.HasValue)
                    query = query.Where(i => i.IssueDateUtc <= request.ToDate.Value);

                // 053: the Includes below do nothing for a projection this
                // shape — EF builds the SQL from the Select, not from them —
                // but they are left as they were rather than removed in a
                // round about naming.
                var rows = await query
                    .Include(i => i.Quote)
                    .Include(i => i.Deal)
                        .ThenInclude(d => d.Contact)
                    .Include(i => i.Deal)
                        .ThenInclude(d => d.Vertical)  // ✅ For VerticalName
                    .Include(i => i.Lines)
                    .OrderByDescending(i => i.CreatedAtUtc)
                    .Select(i => new
                    {
                        Item = new InvoiceListItem
                        {
                            Id = i.Id,
                            Number = i.Number,
                            IssueDateUtc = i.IssueDateUtc,
                            DueDateUtc = i.DueDateUtc,
                            Currency = i.Currency,
                            Status = i.Status.ToString(),
                            GrandTotal = i.Subtotal + i.TaxTotal - i.DiscountTotal,
                            Balance = i.Balance,

                            // CompanyName is NOT set here. It is resolved in
                            // memory a few lines below, because SQL cannot call
                            // CustomerNaming and writing the rule out again
                            // here is exactly how six copies of it drifted.

                            DealTitle = i.Deal != null ? i.Deal.Title :
                                        i.Quote != null && i.Quote.Deal != null ? i.Quote.Deal.Title : null,
                            QuoteNumber = i.Quote != null ? i.Quote.Number : null,
                            ItemCount = i.Lines.Count(l => !l.IsDeleted),
                            IsOverdue = i.DueDateUtc.HasValue && i.DueDateUtc.Value < DateTime.UtcNow && i.Balance > 0 &&
                                        i.Status != Domain.Enums.InvoiceStatus.Cancelled && i.Status != Domain.Enums.InvoiceStatus.Draft,
                            CreatedAtUtc = i.CreatedAtUtc
                        },

                        // 053. The invoice reaches its customer either
                        // directly (DealId) or through the quote it was
                        // raised from — the same either/or the DealTitle
                        // above already does. Four strings, resolved in
                        // memory by the one rule.
                        DealCompany = i.Deal != null
                            ? (i.Deal.Company != null ? i.Deal.Company.Name : null)
                            : (i.Quote != null && i.Quote.Deal != null && i.Quote.Deal.Company != null
                                ? i.Quote.Deal.Company.Name : null),

                        ContactCompany = i.Deal != null
                            ? (i.Deal.Contact != null && i.Deal.Contact.Company != null
                                ? i.Deal.Contact.Company.Name : null)
                            : (i.Quote != null && i.Quote.Deal != null && i.Quote.Deal.Contact != null
                               && i.Quote.Deal.Contact.Company != null
                                ? i.Quote.Deal.Contact.Company.Name : null),

                        FirstName = i.Deal != null
                            ? (i.Deal.Contact != null ? i.Deal.Contact.FirstName : null)
                            : (i.Quote != null && i.Quote.Deal != null && i.Quote.Deal.Contact != null
                                ? i.Quote.Deal.Contact.FirstName : null),

                        LastName = i.Deal != null
                            ? (i.Deal.Contact != null ? i.Deal.Contact.LastName : null)
                            : (i.Quote != null && i.Quote.Deal != null && i.Quote.Deal.Contact != null
                                ? i.Quote.Deal.Contact.LastName : null)
                    })
                    .ToListAsync(cancellationToken);

                var invoices = rows.Select(r =>
                {
                    // Display, not Company: the list has ONE column for this,
                    // so a person-to-person sale with no company should show
                    // the person rather than an empty cell.
                    r.Item.CompanyName = CustomerNaming.Display(
                        r.DealCompany, r.ContactCompany, r.FirstName, r.LastName);
                    return r.Item;
                }).ToList();

                _logger.LogInformation("Found {Count} invoices", invoices.Count);
                return invoices;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting invoices for tenant {TenantId}", request.TenantId);
                throw;
            }
        }
    }

    // ==================== GET INVOICE BY ID ====================
    public class GetInvoiceByIdQuery : ICommandHandler
    {
        public Guid TenantId { get; set; }
        public Guid Id { get; set; }
    }

    public class GetInvoiceByIdHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetInvoiceByIdHandler> _logger;

        public GetInvoiceByIdHandler(
            FlowDbContext context,
            ILogger<GetInvoiceByIdHandler> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<InvoiceDto> Handle(
            GetInvoiceByIdQuery request,
            CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Getting invoice {Id} for tenant {TenantId}", request.Id, request.TenantId);

                var invoice = await _context.Invoices
                    .AsNoTracking()
                    .Where(i => i.Id == request.Id && i.TenantId == request.TenantId && !i.IsDeleted)
                    .Include(i => i.Lines.Where(l => !l.IsDeleted))
                        .ThenInclude(l => l.Product)
                    .Include(i => i.Payments.Where(p => !p.IsDeleted))
                    .Include(i => i.Quote)
                        .ThenInclude(q => q!.Deal)
                            .ThenInclude(d => d!.Contact)
                                .ThenInclude(c => c!.Company)    // 053
                    .Include(i => i.Quote)
                        .ThenInclude(q => q!.Deal)
                            .ThenInclude(d => d!.Company)        // 053
                    .Include(i => i.Deal)
                        .ThenInclude(d => d.Contact)
                            .ThenInclude(c => c!.Company)        // 053
                    .Include(i => i.Deal)
                        .ThenInclude(d => d!.Company)            // 053
                    .Include(i => i.Deal)
                        .ThenInclude(d => d.Vertical)  // ✅ For VerticalName
                    .FirstOrDefaultAsync(cancellationToken);

                if (invoice == null)
                {
                    _logger.LogWarning("Invoice {Id} not found", request.Id);
                    throw new KeyNotFoundException($"Invoice {request.Id} not found");
                }

                var dto = new InvoiceDto
                {
                    Id = invoice.Id,
                    TenantId = invoice.TenantId,
                    QuoteId = invoice.QuoteId,
                    DealId = invoice.DealId,
                    Number = invoice.Number,
                    IssueDateUtc = invoice.IssueDateUtc,
                    DueDateUtc = invoice.DueDateUtc,
                    Currency = invoice.Currency,
                    Status = invoice.Status.ToString(),
                    Subtotal = invoice.Subtotal,
                    DiscountTotal = invoice.DiscountTotal,
                    TaxTotal = invoice.TaxTotal,
                    GrandTotal = invoice.Subtotal + invoice.TaxTotal - invoice.DiscountTotal,
                    Balance = invoice.Balance,
                    // Reversed payments stay in the list below but don't count (018).
                    TotalPaid = invoice.Payments.Where(p => !p.IsDeleted && p.Status == PaymentStatusNames.Captured).Sum(p => p.Amount),
                    QuoteNumber = invoice.Quote?.Number,
                    DealTitle = invoice.Deal?.Title ?? invoice.Quote?.Deal?.Title,

                    // 053. Two bugs on these two lines, not one. CompanyName
                    // held a first name; and ContactName concatenated with a
                    // literal " " in the middle, so with no deal it came out
                    // as a single SPACE rather than null — the detail screen's
                    // "is there a contact?" test was therefore always true and
                    // it drew an icon beside an empty line.
                    CompanyName = CustomerNaming.CompanyOf(invoice.Deal ?? invoice.Quote?.Deal),
                    ContactName = CustomerNaming.PersonOf(invoice.Deal ?? invoice.Quote?.Deal),
                    VerticalName = invoice.Deal?.Vertical?.Name,  // ✅ Industry from deal
                    PdfUrl = invoice.PdfUrl,
                    Notes = invoice.Notes,
                    PromptPayQrImageUrl = invoice.PromptPayQrImageUrl,
                    CheckoutUrl = invoice.CheckoutUrl,
                    Lines = invoice.Lines.Select(l => new InvoiceLineDto
                    {
                        Id = l.Id,
                        ProductId = l.ProductId,
                        Name = l.Name,
                        Description = l.Description,
                        UnitPrice = l.UnitPrice,
                        Quantity = l.Quantity,
                        UnitOfMeasure = l.UnitOfMeasure,        // 052
                        LineDiscount = l.LineDiscount,
                        DiscountPercent = l.DiscountPercent,    // 055
                        TaxRate = l.TaxRate,
                        LineTotal = (l.UnitPrice * l.Quantity) - l.LineDiscount,
                        LineTax = ((l.UnitPrice * l.Quantity) - l.LineDiscount) * l.TaxRate,
                        LineGrandTotal = ((l.UnitPrice * l.Quantity) - l.LineDiscount) * (1 + l.TaxRate),
                        ProductName = l.Product?.Name,
                        ProductSku = l.Product?.Sku
                    }).ToList(),
                    Payments = invoice.Payments.Select(p => new PaymentDto
                    {
                        Id = p.Id,
                        InvoiceId = p.InvoiceId,
                        Amount = p.Amount,
                        Currency = p.Currency,
                        Method = p.Method,
                        Status = p.Status,
                        PaidAtUtc = p.PaidAtUtc,
                        Notes = p.Notes,
                        ProviderTxnId = p.ProviderTxnId,
                        CreatedAtUtc = p.CreatedAtUtc,
                        CreatedBy = p.CreatedBy
                    }).ToList(),
                    CreatedAtUtc = invoice.CreatedAtUtc,
                    CreatedBy = invoice.CreatedBy,
                    UpdatedAtUtc = invoice.UpdatedAtUtc,
                    UpdatedBy = invoice.UpdatedBy,
                    IsOverdue = invoice.DueDateUtc.HasValue && invoice.DueDateUtc.Value < DateTime.UtcNow && invoice.Balance > 0 &&
                                invoice.Status != Domain.Enums.InvoiceStatus.Cancelled && invoice.Status != Domain.Enums.InvoiceStatus.Draft,
                    IsFullyPaid = invoice.Balance <= 0,
                    DaysUntilDue = invoice.DueDateUtc.HasValue ? (invoice.DueDateUtc.Value - DateTime.UtcNow).Days : 0
                };

                _logger.LogInformation("Found invoice {Number}", invoice.Number);
                return dto;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting invoice {Id}", request.Id);
                throw;
            }
        }
    }

    // ==================== GET INVOICE STATISTICS ====================
    public class GetInvoiceStatisticsQuery : ICommandHandler
    {
        public Guid TenantId { get; set; }

        /// <summary>
        /// 043. Optional period, bounding IssueDateUtc. Both null — the
        /// default, and what every existing caller sends — means all time,
        /// so nothing that works today changes.
        ///
        /// IssueDateUtc is a DATE-ONLY column: after round 034 it holds the
        /// picked calendar date at midnight with no offset. So the caller
        /// passes IFiscalYearService.DateOnlyRange, NOT TimestampRangeAsync.
        /// Shifting a date-only column by the tenant's timezone moves every
        /// boundary invoice into the wrong financial year — which, for an
        /// Indian tenant, is the difference between a March invoice landing
        /// in this year's GST return or next year's.
        /// </summary>
        public DateTime? FromUtc { get; set; }
        public DateTime? ToExclusiveUtc { get; set; }
    }

    public class GetInvoiceStatisticsHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetInvoiceStatisticsHandler> _logger;
        private readonly IRecordScopeService _scope;

        public GetInvoiceStatisticsHandler(
            FlowDbContext context,
            IRecordScopeService scope,
            ILogger<GetInvoiceStatisticsHandler> logger)
        {
            _context = context;
            _scope = scope;
            _logger = logger;
        }

        public async Task<InvoiceStatisticsDto> Handle(
            GetInvoiceStatisticsQuery request,
            CancellationToken cancellationToken)
        {
            try
            {
                // Previously loaded every invoice row/column into app memory
                // via ToListAsync() and counted/summed in C#. Fine at tiny
                // demo row counts, but scales linearly with data volume and
                // does work SQL Server is much better positioned to do.
                // Collapsed into one query with conditional aggregates —
                // same pattern as the Leads stats fix — single round-trip,
                // and only the aggregate numbers cross the wire, not every
                // invoice's full row data.
                var cancelled = Domain.Enums.InvoiceStatus.Cancelled;
                var draft     = Domain.Enums.InvoiceStatus.Draft;
                var sent      = Domain.Enums.InvoiceStatus.Sent;
                var viewed    = Domain.Enums.InvoiceStatus.Viewed;
                var paid      = Domain.Enums.InvoiceStatus.Paid;
                var now       = DateTime.UtcNow;

                var dealAccess = await _scope.GetAsync(RecordModules.Deals, cancellationToken);

                // (018) "Issued" = not a draft and not void. Totals, unpaid and
                // overdue are about issued invoices only — a draft isn't owed
                // yet and a void invoice never will be.
                var scoped = _context.Invoices
                    .AsNoTracking()
                    .Where(i => i.TenantId == request.TenantId && !i.IsDeleted)
                    .WithVisibleDeal(_context, dealAccess);

                // ── 043: the period, if one was asked for ─────────────────
                // On the ISSUE date, not CreatedAtUtc. An invoice back-dated
                // to 31 March belongs to that financial year however long it
                // sat in draft — which is exactly the case an accountant asks
                // about. Overdue stays "overdue as of now", judged against
                // DueDateUtc below: an invoice issued in the chosen period and
                // still unpaid today is the useful number.
                if (request.FromUtc.HasValue)
                    scoped = scoped.Where(i => i.IssueDateUtc >= request.FromUtc.Value);

                if (request.ToExclusiveUtc.HasValue)
                    scoped = scoped.Where(i => i.IssueDateUtc < request.ToExclusiveUtc.Value);

                var stats = await scoped
                    .GroupBy(i => 1)
                    .Select(g => new InvoiceStatisticsDto
                    {
                        TotalInvoices   = g.Count(i => i.Status != draft && i.Status != cancelled),
                        DraftInvoices   = g.Count(i => i.Status == draft),
                        SentInvoices    = g.Count(i => i.Status == sent || i.Status == viewed),
                        PaidInvoices    = g.Count(i => i.Status == paid),
                        OverdueInvoices = g.Count(i => i.DueDateUtc.HasValue && i.DueDateUtc.Value < now && i.Balance > 0 &&
                                                       i.Status != cancelled && i.Status != draft),
                        TotalAmount     = g.Where(i => i.Status != draft && i.Status != cancelled)
                                           .Sum(i => i.Subtotal + i.TaxTotal - i.DiscountTotal),
                        PaidAmount      = g.Where(i => i.Status == paid).Sum(i => i.Subtotal + i.TaxTotal - i.DiscountTotal),
                        UnpaidAmount    = g.Where(i => i.Balance > 0 && i.Status != cancelled && i.Status != draft).Sum(i => i.Balance),
                        OverdueAmount   = g.Where(i => i.DueDateUtc.HasValue && i.DueDateUtc.Value < now && i.Balance > 0 &&
                                                       i.Status != cancelled && i.Status != draft).Sum(i => i.Balance),
                        Currency        = string.Empty  // ✅ Set from tenant in view layer
                    })
                    .FirstOrDefaultAsync(cancellationToken);

                // GroupBy on an empty result set returns no groups at all
                // (not a group with zero counts) — handle tenants with no
                // invoices yet explicitly rather than returning null.
                return stats ?? new InvoiceStatisticsDto
                {
                    TotalInvoices = 0, DraftInvoices = 0, SentInvoices = 0,
                    PaidInvoices = 0, OverdueInvoices = 0,
                    TotalAmount = 0, PaidAmount = 0, UnpaidAmount = 0, OverdueAmount = 0,
                    Currency = string.Empty
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting invoice statistics");
                throw;
            }
        }
    }
}
