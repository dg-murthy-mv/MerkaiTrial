// =====================================================================
// SendQuoteEmailHandler.cs
// Location: MerkaiTrial.Application/Commands/Quotes/SendQuoteEmailHandler.cs
//
// NEW FILE (061). The EXPLICIT "email this to the customer" action.
//
// There are two paths to a quote email and they share one implementation:
//
//   AUTOMATIC   UpdateQuoteStatusHandler queues it when the quote moves to
//               Sent, in the same transaction as the status change. That
//               is what makes "Send to customer" finally send to the
//               customer.
//
//   EXPLICIT    this handler — "they say it never arrived", "they deleted
//               it", "send it to them again". Does NOT change the status,
//               because resending a quote is not a new event in its life.
//
// Both call IQuoteCustomerEmail.QueueAsync. The difference is only what
// happens to the skip reason: the automatic path LOGS it, because a
// contact with no email address must never roll back a status change the
// rep just made; this one RETURNS it, so the page can say exactly what to
// fix.
//
// 085 — the person who pressed the button is recorded on the email
// (SentByUserId), so a bounce comes back to them. Read from the signed-in
// user here rather than passed down, so QuotesController is unchanged.
//
// WHY RESEND IS NOT RATE LIMITED HERE
//   Each click queues a row with a fresh id, and a fresh id is a fresh
//   idempotency key — so pressing it twice really does send two emails.
//   That is correct: "send it again" is the entire purpose. The button
//   confirms before posting, which is the right place for that friction.
// =====================================================================

// The same using list QuotesCommandHandler.cs carries, trimmed to what
// this file touches — ICommandHandler, QuoteStatus, AuditAction and
// AuditEntityType live across MerkaiTrial.Application and the two Domain
// namespaces, so all of them are named rather than assumed.
using MerkaiTrial.Application;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Notifications;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Quotes
{
    /// <summary>
    /// What happened, in words the rep can act on. Queued=false is not an
    /// error — it is "here is what to fix first".
    /// </summary>
    public sealed record SendQuoteEmailResult(
        bool Queued,
        string? Reason,
        string? MaskedRecipient);

    public class SendQuoteEmailHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly IQuoteCustomerEmail _quoteEmail;
        private readonly IAuditService _audit;
        private readonly ICurrentUserService _currentUser;     // 085
        private readonly ILogger<SendQuoteEmailHandler> _logger;

        public SendQuoteEmailHandler(
            FlowDbContext db,
            IQuoteCustomerEmail quoteEmail,
            IAuditService audit,
            ICurrentUserService currentUser,
            ILogger<SendQuoteEmailHandler> logger)
        {
            _db = db;
            _quoteEmail = quoteEmail;
            _audit = audit;
            _currentUser = currentUser;
            _logger = logger;
        }

        public async Task<SendQuoteEmailResult> HandleAsync(
            Guid tenantId,
            Guid quoteId,
            string? baseUrl,
            string? sentBy,
            CancellationToken ct = default)
        {
            var quote = await _db.Quotes
                .FirstOrDefaultAsync(q => q.Id == quoteId
                                       && q.TenantId == tenantId
                                       && !q.IsDeleted, ct);

            if (quote is null)
                throw new KeyNotFoundException($"Quote {quoteId} not found");

            // Only a quote that is actually out with the customer. A draft
            // has no public link, and emailing a link to a quote still
            // being edited is how a customer ends up quoting a price that
            // was never agreed.
            if (quote.Status is not (QuoteStatus.Sent or QuoteStatus.Viewed))
            {
                throw new InvalidOperationException(
                    quote.Status is QuoteStatus.Accepted or QuoteStatus.Rejected
                        ? "This quote has already been decided, so there is nothing to send."
                        : "Send this quote to the customer first — that is what creates their link.");
            }

            // The public link may have been stored relative, if whatever
            // sent the quote had no BaseUrl to pass. Repair it here while
            // we have one, so every later email and every Copy link press
            // gets an address that works.
            if (!string.IsNullOrWhiteSpace(baseUrl) &&
                !string.IsNullOrWhiteSpace(quote.PublicLinkToken) &&
                (string.IsNullOrWhiteSpace(quote.PaymentLinkUrl) ||
                 quote.PaymentLinkUrl!.StartsWith("/", StringComparison.Ordinal)))
            {
                quote.PaymentLinkUrl = $"{baseUrl.TrimEnd('/')}/q/{quote.PublicLinkToken}";

                _logger.LogInformation(
                    "Quote {QuoteId} public URL was relative — rebuilt against the request host",
                    quoteId);
            }

            // 085 — who pressed it, for a bounce. Never fails the send.
            Guid? senderId = null;
            try { senderId = (await _currentUser.GetCurrentUserAsync())?.UserId; }
            catch { /* the audit name above is enough; the bounce then goes to the deal owner */ }

            var reason = await _quoteEmail.QueueAsync(quote, tenantId, sentBy, ct, senderId);

            if (reason is not null)
            {
                // Nothing was added, so nothing to save. Not logged as an
                // error: "this contact has no email address" is a data gap,
                // not a fault.
                _logger.LogInformation(
                    "Quote {QuoteNumber} email not queued: {Reason}", quote.Number, reason);

                return new SendQuoteEmailResult(false, reason, null);
            }

            // Commits the OutboundMessage row AND any PaymentLinkUrl repair
            // above, together.
            await _db.SaveChangesAsync(ct);

            await _audit.WriteAsync(
                AuditAction.QuoteStatusChanged, AuditEntityType.Quote,
                quote.Id, tenantId,
                new { number = quote.Number, action = "QuoteEmailResent", by = sentBy },
                CancellationToken.None);

            _logger.LogInformation(
                "Quote {QuoteNumber} queued for re-send by {User}", quote.Number, sentBy ?? "unknown");

            return new SendQuoteEmailResult(true, null, null);
        }
    }
}
