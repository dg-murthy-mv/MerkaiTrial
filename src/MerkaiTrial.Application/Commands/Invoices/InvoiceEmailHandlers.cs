// =====================================================================
// InvoiceEmailHandlers.cs
// Location: MerkaiTrial.Application/Commands/Invoices/InvoiceEmailHandlers.cs
//
// NEW FILE (085). Emailing an invoice, and the customer's page for it.
// Registered by the Scrutor scan (ICommandHandler).
//
//   GetInvoiceEmailPrefillHandler  what the "Email invoice" dialog starts from
//   SendInvoiceEmailHandler        PDF + link to the customer, in one save
//   GetInvoiceByTokenHandler       the customer's /i/{token} page   (anonymous)
//   GetInvoicePublicPdfHandler     its "Download PDF" button          (anonymous)
//
// WHICH INVOICES CAN BE EMAILED
//   Issued ones — Sent, Viewed, Partly paid, Paid, Overdue. Not a DRAFT
//   (no number yet, and the PDF says "DRAFT – not a tax invoice"), and not
//   a VOID one (it is not owed; emailing it reads as a demand). A paid
//   invoice can be emailed: "send me a copy for our records" is common.
//
// ONE SAVE
//   The public token, the stored PDF copy, the outbox row, its attachment
//   and the reply token are all ADDED, then saved together. If any part
//   fails, nothing is half-done — no token without an email, no email
//   without its PDF.
//
// THE ANONYMOUS READS (FlowDbContext item 18)
//   The customer has no tenant claim, so the token lookup is
//   IgnoreQueryFilters; every other read uses the TenantId of the invoice
//   found. The DTO carries what the page prints and nothing else — no
//   payment notes, no internal notes beyond the invoice's own Notes (which
//   are printed on the PDF anyway), no custom fields nobody chose to print.
// =====================================================================

using System.Net.Mail;
using System.Security.Cryptography;
using MerkaiTrial.Application.Common;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Inbound;
using MerkaiTrial.Application.Services.Notifications;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MerkaiTrial.Application.Commands.Invoices
{
    internal static class InvoiceEmailRules
    {
        public const int MaxCc = 5;
        public const int MaxMessage = 2000;

        /// <summary>Why this invoice cannot be emailed, or null when it can.</summary>
        public static string? BlockedReason(InvoiceStatus status) => status switch
        {
            InvoiceStatus.Draft     => "Issue the invoice first — a draft has no invoice number and isn't a tax invoice yet.",
            InvoiceStatus.Cancelled => "This invoice is void, so it can't be emailed.",
            _                       => null
        };

        /// <summary>A normalised address, or null when it is not one.</summary>
        public static string? CleanAddress(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var s = raw.Trim();
            if (s.Length > 254 || s.Contains(' ') || s.Contains(',') || s.Contains(';')) return null;

            try
            {
                var parsed = new MailAddress(s);
                if (!string.Equals(parsed.Address, s, StringComparison.OrdinalIgnoreCase)) return null;
                var at = s.LastIndexOf('@');
                if (at <= 0 || s.IndexOf('.', at) < 0 || s.EndsWith('.')) return null;
                return s;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// <summary>
        /// 087e: PublicLinkTokens.New(). The Base-64 trim this used came up
        /// short of 40 characters about 1 time in 20 and threw — the first
        /// email of an invoice failed at random.
        /// </summary>
        public static string NewPublicToken() => PublicLinkTokens.New();

        public static string PdfFileName(string number)
        {
            var safe = new string((number ?? "invoice").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
            return $"{(string.IsNullOrWhiteSpace(safe) ? "invoice" : safe)}.pdf";
        }

        /// <summary>The customer's absolute link, or null when no web address is known.</summary>
        public static string? PublicUrl(string? requestBaseUrl, string? configuredBaseUrl, string token)
        {
            static bool Absolute(string? u) => !string.IsNullOrWhiteSpace(u)
                && (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

            var baseUrl = Absolute(requestBaseUrl) ? requestBaseUrl!.Trim()
                        : Absolute(configuredBaseUrl) ? configuredBaseUrl!.Trim()
                        : null;

            return baseUrl is null ? null : $"{baseUrl.TrimEnd('/')}/i/{token}";
        }
    }

    // =================================================================
    // PREFILL
    // =================================================================

    public class GetInvoiceEmailPrefillHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly EmailOptions _email;

        public GetInvoiceEmailPrefillHandler(FlowDbContext db, IOptions<EmailOptions> email)
        {
            _db = db;
            _email = email.Value;
        }

        public async Task<InvoiceEmailPrefillDto> Handle(Guid tenantId, Guid invoiceId, CancellationToken ct = default)
        {
            var inv = await _db.Invoices.AsNoTracking()
                .Where(i => i.Id == invoiceId && i.TenantId == tenantId && !i.IsDeleted)
                .Include(i => i.Deal).ThenInclude(d => d!.Contact).ThenInclude(c => c!.Company)
                .Include(i => i.Deal).ThenInclude(d => d!.Company)
                .Include(i => i.Quote).ThenInclude(q => q!.Deal).ThenInclude(d => d!.Contact).ThenInclude(c => c!.Company)
                .Include(i => i.Quote).ThenInclude(q => q!.Deal).ThenInclude(d => d!.Company)
                .FirstOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException($"Invoice {invoiceId} not found");

            var deal = inv.Deal ?? inv.Quote?.Deal;
            var contact = deal?.Contact;

            var tenantName = await _db.Tenants.AsNoTracking()
                .Where(t => t.Id == tenantId).Select(t => t.Name).FirstOrDefaultAsync(ct) ?? "us";

            var last = await _db.OutboundMessages.AsNoTracking()
                .Where(m => m.TenantId == tenantId && m.EntityType == "Invoice" && m.EntityId == invoiceId
                         && m.RecipientUserId == null)
                .OrderByDescending(m => m.CreatedAtUtc)
                .Select(m => new { m.CreatedAtUtc, m.ToAddress })
                .FirstOrDefaultAsync(ct);

            var blocked = InvoiceEmailRules.BlockedReason(inv.Status);
            if (blocked is null && !_email.IsConfigured)
                blocked = "Email isn't set up on this server yet (Email:ApiKey), so nothing can be sent.";

            return new InvoiceEmailPrefillDto(
                CanSend:       blocked is null,
                BlockedReason: blocked,
                ToAddress:     string.IsNullOrWhiteSpace(contact?.Email) ? null : contact!.Email!.Trim(),
                ToName:        contact?.DisplayName,
                ContactName:   contact?.DisplayName,
                Subject:       $"Invoice {inv.Number} from {tenantName}",
                CompanyName:   CustomerNaming.CompanyOf(deal),
                LastSentAtUtc: last?.CreatedAtUtc,
                LastSentTo:    last?.ToAddress,
                ReceivingOn:   _email.IsInboundAddressingOn);
        }
    }

    // =================================================================
    // SEND
    // =================================================================

    public class SendInvoiceEmailHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly GenerateInvoicePdfHandler _pdf;
        private readonly IInvoiceCustomerEmail _queue;
        private readonly ICurrentUserService _currentUser;
        private readonly IAuditService _audit;
        private readonly EmailOptions _email;
        private readonly ILogger<SendInvoiceEmailHandler> _logger;

        public SendInvoiceEmailHandler(
            FlowDbContext db,
            GenerateInvoicePdfHandler pdf,
            IInvoiceCustomerEmail queue,
            ICurrentUserService currentUser,
            IAuditService audit,
            IOptions<EmailOptions> email,
            ILogger<SendInvoiceEmailHandler> logger)
        {
            _db = db;
            _pdf = pdf;
            _queue = queue;
            _currentUser = currentUser;
            _audit = audit;
            _email = email.Value;
            _logger = logger;
        }

        public async Task<SendInvoiceEmailResultDto> Handle(
            Guid tenantId, Guid invoiceId, SendInvoiceEmailDto dto, CancellationToken ct = default)
        {
            if (dto is null) throw new InvalidOperationException("Nothing to send.");

            if (!_email.IsConfigured)
                throw new InvalidOperationException("Email isn't set up on this server yet (Email:ApiKey), so nothing can be sent.");

            // ── The addresses ─────────────────────────────────────────
            var to = InvoiceEmailRules.CleanAddress(dto.ToAddress)
                ?? throw new InvalidOperationException("Enter one valid email address in To.");

            var cc = new List<string>();
            foreach (var part in (dto.CcAddresses ?? string.Empty)
                         .Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var clean = InvoiceEmailRules.CleanAddress(part)
                    ?? throw new InvalidOperationException($"\"{part}\" in Cc isn't a valid email address.");
                if (string.Equals(clean, to, StringComparison.OrdinalIgnoreCase)) continue;
                if (cc.Any(c => string.Equals(c, clean, StringComparison.OrdinalIgnoreCase))) continue;
                cc.Add(clean);
            }

            if (cc.Count > InvoiceEmailRules.MaxCc)
                throw new InvalidOperationException($"Copy at most {InvoiceEmailRules.MaxCc} addresses.");

            var note = string.IsNullOrWhiteSpace(dto.Message) ? null : dto.Message.Trim();
            if (note is { Length: > InvoiceEmailRules.MaxMessage })
                throw new InvalidOperationException($"Keep the message under {InvoiceEmailRules.MaxMessage} characters.");

            // ── The invoice ───────────────────────────────────────────
            var invoice = await _db.Invoices
                .Where(i => i.Id == invoiceId && i.TenantId == tenantId && !i.IsDeleted)
                .Include(i => i.Deal).ThenInclude(d => d!.Contact).ThenInclude(c => c!.Company)
                .Include(i => i.Deal).ThenInclude(d => d!.Company)
                .Include(i => i.Quote).ThenInclude(q => q!.Deal).ThenInclude(d => d!.Contact).ThenInclude(c => c!.Company)
                .Include(i => i.Quote).ThenInclude(q => q!.Deal).ThenInclude(d => d!.Company)
                .FirstOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException($"Invoice {invoiceId} not found");

            var blocked = InvoiceEmailRules.BlockedReason(invoice.Status);
            if (blocked is not null) throw new InvalidOperationException(blocked);

            var deal = invoice.Deal ?? invoice.Quote?.Deal;
            var contact = deal?.Contact;

            // The name only when the address is the contact's own; a typed
            // accounts@ address gets "Hello there", not someone else's name.
            var toName = !string.IsNullOrWhiteSpace(dto.ToName)
                ? dto.ToName!.Trim()
                : contact is not null && string.Equals(contact.Email?.Trim(), to, StringComparison.OrdinalIgnoreCase)
                    ? contact.DisplayName
                    : null;

            // ── The link ──────────────────────────────────────────────
            invoice.PublicLinkToken ??= InvoiceEmailRules.NewPublicToken();

            var url = InvoiceEmailRules.PublicUrl(dto.BaseUrl, _email.AppBaseUrl, invoice.PublicLinkToken)
                ?? throw new InvalidOperationException(
                    "The customer link has no web address. Set Email__AppBaseUrl on the API.");

            // ── The PDF, exactly as the Download button makes it ──────
            byte[] pdf;
            try
            {
                pdf = await _pdf.HandleAsync(tenantId, invoiceId, ct);
            }
            catch (Exception ex) when (ex is not InvalidOperationException and not KeyNotFoundException)
            {
                _logger.LogError(ex, "Could not render the PDF for invoice {InvoiceId} before emailing it", invoiceId);
                throw new InvalidOperationException("The invoice PDF could not be made, so nothing was sent. Please try again.");
            }

            var fileName = InvoiceEmailRules.PdfFileName(invoice.Number);
            var me = await _currentUser.GetCurrentUserAsync();

            // The customer's "Download PDF" copy — one per invoice, replaced.
            var copy = await _db.InvoiceSentCopies
                .FirstOrDefaultAsync(c => c.InvoiceId == invoiceId && c.TenantId == tenantId, ct);

            if (copy is null)
            {
                _db.InvoiceSentCopies.Add(new InvoiceSentCopy
                {
                    InvoiceId = invoiceId,
                    TenantId = tenantId,
                    FileName = fileName,
                    SizeBytes = pdf.Length,
                    PdfContent = pdf,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedBy = me?.FullName
                });
            }
            else
            {
                copy.FileName = fileName;
                copy.SizeBytes = pdf.Length;
                copy.PdfContent = pdf;
                copy.CreatedAtUtc = DateTime.UtcNow;
                copy.CreatedBy = me?.FullName;
            }

            var messageId = await _queue.QueueAsync(new InvoiceEmailRequest(
                Invoice:         invoice,
                TenantId:        tenantId,
                ToAddress:       to,
                ToName:          toName,
                CcAddresses:     cc,
                PersonalMessage: note,
                PublicUrl:       url,
                Pdf:             pdf,
                PdfFileName:     fileName,
                SentByUserId:    me?.UserId,
                SentByName:      me?.FullName,
                CompanyName:     CustomerNaming.CompanyOf(deal)), ct);

            // ONE save — see the header.
            await _db.SaveChangesAsync(ct);

            await _audit.WriteAsync(
                AuditAction.InvoiceEmailed, AuditEntityType.Invoice, invoice.Id, tenantId,
                new
                {
                    number = invoice.Number,
                    to = InboundAddressing.Mask(to),
                    cc = cc.Count,
                    attachment = fileName,
                    by = me?.FullName
                },
                CancellationToken.None);

            return new SendInvoiceEmailResultDto(messageId, to, cc.Count, url);
        }
    }

    // =================================================================
    // THE CUSTOMER'S PAGE (anonymous)
    // =================================================================

    public class GetInvoiceByTokenHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly IAuditService _audit;

        public GetInvoiceByTokenHandler(FlowDbContext db, IAuditService audit)
        {
            _db = db;
            _audit = audit;
        }

        /// <summary>
        /// The invoice behind a public token, or null. Sent → Viewed the
        /// first time it is opened, as quotes have done since 017.
        /// </summary>
        public async Task<PublicInvoiceDto?> HandleAsync(string token, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return null;

            // IgnoreQueryFilters — the anonymous lookup (item 18). Includes
            // ride on the same query, so the deal / contact / company come
            // back instead of being filtered to null.
            var invoice = await _db.Invoices.AsNoTracking()
                .IgnoreQueryFilters()
                .Where(i => i.PublicLinkToken == token && !i.IsDeleted)
                .Include(i => i.Lines.Where(l => !l.IsDeleted))
                .Include(i => i.Payments.Where(p => !p.IsDeleted))
                .Include(i => i.Deal).ThenInclude(d => d!.Contact).ThenInclude(c => c!.Company)
                .Include(i => i.Deal).ThenInclude(d => d!.Company)
                .Include(i => i.Quote).ThenInclude(q => q!.Deal).ThenInclude(d => d!.Contact).ThenInclude(c => c!.Company)
                .Include(i => i.Quote).ThenInclude(q => q!.Deal).ThenInclude(d => d!.Company)
                .FirstOrDefaultAsync(ct);

            // A token is only ever given to an issued invoice; belt and braces.
            if (invoice is null || invoice.Status == InvoiceStatus.Draft) return null;

            // The deal / quote rows are the invoice's own tenant's by
            // construction (same-tenant foreign keys), but say so anyway.
            var deal = invoice.Deal?.TenantId == invoice.TenantId ? invoice.Deal
                     : invoice.Quote?.TenantId == invoice.TenantId ? invoice.Quote?.Deal
                     : null;

            // ── Sent → Viewed ─────────────────────────────────────────
            var status = invoice.Status;
            if (invoice.Status == InvoiceStatus.Sent)
            {
                var tracked = await _db.Invoices.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(i => i.Id == invoice.Id && i.TenantId == invoice.TenantId, ct);
                if (tracked is not null && tracked.Status == InvoiceStatus.Sent)
                {
                    tracked.Status = InvoiceStatus.Viewed;
                    tracked.UpdatedAtUtc = DateTime.UtcNow;
                    tracked.UpdatedBy = "Customer";
                    await _db.SaveChangesAsync(ct);
                    status = InvoiceStatus.Viewed;

                    await _audit.WriteAsync(
                        AuditAction.InvoiceStatusChanged, AuditEntityType.Invoice, tracked.Id, tracked.TenantId,
                        new { number = tracked.Number, from = "Sent", to = "Viewed", bySource = "PublicLink" },
                        ct);
                }
            }

            var seller = await _db.Tenants.AsNoTracking()
                .Where(t => t.Id == invoice.TenantId)
                .Select(t => new QuoteSellerDto
                {
                    Name           = t.Name,
                    LegalName      = t.LegalName,
                    AddressLine1   = t.AddressLine1,
                    AddressLine2   = t.AddressLine2,
                    City           = t.City,
                    State          = t.State,
                    PostalCode     = t.PostalCode,
                    CountryName    = t.Country != null ? t.Country.Name : null,
                    Phone          = t.Phone,
                    Email          = t.ReplyToEmail,
                    Website        = t.Website,
                    TaxNumber      = t.TaxNumber,
                    TaxNumberLabel = t.TaxNumberLabel,
                    TaxLabel       = t.Country != null && t.Country.TaxLabel != null ? t.Country.TaxLabel : "Tax"
                })
                .FirstOrDefaultAsync(ct);

            var copy = await _db.InvoiceSentCopies.AsNoTracking()
                .IgnoreQueryFilters()
                .Where(c => c.InvoiceId == invoice.Id && c.TenantId == invoice.TenantId)
                .Select(c => new { c.CreatedAtUtc, c.FileName })
                .FirstOrDefaultAsync(ct);

            var (printed, printedAt) = await InvoiceDocument.ResolveAsync(_db, invoice, ct);
            var lineFields = await InvoiceDocument.ResolveLinesAsync(_db, invoice, ct);

            var captured = invoice.Payments
                .Where(p => !p.IsDeleted && p.Status == PaymentStatusNames.Captured)
                .Sum(p => p.Amount);

            var isVoid = status == InvoiceStatus.Cancelled;

            var dto = new InvoiceDto
            {
                Id            = invoice.Id,
                TenantId      = Guid.Empty,   // the workspace's id is not the customer's business
                Number        = invoice.Number,
                IssueDateUtc  = invoice.IssueDateUtc,
                DueDateUtc    = invoice.DueDateUtc,
                Currency      = invoice.Currency,
                Status        = status.ToString(),
                Subtotal      = invoice.Subtotal,
                DiscountTotal = invoice.DiscountTotal,
                TaxTotal      = invoice.TaxTotal,
                GrandTotal    = invoice.Subtotal - invoice.DiscountTotal + invoice.TaxTotal,
                Balance       = invoice.Balance,
                TotalPaid     = captured,
                QuoteNumber   = invoice.Quote?.Number,
                DealTitle     = deal?.Title,
                CompanyName   = CustomerNaming.CompanyOf(deal),
                ContactName   = CustomerNaming.PersonOf(deal),
                Notes         = invoice.Notes,

                MilestoneId       = invoice.MilestoneId,
                MilestoneName     = invoice.MilestoneName,
                MilestoneSequence = invoice.MilestoneSequence,
                MilestoneCount    = invoice.MilestoneCount,
                MilestonePercent  = invoice.MilestonePercent,

                DocumentFields            = printed,
                DocumentFieldsFrozenAtUtc = printedAt,

                IsOverdue   = !isVoid && invoice.DueDateUtc.HasValue
                              && invoice.DueDateUtc.Value < DateTime.UtcNow && invoice.Balance > 0,
                IsFullyPaid = invoice.Balance <= 0,

                Lines = invoice.Lines.Where(l => !l.IsDeleted).Select(l => new InvoiceLineDto
                {
                    Id              = l.Id,
                    Name            = l.Name,
                    Description     = l.Description,
                    UnitPrice       = l.UnitPrice,
                    Quantity        = l.Quantity,
                    UnitOfMeasure   = l.UnitOfMeasure,
                    LineDiscount    = l.LineDiscount,
                    DiscountPercent = l.DiscountPercent,
                    TaxRate         = l.TaxRate,
                    TaxCode         = l.TaxCode,
                    LineTotal       = (l.UnitPrice * l.Quantity) - l.LineDiscount,
                    LineTax         = ((l.UnitPrice * l.Quantity) - l.LineDiscount) * l.TaxRate,
                    LineGrandTotal  = ((l.UnitPrice * l.Quantity) - l.LineDiscount) * (1 + l.TaxRate),
                    DocumentFields  = lineFields.GetValueOrDefault(l.Id) ?? new List<DocumentFieldEntry>()
                }).ToList()

                // Payments deliberately empty: amounts paid are summarised
                // (TotalPaid), but methods, references and notes are the
                // workspace's bookkeeping, not the customer's page.
            };

            return new PublicInvoiceDto(dto, seller, copy is not null, copy?.CreatedAtUtc, copy?.FileName);
        }
    }

    public class GetInvoicePublicPdfHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;

        public GetInvoicePublicPdfHandler(FlowDbContext db) => _db = db;

        /// <summary>The PDF as last emailed, or null.</summary>
        public async Task<(byte[] Content, string FileName)?> HandleAsync(string token, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return null;

            var invoice = await _db.Invoices.AsNoTracking()
                .IgnoreQueryFilters()
                .Where(i => i.PublicLinkToken == token && !i.IsDeleted && i.Status != InvoiceStatus.Draft)
                .Select(i => new { i.Id, i.TenantId })
                .FirstOrDefaultAsync(ct);

            if (invoice is null) return null;

            var copy = await _db.InvoiceSentCopies.AsNoTracking()
                .IgnoreQueryFilters()
                .Where(c => c.InvoiceId == invoice.Id && c.TenantId == invoice.TenantId)
                .Select(c => new { c.PdfContent, c.FileName })
                .FirstOrDefaultAsync(ct);

            return copy is null ? null : (copy.PdfContent, copy.FileName);
        }
    }
}
