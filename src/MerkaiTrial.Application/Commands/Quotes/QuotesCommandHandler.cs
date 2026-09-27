// =====================================================================
// QuotesCommandHandler.cs
// Location: MerkaiTrial.Application/Commands/Quotes/QuotesCommandHandler.cs
//
// COMPLETE FILE — replaces the existing one.
//
// QUOTE APPROVALS (017)
//   ✅ Create: the deal must be one the user can see (404 otherwise), and
//      Subtotal is now the GROSS amount (before line discounts) — same as
//      Update already did. Create used to store the NET amount, so
//      Subtotal − Discount + Tax did not add up on the quote page, and the
//      invoice raised from it showed the discount taken off twice.
//      Grand totals were always right. 017 repairs existing rows.
//   ✅ Create no longer builds GetQuoteByIdHandler with `new` — injected.
//   ✅ Line checks: quantity > 0, price ≥ 0, discount not above the line.
//   ✅ Status: only allowed moves (QuoteWorkflow.CanMove). Draft/Revised →
//      Sent is refused when the quote breaks an approval rule, unless the
//      user is a workspace admin. PendingApproval / Approved can't be set
//      here — they belong to api/quote-approvals.
//   ✅ Status: removed the two "DealStageFailed" audit entries that were
//      written after EVERY accepted/rejected quote, including successful
//      ones — the audit log said the deal move failed when it hadn't.
//   ✅ Update: only Draft, Revised and Approved quotes can be edited (Sent
//      and Accepted quotes were editable before — the customer's copy could
//      change under them). Editing an Approved quote returns it to Draft
//      and marks the approval Superseded.
//   ✅ Delete: refused while the quote has an invoice (the page checked
//      this; the API did not). A pending approval is closed as Recalled.
//   ✅ Statistics: DraftQuotes now counts every not-yet-sent quote (Draft,
//      PendingApproval, Approved) so the buckets still add up.
//   ✅ Public link: works only while the quote is Sent/Viewed/Accepted/
//      Rejected/Expired — not while it's being revised or approved.
//
// TENANT-CONFIGURABLE STAGE MOVES (036)
//   The two automatic deal moves in this file used hardcoded stage keys and
//   hardcoded probabilities:
//
//     quote created  -> deal.Stage = "Proposal",   Probability = 40
//                       promoting only from { New, Qualified, Discovery,
//                       Qualification }
//     quote accepted -> deal.Stage = "Negotiation", Probability = 80
//     quote rejected -> deal.Stage = "ClosedLost",  Probability = 0
//     terminal check -> deal.Stage is "Won" or "Lost" or "ClosedWon"
//                       or "ClosedLost"
//
//   Since 019/020 a tenant configures their own stages, so all of that was
//   wrong for anyone who did. Four separate consequences, worst first:
//
//   1. GHOST STAGES. Writing "Proposal" into a workspace whose stages are
//      Prospect / Demo / Commercials / Closed leaves the deal in a key that
//      is not in PipelineStages: no kanban column, ProbabilityOf 0, and
//      IsTerminal FALSE because the stage cannot be found. The deal vanishes
//      from the board.
//
//   2. A CUSTOMER COULD WRECK AN INVOICED DEAL. The terminal check matched
//      four literal names, so for a tenant whose winning stage is called
//      "Contract Signed" it did not fire — and a customer clicking Reject on
//      the public quote link moved a WON, INVOICED deal to "ClosedLost",
//      straight past the invoice rule the rest of the app enforces.
//
//   3. A CLOSING FIGURE ON AN OPEN DEAL. Accepting a quote set
//      deal.ActualValue = quote.GrandTotal while moving the deal to
//      Negotiation, an OPEN stage. ActualValue is a closing figure; 019 fixed
//      exactly this elsewhere, because a deal carrying one is counted as
//      revenue while still in the pipeline.
//
//   4. INVISIBLE HISTORY. The DealStageHistory row written on quote creation
//      omitted TenantId, so it was saved as Guid.Empty and then hidden by the
//      global query filter. The stage change disappeared from the timeline.
//
//   All four go away by routing both moves through StageTransitionGuard, the
//   same way DealsCommandHandlers already does. The guard resolves the stage
//   through TenantStages, takes the probability from the stage itself, and
//   its HistoryFor includes TenantId. The target stage now comes from
//   PipelineRuleSettings.QuoteSentStageKey / QuoteAcceptedStageKey /
//   QuoteRejectedStageKey, where NULL means "do not move the deal".
//
//   Both moves stay NON-FATAL and stay IsSystemMove. A misconfigured pipeline
//   must not turn a saved quote into a 500, and the event that caused the move
//   is the very thing a requirement would have asked about.
//
// IN-APP NOTIFICATIONS (037)
//   UpdateQuoteStatusHandler now tells the deal's owner when a quote is
//   sent, accepted or rejected. Three things about how:
//
//   • The rows are added BEFORE the existing SaveChangesAsync, never after,
//     so the notification and the status change commit together. A
//     notification about a status change that rolled back would be worse
//     than none.
//   • INotificationDispatcher never calls SaveChanges itself — that is its
//     defining rule. It adds to this context and this handler's save
//     commits them.
//   • The dispatcher drops the actor from the recipients, so sending your
//     own quote does not notify you. When the CUSTOMER accepts through the
//     public link there is no signed-in user at all, so the owner always
//     hears about it — which is the case that actually matters.
//
// RECORD VISIBILITY (016): the list and statistics follow deal visibility.
// By-id, update, status, delete, attachments and PDF are guarded in
// QuotesController with QuoteApprovalEngine.CanRead/CanWriteQuoteAsync —
// the controller, not the handlers, because the customer's public link
// uses the same status handler with no signed-in user.
// =====================================================================

using MerkaiTrial.Application.Commands.PipelineStages;
using MerkaiTrial.Application.Services.Notifications;
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
    // ─────────────────────────────────────────────────────────────────
    // WHETHER AN AUTOMATIC MOVE SHOULD HAPPEN AT ALL (036)
    //
    // Shared by both quote-driven moves so they cannot drift apart, which is
    // how the old pair ended up with two different terminal checks — one that
    // listed four stage names and one that listed none.
    //
    // Every rule here is expressed through StageCategory and SortOrder, never
    // through a stage name, so it holds for any pipeline a tenant configures.
    // ─────────────────────────────────────────────────────────────────
    internal static class QuoteStageAutomation
    {
        /// <summary>
        /// True when the deal may be moved automatically to
        /// <paramref name="targetKey"/>. When false, <paramref name="reason"/>
        /// says why, for the log — a silent skip is impossible to support.
        /// </summary>
        public static bool ShouldMove(
            TenantStages stages, string? currentKey, string targetKey, out string reason)
        {
            var target = stages.Find(targetKey);

            // A configured key that is not one of this tenant's stages. This
            // is the ghost-stage bug, now refused instead of written.
            if (target is null)
            {
                reason = $"'{targetKey}' is not a stage in this workspace " +
                         $"(valid: {stages.ValidKeysText})";
                return false;
            }

            if (!target.IsActive)
            {
                reason = $"'{target.Name}' has been retired";
                return false;
            }

            if (string.Equals(currentKey, targetKey, StringComparison.Ordinal))
            {
                reason = "the deal is already in that stage";
                return false;
            }

            // NEVER touch a closed deal automatically.
            //
            // This is the rule that matters most. Leaving a closed stage is a
            // REOPEN, and StageTransitionGuard skips its invoice rule for a
            // system move — so without this, a customer clicking Reject on a
            // public quote link could move a won, invoiced deal to Lost,
            // straight past the one rule nothing is supposed to skip.
            //
            // Asked by CATEGORY, so it is correct for a tenant whose winning
            // stage is called "Contract Signed". The old check listed four
            // literal names and missed every such workspace.
            if (stages.IsTerminal(currentKey))
            {
                reason = $"the deal is already closed ({stages.NameOf(currentKey)})";
                return false;
            }

            var current = stages.Find(currentKey);

            // current is null when the deal sits in a stage that has since
            // been deleted — including one of the ghost keys the old code
            // wrote. Moving it into a real stage is a repair, so that case
            // deliberately falls through.
            if (current is not null
                && target.Category == StageCategory.Open
                && current.SortOrder >= target.SortOrder)
            {
                // Never demote. SortOrder rather than a hardcoded list of
                // "early" stage names, which is what the old promotable-stages
                // array was trying and failing to express.
                reason = $"'{current.Name}' is already at or past '{target.Name}'";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }

    public class CreateQuoteHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<CreateQuoteHandler> _logger;
        private readonly IAuditService _audit;
        private readonly IRecordScopeService _scope;
        private readonly GetQuoteByIdHandler _getById;

        // 036. Both are already registered: IStageResolver explicitly in
        // ApiServiceRegistration, StageTransitionGuard by the Scrutor scan
        // through ICommandHandler (DealsCommandHandlers injects it the same
        // way). No DI changes needed, and no dependency cycle — the guard
        // needs FlowDbContext, IStageResolver, TransitionCatalog,
        // ICurrentUserService and QuoteApprovalEngine, and none of those
        // reaches back into this file.
        private readonly StageTransitionGuard _guard;
        private readonly IStageResolver _stages;

        public CreateQuoteHandler(
            FlowDbContext db,
            ICurrentUserService currentUserService,
            ILogger<CreateQuoteHandler> logger,
            IAuditService audit,
            IRecordScopeService scope,
            GetQuoteByIdHandler getById,
            StageTransitionGuard guard,
            IStageResolver stages)
        {
            _db = db;
            _currentUserService = currentUserService;
            _logger = logger;
            _audit = audit;
            _scope = scope;
            _getById = getById;
            _guard = guard;
            _stages = stages;
        }

        public async Task<QuoteDto> Handle(CreateQuoteDto dto)
        {
            // A quote is raised on a deal — the deal must be one this user can see.
            await _scope.EnsureDealVisibleAsync(_db, dto.TenantId, dto.DealId);

            if (dto.Items == null || dto.Items.Count == 0)
                throw new InvalidOperationException("Add at least one item to the quote.");

            QuoteLineChecks.Validate(dto.Items.Select(i => (i.Name, i.UnitPrice, i.Quantity, i.LineDiscount, i.TaxRate)));

            var currentUser = await _currentUserService.GetCurrentUserAsync();

            // ── Generate quote number ──────────────────────────────────
            var quoteNumber = await GenerateQuoteNumberAsync(dto.TenantId);

            // ── Calculate totals ───────────────────────────────────────
            // Subtotal is GROSS (before line discounts), matching Update and
            // the invoice handlers: Grand = Subtotal − Discount + Tax.
            var subtotal      = dto.Items.Sum(i => i.UnitPrice * i.Quantity);
            var discountTotal = dto.Items.Sum(i => i.LineDiscount);
            var taxTotal      = dto.Items.Sum(i => ((i.UnitPrice * i.Quantity) - i.LineDiscount) * i.TaxRate);
            var grandTotal    = subtotal - discountTotal + taxTotal;

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


            // ── Quote created → move the deal, if this tenant asked for it ─
            await AdvanceDealOnQuoteCreatedAsync(dto.DealId, dto.TenantId, currentUser.FullName);

            return await _getById.Handle(dto.TenantId, quote.Id);
        }

        // ── Quote created → move the deal (036) ───────────────────────
        //
        // Was: deal.Stage = "Proposal", Probability = 40, promoting only from
        // a hardcoded { New, Qualified, Discovery, Qualification }. See the
        // header for what that did to a tenant with their own stages.
        //
        // Now the target comes from PipelineRuleSettings.QuoteSentStageKey,
        // and null — the default — means do not move the deal at all.
        private async Task AdvanceDealOnQuoteCreatedAsync(
            Guid dealId, Guid tenantId, string changedBy)
        {
            // Non-fatal throughout. The quote is already saved and audited;
            // a misconfigured pipeline must not turn that into a 500.
            try
            {
                var (rules, _) = await _guard.GetSettingsAsync(tenantId);

                if (string.IsNullOrWhiteSpace(rules.QuoteSentStageKey))
                    return;      // this workspace has not asked for a move

                var deal = await _db.Deals
                    .FirstOrDefaultAsync(d => d.Id == dealId
                                           && d.TenantId == tenantId
                                           && !d.IsDeleted);
                if (deal == null) return;

                var stages = await _stages.GetAsync(tenantId);

                if (!QuoteStageAutomation.ShouldMove(
                        stages, deal.Stage, rules.QuoteSentStageKey!, out var why))
                {
                    _logger.LogInformation(
                        "Deal {DealId} not moved on quote creation: {Reason}", dealId, why);
                    return;
                }

                var decision = await _guard.CheckAsync(new StageMoveRequest(
                    TenantId: tenantId,
                    Deal: new DealStageSnapshot(
                        deal.Id, deal.Stage, deal.ExpectedValue,
                        deal.ExpectedCloseDateUtc, deal.OwnerUserId),
                    ToStageKey: rules.QuoteSentStageKey!,
                    Note: "A quote was raised on this deal.",
                    IsSystemMove: true));

                var fromStage = deal.Stage;

                // Probability comes from the target stage, not from a literal
                // 40. No actualValue: raising a quote does not close a deal,
                // and ApplyToDeal correctly clears the closing fields on a
                // move into an open stage.
                StageTransitionGuard.ApplyToDeal(deal, decision, changedBy);

                // HistoryFor sets TenantId. The old inline row did not, so it
                // was written as Guid.Empty and hidden by the global query
                // filter — the stage change never appeared on the timeline.
                _db.DealStageHistory.Add(
                    StageTransitionGuard.HistoryFor(tenantId, dealId, decision, changedBy));

                await _db.SaveChangesAsync();

                _logger.LogInformation(
                    "Deal {DealId} moved on quote creation: {From} → {To}",
                    dealId, fromStage, decision.To.Key);
            }
            catch (Exception ex)
            {
                // Non-fatal — the quote was already saved successfully.
                _logger.LogError(ex,
                    "Failed to move deal {DealId} after raising a quote", dealId);
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
        private readonly QuoteApprovalEngine _approvals;
        private readonly StageTransitionGuard _guard;      // 036
        private readonly IStageResolver _stages;           // 036
        private readonly INotificationDispatcher _notify;  // 037

        public UpdateQuoteStatusHandler(
            FlowDbContext db,
            ICurrentUserService currentUserService,
            ILogger<UpdateQuoteStatusHandler> logger,
            IAuditService audit,
            QuoteApprovalEngine approvals,
            StageTransitionGuard guard,
            IStageResolver stages,
            INotificationDispatcher notify)
        {
            _db = db;
            _currentUserService = currentUserService;
            _logger = logger;
            _audit = audit;
            _approvals = approvals;
            _guard = guard;
            _stages = stages;
            _notify = notify;
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

            // Same status again (double click, customer re-opening the link) — nothing to do.
            if (newStatus == quote.Status) return;

            if (!QuoteWorkflow.CanMove(quote.Status, newStatus))
            {
                throw new InvalidOperationException(newStatus is QuoteStatus.PendingApproval or QuoteStatus.Approved
                    ? "Use Submit for approval / Approve on the quote page to change the approval status."
                    : quote.Status == QuoteStatus.PendingApproval
                        ? "This quote is waiting for approval. Recall the request first if you need to change it."
                        : $"A {QuoteWorkflow.Label(quote.Status)} quote can't be marked {QuoteWorkflow.Label(newStatus)}.");
            }

            // The customer's public link has no signed-in user — that path
            // only ever moves Sent/Viewed → Accepted/Rejected.
            CurrentUserContext? currentUser = null;
            try
            {
                currentUser = await _currentUserService.GetCurrentUserAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve current user — using DTO value");
            }

            var changedBy = currentUser?.FullName
                ?? (!string.IsNullOrEmpty(dto.UpdatedBy) ? dto.UpdatedBy : "System");

            // ── APPROVAL GATE ─────────────────────────────────────────────
            // Draft/Revised → Sent needs the quote to be within the rules,
            // unless a workspace admin is sending it. Approved → Sent is
            // always fine: that IS the approval.
            if (newStatus == QuoteStatus.Sent && QuoteWorkflow.IsPreSend(quote.Status) &&
                currentUser?.IsTenantAdmin != true)
            {
                var rules = await _approvals.EvaluateAsync(tenantId, quoteId);
                if (rules.RequiresApproval)
                    throw new InvalidOperationException(
                        "This quote needs approval before it can be sent. " + string.Join(" ", rules.Reasons));
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

            // ── 037: tell the deal's owner ────────────────────────────────
            // Added BEFORE the save, so the notification and the status
            // change commit in one transaction. The dispatcher does not save.
            await AddStatusNotificationAsync(quote, newStatus, currentUser);

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

            // ── AUTO-TRANSITION DEAL STAGE (036) ──────────────────────────────
            // The target stage is the tenant's own, read from
            // PipelineRuleSettings. Null means no automatic move, which is
            // the default until a workspace configures one.
            if (newStatus is QuoteStatus.Accepted or QuoteStatus.Rejected)
            {
                var (rules, _) = await _guard.GetSettingsAsync(tenantId);

                if (newStatus == QuoteStatus.Accepted)
                {
                    // actualValue is passed, but ApplyToDeal only uses it when
                    // the target stage actually CLOSES the deal. That is the
                    // fix for the old code writing deal.ActualValue while
                    // moving the deal to an open stage.
                    await MoveDealOnQuoteDecisionAsync(
                        quote.DealId, tenantId, rules.QuoteAcceptedStageKey, changedBy,
                        note: $"Quote {quote.Number} was accepted.",
                        actualValue: quote.GrandTotal);
                }
                else
                {
                    // The note becomes the deal's LostReason when the target
                    // stage is a Lost stage — ApplyToDeal does that, and only
                    // for Lost, so a deal that is later won does not keep a
                    // stale reason.
                    await MoveDealOnQuoteDecisionAsync(
                        quote.DealId, tenantId, rules.QuoteRejectedStageKey, changedBy,
                        note: $"Quote {quote.Number} was rejected by the customer.");
                }
            }
        }


        // ── 037: the in-app notification for a status change ──────────
        //
        // Adds rows; does NOT save. The caller's SaveChangesAsync commits
        // them alongside the status change.
        private async Task AddStatusNotificationAsync(
            Quote quote, QuoteStatus newStatus, CurrentUserContext? currentUser)
        {
            // Only the three the owner cares about. Draft, Viewed, Expired and
            // the approval statuses are noise: the approval ones get their own
            // notification from the approvals round, and nobody needs telling
            // that a quote is still a draft.
            var eventType = newStatus switch
            {
                QuoteStatus.Sent     => NotificationEventType.QuoteSent,
                QuoteStatus.Accepted => NotificationEventType.QuoteAccepted,
                QuoteStatus.Rejected => NotificationEventType.QuoteRejected,
                _                    => (NotificationEventType?)null
            };

            if (eventType is null) return;

            try
            {
                if (quote.DealId == Guid.Empty) return;

                // Cheap projection rather than loading the deal: this runs on
                // every status change, including the customer's own click.
                var deal = await _db.Deals.AsNoTracking().IgnoreQueryFilters()
                    .Where(d => d.Id == quote.DealId && d.TenantId == quote.TenantId)
                    .Select(d => new { d.OwnerUserId, d.Title })
                    .FirstOrDefaultAsync();

                if (deal is null || string.IsNullOrWhiteSpace(deal.OwnerUserId))
                    return;      // unassigned deal — nobody to tell

                var title = newStatus switch
                {
                    QuoteStatus.Sent     => $"Quote {quote.Number} was sent to the customer",
                    QuoteStatus.Accepted => $"Quote {quote.Number} was accepted",
                    _                    => $"Quote {quote.Number} was rejected"
                };

                // Currency CODE, not a symbol: this handler has no view of the
                // tenant's formatting, and "THB 95,000.00" is unambiguous
                // where a bare number is not.
                var body = $"{deal.Title} — {quote.Currency} {quote.GrandTotal:N2}";

                await _notify.AddForOwnerAsync(
                    new NotificationRequest(
                        TenantId:    quote.TenantId,
                        EventType:   eventType.Value,
                        Title:       title,
                        Body:        body,
                        EntityType:  "Quote",
                        EntityId:    quote.Id,

                        // Null when the customer acted through the public
                        // link, which is exactly when the owner must hear
                        // about it. When a colleague did it, the dispatcher
                        // drops them from their own notification.
                        ActorUserId: currentUser?.UserId,
                        ActorName:   currentUser?.FullName ?? "the customer"),
                    deal.OwnerUserId);
            }
            catch (Exception ex)
            {
                // Never fail a status change because a notification could not
                // be prepared. The quote is the point; the bell is not.
                _logger.LogError(ex,
                    "Could not prepare a notification for quote {QuoteId} → {Status}",
                    quote.Id, newStatus);
            }
        }

        // ── Quote accepted / rejected → move the deal (036) ───────────
        //
        // Was: toStage and probability passed in as literals from the caller
        // ("Negotiation"/80, "ClosedLost"/0), with a terminal check against
        // four hardcoded stage names. The target is now the tenant's own
        // configured key, and everything else the guard decides.
        private async Task MoveDealOnQuoteDecisionAsync(
            Guid dealId, Guid tenantId, string? toStageKey,
            string changedBy, string note, decimal? actualValue = null)
        {
            if (string.IsNullOrWhiteSpace(toStageKey))
                return;      // this workspace has not asked for a move

            try
            {
                var deal = await _db.Deals
                   .IgnoreQueryFilters()
                   .FirstOrDefaultAsync(d =>
                       d.Id == dealId && d.TenantId == tenantId && !d.IsDeleted);

                if (deal == null)
                {
                    _logger.LogError(
                        "Quote decision: deal {DealId} not found for tenant {TenantId}",
                        dealId, tenantId);
                    return;
                }

                var stages = await _stages.GetAsync(tenantId);

                if (!QuoteStageAutomation.ShouldMove(
                        stages, deal.Stage, toStageKey!, out var why))
                {
                    _logger.LogInformation(
                        "Deal {DealId} not moved on quote decision: {Reason}", dealId, why);
                    return;
                }

                var decision = await _guard.CheckAsync(new StageMoveRequest(
                    TenantId: tenantId,
                    Deal: new DealStageSnapshot(
                        deal.Id, deal.Stage, deal.ExpectedValue,
                        deal.ExpectedCloseDateUtc, deal.OwnerUserId),
                    ToStageKey: toStageKey!,
                    Note: note,

                    // A quote accepted or rejected IS the event a requirement
                    // would have asked about, so the process matrix is not
                    // consulted. Safe here only because ShouldMove refuses to
                    // touch a deal that is already closed — otherwise a system
                    // move would be a REOPEN, and a system reopen skips the
                    // invoice rule.
                    IsSystemMove: true));

                var fromStage = deal.Stage;

                // The probability comes from the target stage. actualValue and
                // the close date are applied ONLY when the move closes the
                // deal, and cleared when it does not — which is the fix for
                // the old code stamping a closing figure onto an open deal and
                // having it counted as revenue while still in the pipeline.
                StageTransitionGuard.ApplyToDeal(
                    deal, decision, changedBy, actualValue: actualValue);

                _db.DealStageHistory.Add(
                    StageTransitionGuard.HistoryFor(tenantId, dealId, decision, changedBy));

                await _db.SaveChangesAsync();

                _logger.LogInformation(
                    "Deal {DealId}: {From} → {To} (closing: {IsClosing})",
                    dealId, fromStage, decision.To.Key, decision.IsClosing);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to move deal {DealId} to {Stage} after a quote decision",
                    dealId, toStageKey);

                // Only here — when the move really failed.
                try
                {
                    await _audit.WriteAsync(
                        AuditAction.DealStageFailed, AuditEntityType.Deal, dealId, tenantId,
                        new { attemptedStage = toStageKey, error = ex.Message },
                        CancellationToken.None);
                }
                catch { /* the audit write must never mask the original failure */ }
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

            if (quote.Status == QuoteStatus.PendingApproval)
                throw new InvalidOperationException("This quote is waiting for approval. Recall the request before editing it.");

            if (!QuoteWorkflow.IsEditable(quote.Status))
                throw new InvalidOperationException(
                    $"A {QuoteWorkflow.Label(quote.Status)} quote can't be edited — the customer may already have it. " +
                    "Create a new quote instead.");

            if (dto.Items == null || dto.Items.Count == 0)
                throw new InvalidOperationException("Add at least one item to the quote.");

            QuoteLineChecks.Validate(dto.Items.Select(i => (i.Name ?? "", i.UnitPrice, i.Quantity, i.LineDiscount, i.TaxRate)));

            var currentUser = await _currentUserService.GetCurrentUserAsync();
            var userName = currentUser?.FullName ?? "System";

            // What was approved is no longer what would be sent.
            if (quote.Status == QuoteStatus.Approved)
            {
                quote.Status = QuoteStatus.Draft;

                var approved = await _db.QuoteApprovalRequests
                    .Where(r => r.QuoteId == quoteId && r.TenantId == tenantId &&
                                r.Status == QuoteApprovalRequestStatus.Approved)
                    .OrderByDescending(r => r.DecidedAtUtc)
                    .FirstOrDefaultAsync();

                if (approved != null)
                    approved.Status = QuoteApprovalRequestStatus.Superseded;
            }

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

            // (017) The link only works while the quote is in the customer's
            // hands. A quote pulled back to Revised / Draft / approval is
            // being edited — the customer must not see it half-changed.
            // Re-sending it reuses the same token, so the same link works again.
            if (quote.Status is not (QuoteStatus.Sent or QuoteStatus.Viewed or QuoteStatus.Accepted
                                     or QuoteStatus.Rejected or QuoteStatus.Expired))
                return null;

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

            var invoiceNumber = await _db.Invoices
                .Where(i => i.QuoteId == quoteId && i.TenantId == tenantId && !i.IsDeleted)
                .Select(i => i.Number)
                .FirstOrDefaultAsync();

            if (invoiceNumber != null)
                throw new InvalidOperationException($"Invoice {invoiceNumber} was raised from this quote, so it can't be deleted.");

            var currentUser = await _currentUserService.GetCurrentUserAsync();

            // Close any open approval request — nobody should be asked to
            // approve a quote that no longer exists.
            var pending = await _db.QuoteApprovalRequests
                .Where(r => r.QuoteId == quoteId && r.TenantId == tenantId &&
                            r.Status == QuoteApprovalRequestStatus.Pending)
                .ToListAsync();

            foreach (var r in pending)
            {
                r.Status = QuoteApprovalRequestStatus.Recalled;
                r.DecidedByUserId = currentUser.UserId;
                r.DecidedByName = currentUser.FullName;
                r.DecidedAtUtc = DateTime.UtcNow;
                r.DecisionComment = "Quote deleted";
            }

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
            var pendingApproval = QuoteStatus.PendingApproval;
            var approved = QuoteStatus.Approved;
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
                    // "Not sent yet" — Draft plus the two approval states, so
                    // the buckets still add up to TotalQuotes (017).
                    DraftQuotes    = g.Count(q => q.Status == draft || q.Status == pendingApproval || q.Status == approved),
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

    // ─────────────────────────────────────────────────────────────────
    // LINE CHECKS — shared by Create and Update. The pages check these
    // too, but the API is the one that must not accept nonsense.
    // ─────────────────────────────────────────────────────────────────
    public static class QuoteLineChecks
    {
        public static void Validate(IEnumerable<(string Name, decimal UnitPrice, int Quantity, decimal LineDiscount, decimal TaxRate)> lines)
        {
            foreach (var l in lines)
            {
                var name = string.IsNullOrWhiteSpace(l.Name) ? "An item" : $"\"{l.Name.Trim()}\"";

                if (string.IsNullOrWhiteSpace(l.Name))
                    throw new InvalidOperationException("All items must have a name.");
                if (l.Quantity <= 0)
                    throw new InvalidOperationException($"{name}: quantity must be at least 1.");
                if (l.UnitPrice < 0)
                    throw new InvalidOperationException($"{name}: price can't be negative.");
                if (l.LineDiscount < 0)
                    throw new InvalidOperationException($"{name}: discount can't be negative.");
                if (l.LineDiscount > l.UnitPrice * l.Quantity)
                    throw new InvalidOperationException($"{name}: the discount is more than the line amount.");
                if (l.TaxRate < 0 || l.TaxRate > 1)
                    throw new InvalidOperationException($"{name}: tax rate must be between 0% and 100%.");
            }
        }
    }
}
