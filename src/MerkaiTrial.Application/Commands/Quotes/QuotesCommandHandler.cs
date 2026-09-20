// =====================================================================
// QuotesCommandHandler.cs
// Location: MerkaiTrial.Application/Commands/Quotes/QuotesCommandHandler.cs
//
// COMPLETE FILE — replaces the existing one.
//
// RECORD VISIBILITY (016) — READ SIDE ONLY, on purpose.
//   A quote has no owner of its own; it follows its deal. The quote LIST
//   (Quotes page, Pipeline cards, Dashboard "Recent Quotes") and the quote
//   STATISTICS (Dashboard) now only include quotes on deals the user can
//   see. A rep's dashboard no longer shows the whole workspace's quotes.
//
//   Quote by-id, create, update, status and delete are UNCHANGED — they are
//   part of the quotes/invoices round you planned alongside the approval
//   workflow. GetQuoteByIdHandler in particular keeps its one-argument
//   constructor: CreateQuoteHandler builds it with `new`.
// =====================================================================

using DocumentFormat.OpenXml.Office2016.Drawing.ChartDrawing;
using DocumentFormat.OpenXml.Presentation;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Quotes
{
    public class GetQuotesHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly IRecordScopeService _scope;

        public GetQuotesHandler(FlowDbContext db, IRecordScopeService scope)
        {
            _db = db;
            _scope = scope;
        }

        public async Task<List<QuoteListItem>> Handle(
            Guid tenantId,
            Guid? dealId = null,
            string? status = null,
            DateTime? fromDate = null,
            DateTime? toDate = null)
        {
            // Quotes follow their deal's visibility.
            var dealAccess = await _scope.GetAsync(RecordModules.Deals);

            IQueryable<Quote> query = _db.Quotes
                .Where(q => q.TenantId == tenantId && !q.IsDeleted)
                .WithVisibleDeal(_db, dealAccess)
                .Include(q => q.Deal)
                    .ThenInclude(d => d.Contact)
                .Include(q => q.Items.Where(i => !i.IsDeleted));

            if (dealId.HasValue)
                query = query.Where(q => q.DealId == dealId.Value);

            if (!string.IsNullOrEmpty(status) && Enum.TryParse<QuoteStatus>(status, out var quoteStatus))
                query = query.Where(q => q.Status == quoteStatus);

            if (fromDate.HasValue)
                query = query.Where(q => q.IssueDateUtc >= fromDate.Value);

            if (toDate.HasValue)
                query = query.Where(q => q.IssueDateUtc <= toDate.Value);

            var quotesData = await query
                .OrderByDescending(q => q.CreatedAtUtc)
                .ToListAsync();

            return quotesData.Select(q => new QuoteListItem(
                q.Id,
                q.DealId,
                q.Number,
                q.Deal.Title,
                q.Deal.Contact != null
                    ? string.Join(" ", new[] { q.Deal.Contact.FirstName, q.Deal.Contact.LastName }
                        .Where(x => !string.IsNullOrWhiteSpace(x)))
                    : "Unknown",
                q.IssueDateUtc,
                q.ExpiresAtUtc,
                q.Currency,
                q.Status.ToString(),
                q.GrandTotal,
                q.Items.Count,
                q.CreatedAtUtc
            )).ToList();
        }
    }

    public class GetQuoteByIdHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;

        public GetQuoteByIdHandler(FlowDbContext db) => _db = db;

        public async Task<QuoteDto> Handle(Guid tenantId, Guid quoteId)
        {
            var quote = await _db.Quotes
               .Where(q => q.Id == quoteId && q.TenantId == tenantId && !q.IsDeleted)
               .Include(q => q.Deal)
                   .ThenInclude(d => d.Contact)
               .Include(q => q.Deal)
                   .ThenInclude(d => d.Vertical)   // ✅ For VerticalName on QuoteDto
               .Include(q => q.Items.Where(i => !i.IsDeleted))
               .FirstOrDefaultAsync();

            if (quote == null)
                throw new KeyNotFoundException($"Quote {quoteId} not found");

            return new QuoteDto
            {
                Id            = quote.Id,
                TenantId      = quote.TenantId,
                DealId        = quote.DealId,
                DealTitle     = quote.Deal.Title,
                CompanyName   = quote.Deal.Contact != null
                    ? $"{quote.Deal.Contact.FirstName} {quote.Deal.Contact.LastName}".Trim()
                    : "Unknown",
                Number        = quote.Number,
                IssueDateUtc  = quote.IssueDateUtc,
                ExpiresAtUtc  = quote.ExpiresAtUtc,
                Currency      = quote.Currency,
                Status        = quote.Status.ToString(),
                Subtotal      = quote.Subtotal,
                DiscountTotal = quote.DiscountTotal,
                TaxTotal      = quote.TaxTotal,
                GrandTotal    = quote.GrandTotal,
                PaymentLinkUrl = quote.PaymentLinkUrl,
                PdfUrl        = quote.PdfUrl,
                CreatedAtUtc  = quote.CreatedAtUtc,
                CreatedBy     = quote.CreatedBy,
                VerticalName = quote.Deal.Vertical?.Name,  // ✅ Inherited from Deal
                PublicLinkToken = quote.PublicLinkToken,
                Items         = quote.Items.Select(i => new QuoteItemDto
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
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // CREATE QUOTE  →  auto-advance deal to Proposal
    // ─────────────────────────────────────────────────────────────────
    public class CreateQuoteHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<CreateQuoteHandler> _logger;
        private readonly IAuditService _audit;
        public CreateQuoteHandler(
            FlowDbContext db,
            ICurrentUserService currentUserService,
            ILogger<CreateQuoteHandler> logger,
            IAuditService audit)
        {
            _db = db;
            _currentUserService = currentUserService;
            _logger = logger;
            _audit = audit;
        }

        public async Task<QuoteDto> Handle(CreateQuoteDto dto)
        {
            var currentUser = await _currentUserService.GetCurrentUserAsync();

            // ── Generate quote number ──────────────────────────────────
            var quoteNumber = await GenerateQuoteNumberAsync(dto.TenantId);

            // ── Calculate totals ───────────────────────────────────────
            var subtotal      = dto.Items.Sum(i => (i.UnitPrice * i.Quantity) - i.LineDiscount);
            var taxTotal      = dto.Items.Sum(i => ((i.UnitPrice * i.Quantity) - i.LineDiscount) * i.TaxRate);
            var discountTotal = dto.Items.Sum(i => i.LineDiscount);
            var grandTotal    = subtotal + taxTotal;

            var quote = new Quote
            {
                Id            = Guid.NewGuid(),
                TenantId      = dto.TenantId,
                DealId        = dto.DealId,
                Number        = quoteNumber,
                IssueDateUtc  = dto.IssueDateUtc,
                ExpiresAtUtc  = dto.ExpiresAtUtc,
                Currency      = dto.Currency,
                Status        = QuoteStatus.Draft,
                Subtotal      = subtotal,
                DiscountTotal = discountTotal,
                TaxTotal      = taxTotal,
                GrandTotal    = grandTotal,
                CreatedAtUtc  = DateTime.UtcNow,
                CreatedBy     = dto.CreatedBy ?? currentUser.FullName,
                UpdatedAtUtc  = DateTime.UtcNow,
                UpdatedBy     = currentUser.FullName,
                IsDeleted     = false
            };

            foreach (var itemDto in dto.Items)
            {
                quote.Items.Add(new QuoteItem
                {
                    Id           = Guid.NewGuid(),
                    TenantId     = dto.TenantId,
                    QuoteId      = quote.Id,
                    ProductId    = itemDto.ProductId,
                    Name         = itemDto.Name,
                    Description  = itemDto.Description,
                    UnitPrice    = itemDto.UnitPrice,
                    Quantity     = itemDto.Quantity,
                    LineDiscount = itemDto.LineDiscount,
                    TaxRate      = itemDto.TaxRate,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedBy    = currentUser.FullName,
                    IsDeleted    = false
                });
            }

            _db.Quotes.Add(quote);
            await SaveWithNumberRetryAsync(quote, dto.TenantId);
            await _audit.WriteAsync(
                 AuditAction.QuoteCreated, AuditEntityType.Quote, quote.Id, dto.TenantId,
                 new { number = quote.Number, dealId = quote.DealId, total = quote.GrandTotal },
                 CancellationToken.None);


            // ── ✅ AUTO-ADVANCE: Quote created → Deal moves to Proposal ─
            await AdvanceDealToProposalAsync(dto.DealId, dto.TenantId, currentUser.FullName);

            return await new GetQuoteByIdHandler(_db).Handle(dto.TenantId, quote.Id);
        }

        // ── Advance deal from early stages → Proposal ─────────────────
        private async Task AdvanceDealToProposalAsync(Guid dealId, Guid tenantId, string changedBy)
        {
            try
            {
                var deal = await _db.Deals
                    .FirstOrDefaultAsync(d => d.Id == dealId && d.TenantId == tenantId && !d.IsDeleted);

                if (deal == null) return;

                // Only promote from early/discovery stages — never demote
                var promotableStages = new[] { "New", "Qualified", "Discovery", "Qualification" };
                if (!promotableStages.Contains(deal.Stage, StringComparer.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "Deal {DealId} already in {Stage} — no stage advance needed", dealId, deal.Stage);
                    return;
                }

                var fromStage = deal.Stage;
                deal.Stage       = "Proposal";
                deal.Probability = 40;
                deal.UpdatedAtUtc = DateTime.UtcNow;
                deal.UpdatedBy    = changedBy;

                // Record history
                _db.DealStageHistory.Add(new DealStageHistory
                {
                    Id           = Guid.NewGuid(),
                    DealId       = dealId,
                    FromStage    = fromStage,
                    ToStage      = "Proposal",
                    ChangedAtUtc = DateTime.UtcNow,
                    ChangedBy    = changedBy
                });

                await _db.SaveChangesAsync();

                _logger.LogInformation(
                    "✅ Deal {DealId} auto-advanced: {From} → Proposal (quote created)", dealId, fromStage);
            }
            catch (Exception ex)
            {
                // Non-fatal — quote was already saved successfully
                _logger.LogError(ex, "Failed to auto-advance deal {DealId} to Proposal", dealId);
            }
        }

        // ── Quote numbering ───────────────────────────────────────────
        //
        // Returns the next unused QUO-nnnn for the tenant.
        //
        // Two things here are deliberate and must not be "simplified":
        //
        //  1. It takes the MAX of the parsed sequence, NOT the number of the
        //     most recently created row. Seed data (and any back-dated import)
        //     can have CreatedAtUtc ordering that does not match number
        //     ordering — Nexora's QUO-0003 was seeded with the OLDEST
        //     timestamp, so ordering by CreatedAtUtc returned QUO-0002 and the
        //     generator kept handing out QUO-0003 into a unique-index violation.
        //
        //  2. It is NOT filtered by !IsDeleted. UX_Quotes_TenantId_Number
        //     covers soft-deleted rows, so their numbers can never be reused.
        //
        private const string QuoteNumberPrefix = "QUO-";

        private async Task<string> GenerateQuoteNumberAsync(Guid tenantId)
        {
            var numbers = await _db.Quotes
                .Where(q => q.TenantId == tenantId
                            && q.Number != null
                            && q.Number.StartsWith(QuoteNumberPrefix))
                .Select(q => q.Number)
                .ToListAsync();

            var max = 0;
            foreach (var number in numbers)
            {
                var tail = number.Substring(QuoteNumberPrefix.Length);
                if (int.TryParse(tail, out var value) && value > max)
                    max = value;
            }

            return $"{QuoteNumberPrefix}{(max + 1):D4}";
        }

        // Max-of-existing is correct but not atomic: two concurrent creates can
        // read the same max. The unique index is the real guard — catch its
        // violation, re-read, and retry rather than surfacing a 500.
        private async Task SaveWithNumberRetryAsync(Quote quote, Guid tenantId)
        {
            const int maxAttempts = 5;

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await _db.SaveChangesAsync();
                    return;
                }
                catch (DbUpdateException ex)
                    when (IsDuplicateKeyViolation(ex) && attempt < maxAttempts)
                {
                    var collided = quote.Number;
                    quote.Number = await GenerateQuoteNumberAsync(tenantId);

                    _logger.LogWarning(
                        "Quote number {Collided} already exists for tenant {TenantId}; " +
                        "retrying as {NextNumber} (attempt {Attempt}/{MaxAttempts})",
                        collided, tenantId, quote.Number, attempt, maxAttempts);
                }
            }
        }

        // 2601 = duplicate key row in object with unique index
        // 2627 = violation of unique constraint
        private static bool IsDuplicateKeyViolation(DbUpdateException ex)
        {
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
            {
                if (inner is SqlException sql && (sql.Number == 2601 || sql.Number == 2627))
                    return true;
            }
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // UPDATE QUOTE STATUS  →  auto-transition deal on Accepted/Rejected
    // ─────────────────────────────────────────────────────────────────
    public class UpdateQuoteStatusHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<UpdateQuoteStatusHandler> _logger;
        private readonly IAuditService _audit;
        public UpdateQuoteStatusHandler(
            FlowDbContext db,
            ICurrentUserService currentUserService,
            ILogger<UpdateQuoteStatusHandler> logger,
            IAuditService audit)
        {
            _db = db;
            _currentUserService = currentUserService;
            _logger = logger;
            _audit = audit;
        }

        public async Task Handle(Guid tenantId, Guid quoteId, UpdateQuoteStatusDto dto)
        {
            var quote = await _db.Quotes
                   .IgnoreQueryFilters()
                   .FirstOrDefaultAsync(q =>
                       q.Id == quoteId && q.TenantId == tenantId && !q.IsDeleted);

            if (quote == null)
                throw new KeyNotFoundException($"Quote {quoteId} not found");

            if (!Enum.TryParse<QuoteStatus>(dto.Status, ignoreCase: true, out var newStatus))
                throw new ArgumentException($"Invalid quote status: {dto.Status}");

            string changedBy;
            try
            {
                var currentUser = await _currentUserService.GetCurrentUserAsync();
                changedBy = currentUser.FullName;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve current user — using DTO value");
                changedBy = !string.IsNullOrEmpty(dto.UpdatedBy) ? dto.UpdatedBy : "System";
            }

            // ✅ When sending to customer — generate unique public link token
            if (newStatus == QuoteStatus.Sent && string.IsNullOrEmpty(quote.PublicLinkToken))
            {
                var tokenBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
                var token = Convert.ToBase64String(tokenBytes)
                    .Replace("+", "").Replace("/", "").Replace("=", "")[..40];

                quote.PublicLinkToken = token;

                // PaymentLinkUrl stores the full public URL
                // Base URL resolved from config or passed via dto — default to relative path
                var baseUrl = !string.IsNullOrEmpty(dto.BaseUrl) ? dto.BaseUrl : "";
                quote.PaymentLinkUrl = $"{baseUrl}/q/{token}";

                _logger.LogInformation(
                    "Quote {QuoteId} public link generated: {Url}", quoteId, quote.PaymentLinkUrl);
            }
            var oldStatus = quote.Status;
            quote.Status = newStatus;
            quote.UpdatedAtUtc = DateTime.UtcNow;
            quote.UpdatedBy = changedBy;

            await _db.SaveChangesAsync();
            await _audit.WriteAsync(
                AuditAction.QuoteStatusChanged, AuditEntityType.Quote, quote.Id, tenantId,
                new
                {
                    number = quote.Number,
                    from = oldStatus.ToString(),
                    to = newStatus.ToString(),
                    total = quote.GrandTotal
                },
                CancellationToken.None);

            _logger.LogInformation(
                "Quote {QuoteId} status → {Status} by {User}", quoteId, newStatus, changedBy);

            if (quote.DealId == Guid.Empty)
            {
                _logger.LogWarning("Quote {QuoteId} has no linked Deal — skipping stage transition", quoteId);
                return;
            }

            // ── AUTO-TRANSITION DEAL STAGE ────────────────────────────────────
            if (newStatus == QuoteStatus.Accepted)
            {
                _logger.LogInformation("Quote Accepted — Deal {DealId} → Negotiation", quote.DealId);
                await TransitionDealStageAsync(
                    quote.DealId, tenantId,
                    toStage: "Negotiation", probability: 80, changedBy: changedBy, actualValue: quote.GrandTotal);
                await _audit.WriteAsync(
                    AuditAction.DealStageFailed, AuditEntityType.Deal, quote.DealId, tenantId,
                    new { attemptedStage = "Negotiation", reason = "Quote accepted" },
                    CancellationToken.None);

            }
            else if (newStatus == QuoteStatus.Rejected)
            {
                _logger.LogInformation("Quote Rejected — Deal {DealId} → ClosedLost", quote.DealId);
                await TransitionDealStageAsync(
                    quote.DealId, tenantId,
                    toStage: "ClosedLost", probability: 0, changedBy: changedBy);
                await _audit.WriteAsync(
                    AuditAction.DealStageFailed, AuditEntityType.Deal, quote.DealId, tenantId,
                    new { attemptedStage = "ClosedLost", reason = "Quote rejected" },
                    CancellationToken.None);

            }
        }


        private async Task TransitionDealStageAsync(
            Guid dealId, Guid tenantId, string toStage, int probability, string changedBy, decimal? actualValue = null)
        {
            try
            {
                var tenantIdStr = tenantId.ToString();
                var deal = await _db.Deals
                   .IgnoreQueryFilters()
                   .FirstOrDefaultAsync(d =>
                       d.Id == dealId && d.TenantId == tenantId && !d.IsDeleted);

                if (deal == null)
                {
                    _logger.LogError(
                        "TransitionDealStage: Deal {DealId} not found for tenant {TenantId}",
                        dealId, tenantId);
                    return;
                }

                // No-op if already terminal
                if (deal.Stage is "Won" or "Lost" or "ClosedWon" or "ClosedLost")
                {
                    _logger.LogInformation(
                        "Deal {DealId} already in terminal stage {Stage} — skipping",
                        dealId, deal.Stage);
                    return;
                }

                var fromStage = deal.Stage;
                deal.Stage = toStage;
                deal.Probability = probability;
                deal.UpdatedAtUtc = DateTime.UtcNow;
                deal.UpdatedBy = changedBy;

                // ✅ Update ActualValue when quote is accepted — replaces rough estimate
                // with confirmed quote grand total
                if (actualValue.HasValue)
                {
                    deal.ActualValue = actualValue.Value;
                    _logger.LogInformation(
                        "Deal {DealId} ActualValue updated to {Value} from accepted quote",
                        dealId, actualValue.Value);
                }

                _db.DealStageHistory.Add(new DealStageHistory
                {
                    Id = Guid.NewGuid(),
                    DealId = dealId,
                    TenantId = tenantId,
                    FromStage = fromStage,
                    ToStage = toStage,
                    ChangedAtUtc = DateTime.UtcNow,
                    ChangedBy = changedBy
                });

                await _db.SaveChangesAsync();

                _logger.LogInformation(
                    "✅ Deal {DealId}: {From} → {To}", dealId, fromStage, toStage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to transition Deal {DealId} to {Stage}", dealId, toStage);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // UPDATE QUOTE (unchanged except minor cleanup)
    // ─────────────────────────────────────────────────────────────────
    public class UpdateQuoteHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUserService;

        public UpdateQuoteHandler(FlowDbContext db, ICurrentUserService currentUserService)
        {
            _db = db;
            _currentUserService = currentUserService;
        }

        public async Task Handle(Guid tenantId, Guid quoteId, UpdateQuoteDto dto)
        {
            var now = DateTime.UtcNow;

            var quote = await _db.Quotes
                .Include(q => q.Items)
                .FirstOrDefaultAsync(q => q.Id == quoteId && q.TenantId == tenantId && !q.IsDeleted);

            if (quote == null)
                throw new KeyNotFoundException($"Quote {quoteId} not found");

            var currentUser = await _currentUserService.GetCurrentUserAsync();
            var userName = currentUser?.FullName ?? "System";

            quote.IssueDateUtc = dto.IssueDateUtc;
            quote.ExpiresAtUtc = dto.ExpiresAtUtc;
            quote.Currency     = dto.Currency;
            quote.UpdatedAtUtc = now;
            quote.UpdatedBy    = userName;

            var existingActiveItems  = quote.Items.Where(i => !i.IsDeleted).ToList();
            var existingById         = quote.Items.ToDictionary(i => i.Id, i => i);
            var incomingExistingIds  = dto.Items
                .Where(i => i.Id.HasValue && i.Id.Value != Guid.Empty)
                .Select(i => i.Id!.Value)
                .ToHashSet();

            foreach (var item in existingActiveItems)
            {
                if (!incomingExistingIds.Contains(item.Id))
                {
                    item.IsDeleted    = true;
                    item.UpdatedAtUtc = now;
                    item.UpdatedBy    = userName;
                }
            }

            foreach (var itemDto in dto.Items)
            {
                var name = (itemDto.Name ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name))
                    throw new InvalidOperationException("All items must have a name.");

                QuoteItem existingItem = null;
                var isExisting = itemDto.Id.HasValue
                    && itemDto.Id.Value != Guid.Empty
                    && existingById.TryGetValue(itemDto.Id.Value, out existingItem);

                
                if (isExisting && existingItem != null)
                {
                    existingItem.IsDeleted    = false;
                    existingItem.ProductId    = itemDto.ProductId;
                    existingItem.Name         = name;
                    existingItem.Description  = itemDto.Description ?? "";
                    existingItem.UnitPrice    = itemDto.UnitPrice;
                    existingItem.Quantity     = itemDto.Quantity;
                    existingItem.LineDiscount = itemDto.LineDiscount;
                    existingItem.TaxRate      = itemDto.TaxRate;
                    existingItem.UpdatedAtUtc = now;
                    existingItem.UpdatedBy    = userName;
                }
                else
                {
                    _db.QuoteItems.Add(new QuoteItem
                    {
                        Id           = Guid.NewGuid(),
                        TenantId     = tenantId,
                        QuoteId      = quoteId,
                        ProductId    = itemDto.ProductId,
                        Name         = name,
                        Description  = itemDto.Description ?? "",
                        UnitPrice    = itemDto.UnitPrice,
                        Quantity     = itemDto.Quantity,
                        LineDiscount = itemDto.LineDiscount,
                        TaxRate      = itemDto.TaxRate,
                        CreatedAtUtc = now,
                        CreatedBy    = userName,
                        UpdatedAtUtc = now,
                        UpdatedBy    = userName,
                        IsDeleted    = false
                    });
                }
            }

            var activeItems = quote.Items.Where(i => !i.IsDeleted).ToList();
            var discountTotal = activeItems.Sum(i => i.LineDiscount);
            var taxTotal      = activeItems.Sum(i => Math.Max(0m, (i.UnitPrice * i.Quantity) - i.LineDiscount) * i.TaxRate);

            quote.Subtotal      = activeItems.Sum(i => i.UnitPrice * i.Quantity);
            quote.DiscountTotal = discountTotal;
            quote.TaxTotal      = taxTotal;
            quote.GrandTotal    = Math.Max(0m, quote.Subtotal - discountTotal + taxTotal);

            await _db.SaveChangesAsync();
        }
    }
    public class GetQuoteByTokenHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly IAuditService _audit;
        public GetQuoteByTokenHandler(FlowDbContext db, IAuditService audit)
        {
            _db = db;
            _audit = audit;                                       // ✅ AUDIT
        }

        /// <summary>
        /// Loads a quote by its public token — no tenant or auth required.
        /// Called by the public /q/{token} page.
        /// Also auto-updates status from Sent → Viewed on first open.
        /// </summary>
        public async Task<QuoteDto?> HandleAsync(string token, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;

            // ── IgnoreQueryFilters #1 ────────────────────────────────────
            // Anonymous customer, no tenant claim. Without this the filter
            // compares TenantId against Guid.Empty and the quote is never
            // found — every public link would 404.
            //
            // The Include chain matters too: Deal, Contact and Vertical are
            // all filtered entities, so they would come back null and the
            // rendered quote would show "Unknown" for the customer name with
            // no error anywhere.
            var quote = await _db.Quotes
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(q => q.PublicLinkToken == token && !q.IsDeleted)
                .Include(q => q.Deal)
                    .ThenInclude(d => d.Contact)
                .Include(q => q.Deal)
                    .ThenInclude(d => d.Vertical)
                .Include(q => q.Items.Where(i => !i.IsDeleted))
                .FirstOrDefaultAsync(ct);

            if (quote == null) return null;

            // ── Auto-set Viewed when the customer opens the link ─────────
            if (quote.Status == QuoteStatus.Sent)
            {
                // ── IgnoreQueryFilters #2 ────────────────────────────────
                // Same reason — this tracked re-fetch would find nothing and
                // the status would silently never advance past Sent.
                var tracked = await _db.Quotes
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(q => q.PublicLinkToken == token && !q.IsDeleted, ct);

                if (tracked != null)
                {
                    tracked.Status = QuoteStatus.Viewed;
                    tracked.UpdatedAtUtc = DateTime.UtcNow;
                    tracked.UpdatedBy = "Customer";
                    await _db.SaveChangesAsync(ct);
                    await _audit.WriteAsync(
                     AuditAction.QuoteStatusChanged, AuditEntityType.Quote,
                     tracked.Id, tracked.TenantId,
                     new { number = tracked.Number, from = "Sent", to = "Viewed", bySource = "PublicLink" },
                     ct);
                }
            }

            var contactName = quote.Deal?.Contact != null
                ? $"{quote.Deal.Contact.FirstName} {quote.Deal.Contact.LastName}".Trim()
                : "Unknown";

            return new QuoteDto
            {
                Id = quote.Id,
                TenantId = quote.TenantId,
                DealId = quote.DealId,
                DealTitle = quote.Deal?.Title ?? string.Empty,
                CompanyName = contactName,
                Number = quote.Number,
                IssueDateUtc = quote.IssueDateUtc,
                ExpiresAtUtc = quote.ExpiresAtUtc,
                Currency = quote.Currency,
                Status = quote.Status == QuoteStatus.Sent
                                 ? QuoteStatus.Viewed.ToString()
                                 : quote.Status.ToString(),
                Subtotal = quote.Subtotal,
                DiscountTotal = quote.DiscountTotal,
                TaxTotal = quote.TaxTotal,
                GrandTotal = quote.GrandTotal,
                PaymentLinkUrl = quote.PaymentLinkUrl,
                PublicLinkToken = quote.PublicLinkToken,
                VerticalName = quote.Deal?.Vertical?.Name,
                Items = quote.Items.Select(i => new QuoteItemDto
                {
                    Id = i.Id,
                    QuoteId = i.QuoteId,
                    ProductId = i.ProductId,
                    Name = i.Name,
                    Description = i.Description,
                    UnitPrice = i.UnitPrice,
                    Quantity = i.Quantity,
                    LineDiscount = i.LineDiscount,
                    TaxRate = i.TaxRate,
                    LineTotal = (i.UnitPrice * i.Quantity) - i.LineDiscount,
                    LineTax = ((i.UnitPrice * i.Quantity) - i.LineDiscount) * i.TaxRate,
                    LineGrandTotal = ((i.UnitPrice * i.Quantity) - i.LineDiscount) +
                                     (((i.UnitPrice * i.Quantity) - i.LineDiscount) * i.TaxRate)
                }).ToList()
            };
        }
    }
    // ─────────────────────────────────────────────────────────────────
    // DELETE QUOTE (unchanged)
    // ─────────────────────────────────────────────────────────────────
    public class DeleteQuoteHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUserService;
        private readonly IAuditService _audit;
        public DeleteQuoteHandler(FlowDbContext db, ICurrentUserService currentUserService, IAuditService audit)
        {
            _db = db;
            _currentUserService = currentUserService;
            _audit = audit;
        }

        public async Task Handle(Guid tenantId, Guid quoteId)
        {
            var quote = await _db.Quotes
                .Include(q => q.Items)
                .FirstOrDefaultAsync(q => q.Id == quoteId && q.TenantId == tenantId && !q.IsDeleted);

            if (quote == null)
                throw new KeyNotFoundException($"Quote {quoteId} not found");

            var currentUser = await _currentUserService.GetCurrentUserAsync();

            quote.IsDeleted    = true;
            quote.UpdatedAtUtc = DateTime.UtcNow;
            quote.UpdatedBy    = currentUser.FullName;

            foreach (var item in quote.Items)
            {
                item.IsDeleted    = true;
                item.UpdatedAtUtc = DateTime.UtcNow;
                item.UpdatedBy    = currentUser.FullName;
            }

            await _db.SaveChangesAsync();
            await _audit.WriteAsync(
                AuditAction.QuoteDeleted, AuditEntityType.Quote, quoteId, tenantId,
                new { number = quote.Number, status = quote.Status.ToString() },
                CancellationToken.None);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // GET QUOTE STATISTICS (unchanged)
    // ─────────────────────────────────────────────────────────────────
    public class GetQuoteStatisticsHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly IRecordScopeService _scope;

        public GetQuoteStatisticsHandler(FlowDbContext db, IRecordScopeService scope)
        {
            _db = db;
            _scope = scope;
        }

        public async Task<QuoteStatisticsDto> Handle(Guid tenantId)
        {
            // Previously loaded every quote row into app memory via
            // ToListAsync() and counted/summed in C# — same fix pattern as
            // invoice statistics: collapse into one query with conditional
            // aggregates, single round-trip, only aggregates cross the wire.
            var draft    = QuoteStatus.Draft;
            var sent     = QuoteStatus.Sent;
            var viewed   = QuoteStatus.Viewed;
            var accepted = QuoteStatus.Accepted;
            var rejected = QuoteStatus.Rejected;
            var expired  = QuoteStatus.Expired;

            var dealAccess = await _scope.GetAsync(RecordModules.Deals);

            var stats = await _db.Quotes
                .Where(q => q.TenantId == tenantId && !q.IsDeleted)
                .WithVisibleDeal(_db, dealAccess)
                .GroupBy(q => 1)
                .Select(g => new QuoteStatisticsDto
                {
                    TotalQuotes    = g.Count(),
                    DraftQuotes    = g.Count(q => q.Status == draft),
                    SentQuotes     = g.Count(q => q.Status == sent || q.Status == viewed),
                    AcceptedQuotes = g.Count(q => q.Status == accepted),
                    RejectedQuotes = g.Count(q => q.Status == rejected),
                    ExpiredQuotes  = g.Count(q => q.Status == expired),
                    TotalValue     = g.Sum(q => q.GrandTotal),
                    AcceptedValue  = g.Where(q => q.Status == accepted).Sum(q => q.GrandTotal),
                    PendingValue   = g.Where(q => q.Status == sent || q.Status == viewed).Sum(q => q.GrandTotal)
                })
                .FirstOrDefaultAsync();

            // GroupBy on an empty result set returns no groups at all —
            // handle tenants with no quotes yet explicitly.
            return stats ?? new QuoteStatisticsDto
            {
                TotalQuotes = 0, DraftQuotes = 0, SentQuotes = 0,
                AcceptedQuotes = 0, RejectedQuotes = 0, ExpiredQuotes = 0,
                TotalValue = 0, AcceptedValue = 0, PendingValue = 0
            };
        }
    }
}
