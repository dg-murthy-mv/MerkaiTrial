// =====================================================================
// InvoicesQueries.cs
// Location: MerkaiTrial.Application/Queries/Invoices/InvoicesQueries.cs
//
// COMPLETE FILE — replaces the existing one.
//
// 081: the list carries each row's "Show on the list" custom values; the
// by-id read carries every stored value and what the invoice PRINTS
// (InvoiceDocument — frozen at issue, live on a draft).
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
// 067: the line projection carries TaxCode through to InvoiceLineDto, so
// the invoice page and the invoice PDF can print the HSN / SAC code
// against each line. One line changed. The LIST projection is deliberately
// untouched — a tax code is a per-line detail and has no meaning on a row
// that shows one invoice total.
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
//
// 096 — the Invoices page gets a paged, searched, sorted read
//   (GetInvoicesHandler.HandlePagedAsync). Notes above the handler.
// =====================================================================

using MerkaiTrial.Application.Commands.CustomFields;   // 081: CustomFieldListQuery
using MerkaiTrial.Application.Commands.Invoices;       // 081: InvoiceDocument
using MerkaiTrial.Application.Common;          // 053: CustomerNaming
using MerkaiTrial.Application.Configuration;   // 081: CustomFieldEntityTypes
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
    //
    // 096 — THE INVOICES LIST PAGE, SEARCHED, SORTED AND PAGED ON THE SERVER
    //
    //   Handle (unchanged in meaning) still returns EVERY matching invoice
    //   as a List. The quote and deal pages call it with a quoteId / dealId
    //   and expect the whole set, so it keeps that shape. One change: its
    //   order ends with Id, so two invoices created in the same instant can
    //   no longer swap places between loads.
    //
    //   HandlePagedAsync is new, for the Invoices page, behind
    //   GET api/invoices/paged:
    //     • SearchTerm   number, quote number, deal title, customer (deal
    //                    company, contact's company, contact's name) and
    //                    the invoice's custom text / dropdown values
    //     • CustomFilters  the ?cf= filters the other lists have
    //     • Sort / Dir   number, customer, issued, due, total, balance,
    //                    status, created — always ending with Id. No sort:
    //                    newest issue date first.
    //     • PageNumber / PageSize  clamped 1–100; a page past the end is
    //                    pulled back to the last page
    //   Status / FromDate / ToDate mean exactly what they mean in Handle —
    //   both go through the same Filtered() so they cannot drift apart.
    //
    //   The CUSTOMER sort orders by the same three names CustomerNaming
    //   chooses between (deal company, then contact's company, then the
    //   person) in SQL. The rule itself still lives in CustomerNaming and
    //   decides what is DISPLAYED.

    public class GetInvoicesQuery : ICommandHandler
    {
        public Guid TenantId { get; set; }
        public Guid? QuoteId { get; set; }
        public Guid? DealId { get; set; }
        public string? Status { get; set; }
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }

        // ── 096: the paged read only (HandlePagedAsync) ──────────────
        public string? SearchTerm { get; set; }
        public string? Sort { get; set; }
        public string? Dir { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 10;
        public List<CustomFieldFilter> CustomFilters { get; set; } = new();
    }

    public class GetInvoicesHandler : ICommandHandler
    {
        /// <summary>096. The most rows one page of the paged read can ask for.</summary>
        public const int MaxPageSize = 100;

        /// <summary>096. The column keys the paged read can be sorted by.</summary>
        public static readonly string[] SortKeys =
            { "number", "customer", "issued", "due", "total", "balance", "status", "created" };

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

        /// <summary>Every matching invoice, newest first. See the note above.</summary>
        public async Task<List<InvoiceListItem>> Handle(
            GetInvoicesQuery request,
            CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Getting invoices for tenant {TenantId}", request.TenantId);

                var query = await FilteredAsync(request, cancellationToken);

                var invoices = await ProjectAsync(
                    request.TenantId,
                    query.OrderByDescending(i => i.CreatedAtUtc).ThenBy(i => i.Id),   // 096 — stable
                    cancellationToken);

                _logger.LogInformation("Found {Count} invoices", invoices.Count);
                return invoices;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting invoices for tenant {TenantId}", request.TenantId);
                throw;
            }
        }

        /// <summary>096. One page of the Invoices list — searched, filtered and sorted on the server.</summary>
        public async Task<PaginatedResult<InvoiceListItem>> HandlePagedAsync(
            GetInvoicesQuery request,
            CancellationToken cancellationToken)
        {
            try
            {
                var pageSize = Math.Clamp(request.PageSize, 1, MaxPageSize);
                var page     = Math.Max(request.PageNumber, 1);
                var tenantId = request.TenantId;

                var query = await FilteredAsync(request, cancellationToken);

                var defs = await CustomFieldListQuery.ActiveDefinitionsAsync(
                    _context, tenantId, CustomFieldEntityTypes.Invoice, cancellationToken);

                // ── search ───────────────────────────────────────────────
                if (!string.IsNullOrWhiteSpace(request.SearchTerm))
                {
                    var s = request.SearchTerm.Trim().ToLower();

                    // Null = no custom field could match; keep the plain search.
                    var cfMatches = CustomFieldListQuery.SearchMatches(_context, tenantId, defs, s);

                    // Written out twice rather than OR-ing in a constant
                    // false, which EF would still have to translate.
                    query = cfMatches is null
                        ? query.Where(i =>
                            i.Number.ToLower().Contains(s) ||
                            (i.Quote != null && i.Quote.Number.ToLower().Contains(s)) ||
                            (i.Deal != null && (
                                i.Deal.Title.ToLower().Contains(s) ||
                                (i.Deal.Company != null && i.Deal.Company.Name.ToLower().Contains(s)) ||
                                (i.Deal.Contact != null && (
                                    i.Deal.Contact.FirstName.ToLower().Contains(s) ||
                                    (i.Deal.Contact.LastName != null && i.Deal.Contact.LastName.ToLower().Contains(s)) ||
                                    (i.Deal.Contact.Company != null && i.Deal.Contact.Company.Name.ToLower().Contains(s)))))) ||
                            (i.Deal == null && i.Quote != null && i.Quote.Deal != null && (
                                i.Quote.Deal.Title.ToLower().Contains(s) ||
                                (i.Quote.Deal.Company != null && i.Quote.Deal.Company.Name.ToLower().Contains(s)) ||
                                (i.Quote.Deal.Contact != null && (
                                    i.Quote.Deal.Contact.FirstName.ToLower().Contains(s) ||
                                    (i.Quote.Deal.Contact.LastName != null && i.Quote.Deal.Contact.LastName.ToLower().Contains(s)) ||
                                    (i.Quote.Deal.Contact.Company != null && i.Quote.Deal.Contact.Company.Name.ToLower().Contains(s)))))))
                        : query.Where(i =>
                            i.Number.ToLower().Contains(s) ||
                            (i.Quote != null && i.Quote.Number.ToLower().Contains(s)) ||
                            (i.Deal != null && (
                                i.Deal.Title.ToLower().Contains(s) ||
                                (i.Deal.Company != null && i.Deal.Company.Name.ToLower().Contains(s)) ||
                                (i.Deal.Contact != null && (
                                    i.Deal.Contact.FirstName.ToLower().Contains(s) ||
                                    (i.Deal.Contact.LastName != null && i.Deal.Contact.LastName.ToLower().Contains(s)) ||
                                    (i.Deal.Contact.Company != null && i.Deal.Contact.Company.Name.ToLower().Contains(s)))))) ||
                            (i.Deal == null && i.Quote != null && i.Quote.Deal != null && (
                                i.Quote.Deal.Title.ToLower().Contains(s) ||
                                (i.Quote.Deal.Company != null && i.Quote.Deal.Company.Name.ToLower().Contains(s)) ||
                                (i.Quote.Deal.Contact != null && (
                                    i.Quote.Deal.Contact.FirstName.ToLower().Contains(s) ||
                                    (i.Quote.Deal.Contact.LastName != null && i.Quote.Deal.Contact.LastName.ToLower().Contains(s)) ||
                                    (i.Quote.Deal.Contact.Company != null && i.Quote.Deal.Contact.Company.Name.ToLower().Contains(s)))))) ||
                            cfMatches.Contains(i.Id));
                }

                // ── custom field filters, ANDed ──────────────────────────
                foreach (var clause in CustomFieldListQuery.FilterClauses(_context, tenantId, defs, request.CustomFilters))
                {
                    var ids = clause.EntityIds;   // a local, so EF inlines the subquery
                    query = clause.Exclude
                        ? query.Where(i => !ids.Contains(i.Id))
                        : query.Where(i => ids.Contains(i.Id));
                }

                var totalCount = await query.CountAsync(cancellationToken);

                // A page past the end becomes the last page.
                var totalPages = totalCount == 0 ? 1 : (int)Math.Ceiling(totalCount / (double)pageSize);
                if (page > totalPages) page = totalPages;

                var items = await ProjectAsync(
                    tenantId,
                    Ordered(query, request.Sort, request.Dir).Skip((page - 1) * pageSize).Take(pageSize),
                    cancellationToken);

                return new PaginatedResult<InvoiceListItem>
                {
                    Items      = items,
                    TotalCount = totalCount,
                    Page       = page,
                    PageSize   = pageSize
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting the invoices page for tenant {TenantId}", request.TenantId);
                throw;
            }
        }

        /// <summary>
        /// The visibility rule and the Quote / Deal / Status / date filters —
        /// shared by both reads so they mean the same thing.
        /// </summary>
        private async Task<IQueryable<Invoice>> FilteredAsync(GetInvoicesQuery request, CancellationToken ct)
        {
            var dealAccess = await _scope.GetAsync(RecordModules.Deals, ct);

            var query = _context.Invoices
                .AsNoTracking()
                .Where(i => i.TenantId == request.TenantId && !i.IsDeleted)
                .WithVisibleDeal(_context, dealAccess);

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

            return query;
        }

        /// <summary>
        /// 096. The paged read's order, always ending with Id. Default: the
        /// newest ISSUE date first (what an invoice list is read by), newest
        /// created first within a day.
        /// </summary>
        private static IOrderedQueryable<Invoice> Ordered(IQueryable<Invoice> invoices, string? sort, string? dir)
        {
            var key  = ListSort.Key(sort, SortKeys);
            var desc = ListSort.IsDescending(dir, fallback: key is not ("number" or "customer" or "status"));

            IOrderedQueryable<Invoice> ordered = key switch
            {
                "number"   => invoices.OrderByDir(i => i.Number, desc),
                "customer" => invoices.OrderByDir(i =>
                                  i.Deal != null
                                      ? (i.Deal.Company != null ? i.Deal.Company.Name
                                         : i.Deal.Contact != null && i.Deal.Contact.Company != null ? i.Deal.Contact.Company.Name
                                         : i.Deal.Contact != null ? i.Deal.Contact.FirstName : "")
                                      : i.Quote != null && i.Quote.Deal != null
                                          ? (i.Quote.Deal.Company != null ? i.Quote.Deal.Company.Name
                                             : i.Quote.Deal.Contact != null && i.Quote.Deal.Contact.Company != null ? i.Quote.Deal.Contact.Company.Name
                                             : i.Quote.Deal.Contact != null ? i.Quote.Deal.Contact.FirstName : "")
                                          : "", desc),
                "issued"   => invoices.OrderByDir(i => i.IssueDateUtc, desc),
                // An invoice with no due date goes last in either direction.
                "due"      => invoices.OrderBy(i => i.DueDateUtc == null).ThenByDir(i => i.DueDateUtc, desc),
                "total"    => invoices.OrderByDir(i => i.Subtotal + i.TaxTotal - i.DiscountTotal, desc),
                "balance"  => invoices.OrderByDir(i => i.Balance, desc),
                "status"   => invoices.OrderByDir(i => i.Status, desc),
                "created"  => invoices.OrderByDir(i => i.CreatedAtUtc, desc),
                _          => invoices.OrderByDescending(i => i.IssueDateUtc)
            };

            if (key is not "created")
                ordered = ordered.ThenByDescending(i => i.CreatedAtUtc);

            return ordered.ThenBy(i => i.Id);
        }

        /// <summary>
        /// The list projection: the row, the customer named by the one rule
        /// (CustomerNaming), and the "Show on the list" custom values.
        /// The ORDER is the caller's — this keeps whatever it is given.
        /// </summary>
        private async Task<List<InvoiceListItem>> ProjectAsync(
            Guid tenantId, IQueryable<Invoice> ordered, CancellationToken cancellationToken)
        {
            var rows = await ordered
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
                        CreatedAtUtc = i.CreatedAtUtc,

                        // 071. The SNAPSHOT columns on the invoice row,
                        // not a join to the schedule — which is the
                        // point of them being columns. The list shows
                        // "2 of 3 — On delivery" as a badge; an invoice
                        // for a whole quote carries NULL here and the
                        // badge does not render.
                        MilestoneId = i.MilestoneId,
                        MilestoneName = i.MilestoneName,
                        MilestoneSequence = i.MilestoneSequence,
                        MilestoneCount = i.MilestoneCount
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

            // 081 — the "Show on the list" custom values, these rows only.
            var cfDefs = await CustomFieldListQuery.ActiveDefinitionsAsync(
                _context, tenantId, CustomFieldEntityTypes.Invoice, cancellationToken);
            if (cfDefs.Any(d => d.ShowInList) && invoices.Count > 0)
            {
                var cfValues = await CustomFieldListQuery.ListValuesAsync(
                    _context, tenantId, cfDefs, invoices.Select(i => i.Id).ToList(), cancellationToken);
                foreach (var item in invoices)
                    if (cfValues.TryGetValue(item.Id, out var map)) item.CustomFieldValues = map;
            }

            return invoices;
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

                    // 071. SNAPSHOTS off the invoice row. The schedule is
                    // deliberately NOT joined here: an issued invoice has
                    // to keep saying "Milestone 2 of 3 — On delivery" after
                    // somebody renames the stage or re-cuts the schedule,
                    // and reading it live is how a document starts
                    // disagreeing with itself. Definition live, document
                    // frozen — 067's rule for TaxCode and 070's for bundle
                    // contents, applied a third time.
                    //
                    // QuoteNumber above is what InvoiceDto.MilestoneBasisNote
                    // prints alongside these ("Amounts shown are 40% of
                    // accepted quote QUO-0042"), so the two travel together.
                    MilestoneId = invoice.MilestoneId,
                    MilestoneName = invoice.MilestoneName,
                    MilestoneSequence = invoice.MilestoneSequence,
                    MilestoneCount = invoice.MilestoneCount,
                    MilestonePercent = invoice.MilestonePercent,

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
                        TaxCode = l.TaxCode,                    // 067
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

                // 081 — stored values, and what the invoice prints (frozen
                // at issue, live on a draft).
                await InvoiceDocument.FillAsync(_context, dto, invoice, cancellationToken);

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
