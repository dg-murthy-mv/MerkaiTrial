// =====================================================================
// SentEmailHandlers.cs
// Location: MerkaiTrial.Application/Commands/Emails/SentEmailHandlers.cs
//
// NEW FILE (085). Reading the emails WE sent, for the people allowed to
// see them. Registered by the Scrutor scan (ICommandHandler).
//
//   GetSentEmailsForEntityHandler  a quote's or an invoice's emails   (its card)
//   GetSentEmailsForRecordHandler  a deal's / contact's / lead's      (Emails tab)
//   GetSentEmailHandler            one email in full                  (detail page)
//   RetrySentEmailHandler          put a failed one back in the queue
//
// WHO MAY SEE WHAT — the same rules as the document itself:
//
//   about a QUOTE    quotes.read + that quote is visible (QuoteApprovalEngine)
//   about an INVOICE invoices.read + that invoice is visible (InvoiceAccessHandler)
//   anything else    (our notification emails to users) — workspace admins
//                    only, and WITHOUT the body: the email log's rule since
//                    038. A colleague's "you were assigned a deal" email is
//                    not something to read on a shared screen.
//
// Anything the caller may not see is "not found", never "forbidden", so a
// 403 never confirms that an email exists.
//
// RETRY. Only a message that never left (Failed / Dead / Cancelled). A
// bounced email DID leave — Resend took it — and retrying it would send
// the same thing to the same bad address. The page says to fix the address
// and send again from the quote or invoice instead.
// =====================================================================

using MerkaiTrial.Application.Commands.Invoices;
using MerkaiTrial.Application.Commands.Quotes;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.Emails
{
    /// <summary>The caller and what they may do. Built by SentEmailsController.</summary>
    public sealed record OutboundAccess(
        Guid TenantId,
        Guid UserId,
        string? UserName,
        bool IsAdmin,
        bool QuotesRead,
        bool QuotesUpdate,
        bool InvoicesRead,
        bool InvoicesUpdate,
        bool DealsRead,
        bool ContactsRead,
        bool LeadsRead);

    public static class SentEmailEntityTypes
    {
        public const string Quote = "Quote";
        public const string Invoice = "Invoice";

        public static string? Normalise(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            "quote"   => Quote,
            "invoice" => Invoice,
            _         => null
        };
    }

    // =================================================================
    // SHARED
    // =================================================================

    internal static class SentEmailRead
    {
        /// <summary>The list shape, newest first, with labels and sender names filled in.</summary>
        public static async Task<SentEmailsDto> ListAsync(
            FlowDbContext db, Guid tenantId, IQueryable<OutboundMessage> query, int take, CancellationToken ct)
        {
            var rows = await query
                .OrderByDescending(m => m.CreatedAtUtc)
                .Take(take + 1)
                .Select(m => new
                {
                    m.Id, m.Subject, m.ToAddress, m.ToName, m.CcAddresses,
                    m.Status, m.DeliveryStatus, m.DeliveryDetail, m.LastError, m.AttemptCount,
                    m.CreatedAtUtc, m.SentAtUtc, m.DeliveryStatusAtUtc,
                    m.EntityType, m.EntityId, m.SentByUserId,
                    Files = m.Attachments.Count()
                })
                .ToListAsync(ct);

            var hasMore = rows.Count > take;
            if (hasMore) rows.RemoveAt(rows.Count - 1);

            var labels = await LabelsAsync(db, tenantId,
                rows.Select(r => (r.EntityType, r.EntityId)), ct);

            var senderIds = rows.Where(r => r.SentByUserId.HasValue).Select(r => r.SentByUserId!.Value).Distinct().ToList();
            var names = senderIds.Count == 0
                ? new Dictionary<Guid, string>()
                : await db.Users.AsNoTracking()
                    .Where(u => u.TenantId == tenantId && senderIds.Contains(u.Id))
                    .Select(u => new { u.Id, Name = (u.FirstName + " " + u.LastName).Trim() })
                    .ToDictionaryAsync(u => u.Id, u => u.Name, ct);

            var items = rows.Select(r => new SentEmailListItemDto(
                r.Id, r.Subject, r.ToAddress, r.ToName, r.CcAddresses,
                r.Status.ToString(), r.DeliveryStatus.ToString(), r.DeliveryDetail, r.LastError, r.AttemptCount,
                r.CreatedAtUtc, r.SentAtUtc, r.DeliveryStatusAtUtc,
                r.EntityType, r.EntityId,
                r.EntityId.HasValue && labels.TryGetValue(r.EntityId.Value, out var label) ? label : null,
                r.SentByUserId.HasValue && names.TryGetValue(r.SentByUserId.Value, out var n) ? n : null,
                r.Files)).ToList();

            return new SentEmailsDto(items, hasMore);
        }

        /// <summary>Entity id → "Quote QUO-0007" / "Invoice INV-0003" ("Draft invoice" for a draft).</summary>
        public static async Task<Dictionary<Guid, string>> LabelsAsync(
            FlowDbContext db, Guid tenantId, IEnumerable<(string? Type, Guid? Id)> refs, CancellationToken ct)
        {
            var list = refs.Where(r => r.Id.HasValue).ToList();
            var quoteIds = list.Where(r => r.Type == SentEmailEntityTypes.Quote).Select(r => r.Id!.Value).Distinct().ToList();
            var invoiceIds = list.Where(r => r.Type == SentEmailEntityTypes.Invoice).Select(r => r.Id!.Value).Distinct().ToList();

            var result = new Dictionary<Guid, string>();

            if (quoteIds.Count > 0)
            {
                foreach (var q in await db.Quotes.AsNoTracking()
                             .Where(q => q.TenantId == tenantId && quoteIds.Contains(q.Id))
                             .Select(q => new { q.Id, q.Number }).ToListAsync(ct))
                    result[q.Id] = $"Quote {q.Number}";
            }

            if (invoiceIds.Count > 0)
            {
                foreach (var i in await db.Invoices.AsNoTracking()
                             .Where(i => i.TenantId == tenantId && invoiceIds.Contains(i.Id))
                             .Select(i => new { i.Id, i.Number }).ToListAsync(ct))
                    result[i.Id] = i.Number.StartsWith(InvoiceNumbering.DraftPrefix, StringComparison.Ordinal)
                        ? "Draft invoice"
                        : $"Invoice {i.Number}";
            }

            return result;
        }

        /// <summary>
        /// May the caller see emails about this entity? Quote and Invoice
        /// follow the document's own rules; anything else is admin-only.
        /// </summary>
        public static async Task<bool> CanSeeAsync(
            OutboundAccess a, string? entityType, Guid? entityId,
            QuoteApprovalEngine quotes, InvoiceAccessHandler invoices, CancellationToken ct)
        {
            switch (entityType)
            {
                case SentEmailEntityTypes.Quote when entityId.HasValue:
                    return a.QuotesRead && await quotes.CanReadQuoteAsync(a.TenantId, entityId.Value, ct);

                case SentEmailEntityTypes.Invoice when entityId.HasValue:
                    return a.InvoicesRead && await invoices.CanSeeInvoiceAsync(a.TenantId, entityId.Value, ct);

                default:
                    return a.IsAdmin;
            }
        }

        /// <summary>May the caller put this email back in the queue?</summary>
        public static async Task<bool> CanRetryAsync(
            OutboundAccess a, OutboundMessage m, QuoteApprovalEngine quotes, InvoiceAccessHandler invoices, CancellationToken ct)
        {
            switch (m.EntityType)
            {
                case SentEmailEntityTypes.Quote when m.EntityId.HasValue:
                    return a.QuotesUpdate && await quotes.CanWriteQuoteAsync(a.TenantId, m.EntityId.Value, ct);

                case SentEmailEntityTypes.Invoice when m.EntityId.HasValue:
                    return a.InvoicesUpdate && await invoices.CanSeeInvoiceAsync(a.TenantId, m.EntityId.Value, ct);

                default:
                    return a.IsAdmin;
            }
        }

        /// <summary>Why it cannot be retried, or null when it can (status-wise).</summary>
        public static string? RetryBlockedReason(OutboundMessage m) => m.Status switch
        {
            OutboundStatus.Sent when m.DeliveryStatus is OutboundDeliveryStatus.Bounced
                                                     or OutboundDeliveryStatus.Failed
                                                     or OutboundDeliveryStatus.Complained
                => "This email left — it was refused afterwards. Sending it again would go to the same address: correct the contact's email and send it again from the quote or invoice.",
            OutboundStatus.Sent    => null,  // nothing to retry; no button, no sentence
            OutboundStatus.Pending => null,
            OutboundStatus.Sending => null,
            _                      => null
        };

        public static bool IsRetryableStatus(OutboundStatus s)
            => s is OutboundStatus.Failed or OutboundStatus.Dead or OutboundStatus.Cancelled;
    }

    // =================================================================
    // A QUOTE'S OR AN INVOICE'S EMAILS
    // =================================================================

    public class GetSentEmailsForEntityHandler : ICommandHandler
    {
        private const int Limit = 25;

        private readonly FlowDbContext _db;
        private readonly QuoteApprovalEngine _quotes;
        private readonly InvoiceAccessHandler _invoices;

        public GetSentEmailsForEntityHandler(FlowDbContext db, QuoteApprovalEngine quotes, InvoiceAccessHandler invoices)
        {
            _db = db;
            _quotes = quotes;
            _invoices = invoices;
        }

        /// <summary>Null when the document is not found or not visible.</summary>
        public async Task<SentEmailsDto?> Handle(OutboundAccess a, string entityType, Guid entityId, CancellationToken ct = default)
        {
            var type = SentEmailEntityTypes.Normalise(entityType);
            if (type is null) return null;

            if (!await SentEmailRead.CanSeeAsync(a, type, entityId, _quotes, _invoices, ct)) return null;

            var q = _db.OutboundMessages.AsNoTracking()
                .Where(m => m.TenantId == a.TenantId && m.EntityType == type && m.EntityId == entityId
                         && m.RecipientUserId == null);      // the customer's emails, not our notifications

            return await SentEmailRead.ListAsync(_db, a.TenantId, q, Limit, ct);
        }
    }

    // =================================================================
    // A DEAL'S / CONTACT'S / LEAD'S EMAILS (the Emails tab)
    // =================================================================

    public class GetSentEmailsForRecordHandler : ICommandHandler
    {
        private const int Limit = 50;

        private readonly FlowDbContext _db;
        private readonly IRecordScopeService _scope;

        public GetSentEmailsForRecordHandler(FlowDbContext db, IRecordScopeService scope)
        {
            _db = db;
            _scope = scope;
        }

        /// <summary>Null when the record is not found or not visible.</summary>
        public async Task<SentEmailsDto?> Handle(OutboundAccess a, string entityType, Guid entityId, CancellationToken ct = default)
        {
            var t = a.TenantId;
            List<Guid> dealIds;

            switch (entityType?.Trim().ToLowerInvariant())
            {
                case "deal":
                    if (!a.DealsRead || !await _scope.CanSeeDealAsync(_db, t, entityId, ct)) return null;
                    dealIds = new() { entityId };
                    break;

                case "contact":
                {
                    if (!a.ContactsRead) return null;
                    var exists = await _db.Contacts.AsNoTracking()
                        .AnyAsync(c => c.Id == entityId && c.TenantId == t && !c.IsDeleted, ct);
                    if (!exists) return null;

                    // The contact's deals THIS person can see — a quote on a
                    // colleague's private deal stays private here too.
                    var dealAccess = await _scope.GetAsync(RecordModules.Deals, ct);
                    dealIds = a.DealsRead
                        ? await _db.Deals.AsNoTracking()
                            .Where(d => d.TenantId == t && !d.IsDeleted && d.ContactId == entityId)
                            .VisibleTo(dealAccess)
                            .Select(d => d.Id)
                            .ToListAsync(ct)
                        : new();
                    break;
                }

                case "lead":
                {
                    if (!a.LeadsRead || !await _scope.CanSeeLeadAsync(_db, t, entityId, ct)) return null;
                    var dealId = await _db.Leads.AsNoTracking()
                        .Where(l => l.Id == entityId && l.TenantId == t && !l.IsDeleted)
                        .Select(l => l.DealId)
                        .FirstOrDefaultAsync(ct);
                    dealIds = dealId.HasValue && a.DealsRead && await _scope.CanSeeDealAsync(_db, t, dealId.Value, ct)
                        ? new() { dealId.Value }
                        : new();
                    break;
                }

                default:
                    return null;
            }

            if (dealIds.Count == 0) return new SentEmailsDto(new(), false);

            var quoteIds = a.QuotesRead
                ? await _db.Quotes.AsNoTracking()
                    .Where(q => q.TenantId == t && !q.IsDeleted && dealIds.Contains(q.DealId))
                    .Select(q => q.Id).ToListAsync(ct)
                : new List<Guid>();

            var invoiceIds = a.InvoicesRead
                ? await _db.Invoices.AsNoTracking()
                    .Where(i => i.TenantId == t && !i.IsDeleted
                             && ((i.DealId != null && dealIds.Contains(i.DealId.Value))
                              || (i.Quote != null && dealIds.Contains(i.Quote.DealId))))
                    .Select(i => i.Id).ToListAsync(ct)
                : new List<Guid>();

            if (quoteIds.Count == 0 && invoiceIds.Count == 0) return new SentEmailsDto(new(), false);

            var q2 = _db.OutboundMessages.AsNoTracking()
                .Where(m => m.TenantId == t && m.RecipientUserId == null && m.EntityId != null
                         && ((m.EntityType == SentEmailEntityTypes.Quote && quoteIds.Contains(m.EntityId.Value))
                          || (m.EntityType == SentEmailEntityTypes.Invoice && invoiceIds.Contains(m.EntityId.Value))));

            return await SentEmailRead.ListAsync(_db, t, q2, Limit, ct);
        }
    }

    // =================================================================
    // ONE EMAIL
    // =================================================================

    public class GetSentEmailHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly QuoteApprovalEngine _quotes;
        private readonly InvoiceAccessHandler _invoices;

        public GetSentEmailHandler(FlowDbContext db, QuoteApprovalEngine quotes, InvoiceAccessHandler invoices)
        {
            _db = db;
            _quotes = quotes;
            _invoices = invoices;
        }

        /// <summary>Null when it does not exist or the caller may not see it.</summary>
        public async Task<SentEmailDetailDto?> Handle(OutboundAccess a, Guid id, CancellationToken ct = default)
        {
            var m = await _db.OutboundMessages.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == a.TenantId, ct);
            if (m is null) return null;

            var isCustomerDocument = m.RecipientUserId == null
                && m.EntityType is SentEmailEntityTypes.Quote or SentEmailEntityTypes.Invoice;

            if (!await SentEmailRead.CanSeeAsync(a,
                    isCustomerDocument ? m.EntityType : null, m.EntityId, _quotes, _invoices, ct))
                return null;

            var list = await SentEmailRead.ListAsync(_db, a.TenantId,
                _db.OutboundMessages.AsNoTracking().Where(x => x.Id == id && x.TenantId == a.TenantId), 1, ct);
            var summary = list.Items.FirstOrDefault();
            if (summary is null) return null;

            var files = await _db.OutboundMessageAttachments.AsNoTracking()
                .Where(f => f.OutboundMessageId == id && f.TenantId == a.TenantId)
                .Select(f => new SentEmailAttachmentDto(f.Id, f.FileName, f.SizeBytes))
                .ToListAsync(ct);

            var canRetry = SentEmailRead.IsRetryableStatus(m.Status)
                && await SentEmailRead.CanRetryAsync(a, m, _quotes, _invoices, ct);

            return new SentEmailDetailDto(
                summary,
                m.ReplyToAddress,
                m.EventType.ToString(),
                m.Channel.ToString(),
                isCustomerDocument ? m.BodyHtml : null,
                isCustomerDocument ? m.BodyText : null,
                files,
                canRetry,
                SentEmailRead.RetryBlockedReason(m));
        }
    }

    // =================================================================
    // RETRY
    // =================================================================

    public class RetrySentEmailHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly QuoteApprovalEngine _quotes;
        private readonly InvoiceAccessHandler _invoices;
        private readonly IAuditService _audit;

        public RetrySentEmailHandler(FlowDbContext db, QuoteApprovalEngine quotes, InvoiceAccessHandler invoices, IAuditService audit)
        {
            _db = db;
            _quotes = quotes;
            _invoices = invoices;
            _audit = audit;
        }

        public async Task Handle(OutboundAccess a, Guid id, CancellationToken ct = default)
        {
            var m = await _db.OutboundMessages
                .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == a.TenantId, ct)
                ?? throw new KeyNotFoundException("That email could not be found.");

            var isCustomerDocument = m.RecipientUserId == null
                && m.EntityType is SentEmailEntityTypes.Quote or SentEmailEntityTypes.Invoice;

            if (!await SentEmailRead.CanSeeAsync(a, isCustomerDocument ? m.EntityType : null, m.EntityId, _quotes, _invoices, ct))
                throw new KeyNotFoundException("That email could not be found.");

            if (!await SentEmailRead.CanRetryAsync(a, m, _quotes, _invoices, ct))
                throw new UnauthorizedAccessException("You cannot resend this email.");

            if (m.Status == OutboundStatus.Sent)
                throw new InvalidOperationException(SentEmailRead.RetryBlockedReason(m)
                    ?? "That email was already sent. Sending it again would deliver a duplicate.");

            if (m.Status is OutboundStatus.Sending or OutboundStatus.Pending)
                throw new InvalidOperationException("That email is already on its way.");

            // Same reset as the log's Retry (038): full backoff ladder again,
            // same Id, so the provider's idempotency key is unchanged.
            m.Status = OutboundStatus.Pending;
            m.AttemptCount = 0;
            m.NextAttemptAtUtc = null;
            m.LockedUntilUtc = null;
            m.LastError = null;
            m.UpdatedAtUtc = DateTime.UtcNow;
            m.UpdatedBy = a.UserName;

            await _db.SaveChangesAsync(ct);

            await _audit.WriteAsync(
                AuditAction.EmailRetried, AuditEntityType.OutboundMessage, m.Id, a.TenantId,
                new { about = m.EntityType, entityId = m.EntityId, subject = m.Subject },
                CancellationToken.None);
        }
    }
}
