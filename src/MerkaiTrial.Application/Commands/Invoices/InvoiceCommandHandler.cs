// =====================================================================
// InvoiceCommandHandler.cs
// Location: MerkaiTrial.Application/Commands/Invoices/InvoiceCommandHandler.cs
//
// COMPLETE FILE — replaces the 017 version.
//
// THE INVOICE LIFE (018)
//
//   Draft ──Issue──► Sent/Viewed ──payments──► PartiallyPaid ──► Paid
//     │                  │
//   Delete            Void (with a reason — kept on record, never deleted)
//
//   • DRAFT: number is a placeholder "DRAFT-XXXXXXXX". Lines, dates and
//     notes can change. Can be deleted — no gap in the sequence, because it
//     never had a real number.
//   • ISSUE: gives the next INV-nnnn, dates the invoice today (the due date
//     keeps the same payment terms), locks it. India GST, Thai VAT, UAE VAT
//     and Philippine BIR all expect a tax invoice's number and content not
//     to change after it is issued.
//   • An issued invoice can't be edited or deleted. Mistakes are fixed by
//     VOID (reason required) and issuing a new one.
//   • A payment recorded by mistake is REVERSED (kept, marked Reversed,
//     with a reason) — not deleted. Reverse payments before voiding.
//   • Paid / PartiallyPaid are worked out from payments, never set by hand
//     (InvoiceStatusRules).
//
// APPROVAL FOR MANUAL INVOICES
//   An invoice raised from an ACCEPTED QUOTE went through quote approval
//   already — anyone with invoices.update can issue it, and its lines can't
//   be edited (they are the quote's).
//   A MANUAL invoice (straight from a deal) meets the same limits as a
//   quote (Settings → Quote approvals). If it's over them, only a manager
//   of the deal owner's team or a workspace admin can ISSUE it. The rep can
//   still create and save the draft.
//
// OTHER FIXES
//   ✅ AddPayment refuses drafts ("issue it first") and void invoices, and
//      works the balance out from the payments instead of subtracting.
//   ✅ Line checks on create/update (quantity, price, discount, tax).
//   ✅ Delete: drafts only.
//
// 071 — PARTIAL INVOICING / MILESTONE BILLING
//
//   A quote can carry a BILLING SCHEDULE: "30% on signing, 40% on
//   delivery, 30% on completion". Each stage becomes its own invoice,
//   and each invoice is a COMPLETE copy of the quote at that stage's
//   share — every line, so every line keeps its own tax rate and its
//   own HSN / SAC code. After 067 that is the whole point.
//
//   ✅ THE ONE-INVOICE-PER-QUOTE GUARD IS NOW ONE PER MILESTONE. The
//      message changes from "an invoice already exists for this quote"
//      to naming the stage. For a quote with NO schedule the rule is
//      byte-for-byte what it was: MilestoneId is NULL on every invoice,
//      and the query below looks for a live invoice with a NULL
//      MilestoneId — one per quote. The database now enforces it too
//      (UX_Invoices_Quote_Milestone_Live), which closes a race the
//      read-then-write guard could never close on its own.
//
//   ✅ A quote WITH a schedule REFUSES an invoice that does not name a
//      stage. Quietly billing the whole amount against a quote somebody
//      deliberately split into stages is the worst available reading of
//      an omitted field.
//
//   ✅ LINE SCALING. QuoteMilestones.AllocateLines does the arithmetic;
//      the rounding rule and the proof that the stages sum to exactly
//      the quote total are in that file's header. What this file owns is
//      how a scaled line READS — see the long note in
//      CreateInvoiceFromQuoteHandler. A line that is fully allocated to
//      this stage (a single 100% stage, which is the default) is copied
//      verbatim, so the common case produces exactly the invoice it
//      produced before this round.
//
// 067 — TAX CLASSIFICATION ON THE LINE
//   ✅ Every line path carries TaxCode — the HSN / SAC code in India, the
//      local equivalent elsewhere, null where none applies.
//   ✅ CreateInvoiceFromQuote copies the QUOTE LINE's code across, and
//      passes "" rather than null when the quote line had none, so the
//      product is never consulted on this path. An invoice is the document
//      the tax authority sees: it has to carry the classification the
//      customer accepted, not whatever the catalogue says on the day the
//      invoice is raised.
//   ✅ Manual invoices snapshot the code from the product when the caller
//      sent nothing (LineTaxCodes, in QuotesCommandHandler.cs beside
//      LineUnits, shared so quotes and invoices cannot drift).
//   ✅ The read projection carries it out to InvoiceLineDto.
//
// 055 — A LINE DISCOUNT CAN BE A PERCENTAGE
//   Every line path runs through LineDiscounts.Resolve, the same rule the
//   quote handler uses, and CreateInvoiceFromQuote copies the percentage
//   across with the rest of the line — so an invoice raised from a quote
//   says "-10%" exactly where the quote the customer accepted did. No
//   total below changes: they all still read LineDiscount.
//
// 052 — DECIMAL QUANTITY AND UNIT OF MEASURE (Phase B of the catalogue)
//   ✅ Every line path carries a DECIMAL quantity and a UnitOfMeasure.
//   ✅ CreateInvoiceFromQuote copies the quote line's unit across. This is
//      the path that made the two halves one round rather than two: it
//      copies QuoteItem → CreateInvoiceLineDto field by field, so a
//      decimal quantity on the quote and an int on the invoice would have
//      rounded 12.5 m² down to 12 the moment the invoice was raised — the
//      customer's invoice quietly disagreeing with the quote they accepted.
//   ✅ Manual invoices snapshot the unit from the product when the caller
//      did not send one (LineUnits, in QuotesCommandHandler.cs, shared so
//      quotes and invoices cannot drift).
//   ✅ QuoteLineChecks — the same validator, now with a decimal tuple.
//
// RECORD VISIBILITY (016/017) unchanged: invoices follow their deal;
// InvoiceAccessHandler at the bottom is what InvoicesController checks.
// =====================================================================

using MerkaiTrial.Application.Commands.Deals;
using MerkaiTrial.Application.Commands.Quotes;
using MerkaiTrial.Application.Common;          // 055: LineDiscounts
using MerkaiTrial.Application.Configuration;   // 052: UnitsOfMeasure — 071 needs it for a collapsed line
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MerkaiTrial.Application.Commands.Invoices
{
    #region Helpers — numbering, statuses
    /// <summary>INV-nnnn sequence per tenant, and the retry that makes it safe under concurrency.</summary>
    internal static class InvoiceNumberSequence
    {
        // Two things here are deliberate and must not be "simplified":
        //  1. MAX of the parsed sequence, not the newest row — seed data and
        //     imports don't create rows in number order.
        //  2. NOT filtered by !IsDeleted — the unique index covers deleted
        //     rows, so their numbers can never be reused.
        // DRAFT-… placeholders don't start with INV-, so they never count.
        public static async Task<string> NextAsync(FlowDbContext db, Guid tenantId)
        {
            var numbers = await db.Invoices
                .Where(i => i.TenantId == tenantId
                            && i.Number != null
                            && i.Number.StartsWith(InvoiceNumbering.IssuedPrefix))
                .Select(i => i.Number)
                .ToListAsync();

            var max = 0;
            foreach (var number in numbers)
            {
                var tail = number.Substring(InvoiceNumbering.IssuedPrefix.Length);
                if (int.TryParse(tail, out var value) && value > max)
                    max = value;
            }

            return $"{InvoiceNumbering.IssuedPrefix}{(max + 1):D4}";
        }

        /// <summary>
        /// Max-of-existing is correct but not atomic: two people issuing at
        /// the same moment can read the same max. The unique index is the
        /// real guard — on a clash, take a fresh number and try again.
        /// </summary>
        public static async Task SaveWithRetryAsync(
            FlowDbContext db, Invoice invoice, Func<Task<string>> nextNumber, ILogger logger)
        {
            const int maxAttempts = 5;

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await db.SaveChangesAsync();
                    return;
                }
                catch (DbUpdateException ex) when (IsDuplicateKey(ex) && attempt < maxAttempts)
                {
                    var collided = invoice.Number;
                    invoice.Number = await nextNumber();
                    logger.LogWarning(
                        "Invoice number {Collided} already taken for tenant {TenantId}; retrying as {Next} ({Attempt}/{Max})",
                        collided, invoice.TenantId, invoice.Number, attempt, maxAttempts);
                }
            }
        }

        // 2601 = duplicate key row in object with unique index; 2627 = unique constraint.
        // Walks the exception itself too — ExecuteUpdate surfaces the raw SqlException.
        public static bool IsDuplicateKey(Exception ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
                if (e is SqlException sql && (sql.Number == 2601 || sql.Number == 2627))
                    return true;
            return false;
        }
    }

    internal static class InvoiceStates
    {
        /// <summary>Issued and not void — owed, or paid.</summary>
        public static bool IsIssued(InvoiceStatus s) => s is not (InvoiceStatus.Draft or InvoiceStatus.Cancelled);

        public static decimal CapturedTotal(IEnumerable<Payment> payments)
            => payments.Where(p => !p.IsDeleted && p.Status == PaymentStatusNames.Captured).Sum(p => p.Amount);

        public static string Label(InvoiceStatus s) => s switch
        {
            InvoiceStatus.Cancelled => "void",
            InvoiceStatus.PartiallyPaid => "partly paid",
            _ => s.ToString().ToLowerInvariant()
        };
    }
    #endregion

    #region Create Invoice (always a draft; optionally issued straight away)
    public class CreateInvoiceHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<CreateInvoiceHandler> _logger;
        private readonly IAuditService _audit;
        private readonly IRecordScopeService _scope;
        private readonly IssueInvoiceHandler _issue;

        public CreateInvoiceHandler(
            FlowDbContext db,
            ICurrentUserService currentUserService,
            ILogger<CreateInvoiceHandler> logger,
            IAuditService audit,
            IRecordScopeService scope,
            IssueInvoiceHandler issue)
        {
            _db = db;
            _currentUserService = currentUserService;
            _logger = logger;
            _audit = audit;
            _scope = scope;
            _issue = issue;
        }

        /// <summary>
        /// Creates a DRAFT. With SendImmediately it is then issued — unless
        /// the user may not issue it (manual invoice over the limits), in
        /// which case it stays a draft and the returned Status says so.
        /// </summary>
        public async Task<InvoiceDto> Handle(CreateInvoiceDto dto)
        {
            _logger.LogInformation("Creating invoice for tenant {TenantId}", dto.TenantId);

            // ── Record visibility ─────────────────────────────────────────
            if (!dto.DealId.HasValue && dto.QuoteId.HasValue)
            {
                // Linked to a quote only — take the quote's deal.
                var quoteDealId = await _db.Quotes.AsNoTracking()
                    .Where(q => q.Id == dto.QuoteId.Value && q.TenantId == dto.TenantId && !q.IsDeleted)
                    .Select(q => (Guid?)q.DealId)
                    .FirstOrDefaultAsync()
                    ?? throw new KeyNotFoundException($"Quote {dto.QuoteId} not found");

                dto.DealId = quoteDealId;
            }

            if (dto.DealId.HasValue)
            {
                await _scope.EnsureDealVisibleAsync(_db, dto.TenantId, dto.DealId.Value);
            }
            else
            {
                var dealAccess = await _scope.GetAsync(RecordModules.Deals);
                if (!dealAccess.SeesAll)
                    throw new InvalidOperationException("Link this invoice to a deal, so you (and your team) can find it again.");
            }

            if (dto.Lines == null || dto.Lines.Count == 0)
                throw new InvalidOperationException("Add at least one item to the invoice.");

            // 055. FIRST, before anything reads LineDiscount — a
            // percentage replaces whatever amount came with it, so the
            // validation, the totals and the stored row all use the same
            // number. LineDiscounts.Resolve is the same rule the quote
            // handler uses.
            foreach (var l in dto.Lines)
            {
                var (amount, percent) = LineDiscounts.Resolve(
                    l.Name, l.UnitPrice, l.Quantity, l.LineDiscount, l.DiscountPercent);
                l.LineDiscount    = amount;
                l.DiscountPercent = percent;
            }

            QuoteLineChecks.Validate(dto.Lines.Select(l => (l.Name ?? "", l.UnitPrice, l.Quantity, l.LineDiscount, l.TaxRate)));

            // 052: one query for the catalogue products on this invoice, so a
            // line that arrived without a unit still stores the product's own.
            // CreateInvoiceFromQuote sends the quote's unit explicitly, so for
            // that path this lookup finds nothing to fill in — as it should.
            var units = await LineUnits.LookupAsync(
                _db, dto.TenantId, dto.Lines.Select(l => l.ProductId));

            // 067: the same shape for the tax classification code, and the
            // same note applies — CreateInvoiceFromQuote sends the quote
            // line's code explicitly, so for that path this lookup fills in
            // nothing. That is the point: the invoice must say what the
            // customer accepted, not what the catalogue says today.
            var taxCodes = await LineTaxCodes.LookupAsync(
                _db, dto.TenantId, dto.Lines.Select(l => l.ProductId));

            var currentUser = await _currentUserService.GetCurrentUserAsync();
            var now = DateTime.UtcNow;

            // Totals — Subtotal is GROSS: Total = Subtotal − Discount + Tax
            var subtotal = dto.Lines.Sum(l => l.UnitPrice * l.Quantity);
            var discountTotal = dto.Lines.Sum(l => l.LineDiscount);
            var taxTotal = dto.Lines.Sum(l => ((l.UnitPrice * l.Quantity) - l.LineDiscount) * l.TaxRate);
            var grandTotal = subtotal - discountTotal + taxTotal;

            var invoice = new Invoice
            {
                Id = Guid.NewGuid(),
                TenantId = dto.TenantId,
                QuoteId = dto.QuoteId,
                DealId = dto.DealId,
                Number = InvoiceNumbering.NewDraftNumber(),   // real INV-nnnn on issue
                IssueDateUtc = dto.IssueDateUtc,
                DueDateUtc = dto.DueDateUtc,
                Currency = dto.Currency,
                Status = InvoiceStatus.Draft,
                Subtotal = subtotal,
                DiscountTotal = discountTotal,
                TaxTotal = taxTotal,
                Total = grandTotal,
                Amount = grandTotal,
                Balance = grandTotal,
                Notes = dto.Notes,

                // 071. All five NULL unless CreateInvoiceFromQuoteHandler
                // filled them in — which is the only thing that does. A
                // manual invoice has no quote and therefore no schedule,
                // and the database refuses the combination anyway
                // (CK_Invoices_MilestoneNeedsQuote).
                MilestoneId = dto.MilestoneId,
                MilestoneName = dto.MilestoneName,
                MilestoneSequence = dto.MilestoneSequence,
                MilestoneCount = dto.MilestoneCount,
                MilestonePercent = dto.MilestonePercent,

                CreatedAtUtc = now,
                CreatedBy = string.IsNullOrWhiteSpace(dto.CreatedBy) ? currentUser.FullName : dto.CreatedBy!,
                IsDeleted = false
            };

            foreach (var lineDto in dto.Lines)
            {
                invoice.Lines.Add(new InvoiceLine
                {
                    Id = Guid.NewGuid(),
                    TenantId = dto.TenantId,
                    InvoiceId = invoice.Id,
                    ProductId = lineDto.ProductId,
                    Name = (lineDto.Name ?? "").Trim(),
                    Description = lineDto.Description,
                    UnitPrice = lineDto.UnitPrice,
                    Quantity = lineDto.Quantity,
                    UnitOfMeasure = LineUnits.Resolve(lineDto.UnitOfMeasure, lineDto.ProductId, units),  // 052
                    LineDiscount = lineDto.LineDiscount,
                    DiscountPercent = lineDto.DiscountPercent,     // 055 — resolved above
                    TaxRate = lineDto.TaxRate,
                    TaxCode = LineTaxCodes.Resolve(lineDto.TaxCode, lineDto.ProductId, taxCodes),  // 067
                    Amount = ((lineDto.UnitPrice * lineDto.Quantity) - lineDto.LineDiscount) * (1 + lineDto.TaxRate),
                    CreatedAtUtc = now,
                    CreatedBy = invoice.CreatedBy,
                    IsDeleted = false
                });
            }

            _db.Invoices.Add(invoice);
            await InvoiceNumberSequence.SaveWithRetryAsync(
                _db, invoice, () => Task.FromResult(InvoiceNumbering.NewDraftNumber()), _logger);

            await _audit.WriteAsync(
                AuditAction.InvoiceCreated, AuditEntityType.Invoice, invoice.Id, dto.TenantId,
                new
                {
                    number = invoice.Number,
                    total = invoice.Total,
                    quoteId = invoice.QuoteId,
                    // 071. Which stage, and what share of the quote. "Why
                    // is this invoice 40% of the quote" has to be
                    // answerable from the audit log and not only from a
                    // screen that may have been re-cut since.
                    milestoneId = invoice.MilestoneId,
                    milestone = invoice.MilestoneLabel
                });

            if (dto.SendImmediately)
            {
                try
                {
                    await _issue.Handle(dto.TenantId, invoice.Id);
                }
                catch (InvalidOperationException ex)
                {
                    // Over the limits for this user — it stays a draft. The
                    // page reads Status == "Draft" and explains.
                    _logger.LogInformation("Invoice {Id} created as draft, not issued: {Reason}", invoice.Id, ex.Message);
                }
            }

            _logger.LogInformation("Invoice {Id} created", invoice.Id);

            var saved = await _db.Invoices
                .AsNoTracking()
                .Where(i => i.Id == invoice.Id)
                .Include(i => i.Lines.Where(l => !l.IsDeleted))
                .Include(i => i.Quote)
                .Include(i => i.Deal)
                .FirstAsync();

            return MapToDto(saved);
        }

        internal static InvoiceDto MapToDto(Invoice invoice) => new()
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
            GrandTotal = invoice.Subtotal - invoice.DiscountTotal + invoice.TaxTotal,
            Balance = invoice.Balance,
            Notes = invoice.Notes,

            // 071. The snapshots, straight off the row — the schedule is
            // deliberately not consulted. QuoteNumber comes with them
            // because InvoiceDto.MilestoneBasisNote prints it ("Amounts
            // shown are 40% of accepted quote QUO-0042"), and the Quote
            // is already Included by the caller's query.
            QuoteNumber = invoice.Quote?.Number,
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
                UnitOfMeasure = l.UnitOfMeasure,               // 052
                LineDiscount = l.LineDiscount,
                DiscountPercent = l.DiscountPercent,           // 055
                TaxRate = l.TaxRate,
                TaxCode = l.TaxCode,                           // 067
                LineTotal = (l.UnitPrice * l.Quantity) - l.LineDiscount,
                LineTax = ((l.UnitPrice * l.Quantity) - l.LineDiscount) * l.TaxRate,
                LineGrandTotal = ((l.UnitPrice * l.Quantity) - l.LineDiscount) * (1 + l.TaxRate)
            }).ToList(),
            CreatedAtUtc = invoice.CreatedAtUtc,
            CreatedBy = invoice.CreatedBy
        };
    }
    #endregion

    #region Create Invoice From Quote
    public class CreateInvoiceFromQuoteHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly CreateInvoiceHandler _createInvoice;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<CreateInvoiceFromQuoteHandler> _logger;
        private readonly IRecordScopeService _scope;

        public CreateInvoiceFromQuoteHandler(
            FlowDbContext db,
            CreateInvoiceHandler createInvoice,
            ICurrentUserService currentUserService,
            ILogger<CreateInvoiceFromQuoteHandler> logger,
            IRecordScopeService scope)
        {
            _db = db;
            _createInvoice = createInvoice;
            _currentUserService = currentUserService;
            _logger = logger;
            _scope = scope;
        }

        public async Task<InvoiceDto> Handle(CreateInvoiceFromQuoteDto dto)
        {
            _logger.LogInformation("Creating invoice from quote {QuoteId}", dto.QuoteId);

            var quote = await _db.Quotes
                .Include(q => q.Items)
                .Include(q => q.Deal)
                .FirstOrDefaultAsync(q => q.Id == dto.QuoteId && q.TenantId == dto.TenantId && !q.IsDeleted);

            if (quote == null)
                throw new KeyNotFoundException($"Quote {dto.QuoteId} not found");

            // The quote follows its deal — not visible → not found.
            if (!await _scope.CanSeeDealAsync(_db, dto.TenantId, quote.DealId))
                throw new KeyNotFoundException($"Quote {dto.QuoteId} not found");

            if (quote.Status != QuoteStatus.Accepted)
                throw new InvalidOperationException("Can only create invoice from accepted quotes");

            // ── 071: which stage of the schedule is this? ─────────────
            //
            // An EMPTY schedule is the normal case and means "the whole
            // quote" — the pre-071 behaviour, unchanged. A schedule WITH
            // stages requires the caller to name one.
            var milestones = await QuoteMilestones.GetForQuoteAsync(_db, dto.TenantId, dto.QuoteId);
            var shares = QuoteMilestones.Allocate(milestones, quote.GrandTotal);

            var stageIndex = -1;
            MilestoneShare? stage = null;

            if (shares.Count > 0)
            {
                if (!dto.MilestoneId.HasValue)
                    throw new InvalidOperationException(
                        $"Quote {quote.Number} is billed in {shares.Count} stages. " +
                        $"Pick which one to invoice: {string.Join("; ", shares.Select(s => $"{s.Sequence}. {s.Name}"))}.");

                stageIndex = shares.FindIndex(s => s.MilestoneId == dto.MilestoneId.Value);

                if (stageIndex < 0)
                    throw new InvalidOperationException(
                        "That payment stage is no longer on this quote. Reload the quote and pick a stage again.");

                stage = shares[stageIndex];

                if (stage.Amount <= 0m)
                    throw new InvalidOperationException(
                        $"Stage {stage.Sequence} ({stage.Name}) comes to nothing on this quote, so there is no invoice to raise.");
            }
            else if (dto.MilestoneId.HasValue)
            {
                throw new InvalidOperationException(
                    "This quote has no payment schedule, so there is no stage to invoice. Invoice the whole quote instead.");
            }

            // ── The guard: ONE LIVE INVOICE PER STAGE ─────────────────
            //
            // A VOID invoice doesn't block a new one — that is how a
            // mistake on an issued invoice is corrected.
            //
            // The MilestoneId comparison is BRANCHED IN C# rather than
            // written as one expression. `i.MilestoneId == dto.MilestoneId`
            // with a null on the right is the kind of thing that depends
            // on the provider's null semantics to come out as IS NULL
            // instead of `= @p` — and if it ever came out as the latter,
            // the no-schedule case would match nothing, the guard would
            // pass, and a quote could be invoiced twice. Two explicit
            // Where clauses cannot be read two ways.
            var liveInvoices = _db.Invoices
                .Where(i => i.QuoteId == dto.QuoteId
                            && i.TenantId == dto.TenantId
                            && !i.IsDeleted
                            && i.Status != InvoiceStatus.Cancelled);

            // ⚠ BEFORE the per-stage check: a live invoice for the WHOLE
            // quote blocks EVERY stage.
            //
            // The case is a quote with no schedule that gets invoiced in
            // full, after which somebody adds a schedule.
            // SaveBillingScheduleHandler refuses that, so it should not
            // be reachable — but "should not be reachable" is how a quote
            // ends up billed twice, and this is the handler that would
            // actually do it. A pre-071 invoice carries MilestoneId NULL,
            // which is exactly this shape.
            if (dto.MilestoneId.HasValue)
            {
                var wholeQuote = await _db.Invoices
                    .Where(i => i.QuoteId == dto.QuoteId
                                && i.TenantId == dto.TenantId
                                && !i.IsDeleted
                                && i.Status != InvoiceStatus.Cancelled
                                && i.MilestoneId == null)
                    .Select(i => i.Number)
                    .FirstOrDefaultAsync();

                if (wholeQuote != null)
                    throw new InvalidOperationException(
                        $"This quote is already invoiced in full ({wholeQuote}), so a stage cannot be billed on top of it. " +
                        "Void that invoice first.");
            }

            liveInvoices = dto.MilestoneId.HasValue
                ? liveInvoices.Where(i => i.MilestoneId == dto.MilestoneId.Value)
                : liveInvoices.Where(i => i.MilestoneId == null);

            var existingInvoice = await liveInvoices.FirstOrDefaultAsync();

            if (existingInvoice != null)
            {
                var what = stage == null
                    ? "this quote"
                    : $"stage {stage.Sequence} ({stage.Name}) of this quote";

                throw new InvalidOperationException(existingInvoice.Status == InvoiceStatus.Draft
                    ? $"A draft invoice already exists for {what} — open it instead."
                    : $"Invoice {existingInvoice.Number} already covers {what}. Void it first if it needs to be replaced.");
            }

            var currentUser = await _currentUserService.GetCurrentUserAsync();

            // ── 071: the lines, scaled to this stage ──────────────────
            //
            // Quote order, so the invoice lists what the quote lists in
            // the order the quote listed it.
            var items = quote.Items.Where(i => !i.IsDeleted).ToList();
            var lineShares = QuoteMilestones.AllocateLines(items, shares, stageIndex);

            var lines = new List<CreateInvoiceLineDto>();

            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                var shareOfLine = lineShares[index];

                var grossFull = item.UnitPrice * item.Quantity;

                var line = new CreateInvoiceLineDto
                {
                    ProductId = item.ProductId,
                    Name = item.Name,
                    Description = item.Description,
                    TaxRate = item.TaxRate,
                    // 067: the HSN / SAC code travels with the line, and
                    // this is the copy that matters most. The invoice is
                    // the document the tax authority sees; it must carry
                    // the classification the customer accepted on the
                    // quote, not whatever the catalogue happens to say
                    // on the day the invoice is raised.
                    //
                    // ?? "" rather than passing the quote's null straight
                    // through: null tells CreateInvoiceHandler "nothing
                    // was said about the code, go and ask the product",
                    // and that is exactly what must NOT happen here. A
                    // quote line deliberately carrying no code has to
                    // produce an invoice line carrying no code — see
                    // LineTaxCodes.Resolve, where null and "" mean two
                    // different things on purpose.
                    //
                    // 071 does not change this. A stage bills a share of
                    // the money, never a different classification.
                    TaxCode = item.TaxCode ?? ""
                };

                // ── HOW A SCALED LINE READS ───────────────────────────
                //
                // Three cases, and the first one is the one that runs
                // almost always.
                //
                // 1. THE WHOLE LINE. No schedule, or a single 100% stage
                //    (the default), or a stage that happens to take all
                //    of this line. Copied verbatim — quantity, unit
                //    price, unit of measure and the discount PERCENTAGE,
                //    exactly as before 071. This is what makes the
                //    common case produce byte-for-byte the invoice it
                //    produced in round 070.
                //
                // 2. THE QUANTITY DIVIDES EVENLY. "3 days × ₹15,000" at
                //    40% becomes "3 days × ₹6,000". The quantity and the
                //    unit survive, which is the whole point of 052, and
                //    the arithmetic is exact because the division came
                //    out at two places.
                //
                // 3. IT DOES NOT DIVIDE EVENLY. The line collapses to
                //    one unit at the stage's value. The alternative is a
                //    unit price with a rounding error in it, multiplied
                //    by the quantity — which would make the invoices
                //    stop summing to the quote, and that is the one
                //    promise this round is not allowed to break.
                //
                // THE DISCOUNT PERCENTAGE IS DROPPED in cases 2 and 3,
                // on purpose. CreateInvoiceHandler treats a percentage
                // as authoritative and RECOMPUTES the amount from it
                // (LineDiscounts.Resolve) — which would throw away the
                // allocated figure and put the rounding error straight
                // back. The allocated amount is passed instead; the
                // screens still show an implied percentage where they
                // want one (LineDiscounts.ImpliedPercent).
                if (shareOfLine.Gross == grossFull && shareOfLine.Discount == item.LineDiscount)
                {
                    // Case 1.
                    line.UnitPrice = item.UnitPrice;
                    line.Quantity = item.Quantity;
                    // 052: the quote's unit, copied like Name and
                    // UnitPrice. An invoice raised from a quote must say
                    // the same thing the customer accepted, down to "m²".
                    line.UnitOfMeasure = item.UnitOfMeasure;
                    line.LineDiscount = item.LineDiscount;
                    // 055: the percentage travels with the line. Without
                    // it the invoice would show a flat amount where the
                    // quote said "-10%", which is the same money
                    // described two different ways to the same customer.
                    line.DiscountPercent = item.DiscountPercent;
                }
                else
                {
                    var scaledUnitPrice = item.Quantity > 0m
                        ? decimal.Round(shareOfLine.Gross / item.Quantity, 2, MidpointRounding.AwayFromZero)
                        : 0m;

                    if (item.Quantity > 0m && scaledUnitPrice * item.Quantity == shareOfLine.Gross)
                    {
                        // Case 2.
                        line.UnitPrice = scaledUnitPrice;
                        line.Quantity = item.Quantity;
                        line.UnitOfMeasure = item.UnitOfMeasure;
                    }
                    else
                    {
                        // Case 3.
                        line.UnitPrice = shareOfLine.Gross;
                        line.Quantity = 1m;
                        line.UnitOfMeasure = UnitsOfMeasure.Unit;
                    }

                    line.LineDiscount = shareOfLine.Discount;
                    line.DiscountPercent = null;   // see the note above
                }

                lines.Add(line);
            }

            var createDto = new CreateInvoiceDto
            {
                TenantId = dto.TenantId,
                QuoteId = dto.QuoteId,
                DealId = quote.DealId,
                // 071. DueDateUtc stays the caller's — the stage's own
                // DueDateUtc is a SUGGESTION the page pre-fills with, not
                // something this handler imposes behind the user's back.
                IssueDateUtc = dto.IssueDateUtc,
                DueDateUtc = dto.DueDateUtc,
                Currency = quote.Currency,
                Notes = dto.Notes,
                SendImmediately = dto.SendImmediately,
                CreatedBy = string.IsNullOrWhiteSpace(dto.CreatedBy) ? currentUser.FullName : dto.CreatedBy,

                // 071. The snapshots. stage is null for a whole-quote
                // invoice, which leaves all five NULL — the pre-071 row.
                MilestoneId = stage?.MilestoneId,
                MilestoneName = stage?.Name,
                MilestoneSequence = stage?.Sequence,
                MilestoneCount = stage?.Count,
                MilestonePercent = stage?.Percent,

                Lines = lines
            };

            var result = await _createInvoice.Handle(createDto);

            if (stage == null)
                _logger.LogInformation("Invoice {Number} created from quote {QuoteNumber}", result.Number, quote.Number);
            else
                _logger.LogInformation(
                    "Invoice {Number} created from quote {QuoteNumber} for milestone {Sequence}/{Count} ({Percent}%)",
                    result.Number, quote.Number, stage.Sequence, stage.Count, stage.Percent);

            return result;
        }
    }
    #endregion

    #region Issue
    public sealed record InvoiceIssueCheck(bool Allowed, string? BlockedReason, List<string> RuleReasons, List<string> ApproverNames);

    public class IssueInvoiceHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;
        private readonly QuoteApprovalEngine _approvals;
        private readonly IAuditService _audit;
        private readonly ILogger<IssueInvoiceHandler> _logger;

        public IssueInvoiceHandler(
            FlowDbContext db,
            ICurrentUserService currentUser,
            QuoteApprovalEngine approvals,
            IAuditService audit,
            ILogger<IssueInvoiceHandler> logger)
        {
            _db = db;
            _currentUser = currentUser;
            _approvals = approvals;
            _audit = audit;
            _logger = logger;
        }

        /// <summary>Issues a draft. Returns the invoice number it was given.</summary>
        public async Task<string> Handle(Guid tenantId, Guid invoiceId, CancellationToken ct = default)
        {
            // Read-only load: the write below is ONE conditional UPDATE, so two
            // people issuing the same draft at once can't both win.
            var invoice = await _db.Invoices.AsNoTracking()
                .Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId && !i.IsDeleted, ct)
                ?? throw new KeyNotFoundException($"Invoice {invoiceId} not found");

            if (invoice.Status != InvoiceStatus.Draft)
                throw new InvalidOperationException($"This invoice is already {InvoiceStates.Label(invoice.Status)} — only a draft can be issued.");

            if (!invoice.Lines.Any(l => !l.IsDeleted))
                throw new InvalidOperationException("Add at least one item before issuing the invoice.");

            var me = await _currentUser.GetCurrentUserAsync();
            var check = await CheckAsync(invoice, me, ct);
            if (!check.Allowed)
                throw new InvalidOperationException(check.BlockedReason ?? "You can't issue this invoice.");

            // ── Dates ────────────────────────────────────────────────────
            // A tax invoice is dated the day it is issued. The due date keeps
            // the payment terms the draft had (e.g. Net 30).
            var today = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);
            DateTime? newDue = invoice.DueDateUtc.HasValue
                ? today.AddDays(Math.Max(0, (invoice.DueDateUtc.Value.Date - invoice.IssueDateUtc.Date).Days))
                : null;
            var now = DateTime.UtcNow;

            // ── Number + claim the draft, atomically ─────────────────────
            // Legacy drafts created before 018 already carry an INV number —
            // they keep it. Otherwise take the next INV-nnnn; if someone else
            // took it a moment ago the unique index refuses and we try the next.
            var keepNumber = !string.IsNullOrWhiteSpace(invoice.Number) && !invoice.IsDraftNumber;
            const int maxAttempts = 5;

            for (var attempt = 1; ; attempt++)
            {
                var number = keepNumber ? invoice.Number : await InvoiceNumberSequence.NextAsync(_db, tenantId);
                int claimed;

                try
                {
                    claimed = await _db.Invoices
                        .Where(i => i.Id == invoiceId && i.TenantId == tenantId && !i.IsDeleted &&
                                    i.Status == InvoiceStatus.Draft)
                        .ExecuteUpdateAsync(set => set
                            .SetProperty(i => i.Number, number)
                            .SetProperty(i => i.IssueDateUtc, today)
                            .SetProperty(i => i.DueDateUtc, newDue)
                            .SetProperty(i => i.Status, InvoiceStatus.Sent)
                            .SetProperty(i => i.IssuedAtUtc, (DateTime?)now)
                            .SetProperty(i => i.IssuedBy, me.FullName)
                            .SetProperty(i => i.UpdatedAtUtc, (DateTime?)now)
                            .SetProperty(i => i.UpdatedBy, me.FullName), ct);
                }
                catch (Exception ex) when (!keepNumber && InvoiceNumberSequence.IsDuplicateKey(ex) && attempt < maxAttempts)
                {
                    _logger.LogWarning("Invoice number {Number} already taken for tenant {TenantId}; retrying ({Attempt}/{Max})",
                        number, tenantId, attempt, maxAttempts);
                    continue;
                }

                if (claimed == 0)
                    throw new InvalidOperationException("This invoice has just been issued (or changed) by someone else. Refresh to see it.");

                invoice.Number = number;
                break;
            }

            await _audit.WriteAsync(
                AuditAction.InvoiceStatusChanged, AuditEntityType.Invoice, invoice.Id, tenantId,
                new { number = invoice.Number, from = "Draft", to = "Sent", via = "Issued", total = invoice.Total },
                ct);

            _logger.LogInformation("Invoice {Number} issued by {User}", invoice.Number, me.FullName);
            return invoice.Number;
        }

        /// <summary>
        /// May THIS user issue THIS draft?
        ///   from an accepted quote → yes (it was approved as a quote)
        ///   workspace admin        → yes
        ///   manual, within limits  → yes
        ///   manual, over limits    → only a manager of the deal owner's team
        /// </summary>
        public async Task<InvoiceIssueCheck> CheckAsync(Invoice invoice, CurrentUserContext me, CancellationToken ct = default)
        {
            var none = new List<string>();

            if (invoice.QuoteId.HasValue || me.IsTenantAdmin)
                return new InvoiceIssueCheck(true, null, none, none);

            var lines = invoice.Lines
                .Where(l => !l.IsDeleted)
                .Select(l => new PricedLine(l.Name, l.UnitPrice, l.Quantity, l.LineDiscount, l.ProductId))
                .ToList();

            var rules = await _approvals.EvaluateLinesAsync(invoice.TenantId, lines, invoice.Total, invoice.Currency ?? "", ct);
            if (!rules.RequiresApproval)
                return new InvoiceIssueCheck(true, null, none, none);

            // Nobody is excluded — a manager may issue their own team's invoice.
            // For a deal with no owner, the ISSUER's team is the one that counts.
            var approvers = await _approvals.ApproversForDealAsync(
                invoice.TenantId, invoice.DealId, excludeUserId: null, teamFallbackUserId: me.UserId, ct);
            var names = approvers.Select(a => a.IsAdmin ? $"{a.Name} (admin)" : a.Name).ToList();

            if (approvers.Any(a => a.UserId == me.UserId))
                return new InvoiceIssueCheck(true, null, rules.Reasons, names);

            var who = names.Count == 0 ? "a workspace admin" : string.Join(" or ", names.Take(3));
            return new InvoiceIssueCheck(false,
                $"This invoice is over your workspace's limits, so {who} needs to issue it. " + string.Join(" ", rules.Reasons),
                rules.Reasons, names);
        }
    }
    #endregion

    #region Update (draft only)
    public class UpdateInvoiceCommand : ICommandHandler
    {
        public Guid TenantId { get; set; }
        public Guid Id { get; set; }
        public DateTime? DueDateUtc { get; set; }
        public string? Notes { get; set; }
        public List<CreateInvoiceLineDto> Lines { get; set; } = new();
        public string UpdatedBy { get; set; } = string.Empty;
    }

    public class UpdateInvoiceHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<UpdateInvoiceHandler> _logger;
        private readonly IAuditService _audit;

        public UpdateInvoiceHandler(FlowDbContext context, ILogger<UpdateInvoiceHandler> logger, IAuditService audit)
        {
            _context = context;
            _logger = logger;
            _audit = audit;
        }

        public async Task<InvoiceDto> Handle(UpdateInvoiceCommand request, CancellationToken cancellationToken)
        {
            var invoice = await _context.Invoices
                .Include(i => i.Lines)
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.Id == request.Id && i.TenantId == request.TenantId && !i.IsDeleted, cancellationToken)
                ?? throw new KeyNotFoundException($"Invoice {request.Id} not found");

            if (invoice.Status != InvoiceStatus.Draft)
                throw new InvalidOperationException(
                    "An issued invoice can't be changed. Void it and issue a new one if something is wrong.");

            var now = DateTime.UtcNow;

            if (request.DueDateUtc.HasValue)
                invoice.DueDateUtc = request.DueDateUtc.Value;

            invoice.Notes = request.Notes;
            invoice.UpdatedAtUtc = now;
            invoice.UpdatedBy = request.UpdatedBy;

            var linesChanged = false;

            // Lines of an invoice raised from a quote ARE the quote's — the
            // quote may have been approved at exactly these prices. Only due
            // date and notes can change here.
            if (!invoice.QuoteId.HasValue)
            {
                if (request.Lines == null || request.Lines.Count == 0)
                    throw new InvalidOperationException("Add at least one item to the invoice.");

                // 055. FIRST, before anything reads LineDiscount — a
                // percentage replaces whatever amount came with it, so the
                // validation, the totals and the stored row all use the same
                // number. LineDiscounts.Resolve is the same rule the quote
                // handler uses.
                foreach (var l in request.Lines)
                {
                    var (amount, percent) = LineDiscounts.Resolve(
                        l.Name, l.UnitPrice, l.Quantity, l.LineDiscount, l.DiscountPercent);
                    l.LineDiscount    = amount;
                    l.DiscountPercent = percent;
                }

                QuoteLineChecks.Validate(request.Lines.Select(l => (l.Name ?? "", l.UnitPrice, l.Quantity, l.LineDiscount, l.TaxRate)));

                // 052 — same single lookup as create.
                var units = await LineUnits.LookupAsync(
                    _context, request.TenantId, request.Lines.Select(l => l.ProductId));

                // 067 — likewise. Note that this path REPLACES every line
                // rather than editing them in place (see the soft-delete
                // loop just below), so there is no "existing" code to fall
                // back to: whatever the caller sends is what the invoice
                // ends up with, and a caller that sends null gets the
                // product's code. That is the same behaviour the unit has
                // had here since 052.
                var taxCodes = await LineTaxCodes.LookupAsync(
                    _context, request.TenantId, request.Lines.Select(l => l.ProductId));

                foreach (var existingLine in invoice.Lines.Where(l => !l.IsDeleted))
                {
                    existingLine.IsDeleted = true;
                    existingLine.UpdatedAtUtc = now;
                    existingLine.UpdatedBy = request.UpdatedBy;
                }

                foreach (var lineDto in request.Lines)
                {
                    var lineNet = (lineDto.UnitPrice * lineDto.Quantity) - lineDto.LineDiscount;
                    var newLine = new InvoiceLine
                    {
                        Id = Guid.NewGuid(),
                        TenantId = request.TenantId,
                        InvoiceId = invoice.Id,
                        ProductId = lineDto.ProductId,
                        Name = (lineDto.Name ?? "").Trim(),
                        Description = lineDto.Description,
                        UnitPrice = lineDto.UnitPrice,
                        Quantity = lineDto.Quantity,
                        UnitOfMeasure = LineUnits.Resolve(lineDto.UnitOfMeasure, lineDto.ProductId, units),  // 052
                        LineDiscount = lineDto.LineDiscount,
                        DiscountPercent = lineDto.DiscountPercent,     // 055
                        TaxRate = lineDto.TaxRate,
                        TaxCode = LineTaxCodes.Resolve(lineDto.TaxCode, lineDto.ProductId, taxCodes),  // 067
                        Amount = lineNet * (1 + lineDto.TaxRate),
                        CreatedAtUtc = now,
                        CreatedBy = request.UpdatedBy,
                        IsDeleted = false
                    };

                    // Added explicitly: a new child with its Guid key already set
                    // would otherwise be tracked as MODIFIED (an UPDATE of a row
                    // that doesn't exist → concurrency exception).
                    invoice.Lines.Add(newLine);
                    _context.InvoiceLines.Add(newLine);
                }

                var active = invoice.Lines.Where(l => !l.IsDeleted).ToList();
                invoice.Subtotal = active.Sum(l => l.UnitPrice * l.Quantity);
                invoice.DiscountTotal = active.Sum(l => l.LineDiscount);
                invoice.TaxTotal = active.Sum(l => ((l.UnitPrice * l.Quantity) - l.LineDiscount) * l.TaxRate);
                invoice.Total = invoice.Subtotal - invoice.DiscountTotal + invoice.TaxTotal;
                invoice.Amount = invoice.Total;
                invoice.Balance = invoice.Total - InvoiceStates.CapturedTotal(invoice.Payments);
                linesChanged = true;
            }

            await _context.SaveChangesAsync(cancellationToken);
            await _audit.WriteAsync(
                AuditAction.InvoiceUpdated, AuditEntityType.Invoice, invoice.Id, request.TenantId,
                new { number = invoice.Number, linesChanged, total = invoice.Total },
                cancellationToken);

            _logger.LogInformation("Invoice {Number} updated", invoice.Number);

            var dto = CreateInvoiceHandler.MapToDto(new Invoice
            {
                Id = invoice.Id, TenantId = invoice.TenantId, QuoteId = invoice.QuoteId, DealId = invoice.DealId,
                Number = invoice.Number, IssueDateUtc = invoice.IssueDateUtc, DueDateUtc = invoice.DueDateUtc,
                Currency = invoice.Currency, Status = invoice.Status, Subtotal = invoice.Subtotal,
                DiscountTotal = invoice.DiscountTotal, TaxTotal = invoice.TaxTotal, Balance = invoice.Balance,
                Notes = invoice.Notes, CreatedAtUtc = invoice.CreatedAtUtc, CreatedBy = invoice.CreatedBy,
                Lines = invoice.Lines.Where(l => !l.IsDeleted).ToList()
            });
            dto.UpdatedAtUtc = invoice.UpdatedAtUtc;
            dto.UpdatedBy = invoice.UpdatedBy;
            return dto;
        }
    }
    #endregion

    #region Update Invoice Status (only the moves a person may make)
    public class UpdateInvoiceStatusHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<UpdateInvoiceStatusHandler> _logger;
        private readonly IAuditService _audit;
        private readonly IssueInvoiceHandler _issue;

        public UpdateInvoiceStatusHandler(
            FlowDbContext db,
            ICurrentUserService currentUserService,
            ILogger<UpdateInvoiceStatusHandler> logger,
            IAuditService audit,
            IssueInvoiceHandler issue)
        {
            _db = db;
            _currentUserService = currentUserService;
            _logger = logger;
            _audit = audit;
            _issue = issue;
        }

        public async Task Handle(Guid tenantId, Guid invoiceId, UpdateInvoiceStatusDto dto)
        {
            var invoice = await _db.Invoices
                .FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId && !i.IsDeleted)
                ?? throw new KeyNotFoundException($"Invoice {invoiceId} not found");

            if (!Enum.TryParse<InvoiceStatus>(dto.Status, ignoreCase: true, out var newStatus))
                throw new ArgumentException($"Invalid status: {dto.Status}");

            if (newStatus == invoice.Status) return;

            // Draft → Sent is "Issue" — number, date, lock. Older pages still
            // post status=Sent; route them to the real thing.
            if (invoice.Status == InvoiceStatus.Draft && newStatus == InvoiceStatus.Sent)
            {
                await _issue.Handle(tenantId, invoiceId);
                return;
            }

            string? refusal = newStatus switch
            {
                InvoiceStatus.Cancelled => "Use Void — it asks for a reason and keeps the invoice on record.",
                InvoiceStatus.Draft => "An issued invoice can't go back to draft. Void it and issue a new one.",
                _ when invoice.Status == InvoiceStatus.Draft => "Issue the invoice first.",
                _ when invoice.Status == InvoiceStatus.Cancelled => "This invoice is void.",
                _ => InvoiceStatusRules.RejectionReason(invoice.Status, newStatus)
            };

            if (refusal != null)
            {
                await _audit.WriteAsync(
                    AuditAction.ActionRefused, AuditEntityType.Invoice, invoice.Id, tenantId,
                    new { attempted = newStatus.ToString(), current = invoice.Status.ToString(), reason = refusal });

                throw new InvalidOperationException(refusal);
            }

            var oldStatus = invoice.Status;
            var currentUser = await _currentUserService.GetCurrentUserAsync();

            invoice.Status = newStatus;
            invoice.UpdatedAtUtc = DateTime.UtcNow;
            invoice.UpdatedBy = string.IsNullOrWhiteSpace(dto.UpdatedBy) || dto.UpdatedBy == "User"
                ? currentUser.FullName
                : dto.UpdatedBy!;

            await _db.SaveChangesAsync();
            await _audit.WriteAsync(
               AuditAction.InvoiceStatusChanged, AuditEntityType.Invoice, invoice.Id, tenantId,
               new { number = invoice.Number, from = oldStatus.ToString(), to = newStatus.ToString() });
            _logger.LogInformation("Invoice {Number} status updated to {Status}", invoice.Number, newStatus);
        }
    }
    #endregion

    #region Void
    public class VoidInvoiceHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;
        private readonly IAuditService _audit;

        public VoidInvoiceHandler(FlowDbContext db, ICurrentUserService currentUser, IAuditService audit)
        {
            _db = db;
            _currentUser = currentUser;
            _audit = audit;
        }

        public async Task Handle(Guid tenantId, Guid invoiceId, string? reason, CancellationToken ct = default)
        {
            var invoice = await _db.Invoices
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId && !i.IsDeleted, ct)
                ?? throw new KeyNotFoundException($"Invoice {invoiceId} not found");

            if (invoice.Status == InvoiceStatus.Draft)
                throw new InvalidOperationException("A draft isn't a real invoice yet — delete it instead.");

            if (invoice.Status == InvoiceStatus.Cancelled)
                throw new InvalidOperationException("This invoice is already void.");

            if (InvoiceStates.CapturedTotal(invoice.Payments) > 0)
                throw new InvalidOperationException("This invoice has payments recorded. Reverse them first, then void it.");

            var clean = reason?.Trim();
            if (string.IsNullOrWhiteSpace(clean))
                throw new InvalidOperationException("Give a reason for voiding — it stays on the invoice's record.");
            if (clean.Length > 500) clean = clean[..500];

            var me = await _currentUser.GetCurrentUserAsync();
            var from = invoice.Status;

            invoice.Status = InvoiceStatus.Cancelled;
            invoice.VoidedAtUtc = DateTime.UtcNow;
            invoice.VoidedBy = me.FullName;
            invoice.VoidReason = clean;
            invoice.UpdatedAtUtc = DateTime.UtcNow;
            invoice.UpdatedBy = me.FullName;

            await _db.SaveChangesAsync(ct);

            await _audit.WriteAsync(
                AuditAction.InvoiceStatusChanged, AuditEntityType.Invoice, invoice.Id, tenantId,
                new { number = invoice.Number, from = from.ToString(), to = "Void", reason = clean, total = invoice.Total },
                ct);
        }
    }
    #endregion

    #region Add Payment
    public class AddPaymentHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUserService;
        private readonly TransitionDealStageHandler _dealTransition;
        private readonly ILogger<AddPaymentHandler> _logger;
        private readonly IAuditService _audit;

        public AddPaymentHandler(
            FlowDbContext db,
            ICurrentUserService currentUserService,
            TransitionDealStageHandler dealTransition,
            ILogger<AddPaymentHandler> logger,
            IAuditService audit)
        {
            _db = db;
            _currentUserService = currentUserService;
            _dealTransition = dealTransition;
            _logger = logger;
            _audit = audit;
        }

        public async Task<PaymentDto> Handle(CreatePaymentDto dto)
        {
            _logger.LogInformation("Recording payment for invoice {InvoiceId}", dto.InvoiceId);

            var invoice = await _db.Invoices
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.Id == dto.InvoiceId && i.TenantId == dto.TenantId && !i.IsDeleted)
                ?? throw new KeyNotFoundException($"Invoice {dto.InvoiceId} not found");

            if (invoice.Status == InvoiceStatus.Draft)
                throw new InvalidOperationException("Issue the invoice before recording a payment against it.");

            if (invoice.Status == InvoiceStatus.Cancelled)
                throw new InvalidOperationException("This invoice is void — payments can't be recorded against it.");

            if (dto.Amount <= 0)
                throw new InvalidOperationException("The payment amount must be more than zero.");

            var paidSoFar = InvoiceStates.CapturedTotal(invoice.Payments);
            var owed = invoice.Total - paidSoFar;

            if (dto.Amount > owed)
                throw new InvalidOperationException(
                    $"Payment amount ({dto.Amount:N2}) is more than what's owed ({owed:N2}).");

            var currentUser = await _currentUserService.GetCurrentUserAsync();

            var payment = new Payment
            {
                Id = Guid.NewGuid(),
                TenantId = dto.TenantId,
                InvoiceId = dto.InvoiceId,
                Amount = dto.Amount,
                Currency = string.IsNullOrWhiteSpace(dto.Currency) ? invoice.Currency : dto.Currency,
                Method = dto.Method,
                Status = PaymentStatusNames.Captured,
                PaidAtUtc = dto.PaidAtUtc,
                Notes = dto.Notes,
                ProviderTxnId = dto.ProviderTxnId,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = string.IsNullOrWhiteSpace(dto.CreatedBy) ? currentUser.FullName : dto.CreatedBy!,
                IsDeleted = false
            };

            invoice.Payments.Add(payment);
            _db.Entry(payment).State = EntityState.Added;

            // Balance and status from the ledger — never by subtraction alone.
            var paid = paidSoFar + dto.Amount;
            invoice.Balance = Math.Max(0, invoice.Total - paid);
            invoice.Status = InvoiceStatusRules.DeriveFromPayments(invoice.Status, invoice.Total, paid);
            invoice.UpdatedAtUtc = DateTime.UtcNow;
            invoice.UpdatedBy = payment.CreatedBy;

            await _db.SaveChangesAsync();

            if (invoice.Status == InvoiceStatus.Paid)
                await TryTransitionDealToWonAsync(invoice, dto.TenantId, payment.CreatedBy);

            await _audit.WriteCriticalAsync(
                AuditAction.PaymentRecorded, AuditEntityType.Payment, payment.Id, dto.TenantId,
                new
                {
                    invoiceNumber = invoice.Number,
                    amount = payment.Amount,
                    method = payment.Method,
                    invoiceStatus = invoice.Status.ToString()
                });

            _logger.LogInformation("Payment of {Amount} recorded for invoice {Number}. Balance now {Balance}",
                dto.Amount, invoice.Number, invoice.Balance);

            return new PaymentDto
            {
                Id = payment.Id,
                InvoiceId = payment.InvoiceId,
                Amount = payment.Amount,
                Currency = payment.Currency,
                Method = payment.Method,
                Status = payment.Status,
                PaidAtUtc = payment.PaidAtUtc,
                Notes = payment.Notes,
                ProviderTxnId = payment.ProviderTxnId,
                CreatedAtUtc = payment.CreatedAtUtc,
                CreatedBy = payment.CreatedBy
            };
        }

        // Deal → Won when the invoice is fully paid (never throws — log only).
        private async Task TryTransitionDealToWonAsync(Invoice invoice, Guid tenantId, string changedBy)
        {
            try
            {
                var dealId = invoice.DealId;

                if (dealId == null && invoice.QuoteId.HasValue)
                {
                    dealId = await _db.Quotes.AsNoTracking()
                        .Where(q => q.Id == invoice.QuoteId.Value && !q.IsDeleted)
                        .Select(q => (Guid?)q.DealId)
                        .FirstOrDefaultAsync();
                }

                if (dealId == null)
                {
                    _logger.LogWarning("Invoice {InvoiceId} has no linked Deal — skipping Won transition", invoice.Id);
                    return;
                }

                await _dealTransition.HandleAsync(
                    tenantId.ToString(), dealId.Value, toStage: "ClosedWon", probability: 100, changedBy: changedBy);
            }
            catch (Exception ex)
            {
                // Payment is already saved — don't roll back over a stage-history failure.
                _logger.LogError(ex, "Failed to transition Deal to Won for invoice {InvoiceId} — payment still recorded", invoice.Id);
            }
        }
    }
    #endregion

    #region Reverse Payment
    public class ReversePaymentHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;
        private readonly IAuditService _audit;

        public ReversePaymentHandler(FlowDbContext db, ICurrentUserService currentUser, IAuditService audit)
        {
            _db = db;
            _currentUser = currentUser;
            _audit = audit;
        }

        /// <summary>
        /// Takes a mistaken payment back out. It stays in the history as
        /// "Reversed" with who, when and why; the balance and status are
        /// worked out again from what's left. A deal already moved to Won
        /// is NOT moved back — do that by hand if the sale really fell
        /// through.
        /// </summary>
        public async Task Handle(Guid tenantId, Guid invoiceId, Guid paymentId, string? reason, CancellationToken ct = default)
        {
            var invoice = await _db.Invoices
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId && !i.IsDeleted, ct)
                ?? throw new KeyNotFoundException($"Invoice {invoiceId} not found");

            var payment = invoice.Payments.FirstOrDefault(p => p.Id == paymentId && !p.IsDeleted)
                ?? throw new KeyNotFoundException($"Payment {paymentId} not found");

            if (payment.Status != PaymentStatusNames.Captured)
                throw new InvalidOperationException("Only a recorded (captured) payment can be reversed.");

            var clean = reason?.Trim();
            if (string.IsNullOrWhiteSpace(clean))
                throw new InvalidOperationException("Give a reason for reversing the payment.");
            if (clean.Length > 500) clean = clean[..500];

            var me = await _currentUser.GetCurrentUserAsync();

            payment.Status = PaymentStatusNames.Reversed;
            payment.ReversedAtUtc = DateTime.UtcNow;
            payment.ReversedBy = me.FullName;
            payment.ReversalReason = clean;
            payment.UpdatedAtUtc = DateTime.UtcNow;
            payment.UpdatedBy = me.FullName;

            var paid = InvoiceStates.CapturedTotal(invoice.Payments);
            invoice.Balance = Math.Max(0, invoice.Total - paid);
            invoice.Status = InvoiceStatusRules.DeriveFromPayments(invoice.Status, invoice.Total, paid);
            invoice.UpdatedAtUtc = DateTime.UtcNow;
            invoice.UpdatedBy = me.FullName;

            await _db.SaveChangesAsync(ct);

            await _audit.WriteCriticalAsync(
                "PaymentReversed", AuditEntityType.Payment, payment.Id, tenantId,
                new
                {
                    invoiceNumber = invoice.Number,
                    amount = payment.Amount,
                    reason = clean,
                    invoiceStatus = invoice.Status.ToString(),
                    balance = invoice.Balance
                });
        }
    }
    #endregion

    #region Delete Invoice (drafts only)
    public class DeleteInvoiceHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<DeleteInvoiceHandler> _logger;
        private readonly IAuditService _audit;

        public DeleteInvoiceHandler(
            FlowDbContext db,
            ICurrentUserService currentUserService,
            ILogger<DeleteInvoiceHandler> logger,
            IAuditService audit)
        {
            _db = db;
            _currentUserService = currentUserService;
            _logger = logger;
            _audit = audit;
        }

        public async Task Handle(Guid tenantId, Guid invoiceId, string? deletedBy = null)
        {
            var invoice = await _db.Invoices
                .Include(i => i.Lines)
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId && !i.IsDeleted)
                ?? throw new KeyNotFoundException($"Invoice {invoiceId} not found");

            // An issued invoice is a tax document — it is voided, not deleted.
            if (invoice.Status != InvoiceStatus.Draft)
                throw new InvalidOperationException(invoice.Status == InvoiceStatus.Cancelled
                    ? "A void invoice stays on record and can't be deleted."
                    : "An issued invoice can't be deleted. Void it instead (it asks for a reason).");

            // Drafts created before 018 could have payments against them —
            // deleting would orphan real money.
            if (InvoiceStates.CapturedTotal(invoice.Payments) > 0)
                throw new InvalidOperationException("This draft has payments recorded. Reverse them before deleting it.");

            var currentUser = await _currentUserService.GetCurrentUserAsync();
            var actor = string.IsNullOrWhiteSpace(deletedBy) ? currentUser.FullName : deletedBy!;
            var now = DateTime.UtcNow;

            invoice.IsDeleted = true;
            invoice.UpdatedAtUtc = now;
            invoice.UpdatedBy = actor;

            foreach (var line in invoice.Lines)
            {
                line.IsDeleted = true;
                line.UpdatedAtUtc = now;
                line.UpdatedBy = actor;
            }

            await _db.SaveChangesAsync();
            await _audit.WriteAsync(
               AuditAction.InvoiceDeleted, AuditEntityType.Invoice, invoiceId, tenantId,
               new { number = invoice.Number, status = invoice.Status.ToString(), total = invoice.Total });

            _logger.LogInformation("Draft invoice {Number} deleted", invoice.Number);
        }
    }
    #endregion

    #region Workflow state (what the pages show and allow)
    public class GetInvoiceWorkflowHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;
        private readonly IssueInvoiceHandler _issue;

        public GetInvoiceWorkflowHandler(FlowDbContext db, ICurrentUserService currentUser, IssueInvoiceHandler issue)
        {
            _db = db;
            _currentUser = currentUser;
            _issue = issue;
        }

        public async Task<InvoiceWorkflowDto> Handle(Guid tenantId, Guid invoiceId, CancellationToken ct = default)
        {
            var invoice = await _db.Invoices.AsNoTracking()
                .Include(i => i.Lines)
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId && !i.IsDeleted, ct)
                ?? throw new KeyNotFoundException($"Invoice {invoiceId} not found");

            var me = await _currentUser.GetCurrentUserAsync();

            var isDraft = invoice.Status == InvoiceStatus.Draft;
            var isVoid = invoice.Status == InvoiceStatus.Cancelled;
            var captured = InvoiceStates.CapturedTotal(invoice.Payments);

            var canIssue = false;
            string? issueBlocked = null;
            var ruleReasons = new List<string>();
            var approverNames = new List<string>();

            if (isDraft)
            {
                var check = await _issue.CheckAsync(invoice, me, ct);
                canIssue = check.Allowed && invoice.Lines.Any(l => !l.IsDeleted);
                issueBlocked = !invoice.Lines.Any(l => !l.IsDeleted)
                    ? "Add at least one item before issuing."
                    : check.BlockedReason;
                ruleReasons = check.RuleReasons;
                approverNames = check.ApproverNames;
            }

            var canVoid = !isDraft && !isVoid && captured <= 0;
            string? voidBlocked = !isDraft && !isVoid && captured > 0
                ? "Reverse the recorded payments before voiding."
                : null;

            return new InvoiceWorkflowDto(
                invoice.Id,
                invoice.Status.ToString(),
                isDraft,
                isVoid,
                invoice.QuoteId.HasValue,
                invoice.IssuedAtUtc,
                invoice.IssuedBy,
                invoice.VoidedAtUtc,
                invoice.VoidedBy,
                invoice.VoidReason,
                CanEdit: isDraft,
                CanEditLines: isDraft && !invoice.QuoteId.HasValue,
                CanIssue: canIssue,
                IssueBlockedReason: issueBlocked,
                IssueRuleReasons: ruleReasons,
                IssueApproverNames: approverNames,
                CanVoid: canVoid,
                VoidBlockedReason: voidBlocked,
                CanDelete: isDraft,
                CanRecordPayment: InvoiceStates.IsIssued(invoice.Status) && invoice.Total - captured > 0,
                ReversiblePaymentIds: isVoid
                    ? new List<Guid>()
                    : invoice.Payments
                        .Where(p => !p.IsDeleted && p.Status == PaymentStatusNames.Captured)
                        .Select(p => p.Id)
                        .ToList());
        }
    }
    #endregion

    #region Invoice access (record visibility)
    /// <summary>
    /// "Can the current user see this invoice?" — for InvoicesController.
    /// An invoice follows its deal, directly (Invoice.DealId) or through the
    /// quote it was raised from. One linked to neither is visible only to
    /// users who see all deals.
    /// </summary>
    public class InvoiceAccessHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly IRecordScopeService _scope;

        public InvoiceAccessHandler(FlowDbContext db, IRecordScopeService scope)
        {
            _db = db;
            _scope = scope;
        }

        public async Task<bool> CanSeeInvoiceAsync(Guid tenantId, Guid invoiceId, CancellationToken ct = default)
        {
            var dealAccess = await _scope.GetAsync(RecordModules.Deals, ct);

            return await _db.Invoices.AsNoTracking()
                .Where(i => i.Id == invoiceId && i.TenantId == tenantId && !i.IsDeleted)
                .WithVisibleDeal(_db, dealAccess)
                .AnyAsync(ct);
        }
    }
    #endregion
}
