using DocumentFormat.OpenXml.Presentation;
using MerkaiTrial.Application.Commands.Deals;
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
    
    

    #region Create Invoice
    public class CreateInvoiceHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<CreateInvoiceHandler> _logger;
        private readonly IAuditService _audit;
        public CreateInvoiceHandler(
            FlowDbContext db,
            ICurrentUserService currentUserService,
            ILogger<CreateInvoiceHandler> logger,
            IAuditService audit)    
        {
            _db = db;
            _currentUserService = currentUserService;
            _logger = logger;
            _audit = audit;
        }

        public async Task<InvoiceDto> Handle(CreateInvoiceDto dto)
        {
            _logger.LogInformation("Creating invoice for tenant {TenantId}", dto.TenantId);

            var currentUser = await _currentUserService.GetCurrentUserAsync();

            // Generate invoice number (per-tenant sequenced)
            var number = await GenerateInvoiceNumberAsync(dto.TenantId);

            // Totals
            var subtotal = dto.Lines.Sum(l => (l.UnitPrice * l.Quantity) - l.LineDiscount);
            var taxTotal = dto.Lines.Sum(l => ((l.UnitPrice * l.Quantity) - l.LineDiscount) * l.TaxRate);
            var discountTotal = dto.Lines.Sum(l => l.LineDiscount);
            var grandTotal = subtotal + taxTotal;

            var now = DateTime.UtcNow;

            var invoice = new Invoice
            {
                Id = Guid.NewGuid(),
                TenantId = dto.TenantId,
                QuoteId = dto.QuoteId,
                DealId = dto.DealId,
                Number = number,
                IssueDateUtc = dto.IssueDateUtc,
                DueDateUtc = dto.DueDateUtc,
                Currency = dto.Currency,
                Status = dto.SendImmediately ? InvoiceStatus.Sent : InvoiceStatus.Draft,
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

            // Lines
            foreach (var lineDto in dto.Lines)
            {
                var line = new InvoiceLine
                {
                    Id = Guid.NewGuid(),
                    TenantId = dto.TenantId,
                    InvoiceId = invoice.Id,
                    ProductId = lineDto.ProductId,
                    Name = lineDto.Name,
                    Description = lineDto.Description,
                    UnitPrice = lineDto.UnitPrice,
                    Quantity = lineDto.Quantity,
                    LineDiscount = lineDto.LineDiscount,
                    TaxRate = lineDto.TaxRate,
                    Amount = ((lineDto.UnitPrice * lineDto.Quantity) - lineDto.LineDiscount) * (1 + lineDto.TaxRate),
                    CreatedAtUtc = now,
                    CreatedBy = invoice.CreatedBy,
                    IsDeleted = false
                };
                invoice.Lines.Add(line);
            }

            _db.Invoices.Add(invoice);
            await SaveWithNumberRetryAsync(invoice, dto.TenantId);
            await _audit.WriteAsync(
                AuditAction.InvoiceCreated, AuditEntityType.Invoice, invoice.Id, dto.TenantId,
                new { number = invoice.Number, total = invoice.Total, quoteId = invoice.QuoteId });


            _logger.LogInformation("Invoice {Number} created successfully", invoice.Number);

            var saved = await _db.Invoices
                .AsNoTracking()
                .Where(i => i.Id == invoice.Id)
                .Include(i => i.Lines)
                .Include(i => i.Quote)
                .Include(i => i.Deal)
                .FirstAsync();

            return MapToDto(saved);
        }

        // -- Invoice numbering ----------------------------------------
        //
        // Returns the next unused INV-nnnn for the tenant.
        //
        // Two things here are deliberate and must not be "simplified":
        //
        //  1. It takes the MAX of the parsed sequence, NOT the number of the
        //     most recently created row. Seed data (and any back-dated import)
        //     can have CreatedAtUtc ordering that does not match number
        //     ordering. The identical bug in the quote generator produced a
        //     hard duplicate-key failure on Nexora.
        //
        //  2. It is NOT filtered by !IsDeleted. The unique index covers
        //     soft-deleted rows, so their numbers can never be reused - the
        //     previous !IsDeleted filter meant deleting the newest invoice
        //     made the next create collide with it.
        //
        private const string InvoiceNumberPrefix = "INV-";

        private async Task<string> GenerateInvoiceNumberAsync(Guid tenantId)
        {
            var numbers = await _db.Invoices
                .Where(i => i.TenantId == tenantId
                            && i.Number != null
                            && i.Number.StartsWith(InvoiceNumberPrefix))
                .Select(i => i.Number)
                .ToListAsync();

            var max = 0;
            foreach (var number in numbers)
            {
                var tail = number.Substring(InvoiceNumberPrefix.Length);
                if (int.TryParse(tail, out var value) && value > max)
                    max = value;
            }

            return $"{InvoiceNumberPrefix}{(max + 1):D4}";
        }

        // Max-of-existing is correct but not atomic: two concurrent creates can
        // read the same max. The unique index is the real guard - catch its
        // violation, re-read, and retry rather than surfacing a 500.
        private async Task SaveWithNumberRetryAsync(Invoice invoice, Guid tenantId)
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
                    var collided = invoice.Number;
                    invoice.Number = await GenerateInvoiceNumberAsync(tenantId);

                    _logger.LogWarning(
                        "Invoice number {Collided} already exists for tenant {TenantId}; " +
                        "retrying as {NextNumber} (attempt {Attempt}/{MaxAttempts})",
                        collided, tenantId, invoice.Number, attempt, maxAttempts);
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

        private static InvoiceDto MapToDto(Invoice invoice)
        {
            return new InvoiceDto
            {
                Id = invoice.Id,
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
                Notes = invoice.Notes,
                Lines = invoice.Lines.Select(l => new InvoiceLineDto
                {
                    Id = l.Id,
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
    }
    #endregion

    #region Create Invoice From Quote
    public class CreateInvoiceFromQuoteHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly CreateInvoiceHandler _createInvoice;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<CreateInvoiceFromQuoteHandler> _logger;

        public CreateInvoiceFromQuoteHandler(
            FlowDbContext db,
            CreateInvoiceHandler createInvoice,
            ICurrentUserService currentUserService,
            ILogger<CreateInvoiceFromQuoteHandler> logger)
        {
            _db = db;
            _createInvoice = createInvoice;
            _currentUserService = currentUserService;
            _logger = logger;
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

            if (quote.Status != QuoteStatus.Accepted)
                throw new InvalidOperationException("Can only create invoice from accepted quotes");

            var existingInvoice = await _db.Invoices
                .FirstOrDefaultAsync(i => i.QuoteId == dto.QuoteId && !i.IsDeleted);

            if (existingInvoice != null)
                throw new InvalidOperationException($"Invoice {existingInvoice.Number} already exists for this quote");

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
                Lines = quote.Items.Select(item => new CreateInvoiceLineDto
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
        public UpdateInvoiceHandler(
            FlowDbContext context,
            ILogger<UpdateInvoiceHandler> logger,
            IAuditService audit)    
        {
            _context = context;
            _logger = logger;
            _audit = audit;
        }

        public async Task<InvoiceDto> Handle(
            UpdateInvoiceCommand request,
            CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Updating invoice {Id}", request.Id);

                // Load invoice with lines and payments
                var invoice = await _context.Invoices
                    .Include(i => i.Lines)
                    .Include(i => i.Payments)
                    .FirstOrDefaultAsync(i => i.Id == request.Id && i.TenantId == request.TenantId && !i.IsDeleted, cancellationToken);

                if (invoice == null)
                {
                    throw new KeyNotFoundException($"Invoice {request.Id} not found");
                }

                // ✅ Only allow editing Draft invoices
                if (invoice.Status != InvoiceStatus.Draft)
                {
                    throw new InvalidOperationException("Only draft invoices can be edited. Create a new invoice instead.");
                }

                // Update basic fields
                if (request.DueDateUtc.HasValue)
                {
                    invoice.DueDateUtc = request.DueDateUtc.Value;
                }

                invoice.Notes = request.Notes;
                invoice.UpdatedAtUtc = DateTime.UtcNow;
                invoice.UpdatedBy = request.UpdatedBy;

                // ✅ Soft delete all existing lines
                foreach (var existingLine in invoice.Lines.Where(l => !l.IsDeleted))
                {
                    existingLine.IsDeleted = true;
                    existingLine.UpdatedAtUtc = DateTime.UtcNow;
                    existingLine.UpdatedBy = request.UpdatedBy;
                }

                // ✅ Add new lines from request
                decimal subtotal = 0;
                decimal discountTotal = 0;
                decimal taxTotal = 0;

                var newLines = new List<InvoiceLine>();

                foreach (var lineDto in request.Lines)
                {
                    var lineSubtotal = lineDto.UnitPrice * lineDto.Quantity;
                    var lineAfterDiscount = lineSubtotal - lineDto.LineDiscount;
                    var lineTax = lineAfterDiscount * lineDto.TaxRate;

                    subtotal += lineSubtotal;
                    discountTotal += lineDto.LineDiscount;
                    taxTotal += lineTax;

                    var line = new InvoiceLine
                    {
                        Id = Guid.NewGuid(),
                        TenantId = request.TenantId,
                        InvoiceId = invoice.Id,
                        ProductId = lineDto.ProductId,
                        Name = lineDto.Name,
                        Description = lineDto.Description,
                        UnitPrice = lineDto.UnitPrice,
                        Quantity = lineDto.Quantity,
                        LineDiscount = lineDto.LineDiscount,
                        TaxRate = lineDto.TaxRate,
                        Amount = lineSubtotal,
                        CreatedAtUtc = DateTime.UtcNow,
                        CreatedBy = request.UpdatedBy,
                        IsDeleted = false
                    };

                    invoice.Lines.Add(line);
                    newLines.Add(line);
                }

                // ✅ Recalculate totals
                var grandTotal = subtotal + taxTotal - discountTotal;

                invoice.Subtotal = subtotal;
                invoice.TaxTotal = taxTotal;
                invoice.DiscountTotal = discountTotal;
                invoice.Total = grandTotal;
                invoice.Amount = grandTotal;

                // ✅ Only update balance if no payments made yet
                if (invoice.TotalPaid == 0)
                {
                    invoice.Balance = grandTotal;
                }

                await _context.SaveChangesAsync(cancellationToken);
                await _audit.WriteAsync(
                        AuditAction.InvoiceUpdated, AuditEntityType.Invoice, invoice.Id, request.TenantId,
                        new { number = invoice.Number, lineCount = newLines.Count, total = grandTotal },
                        cancellationToken);

                _logger.LogInformation("✅ Invoice {Number} updated successfully", invoice.Number);

                // ✅ Return updated DTO
                return new InvoiceDto
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
                    GrandTotal = grandTotal,
                    Balance = invoice.Balance,
                    TotalPaid = invoice.TotalPaid,
                    Notes = invoice.Notes,
                    Lines = newLines.Select(l => new InvoiceLineDto
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
                    Payments = invoice.Payments.Where(p => !p.IsDeleted).Select(p => new PaymentDto
                    {
                        Id = p.Id,
                        InvoiceId = p.InvoiceId,
                        Amount = p.Amount,
                        Currency = p.Currency,
                        Method = p.Method,
                        Status = p.Status,
                        PaidAtUtc = p.PaidAtUtc,
                        Notes = p.Notes,
                        CreatedAtUtc = p.CreatedAtUtc,
                        CreatedBy = p.CreatedBy
                    }).ToList(),
                    CreatedAtUtc = invoice.CreatedAtUtc,
                    CreatedBy = invoice.CreatedBy,
                    UpdatedAtUtc = invoice.UpdatedAtUtc,
                    UpdatedBy = invoice.UpdatedBy
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating invoice {Id}", request.Id);
                throw;
            }
        }
    }
    #region Update Invoice Status
    public class UpdateInvoiceStatusHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<UpdateInvoiceStatusHandler> _logger;
        private readonly IAuditService _audit;
        public UpdateInvoiceStatusHandler(
            FlowDbContext db,
            ICurrentUserService currentUserService,
            ILogger<UpdateInvoiceStatusHandler> logger,
            IAuditService audit)
        {
            _db = db;
            _currentUserService = currentUserService;
            _logger = logger;
            _audit = audit;
        }

        public async Task Handle(Guid tenantId, Guid invoiceId, UpdateInvoiceStatusDto dto)
        {
            var invoice = await _db.Invoices
                .FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId && !i.IsDeleted);

            if (invoice == null)
                throw new KeyNotFoundException($"Invoice {invoiceId} not found");

            if (!Enum.TryParse<InvoiceStatus>(dto.Status, out var newStatus))
                throw new ArgumentException($"Invalid status: {dto.Status}");

            var rejection = InvoiceStatusRules.RejectionReason(invoice.Status, newStatus);
            if (rejection != null)
            {
                await _audit.WriteAsync(
                    AuditAction.ActionRefused, AuditEntityType.Invoice, invoice.Id, tenantId,
                    new { attempted = newStatus.ToString(), current = invoice.Status.ToString(), reason = rejection });

                throw new InvalidOperationException(rejection);
            }

            var oldStatus = invoice.Status;

            var currentUser = await _currentUserService.GetCurrentUserAsync();

            invoice.Status = newStatus;
            invoice.UpdatedAtUtc = DateTime.UtcNow;
            invoice.UpdatedBy = string.IsNullOrWhiteSpace(dto.UpdatedBy) ? currentUser.FullName : dto.UpdatedBy!;

            await _db.SaveChangesAsync();
            await _audit.WriteAsync(
               AuditAction.InvoiceStatusChanged, AuditEntityType.Invoice, invoice.Id, tenantId,
               new { number = invoice.Number, from = oldStatus.ToString(), to = newStatus.ToString() });
            _logger.LogInformation("Invoice {Number} status updated to {Status}", invoice.Number, newStatus);
        }
    }
    #endregion

    #region Add Payment
    public class AddPaymentHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUserService;
        private readonly TransitionDealStageHandler _dealTransition;     // ← NEW
        private readonly ILogger<AddPaymentHandler> _logger;
        private readonly IAuditService _audit;
        public AddPaymentHandler(
            FlowDbContext db,
            ICurrentUserService currentUserService,
            TransitionDealStageHandler dealTransition,                    // ← NEW
            ILogger<AddPaymentHandler> logger,
            IAuditService audit)        
        {
            _db = db;
            _currentUserService = currentUserService;
            _dealTransition = dealTransition;                               // ← NEW
            _logger = logger;
            _audit = audit;
        }

        public async Task<PaymentDto> Handle(CreatePaymentDto dto)
        {
            _logger.LogInformation("Recording payment for invoice {InvoiceId}", dto.InvoiceId);

            var invoice = await _db.Invoices
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.Id == dto.InvoiceId && i.TenantId == dto.TenantId && !i.IsDeleted);

            if (invoice == null)
                throw new KeyNotFoundException($"Invoice {dto.InvoiceId} not found");

            if (dto.Amount > invoice.Balance)
                throw new InvalidOperationException(
                    $"Payment amount ({dto.Amount}) exceeds invoice balance ({invoice.Balance})");

            var currentUser = await _currentUserService.GetCurrentUserAsync();

            var payment = new Payment
            {
                Id = Guid.NewGuid(),
                TenantId = dto.TenantId,
                InvoiceId = dto.InvoiceId,
                Amount = dto.Amount,
                Currency = dto.Currency,
                Method = dto.Method,
                Status = "Captured",
                PaidAtUtc = dto.PaidAtUtc,
                Notes = dto.Notes,
                ProviderTxnId = dto.ProviderTxnId,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = string.IsNullOrWhiteSpace(dto.CreatedBy) ? currentUser.FullName : dto.CreatedBy!,
                IsDeleted = false
            };

            invoice.Payments.Add(payment);
            _db.Entry(payment).State = EntityState.Added;

            // ── Update invoice balance & status ───────────────────────────
            invoice.Balance -= dto.Amount;

            if (invoice.Balance <= 0)
            {
                invoice.Status = InvoiceStatus.Paid;
                invoice.Balance = 0; // guard against floating-point negative

                // ── ✅ FIX: auto-transition Deal to Won ───────────────────
                await TryTransitionDealToWonAsync(invoice, dto.TenantId, payment.CreatedBy);
            }
            else if (invoice.Status == InvoiceStatus.Draft
                  || invoice.Status == InvoiceStatus.Sent
                  || invoice.Status == InvoiceStatus.Viewed)
            {
                invoice.Status = InvoiceStatus.PartiallyPaid;
            }

            invoice.UpdatedAtUtc = DateTime.UtcNow;
            invoice.UpdatedBy = payment.CreatedBy;

            await _db.SaveChangesAsync();
            await _audit.WriteCriticalAsync(
                AuditAction.PaymentRecorded, AuditEntityType.Payment, payment.Id, dto.TenantId,
                new
                {
                    invoiceNumber = invoice.Number,
                    amount = payment.Amount,
                    method = payment.Method,
                    paidAmount = dto.Amount,
                    invoiceStatus = invoice.Status.ToString()
                });
            _logger.LogInformation(
                "Payment of {Amount} recorded for invoice {Number}. New balance: {Balance}",
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

        // ── Resolve deal ID → transition to Won (never throws — log only) ─
        private async Task TryTransitionDealToWonAsync(
            Invoice invoice, Guid tenantId, string changedBy)
        {
            try
            {
                // Deal can be linked directly or via the Quote
                var dealId = invoice.DealId;

                if (dealId == null && invoice.QuoteId.HasValue)
                {
                    dealId = await _db.Quotes
                        .AsNoTracking()
                        .Where(q => q.Id == invoice.QuoteId.Value && !q.IsDeleted)
                        .Select(q => (Guid?)q.DealId)
                        .FirstOrDefaultAsync();
                }

                if (dealId == null)
                {
                    _logger.LogWarning(
                        "Invoice {InvoiceId} has no linked Deal — skipping Won transition",
                        invoice.Id);
                    return;
                }

                // TransitionDealStageHandler already guards against double-Won
                await _dealTransition.HandleAsync(
                    tenantId.ToString(),
                    dealId.Value,
                    toStage: "ClosedWon",
                    probability: 100,
                    changedBy: changedBy);

                _logger.LogInformation(
                    "Deal {DealId} transitioned to Won after invoice {InvoiceNumber} fully paid",
                    dealId.Value, invoice.Number);
            }
            catch (Exception ex)
            {
                // Payment is already saved — don't roll back over a stage-history failure
                _logger.LogError(ex,
                    "Failed to transition Deal to Won for invoice {InvoiceId} — payment still recorded",
                    invoice.Id);
            }
        }
    }
    #endregion

    #region Delete Invoice (soft)
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
                .FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId && !i.IsDeleted);

            if (invoice == null)
                throw new KeyNotFoundException($"Invoice {invoiceId} not found");

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

            foreach (var payment in invoice.Payments)
            {
                payment.IsDeleted = true;
                payment.UpdatedAtUtc = now;
                payment.UpdatedBy = actor;
            }

            await _db.SaveChangesAsync();
            await _audit.WriteAsync(
               AuditAction.InvoiceDeleted, AuditEntityType.Invoice, invoiceId, tenantId,
               new { number = invoice.Number, status = invoice.Status.ToString(), total = invoice.Total });

            _logger.LogInformation("Invoice {Number} soft-deleted", invoice.Number);
        }
    }
    #endregion
}
