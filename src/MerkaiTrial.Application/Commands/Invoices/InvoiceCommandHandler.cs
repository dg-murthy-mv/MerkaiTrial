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
// RECORD VISIBILITY (016/017) unchanged: invoices follow their deal;
// InvoiceAccessHandler at the bottom is what InvoicesController checks.
// =====================================================================

using MerkaiTrial.Application.Commands.Deals;
using MerkaiTrial.Application.Commands.Quotes;
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

            QuoteLineChecks.Validate(dto.Lines.Select(l => (l.Name ?? "", l.UnitPrice, l.Quantity, l.LineDiscount, l.TaxRate)));

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
                    LineDiscount = lineDto.LineDiscount,
                    TaxRate = lineDto.TaxRate,
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
                new { number = invoice.Number, total = invoice.Total, quoteId = invoice.QuoteId });

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
            Lines = invoice.Lines.Select(l => new InvoiceLineDto
            {
                Id = l.Id,
                ProductId = l.ProductId,
                Name = l.Name,
                Description = l.Description,
                UnitPrice = l.UnitPrice,
                Quantity = l.Quantity,
                LineDiscount = l.LineDiscount,
                TaxRate = l.TaxRate,
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

            // A VOID invoice doesn't block a new one — that is how a mistake
            // on an issued invoice is corrected.
            var existingInvoice = await _db.Invoices
                .FirstOrDefaultAsync(i => i.QuoteId == dto.QuoteId && i.TenantId == dto.TenantId &&
                                          !i.IsDeleted && i.Status != InvoiceStatus.Cancelled);

            if (existingInvoice != null)
                throw new InvalidOperationException(existingInvoice.Status == InvoiceStatus.Draft
                    ? "A draft invoice already exists for this quote — open it instead."
                    : $"Invoice {existingInvoice.Number} already exists for this quote. Void it first if it needs to be replaced.");

            var currentUser = await _currentUserService.GetCurrentUserAsync();

            var createDto = new CreateInvoiceDto
            {
                TenantId = dto.TenantId,
                QuoteId = dto.QuoteId,
                DealId = quote.DealId,
                IssueDateUtc = dto.IssueDateUtc,
                DueDateUtc = dto.DueDateUtc,
                Currency = quote.Currency,
                Notes = dto.Notes,
                SendImmediately = dto.SendImmediately,
                CreatedBy = string.IsNullOrWhiteSpace(dto.CreatedBy) ? currentUser.FullName : dto.CreatedBy,
                Lines = quote.Items.Where(i => !i.IsDeleted).Select(item => new CreateInvoiceLineDto
                {
                    ProductId = item.ProductId,
                    Name = item.Name,
                    Description = item.Description,
                    UnitPrice = item.UnitPrice,
                    Quantity = item.Quantity,
                    LineDiscount = item.LineDiscount,
                    TaxRate = item.TaxRate
                }).ToList()
            };

            var result = await _createInvoice.Handle(createDto);
            _logger.LogInformation("Invoice {Number} created from quote {QuoteNumber}", result.Number, quote.Number);
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

                QuoteLineChecks.Validate(request.Lines.Select(l => (l.Name ?? "", l.UnitPrice, l.Quantity, l.LineDiscount, l.TaxRate)));

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
                        LineDiscount = lineDto.LineDiscount,
                        TaxRate = lineDto.TaxRate,
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
