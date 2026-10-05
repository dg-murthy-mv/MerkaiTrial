// =====================================================================
// QuoteMilestoneHandlers.cs
// Location: MerkaiTrial.Application/Commands/Quotes/QuoteMilestoneHandlers.cs
//
// NEW FILE (071). Two handlers, picked up automatically by the Scrutor
// scan in AddApiHandlers() — nothing has to be registered.
//
//   GetBillingScheduleHandler   — the schedule plus the agreed /
//                                 invoiced / outstanding rollup. This is
//                                 the read the quote page, the schedule
//                                 editor, the invoice-create page and
//                                 the quote PDF all do.
//
//   SaveBillingScheduleHandler  — validate, write, audit.
//
// THE ONE THING THAT IS EASY TO GET WRONG HERE
//   Both handlers check RECORD VISIBILITY through the quote's DEAL, not
//   the quote. A quote has no owner of its own in this product; it
//   follows its deal, and that is how CreateInvoiceFromQuoteHandler and
//   every quote query already behave. Checking the tenant alone would
//   let a rep read — and re-cut — the payment terms on somebody else's
//   deal.
//
// WHAT IS **NOT** HERE
//   The arithmetic. Every share, every remainder and every validation
//   message lives in QuoteMilestones.cs, so the editor, the invoice
//   handler and the PDFs cannot drift apart.
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MerkaiTrial.Application.Commands.Quotes
{
    #region Read
    public class GetBillingScheduleHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly IRecordScopeService _scope;
        private readonly ILogger<GetBillingScheduleHandler> _logger;

        public GetBillingScheduleHandler(
            FlowDbContext db,
            IRecordScopeService scope,
            ILogger<GetBillingScheduleHandler> logger)
        {
            _db = db;
            _scope = scope;
            _logger = logger;
        }

        public async Task<BillingScheduleDto> Handle(Guid tenantId, Guid quoteId, CancellationToken ct = default)
        {
            var quote = await _db.Quotes
                .AsNoTracking()
                .Where(q => q.Id == quoteId && q.TenantId == tenantId && !q.IsDeleted)
                .Select(q => new
                {
                    q.Id,
                    q.DealId,
                    q.Number,
                    q.Status,
                    q.Currency,
                    q.GrandTotal
                })
                .FirstOrDefaultAsync(ct);

            if (quote == null)
                throw new KeyNotFoundException($"Quote {quoteId} not found");

            // The quote follows its deal — not visible, not found.
            if (!await _scope.CanSeeDealAsync(_db, tenantId, quote.DealId))
                throw new KeyNotFoundException($"Quote {quoteId} not found");

            var milestones = await QuoteMilestones.GetForQuoteAsync(_db, tenantId, quoteId, ct);
            var shares = QuoteMilestones.Allocate(milestones, quote.GrandTotal);

            // Every invoice ever raised against this quote, void ones
            // included: a void invoice does not hold its stage, but the
            // quote page has to be able to show that it happened.
            var invoices = await _db.Invoices
                .AsNoTracking()
                .Where(i => i.TenantId == tenantId && i.QuoteId == quoteId && !i.IsDeleted)
                .Select(i => new
                {
                    i.Id,
                    i.Number,
                    i.Status,
                    i.Total,
                    i.MilestoneId,
                    Paid = i.Payments
                        .Where(p => !p.IsDeleted && p.Status == PaymentStatusNames.Captured)
                        .Sum(p => (decimal?)p.Amount) ?? 0m
                })
                .ToListAsync(ct);

            var live = invoices.Where(i => i.Status != InvoiceStatus.Cancelled).ToList();

            var (billable, notBillableReason) = QuoteMilestones.QuoteBillability(quote.Status);

            var dto = new BillingScheduleDto
            {
                QuoteId = quote.Id,
                QuoteNumber = quote.Number,
                QuoteStatus = quote.Status.ToString(),
                Currency = quote.Currency,
                QuoteTotal = quote.GrandTotal,
                InvoicedTotal = live.Sum(i => i.Total),
                PaidTotal = live.Sum(i => i.Paid),
                QuoteIsBillable = billable,
                NotBillableReason = notBillableReason,

                // ⚠ A live invoice for the WHOLE quote — no stage against
                // it. Normally null. When it is not, no schedule can be
                // saved and no stage can be billed: the quote is already
                // fully invoiced, and stages on top of it would bill the
                // customer twice. See QuoteMilestones.Validate.
                WholeQuoteInvoiceNumber = live.FirstOrDefault(i => !i.MilestoneId.HasValue)?.Number,

                // What the stages SAY they come to, before the
                // last-stage remainder rule. Drives
                // BillingScheduleDto.MatchesQuoteTotal — see the long
                // note there for why the allocated sum would be useless
                // here. The case this catches is a quote edited after its
                // schedule was set.
                StatedTotal = milestones.Sum(m => QuoteMilestones.StatedAmount(m, quote.GrandTotal))
            };

            var invoicedInFull = dto.IsInvoicedInFull;

            // NOTE: there is no amountsLocked local here. Any live
            // invoice freezes the schedule's money, and that is computed
            // on the DTO (BillingScheduleDto.AmountsLocked) from the rows
            // this loop is about to fill in plus WholeQuoteInvoiceNumber
            // above — so working it out a second time here would be two
            // definitions of the same rule, one of which would eventually
            // be the stale one.
            foreach (var share in shares)
            {
                var source = milestones.First(m => m.Id == share.MilestoneId);
                var liveInvoice = live.FirstOrDefault(i => i.MilestoneId == share.MilestoneId);

                var row = new MilestoneRowDto
                {
                    Id = source.Id,
                    SortOrder = source.SortOrder,
                    Name = source.Name,
                    Percent = source.Percent,
                    FixedAmount = source.FixedAmount,
                    DueCondition = source.DueCondition,
                    DueDateUtc = source.DueDateUtc,

                    Sequence = share.Sequence,
                    Count = share.Count,
                    Amount = share.Amount,
                    EffectivePercent = share.Percent,
                    IsFinal = share.IsFinal,

                    InvoiceId = liveInvoice?.Id,
                    InvoiceNumber = liveInvoice?.Number,
                    InvoiceStatus = liveInvoice?.Status.ToString(),
                    PaidAmount = liveInvoice?.Paid ?? 0m
                };

                if (liveInvoice != null)
                {
                    row.CanBill = false;
                    row.BlockedReason = $"Already invoiced — {liveInvoice.Number}.";
                }
                else if (invoicedInFull)
                {
                    // The quote is already billed in full, so no stage
                    // can be billed on top of it.
                    row.CanBill = false;
                    row.BlockedReason =
                        $"This quote is already invoiced in full ({dto.WholeQuoteInvoiceNumber}). " +
                        "Void that invoice to bill in stages instead.";
                }
                else if (!billable)
                {
                    row.CanBill = false;
                    row.BlockedReason = notBillableReason;
                }
                else
                {
                    row.CanBill = true;
                }

                dto.Rows.Add(row);
            }

            return dto;
        }
    }
    #endregion

    #region Save
    public class SaveBillingScheduleHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;
        private readonly IRecordScopeService _scope;
        private readonly IAuditService _audit;
        private readonly GetBillingScheduleHandler _read;
        private readonly ILogger<SaveBillingScheduleHandler> _logger;

        public SaveBillingScheduleHandler(
            FlowDbContext db,
            ICurrentUserService currentUser,
            IRecordScopeService scope,
            IAuditService audit,
            GetBillingScheduleHandler read,
            ILogger<SaveBillingScheduleHandler> logger)
        {
            _db = db;
            _currentUser = currentUser;
            _scope = scope;
            _audit = audit;
            _read = read;
            _logger = logger;
        }

        public async Task<BillingScheduleDto> Handle(SaveBillingScheduleDto dto, CancellationToken ct = default)
        {
            var quote = await _db.Quotes
                .FirstOrDefaultAsync(q => q.Id == dto.QuoteId && q.TenantId == dto.TenantId && !q.IsDeleted, ct);

            if (quote == null)
                throw new KeyNotFoundException($"Quote {dto.QuoteId} not found");

            // Write access follows the deal, not just the tenant.
            //
            // CanSeeDealAsync + KeyNotFoundException rather than
            // EnsureDealVisibleAsync, deliberately, and for the same
            // reason CreateInvoiceFromQuoteHandler does it this way: the
            // READ path above answers "not found" for an invisible deal,
            // and the write path has to answer identically or the pair of
            // them becomes an oracle for whether somebody else's deal
            // exists.
            if (!await _scope.CanSeeDealAsync(_db, dto.TenantId, quote.DealId))
                throw new KeyNotFoundException($"Quote {dto.QuoteId} not found");

            var existing = await QuoteMilestones.GetForQuoteAsync(_db, dto.TenantId, dto.QuoteId, ct);

            // Which live invoice is holding the schedule, if any. The
            // same "live means not void" rule as everywhere else: a void
            // invoice frees its stage, which is how a mistake on an
            // issued invoice is corrected.
            //
            // ⚠ DELIBERATELY NOT FILTERED ON MilestoneId != null, and
            // this is the defect the filter would have been:
            //
            //   A quote with no schedule is invoiced in FULL. Somebody
            //   then adds a three-stage schedule. With the filter, that
            //   whole-quote invoice would be invisible to the check
            //   below, the schedule would save, and all three stages
            //   would then be billable ON TOP of the invoice already
            //   sent — the customer billed twice, with nothing on any
            //   screen to suggest it.
            //
            // A whole-quote invoice therefore refuses a schedule
            // outright, and QuoteMilestones.Validate needs to know WHICH
            // kind of invoice it is in order to say so. Ordered so the
            // whole-quote one wins if somehow both exist.
            var holding = await _db.Invoices
                .AsNoTracking()
                .Where(i => i.TenantId == dto.TenantId
                            && i.QuoteId == dto.QuoteId
                            && !i.IsDeleted
                            && i.Status != InvoiceStatus.Cancelled)
                .OrderBy(i => i.MilestoneId == null ? 0 : 1)
                .Select(i => new { i.Number, i.MilestoneId })
                .FirstOrDefaultAsync(ct);

            var rows = QuoteMilestones.Validate(
                dto.Milestones,
                quote.GrandTotal,
                existing,
                liveInvoiceNumber: holding?.Number,
                liveInvoiceIsWholeQuote: holding != null && holding.MilestoneId == null);

            var currentUser = await _currentUser.GetCurrentUserAsync();
            var actor = string.IsNullOrWhiteSpace(dto.ActorName) ? currentUser.FullName : dto.ActorName!;

            // null here would mean "leave the schedule alone", and the
            // page always sends a list — so this is the API-caller path.
            // Nothing to do, and nothing to audit.
            if (dto.Milestones == null)
                return await _read.Handle(dto.TenantId, dto.QuoteId, ct);

            var written = await QuoteMilestones.SaveAsync(
                _db, dto.TenantId, dto.QuoteId, rows, actor, ct);

            await _db.SaveChangesAsync(ct);

            await _audit.WriteAsync(
                written == 0 ? AuditAction.BillingScheduleCleared : AuditAction.BillingScheduleSaved,
                AuditEntityType.Quote,
                dto.QuoteId,
                dto.TenantId,
                new
                {
                    quote = quote.Number,
                    stages = written,
                    total = quote.GrandTotal,
                    currency = quote.Currency,
                    // The schedule itself, so "why is this invoice 40%"
                    // is answerable from the audit log alone.
                    schedule = rows.Select(r => new
                    {
                        r.Name,
                        r.Percent,
                        r.FixedAmount,
                        r.DueCondition
                    }).ToList()
                });

            _logger.LogInformation(
                "Billing schedule for quote {Number} saved with {Stages} stage(s) by {Actor}",
                quote.Number, written, actor);

            return await _read.Handle(dto.TenantId, dto.QuoteId, ct);
        }
    }
    #endregion
}
