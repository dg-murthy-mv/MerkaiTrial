// =====================================================================
// InboundEmailHandlers.cs
// Location: MerkaiTrial.Application/Commands/InboundEmails/InboundEmailHandlers.cs
//
// NEW FILE (083). Everything the PAGES do with received email: the Inbox,
// one email, a record's Emails section, linking, dismissing, deleting, and
// the settings page. Registered by the Scrutor scan (ICommandHandler).
//
// ─────────────────────────────────────────────────────────────────────
// WHO MAY SEE AN EMAIL
//
//   An email is as private as the record it is filed on — and an
//   UNMATCHED one, which could be from anybody about anything, is visible
//   only to people with the new "Shared inbox" permission (inbox.read).
//   Nobody holds that until a workspace admin grants it; admins bypass.
//
//   One email is readable by a person who has ANY of:
//     inbox.read
//     leads.read     and can see the lead it is filed on (record scope)
//     deals.read     and can see the deal it is filed on
//     contacts.read  and it is filed on a contact
//     deals.read     and can see a deal with that contact — because the
//                    deal page shows its contact's emails
//
//   Anything else is "not found", never "forbidden": a 403 would confirm
//   the email exists.
//
// WHO MAY MOVE ONE
//   Linking, unlinking, dismissing and restoring: inbox.update — PLUS
//   update permission on the record it is being linked TO, and that
//   record must be visible. Deleting: inbox.delete. A rep can read the
//   emails on their own deals without any of this; sorting the shared
//   inbox is a separate job, given to separate people.
//
// THE CONTROLLER DECIDES PERMISSIONS, THE HANDLER APPLIES THEM
//   Handlers never read claims (the rule ActivityHandlers states). The
//   controller works out the caller's permissions once and passes them in
//   as InboundAccess; record visibility (Own / Team / All) comes from
//   IRecordScopeService here, the way every other lead and deal query
//   gets it.
// ─────────────────────────────────────────────────────────────────────
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Inbound;
using MerkaiTrial.Application.Services.Notifications;
using MerkaiTrial.Application.Services.Storage;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MerkaiTrial.Application.Commands.InboundEmails
{
    /// <summary>
    /// The caller, and what they may do. Built by InboundEmailsController
    /// from the token and the permission policies.
    /// </summary>
    public sealed record InboundAccess(
        Guid TenantId,
        Guid UserId,
        string? UserName,
        bool InboxRead,
        bool InboxUpdate,
        bool InboxDelete,
        bool LeadsRead,
        bool LeadsUpdate,
        bool DealsRead,
        bool DealsUpdate,
        bool ContactsRead,
        bool ContactsUpdate,
        bool CompaniesRead,
        bool SettingsRead,
        bool SettingsUpdate);

    // =================================================================
    // SHARED
    // =================================================================

    internal static class InboundRead
    {
        public static async Task<bool> CanViewAsync(
            FlowDbContext db, IRecordScopeService scope, InboundAccess a, InboundEmail e, CancellationToken ct)
        {
            if (a.InboxRead) return true;

            if (e.LeadId.HasValue && a.LeadsRead && await scope.CanSeeLeadAsync(db, a.TenantId, e.LeadId.Value, ct))
                return true;

            if (e.DealId.HasValue && a.DealsRead && await scope.CanSeeDealAsync(db, a.TenantId, e.DealId.Value, ct))
                return true;

            if (e.ContactId.HasValue && a.ContactsRead)
                return true;

            if (e.ContactId.HasValue && a.DealsRead)
            {
                var dealAccess = await scope.GetAsync(RecordModules.Deals, ct);
                var anyDeal = await db.Deals.AsNoTracking()
                    .Where(d => d.TenantId == a.TenantId && !d.IsDeleted && d.ContactId == e.ContactId)
                    .VisibleTo(dealAccess)
                    .AnyAsync(ct);
                if (anyDeal) return true;
            }

            return false;
        }

        public static async Task<Dictionary<Guid, string>> UserNamesAsync(
            FlowDbContext db, Guid tenantId, IEnumerable<Guid?> ids, CancellationToken ct)
        {
            var wanted = ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
            if (wanted.Count == 0) return new();

            // Users has no global filter — the TenantId predicate is the only scope.
            return await db.Users.AsNoTracking()
                .Where(u => u.TenantId == tenantId && wanted.Contains(u.Id))
                .Select(u => new { u.Id, Name = (u.FirstName + " " + u.LastName).Trim() })
                .ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        }

        public static InboundEmailListItemDto ToItem(InboundEmail e, IReadOnlyDictionary<Guid, string> names) => new(
            e.Id,
            e.Direction.ToString(),
            e.Status.ToString(),
            e.FromAddress,
            e.FromName,
            e.CounterpartAddress,
            e.ToAddresses,
            e.Subject,
            e.Snippet,
            e.ReceivedAtUtc,
            e.AttachmentCount,
            e.IsAutoReply,
            e.SenderVerified,
            e.ProcessingState.ToString(),
            e.ForwardedByUserId.HasValue ? names.GetValueOrDefault(e.ForwardedByUserId.Value) : null,
            e.MatchNote);

        /// <summary>The list columns only — never the bodies.</summary>
        public static IQueryable<InboundEmail> ListShape(IQueryable<InboundEmail> q) =>
            q.Select(e => new InboundEmail
            {
                Id = e.Id, TenantId = e.TenantId, Direction = e.Direction, Status = e.Status,
                FromAddress = e.FromAddress, FromName = e.FromName, CounterpartAddress = e.CounterpartAddress,
                ToAddresses = e.ToAddresses, Subject = e.Subject, Snippet = e.Snippet,
                ReceivedAtUtc = e.ReceivedAtUtc, AttachmentCount = e.AttachmentCount, IsAutoReply = e.IsAutoReply,
                SenderVerified = e.SenderVerified, ProcessingState = e.ProcessingState,
                ForwardedByUserId = e.ForwardedByUserId, MatchNote = e.MatchNote
            });

        public static async Task<string?> WorkspaceAddressAsync(
            FlowDbContext db, EmailOptions options, Guid tenantId, CancellationToken ct)
        {
            var domain = options.NormalisedInboundDomain;
            if (domain is null) return null;

            var alias = await db.Tenants.AsNoTracking()
                .Where(t => t.Id == tenantId).Select(t => t.InboundAlias).FirstOrDefaultAsync(ct);

            return string.IsNullOrWhiteSpace(alias) ? null : InboundAddressing.WorkspaceAddress(alias, domain);
        }
    }

    // =================================================================
    // THE INBOX
    // =================================================================

    public class GetInboundEmailsHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;

        public GetInboundEmailsHandler(FlowDbContext db) => _db = db;

        /// <param name="status">"unmatched" (default), "dismissed", "problems" or "all".</param>
        public async Task<InboundEmailPageDto> Handle(
            InboundAccess a, string? status, string? search, int skip, int take, CancellationToken ct = default)
        {
            if (!a.InboxRead) throw new UnauthorizedAccessException("You do not have access to the shared inbox.");

            take = Math.Clamp(take, 1, 100);
            skip = Math.Max(0, skip);

            var baseQuery = _db.InboundEmails.AsNoTracking()
                .Where(e => e.TenantId == a.TenantId && !e.IsDeleted);

            var unmatched = await baseQuery.CountAsync(e => e.Status == InboundEmailStatus.Unmatched, ct);
            var dismissed = await baseQuery.CountAsync(e => e.Status == InboundEmailStatus.Dismissed, ct);

            var q = (status ?? "unmatched").Trim().ToLowerInvariant() switch
            {
                "dismissed" => baseQuery.Where(e => e.Status == InboundEmailStatus.Dismissed),
                "problems"  => baseQuery.Where(e => e.ProcessingState == InboundProcessingState.Failed),
                "all"       => baseQuery,
                _           => baseQuery.Where(e => e.Status == InboundEmailStatus.Unmatched)
            };

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                if (s.Length > 100) s = s[..100];
                q = q.Where(e => e.Subject.Contains(s) || e.FromAddress.Contains(s)
                              || (e.FromName != null && e.FromName.Contains(s))
                              || (e.CounterpartAddress != null && e.CounterpartAddress.Contains(s)));
            }

            var total = await q.CountAsync(ct);

            var rows = await InboundRead.ListShape(q.OrderByDescending(e => e.ReceivedAtUtc))
                .Skip(skip).Take(take + 1)
                .ToListAsync(ct);

            var hasMore = rows.Count > take;
            if (hasMore) rows.RemoveAt(rows.Count - 1);

            var names = await InboundRead.UserNamesAsync(_db, a.TenantId, rows.Select(r => r.ForwardedByUserId), ct);

            return new InboundEmailPageDto(
                rows.Select(r => InboundRead.ToItem(r, names)).ToList(),
                total, unmatched, dismissed, hasMore);
        }
    }

    public class GetInboundCountHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public GetInboundCountHandler(FlowDbContext db) => _db = db;

        public async Task<InboundCountDto> Handle(InboundAccess a, CancellationToken ct = default)
        {
            if (!a.InboxRead) return new InboundCountDto(0);

            var n = await _db.InboundEmails.AsNoTracking()
                .CountAsync(e => e.TenantId == a.TenantId && !e.IsDeleted
                              && e.Status == InboundEmailStatus.Unmatched, ct);
            return new InboundCountDto(n);
        }
    }

    // =================================================================
    // ONE EMAIL
    // =================================================================

    public class GetInboundEmailHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly IRecordScopeService _scope;
        private readonly IFileStorageService _storage;

        public GetInboundEmailHandler(FlowDbContext db, IRecordScopeService scope, IFileStorageService storage)
        {
            _db = db;
            _scope = scope;
            _storage = storage;
        }

        /// <summary>Null when it does not exist OR the caller may not see it — the same answer on purpose.</summary>
        public async Task<InboundEmailDetailDto?> Handle(InboundAccess a, Guid id, CancellationToken ct = default)
        {
            var e = await _db.InboundEmails.AsNoTracking()
                .Include(x => x.Attachments)
                .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == a.TenantId && !x.IsDeleted, ct);

            if (e is null || !await InboundRead.CanViewAsync(_db, _scope, a, e, ct)) return null;

            var names = await InboundRead.UserNamesAsync(_db, a.TenantId, new[] { e.ForwardedByUserId }, ct);

            return new InboundEmailDetailDto(
                e.Id,
                e.Direction.ToString(),
                e.Status.ToString(),
                e.MatchMethod.ToString(),
                e.FromAddress,
                e.FromName,
                e.ToAddresses,
                e.CcAddresses,
                e.CounterpartAddress,
                e.Subject,
                e.BodyText,
                e.BodyHtml,
                e.ReceivedAtUtc,
                e.IsAutoReply,
                e.SenderVerified,
                e.AuthSummary,
                e.MatchNote,
                e.AttachmentNote,
                e.ProcessingState.ToString(),
                e.ProcessingError,
                e.ForwardedByUserId.HasValue ? names.GetValueOrDefault(e.ForwardedByUserId.Value) : null,
                e.LinkedBy,
                e.LinkedAtUtc,
                await LinksAsync(a, e, ct),
                e.Attachments
                    .OrderBy(f => f.FileName)
                    .Select(f => new InboundEmailAttachmentDto(
                        f.Id, f.FileName, _storage.GetDownloadUrl(f.FileUrl), f.FileSize, f.MimeType))
                    .ToList(),
                CanLink:    a.InboxUpdate,
                CanDismiss: a.InboxUpdate,
                CanDelete:  a.InboxDelete);
        }

        private async Task<List<InboundEmailLinkDto>> LinksAsync(InboundAccess a, InboundEmail e, CancellationToken ct)
        {
            var links = new List<InboundEmailLinkDto>();
            var t = a.TenantId;

            if (e.DealId.HasValue)
            {
                var title = await _db.Deals.AsNoTracking()
                    .Where(d => d.Id == e.DealId && d.TenantId == t && !d.IsDeleted)
                    .Select(d => d.Title).FirstOrDefaultAsync(ct);
                if (title is not null)
                    links.Add(new("Deal", e.DealId.Value, title,
                        a.DealsRead && await _scope.CanSeeDealAsync(_db, t, e.DealId.Value, ct)));
            }

            if (e.LeadId.HasValue)
            {
                var name = await _db.Leads.AsNoTracking()
                    .Where(l => l.Id == e.LeadId && l.TenantId == t && !l.IsDeleted)
                    .Select(l => l.FullName).FirstOrDefaultAsync(ct);
                if (name is not null)
                    links.Add(new("Lead", e.LeadId.Value, name,
                        a.LeadsRead && await _scope.CanSeeLeadAsync(_db, t, e.LeadId.Value, ct)));
            }

            if (e.ContactId.HasValue)
            {
                var name = await _db.Contacts.AsNoTracking()
                    .Where(c => c.Id == e.ContactId && c.TenantId == t && !c.IsDeleted)
                    .Select(c => (c.FirstName + " " + (c.LastName ?? "")).Trim()).FirstOrDefaultAsync(ct);
                if (name is not null)
                    links.Add(new("Contact", e.ContactId.Value, name, a.ContactsRead));
            }

            if (e.CompanyId.HasValue)
            {
                var name = await _db.Companies.AsNoTracking()
                    .Where(c => c.Id == e.CompanyId && c.TenantId == t && !c.IsDeleted)
                    .Select(c => c.Name).FirstOrDefaultAsync(ct);
                if (name is not null)
                    links.Add(new("Company", e.CompanyId.Value, name, a.CompaniesRead));
            }

            return links;
        }
    }

    // =================================================================
    // A RECORD'S EMAILS
    // =================================================================

    public class GetRecordEmailsHandler : ICommandHandler
    {
        private const int Limit = 50;

        private readonly FlowDbContext _db;
        private readonly IRecordScopeService _scope;
        private readonly EmailOptions _options;

        public GetRecordEmailsHandler(FlowDbContext db, IRecordScopeService scope, IOptions<EmailOptions> options)
        {
            _db = db;
            _scope = scope;
            _options = options.Value;
        }

        /// <summary>Null when the record is not found or not visible to the caller.</summary>
        public async Task<RecordEmailsDto?> Handle(
            InboundAccess a, string entityType, Guid entityId, CancellationToken ct = default)
        {
            var t = a.TenantId;
            IQueryable<InboundEmail> q = _db.InboundEmails.AsNoTracking()
                .Where(e => e.TenantId == t && !e.IsDeleted && e.Status != InboundEmailStatus.Dismissed);

            switch (InboundLinkTypes.Normalise(entityType))
            {
                case InboundLinkTypes.Lead:
                    if (!a.LeadsRead || !await _scope.CanSeeLeadAsync(_db, t, entityId, ct)) return null;
                    q = q.Where(e => e.LeadId == entityId);
                    break;

                case InboundLinkTypes.Deal:
                {
                    if (!a.DealsRead || !await _scope.CanSeeDealAsync(_db, t, entityId, ct)) return null;

                    var deal = await _db.Deals.AsNoTracking()
                        .Where(d => d.Id == entityId && d.TenantId == t && !d.IsDeleted)
                        .Select(d => new { d.ContactId, d.LeadId })
                        .FirstOrDefaultAsync(ct);
                    if (deal is null) return null;

                    // The deal's own emails, its contact's, and the lead it
                    // came from — the whole conversation, wherever it was filed.
                    var contactId = deal.ContactId == Guid.Empty ? (Guid?)null : deal.ContactId;
                    q = q.Where(e => e.DealId == entityId
                                  || (contactId != null && e.ContactId == contactId)
                                  || (deal.LeadId != null && e.LeadId == deal.LeadId));
                    break;
                }

                case InboundLinkTypes.Contact:
                    if (!a.ContactsRead) return null;
                    var exists = await _db.Contacts.AsNoTracking()
                        .AnyAsync(c => c.Id == entityId && c.TenantId == t && !c.IsDeleted, ct);
                    if (!exists) return null;
                    q = q.Where(e => e.ContactId == entityId);
                    break;

                default:
                    return null;
            }

            var rows = await InboundRead.ListShape(q.OrderByDescending(e => e.ReceivedAtUtc))
                .Take(Limit)
                .ToListAsync(ct);

            var names = await InboundRead.UserNamesAsync(_db, t, rows.Select(r => r.ForwardedByUserId), ct);

            return new RecordEmailsDto(
                rows.Select(r => InboundRead.ToItem(r, names)).ToList(),
                await InboundRead.WorkspaceAddressAsync(_db, _options, t, ct),
                _options.IsInboundAddressingOn);
        }
    }

    // =================================================================
    // LINK / UNLINK / DISMISS / RESTORE / DELETE
    // =================================================================

    public class InboundEmailActionsHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly IRecordScopeService _scope;
        private readonly IFileStorageService _storage;
        private readonly INotificationDispatcher _dispatcher;
        private readonly IAuditService _audit;
        private readonly ILogger<InboundEmailActionsHandler> _logger;

        public InboundEmailActionsHandler(
            FlowDbContext db,
            IRecordScopeService scope,
            IFileStorageService storage,
            INotificationDispatcher dispatcher,
            IAuditService audit,
            ILogger<InboundEmailActionsHandler> logger)
        {
            _db = db;
            _scope = scope;
            _storage = storage;
            _dispatcher = dispatcher;
            _audit = audit;
            _logger = logger;
        }

        public async Task LinkAsync(InboundAccess a, Guid id, LinkInboundEmailDto dto, CancellationToken ct = default)
        {
            if (!a.InboxUpdate) throw new UnauthorizedAccessException("You cannot file emails from the shared inbox.");

            var type = InboundLinkTypes.Normalise(dto?.EntityType);
            if (type is null || !InboundLinkTypes.Linkable.Contains(type) || dto!.EntityId == Guid.Empty)
                throw new InvalidOperationException("Choose a lead, deal or contact to file this email on.");

            // The person must be allowed to change the record they are
            // putting a customer's email on, and must be able to see it.
            var allowed = type switch
            {
                InboundLinkTypes.Lead    => a.LeadsUpdate && await _scope.CanSeeLeadAsync(_db, a.TenantId, dto.EntityId, ct),
                InboundLinkTypes.Deal    => a.DealsUpdate && await _scope.CanSeeDealAsync(_db, a.TenantId, dto.EntityId, ct),
                InboundLinkTypes.Contact => a.ContactsUpdate,
                _                        => false
            };

            if (!allowed)
                throw new KeyNotFoundException("That record could not be found, or you cannot change it.");

            var email = await LoadAsync(a, id, includeFiles: true, ct);

            var wasLinkedTo = (email.LeadId, email.DealId, email.ContactId);

            if (!await InboundEmailFiling.ApplyTargetAsync(_db, email, type, dto.EntityId, ct))
                throw new KeyNotFoundException("That record could not be found.");

            email.Status      = InboundEmailStatus.Matched;     // linking says it is genuine
            email.MatchMethod = InboundMatchMethod.Manual;
            email.MatchNote   = null;
            email.LinkedBy    = InboundAddressing.Truncate(a.UserName, 200);
            email.LinkedAtUtc = DateTime.UtcNow;

            await InboundEmailFiling.CopyAttachmentsToRecordsAsync(
                _db, _storage, email, email.Attachments.ToList(), _logger, ct);

            // The owner hears about a customer email that just landed on
            // their record — unless it was already there, or is not news.
            var moved = wasLinkedTo != (email.LeadId, email.DealId, email.ContactId);
            if (moved && email.Direction != InboundEmailDirection.SentByUser && !email.IsAutoReply)
            {
                var owners = await InboundEmailFiling.OwnersAsync(_db, email, ct);
                if (owners.Count > 0)
                {
                    await _dispatcher.AddForUsersAsync(new NotificationRequest(
                        TenantId:    a.TenantId,
                        EventType:   NotificationEventType.CustomerEmailReceived,
                        Title:       $"Email from {email.CounterpartAddress ?? email.FromName ?? email.FromAddress} filed on your record",
                        Body:        email.Subject,
                        EntityType:  InboundEmailEntityType.InboundEmail,
                        EntityId:    email.Id,
                        ActorUserId: a.UserId,
                        ActorName:   a.UserName), owners, ct);
                }
            }

            await _db.SaveChangesAsync(ct);
            await AuditAsync(AuditAction.InboundEmailLinked, email, a, new { entityType = type, entityId = dto.EntityId }, ct);
        }

        public async Task UnlinkAsync(InboundAccess a, Guid id, CancellationToken ct = default)
        {
            if (!a.InboxUpdate) throw new UnauthorizedAccessException("You cannot file emails from the shared inbox.");

            var email = await LoadAsync(a, id, includeFiles: false, ct);
            if (!email.HasLinks) return;

            InboundEmailFiling.ClearLinks(email);
            email.LinkedBy = InboundAddressing.Truncate(a.UserName, 200);
            email.LinkedAtUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);
            await AuditAsync(AuditAction.InboundEmailUnlinked, email, a, null, ct);
        }

        public async Task DismissAsync(InboundAccess a, Guid id, CancellationToken ct = default)
        {
            if (!a.InboxUpdate) throw new UnauthorizedAccessException("You cannot file emails from the shared inbox.");

            var email = await LoadAsync(a, id, includeFiles: false, ct);
            if (email.Status == InboundEmailStatus.Dismissed) return;

            email.Status = InboundEmailStatus.Dismissed;
            await _db.SaveChangesAsync(ct);
            await AuditAsync(AuditAction.InboundEmailDismissed, email, a, null, ct);
        }

        public async Task RestoreAsync(InboundAccess a, Guid id, CancellationToken ct = default)
        {
            if (!a.InboxUpdate) throw new UnauthorizedAccessException("You cannot file emails from the shared inbox.");

            var email = await LoadAsync(a, id, includeFiles: false, ct);
            if (email.Status != InboundEmailStatus.Dismissed) return;

            email.Status = email.HasLinks ? InboundEmailStatus.Matched : InboundEmailStatus.Unmatched;
            await _db.SaveChangesAsync(ct);
            await AuditAsync(AuditAction.InboundEmailRestored, email, a, null, ct);
        }

        /// <summary>
        /// Removes the content and the files, keeps a tombstone row. The
        /// row must stay: it is what stops a late Resend retry of the same
        /// email from filing it all over again. Copies already placed on a
        /// lead's or deal's Attachments tab are the record's now and stay.
        /// </summary>
        public async Task DeleteAsync(InboundAccess a, Guid id, CancellationToken ct = default)
        {
            if (!a.InboxDelete) throw new UnauthorizedAccessException("You cannot delete emails.");

            var email = await LoadAsync(a, id, includeFiles: true, ct);

            foreach (var file in email.Attachments.ToList())
            {
                try { await _storage.DeleteAsync(file.FileUrl, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not delete stored file for email attachment {Id}", file.Id);
                }
                _db.InboundEmailAttachments.Remove(file);
            }

            // Captured before the content is wiped, for the audit row.
            var subject = email.Subject;

            email.IsDeleted       = true;
            email.DeletedAtUtc    = DateTime.UtcNow;
            email.DeletedBy       = InboundAddressing.Truncate(a.UserName, 200);
            email.BodyHtml        = null;
            email.BodyText        = null;
            email.Snippet         = null;
            email.MetadataJson    = null;
            email.AttachmentCount = 0;
            email.Subject         = "(deleted)";

            await _db.SaveChangesAsync(ct);
            await AuditAsync(AuditAction.InboundEmailDeleted, email, a, new { subject }, ct);
        }

        private async Task<InboundEmail> LoadAsync(InboundAccess a, Guid id, bool includeFiles, CancellationToken ct)
        {
            IQueryable<InboundEmail> q = _db.InboundEmails;
            if (includeFiles) q = q.Include(e => e.Attachments);

            return await q.FirstOrDefaultAsync(e => e.Id == id && e.TenantId == a.TenantId && !e.IsDeleted, ct)
                   ?? throw new KeyNotFoundException("That email could not be found.");
        }

        private Task AuditAsync(string action, InboundEmail email, InboundAccess a, object? extra, CancellationToken ct)
            => _audit.WriteAsync(action, AuditEntityType.InboundEmail, email.Id, a.TenantId,
                new
                {
                    from    = InboundAddressing.Mask(email.CounterpartAddress ?? email.FromAddress),
                    subject = InboundAddressing.Truncate(email.Subject, 120),
                    extra
                }, ct);
    }

    // =================================================================
    // "LINK TO A RECORD" — the search box
    // =================================================================

    public class GetInboundLinkTargetsHandler : ICommandHandler
    {
        private const int PerType = 8;

        /// <summary>Addresses at these never point at one company.</summary>
        private static readonly HashSet<string> FreeMail = new(StringComparer.OrdinalIgnoreCase)
        {
            "gmail.com", "googlemail.com", "yahoo.com", "yahoo.co.in", "yahoo.co.th", "hotmail.com",
            "outlook.com", "live.com", "msn.com", "icloud.com", "me.com", "aol.com", "proton.me",
            "protonmail.com", "rediffmail.com", "zoho.com", "gmx.com", "mail.com", "yandex.com"
        };

        private readonly FlowDbContext _db;
        private readonly IRecordScopeService _scope;

        public GetInboundLinkTargetsHandler(FlowDbContext db, IRecordScopeService scope)
        {
            _db = db;
            _scope = scope;
        }

        /// <summary>
        /// With a search: names, emails and companies containing it. With
        /// none: records at the same company domain as the customer —
        /// "someone else at acme.com is a contact" is usually the answer.
        /// Only records the caller could link to (update + visible).
        /// </summary>
        public async Task<InboundLinkTargetsDto> Handle(
            InboundAccess a, Guid emailId, string? search, CancellationToken ct = default)
        {
            if (!a.InboxUpdate) return new InboundLinkTargetsDto(new());

            var t = a.TenantId;
            var customer = await _db.InboundEmails.AsNoTracking()
                .Where(e => e.Id == emailId && e.TenantId == t && !e.IsDeleted)
                .Select(e => e.CounterpartAddress ?? e.FromAddress)
                .FirstOrDefaultAsync(ct);

            var s = search?.Trim();
            if (s is { Length: > 100 }) s = s[..100];

            string? domainLike = null;
            if (string.IsNullOrEmpty(s) && customer is not null)
            {
                var at = customer.LastIndexOf('@');
                var domain = at > 0 ? customer[(at + 1)..] : null;
                if (domain is not null && !FreeMail.Contains(domain)) domainLike = "@" + domain;
            }

            if (string.IsNullOrEmpty(s) && domainLike is null) return new InboundLinkTargetsDto(new());
            if (s is { Length: < 2 }) return new InboundLinkTargetsDto(new());

            var results = new List<InboundLinkTargetDto>();
            bool Same(string? address) =>
                customer is not null && string.Equals(address?.Trim(), customer, StringComparison.OrdinalIgnoreCase);

            if (a.ContactsUpdate)
            {
                var contacts = _db.Contacts.AsNoTracking().Where(c => c.TenantId == t && !c.IsDeleted);
                contacts = s is not null && s.Length > 0
                    ? contacts.Where(c => c.FirstName.Contains(s) || (c.LastName != null && c.LastName.Contains(s))
                                       || (c.Email != null && c.Email.Contains(s)))
                    : contacts.Where(c => c.Email != null && c.Email.EndsWith(domainLike!));

                var rows = await contacts
                    .OrderBy(c => c.FirstName)
                    .Select(c => new { c.Id, Name = (c.FirstName + " " + (c.LastName ?? "")).Trim(), c.Email,
                                       Company = c.Company != null ? c.Company.Name : null })
                    .Take(PerType).ToListAsync(ct);

                results.AddRange(rows.Select(r => new InboundLinkTargetDto(
                    InboundLinkTypes.Contact, r.Id, r.Name,
                    string.Join(" · ", new[] { r.Email, r.Company }.Where(x => !string.IsNullOrWhiteSpace(x))),
                    Same(r.Email))));
            }

            if (a.LeadsUpdate)
            {
                var access = await _scope.GetAsync(RecordModules.Leads, ct);
                var leads = _db.Leads.AsNoTracking().Where(l => l.TenantId == t && !l.IsDeleted);
                leads = s is not null && s.Length > 0
                    ? leads.Where(l => l.FullName.Contains(s) || (l.Email != null && l.Email.Contains(s))
                                    || (l.CompanyName != null && l.CompanyName.Contains(s)))
                    : leads.Where(l => l.Email != null && l.Email.EndsWith(domainLike!));

                var rows = await leads.VisibleTo(access)
                    .OrderBy(l => l.IsConverted).ThenByDescending(l => l.CreatedAtUtc)
                    .Select(l => new { l.Id, l.FullName, l.Email, l.CompanyName, l.IsConverted })
                    .Take(PerType).ToListAsync(ct);

                results.AddRange(rows.Select(r => new InboundLinkTargetDto(
                    InboundLinkTypes.Lead, r.Id, r.FullName,
                    string.Join(" · ", new[] { r.Email, r.CompanyName, r.IsConverted ? "converted" : null }
                        .Where(x => !string.IsNullOrWhiteSpace(x))),
                    Same(r.Email))));
            }

            if (a.DealsUpdate)
            {
                var access = await _scope.GetAsync(RecordModules.Deals, ct);
                var deals = _db.Deals.AsNoTracking().Where(d => d.TenantId == t && !d.IsDeleted);
                deals = s is not null && s.Length > 0
                    ? deals.Where(d => d.Title.Contains(s)
                                    || (d.Contact != null && d.Contact.Email != null && d.Contact.Email.Contains(s))
                                    || (d.Company != null && d.Company.Name.Contains(s)))
                    : deals.Where(d => d.Contact != null && d.Contact.Email != null && d.Contact.Email.EndsWith(domainLike!));

                var rows = await deals.VisibleTo(access)
                    .OrderByDescending(d => d.UpdatedAtUtc)
                    .Select(d => new { d.Id, d.Title, Company = d.Company != null ? d.Company.Name : null,
                                       ContactEmail = d.Contact != null ? d.Contact.Email : null, d.Stage })
                    .Take(PerType).ToListAsync(ct);

                results.AddRange(rows.Select(r => new InboundLinkTargetDto(
                    InboundLinkTypes.Deal, r.Id, r.Title,
                    string.Join(" · ", new[] { r.Company, r.ContactEmail }.Where(x => !string.IsNullOrWhiteSpace(x))),
                    Same(r.ContactEmail))));
            }

            return new InboundLinkTargetsDto(
                results.OrderByDescending(r => r.EmailMatches).ToList());
        }
    }

    // =================================================================
    // SETTINGS → INBOUND EMAIL
    // =================================================================

    public class InboundEmailSettingsHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly EmailOptions _options;
        private readonly IAuditService _audit;
        private readonly ILogger<InboundEmailSettingsHandler> _logger;

        public InboundEmailSettingsHandler(
            FlowDbContext db, IOptions<EmailOptions> options, IAuditService audit,
            ILogger<InboundEmailSettingsHandler> logger)
        {
            _db = db;
            _options = options.Value;
            _audit = audit;
            _logger = logger;
        }

        /// <summary>
        /// The workspace's address and the setup checklist. Creates the
        /// address the first time it is asked for — so it exists the moment
        /// anybody could need it, and never before.
        /// </summary>
        public async Task<InboundEmailSettingsDto> GetAsync(InboundAccess a, CancellationToken ct = default)
        {
            if (!a.SettingsRead) throw new UnauthorizedAccessException("You do not have access to workspace settings.");

            var alias = await EnsureAliasAsync(a.TenantId, ct);
            var t = a.TenantId;
            var since = DateTime.UtcNow.AddDays(-30);

            var emails = _db.InboundEmails.AsNoTracking().Where(e => e.TenantId == t && !e.IsDeleted);

            var last = await emails.OrderByDescending(e => e.ReceivedAtUtc)
                .Select(e => (DateTime?)e.ReceivedAtUtc).FirstOrDefaultAsync(ct);

            var domain = _options.NormalisedInboundDomain;

            return new InboundEmailSettingsDto(
                WorkspaceAddress:        domain is null || alias is null ? null : InboundAddressing.WorkspaceAddress(alias, domain),
                Alias:                   alias,
                Domain:                  domain,
                DomainConfigured:        domain is not null,
                WebhookSecretConfigured: !string.IsNullOrWhiteSpace(_options.InboundWebhookSecret),
                ApiKeyConfigured:        _options.IsConfigured,
                ReplyTrackingOn:         _options.IsInboundAddressingOn,
                LastReceivedAtUtc:       last,
                ReceivedLast30Days:      await emails.CountAsync(e => e.ReceivedAtUtc >= since, ct),
                UnmatchedCount:          await emails.CountAsync(e => e.Status == InboundEmailStatus.Unmatched, ct),
                WaitingCount:            await emails.CountAsync(e => e.ProcessingState == InboundProcessingState.Pending
                                                                   || e.ProcessingState == InboundProcessingState.Processing
                                                                   || e.ProcessingState == InboundProcessingState.Retry, ct),
                FailedCount:             await emails.CountAsync(e => e.ProcessingState == InboundProcessingState.Failed, ct));
        }

        /// <summary>
        /// A new address; the old one stops working at once. For when the
        /// old one has leaked onto a spam list. Reply addresses on emails
        /// already sent are NOT affected — they carry their own tokens.
        /// </summary>
        public async Task<InboundEmailSettingsDto> RegenerateAsync(InboundAccess a, CancellationToken ct = default)
        {
            if (!a.SettingsUpdate) throw new UnauthorizedAccessException("You cannot change workspace settings.");

            var tenant = await _db.Tenants.FirstOrDefaultAsync(x => x.Id == a.TenantId, ct)
                         ?? throw new KeyNotFoundException("Workspace not found.");

            var previous = tenant.InboundAlias;
            await AssignNewAliasAsync(tenant, ct);

            await _audit.WriteAsync(AuditAction.InboundAddressChanged, AuditEntityType.Tenant, tenant.Id, tenant.Id,
                new { previous, current = tenant.InboundAlias }, ct);

            return await GetAsync(a, ct);
        }

        private async Task<string?> EnsureAliasAsync(Guid tenantId, CancellationToken ct)
        {
            var tenant = await _db.Tenants.FirstOrDefaultAsync(x => x.Id == tenantId, ct);
            if (tenant is null) return null;
            if (!string.IsNullOrWhiteSpace(tenant.InboundAlias)) return tenant.InboundAlias;

            await AssignNewAliasAsync(tenant, ct);
            return tenant.InboundAlias;
        }

        /// <summary>
        /// Random, so a clash is a one-in-a-billion event — but the unique
        /// index is the real guarantee, and a clash just tries again.
        /// </summary>
        private async Task AssignNewAliasAsync(Tenant tenant, CancellationToken ct)
        {
            for (var attempt = 1; ; attempt++)
            {
                var candidate = InboundAddressing.NewAlias(tenant.Name);

                var taken = await _db.Tenants.AsNoTracking()
                    .AnyAsync(x => x.InboundAlias == candidate && x.Id != tenant.Id, ct);
                if (taken && attempt < 5) continue;

                tenant.InboundAlias = candidate;
                tenant.UpdatedAtUtc = DateTime.UtcNow;

                try
                {
                    await _db.SaveChangesAsync(ct);
                    _logger.LogInformation("Workspace {TenantId} has a new receiving address", tenant.Id);
                    return;
                }
                catch (DbUpdateException) when (attempt < 5)
                {
                    // Lost a race for the same random value. Try another.
                    _db.Entry(tenant).Property(x => x.InboundAlias).CurrentValue = null;
                }
            }
        }
    }
}
