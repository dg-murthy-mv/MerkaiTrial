// =====================================================================
// NotificationSettingsHandlers.cs
// Location: MerkaiTrial.Application/Commands/Notifications/NotificationSettingsHandlers.cs
//
// NEW FILE (038). Registered by the Scrutor scan through ICommandHandler.
//
// TWO AUDIENCES, DELIBERATELY DIFFERENT SCOPES
//
//   Preferences  — personal. Every query filters on UserId == the caller,
//                  exactly like 037's notification handlers. There is no
//                  "edit someone else's preferences" path, not for an
//                  admin either.
//
//   Email log    — the WORKSPACE's outgoing mail. Tenant-scoped, and the
//                  controller restricts it to workspace admins. A log
//                  showing every colleague's notification emails is not
//                  something an ordinary user should browse.
//
// THE LOG DOES NOT SHOW MESSAGE BODIES
//   Subject, recipient, status and error only. A rendered body can quote a
//   deal value or a customer's name, and a log page is exactly the sort of
//   screen that gets left open on a shared monitor. Anyone who genuinely
//   needs the body can read the row in the database.
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Notifications;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MerkaiTrial.Application.Commands.Notifications
{
    // =================================================================
    // PREFERENCES
    // =================================================================

    public class GetNotificationPreferencesHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;
        private readonly EmailOptions _email;

        public GetNotificationPreferencesHandler(
            FlowDbContext db, ICurrentUserService currentUser, IOptions<EmailOptions> email)
        {
            _db = db;
            _currentUser = currentUser;
            _email = email.Value;
        }

        public async Task<NotificationPreferencesDto> Handle(CancellationToken ct = default)
        {
            var me = await _currentUser.GetCurrentUserAsync();

            var saved = await _db.UserNotificationPreferences.AsNoTracking()
                .Where(p => p.TenantId == me.TenantId && p.UserId == me.UserId)
                .ToDictionaryAsync(p => p.EventType, ct);

            // 040: the workspace's defaults and locks. The page MUST render
            // what the dispatcher will actually do, so it calls the same
            // NotificationResolution.For — anything else and the page is
            // telling the user something untrue about their own settings.
            var tenantDefaults = await _db.TenantNotificationDefaults.AsNoTracking()
                .Where(d => d.TenantId == me.TenantId)
                .ToDictionaryAsync(d => d.EventType, ct);

            var myAddress = await _db.Users.AsNoTracking()
                .Where(u => u.Id == me.UserId && u.TenantId == me.TenantId)
                .Select(u => u.Email)
                .FirstOrDefaultAsync(ct);

            // Built from the enum's display order, not from the saved rows:
            // a user who has never touched this page still sees every event,
            // and an event type added later appears for everyone at once.
            var items = NotificationDefaults.AllInDisplayOrder
                .Select(eventType =>
                {
                    saved.TryGetValue(eventType, out var row);
                    tenantDefaults.TryGetValue(eventType, out var workspace);

                    var resolved = NotificationResolution.For(eventType, workspace, row);

                    return new NotificationPreferenceDto(
                        EventType: eventType.ToString(),
                        Label: NotificationDefaults.Label(eventType),
                        Group: NotificationDefaults.Group(eventType),
                        InApp: resolved.InApp,
                        Email: resolved.Email,

                        // "Using the default" now covers both the built-in
                        // default and the workspace's — from the user's point
                        // of view they are the same thing: I have not chosen.
                        IsDefault: !resolved.IsUserChoice,
                        IsLocked: resolved.IsLocked);
                })
                .ToList();

            return new NotificationPreferencesDto(items, _email.IsConfigured, myAddress);
        }
    }

    public class SaveNotificationPreferencesHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;

        public SaveNotificationPreferencesHandler(FlowDbContext db, ICurrentUserService currentUser)
        {
            _db = db;
            _currentUser = currentUser;
        }

        public async Task Handle(SaveNotificationPreferencesDto dto, CancellationToken ct = default)
        {
            if (dto?.Items is null || dto.Items.Count == 0) return;

            var me = await _currentUser.GetCurrentUserAsync();
            var now = DateTime.UtcNow;

            var existing = await _db.UserNotificationPreferences
                .Where(p => p.TenantId == me.TenantId && p.UserId == me.UserId)
                .ToListAsync(ct);

            // 040: locked events are refused HERE, server-side. The page
            // renders them disabled, but a disabled input is a courtesy —
            // anyone can post whatever they like. This is the control.
            var lockedEvents = await _db.TenantNotificationDefaults.AsNoTracking()
                .Where(d => d.TenantId == me.TenantId && d.IsLocked)
                .Select(d => d.EventType)
                .ToListAsync(ct);

            foreach (var item in dto.Items)
            {
                // An unparseable event type is ignored rather than throwing.
                // The page posts whatever it rendered, and a stale tab from
                // before an enum change should not produce a 400.
                if (!Enum.TryParse<NotificationEventType>(item.EventType, out var eventType))
                    continue;

                // Locked by the workspace: silently ignored rather than
                // throwing. The page submits every row it rendered, so a
                // locked one arriving is normal traffic, not an attack — and
                // failing the whole save because of it would stop the user
                // changing the rows they ARE allowed to change.
                if (lockedEvents.Contains(eventType))
                    continue;

                var row = existing.FirstOrDefault(p => p.EventType == eventType);

                if (row is null)
                {
                    // A row is written even when the values match the
                    // defaults. "I looked at this and chose this" is a
                    // different fact from "I never looked", and it is what
                    // keeps a future change to the defaults from silently
                    // overriding a deliberate choice.
                    _db.UserNotificationPreferences.Add(new UserNotificationPreference
                    {
                        Id = Guid.NewGuid(),
                        TenantId = me.TenantId,
                        UserId = me.UserId,
                        EventType = eventType,
                        InApp = item.InApp,
                        Email = item.Email,
                        UpdatedAtUtc = now,
                        UpdatedBy = me.FullName
                    });
                }
                else if (row.InApp != item.InApp || row.Email != item.Email)
                {
                    row.InApp = item.InApp;
                    row.Email = item.Email;
                    row.UpdatedAtUtc = now;
                    row.UpdatedBy = me.FullName;
                }
            }

            await _db.SaveChangesAsync(ct);
        }
    }

    // =================================================================
    // WORKSPACE DEFAULTS (040) — admins only, enforced by the controller
    // =================================================================

    /// <summary>
    /// The workspace defaults tab.
    ///
    /// The counts are the point of this screen. The bug that produced this
    /// feature was invisible: an admin ticked Email on their OWN settings
    /// page, assumed the workspace was covered, and nothing in the UI said
    /// otherwise. "6 of 8 people will get this by email" is the sentence
    /// that would have prevented it.
    /// </summary>
    public class GetTenantNotificationDefaultsHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;
        private readonly EmailOptions _email;

        public GetTenantNotificationDefaultsHandler(
            FlowDbContext db, ICurrentUserService currentUser, IOptions<EmailOptions> email)
        {
            _db = db;
            _currentUser = currentUser;
            _email = email.Value;
        }

        public async Task<TenantNotificationDefaultsDto> Handle(CancellationToken ct = default)
        {
            var me = await _currentUser.GetCurrentUserAsync();

            var defaults = await _db.TenantNotificationDefaults.AsNoTracking()
                .Where(d => d.TenantId == me.TenantId)
                .ToDictionaryAsync(d => d.EventType, ct);

            // Every active user, with whether they have an address at all.
            // Users is not tenant-filtered, so the TenantId predicate here is
            // load-bearing rather than decorative.
            var users = await _db.Users.AsNoTracking()
                .Where(u => u.TenantId == me.TenantId && u.IsActive && !u.IsDeleted)
                .Select(u => new { u.Id, HasEmail = u.Email != null && u.Email != "" })
                .ToListAsync(ct);

            // Every override in the workspace, in one query rather than one
            // per event. A workspace of 50 users and 8 events is 400 rows at
            // the absolute worst, and normally a handful.
            var overrides = await _db.UserNotificationPreferences.AsNoTracking()
                .Where(p => p.TenantId == me.TenantId)
                .Select(p => new { p.UserId, p.EventType, p.InApp, p.Email })
                .ToListAsync(ct);

            var items = new List<TenantNotificationDefaultDto>();

            foreach (var eventType in NotificationDefaults.AllInDisplayOrder)
            {
                defaults.TryGetValue(eventType, out var workspace);

                var byUser = overrides
                    .Where(o => o.EventType == eventType)
                    .ToDictionary(o => o.UserId);

                int inAppCount = 0, emailCount = 0, missingAddress = 0;

                foreach (var user in users)
                {
                    byUser.TryGetValue(user.Id, out var o);

                    // Reconstructed rather than loaded as an entity: the
                    // projection above is deliberately narrow, and
                    // NotificationResolution only reads these two fields.
                    var pref = o is null
                        ? null
                        : new UserNotificationPreference { InApp = o.InApp, Email = o.Email };

                    // THE SAME resolution the dispatcher uses. If this screen
                    // computed it differently it would report numbers that do
                    // not match what actually happens, which is worse than
                    // showing nothing.
                    var resolved = NotificationResolution.For(eventType, workspace, pref);

                    if (resolved.InApp) inAppCount++;

                    if (resolved.Email)
                    {
                        if (user.HasEmail) emailCount++;
                        else missingAddress++;
                    }
                }

                items.Add(new TenantNotificationDefaultDto(
                    EventType: eventType.ToString(),
                    Label: NotificationDefaults.Label(eventType),
                    Group: NotificationDefaults.Group(eventType),
                    InApp: workspace?.InApp ?? NotificationDefaults.InAppFor(eventType),
                    Email: workspace?.Email ?? NotificationDefaults.EmailFor(eventType),
                    IsLocked: workspace?.IsLocked ?? false,
                    IsConfigured: workspace is not null,
                    InAppRecipientCount: inAppCount,
                    EmailRecipientCount: emailCount,
                    MissingEmailAddressCount: missingAddress,

                    // Only counts as an override where it can actually take
                    // effect. Under a lock the user's row is ignored, so
                    // reporting it as an override would be misleading.
                    OverriddenCount: workspace is { IsLocked: true } ? 0 : byUser.Count));
            }

            return new TenantNotificationDefaultsDto(items, users.Count, _email.IsConfigured);
        }
    }

    public class SaveTenantNotificationDefaultsHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;

        public SaveTenantNotificationDefaultsHandler(
            FlowDbContext db, ICurrentUserService currentUser)
        {
            _db = db;
            _currentUser = currentUser;
        }

        public async Task Handle(
            SaveTenantNotificationDefaultsDto dto, CancellationToken ct = default)
        {
            if (dto?.Items is null || dto.Items.Count == 0) return;

            var me = await _currentUser.GetCurrentUserAsync();

            // Defence in depth. The controller checks this too, but "only an
            // admin may set workspace policy" is important enough to assert
            // in the handler as well — a future caller that forgets the
            // controller check would otherwise be a silent privilege hole.
            if (!me.IsTenantAdmin)
                throw new UnauthorizedAccessException(
                    "Only workspace admins can change notification defaults.");

            var now = DateTime.UtcNow;

            var existing = await _db.TenantNotificationDefaults
                .Where(d => d.TenantId == me.TenantId)
                .ToListAsync(ct);

            foreach (var item in dto.Items)
            {
                if (!Enum.TryParse<NotificationEventType>(item.EventType, out var eventType))
                    continue;

                var row = existing.FirstOrDefault(d => d.EventType == eventType);

                if (row is null)
                {
                    _db.TenantNotificationDefaults.Add(new TenantNotificationDefault
                    {
                        Id = Guid.NewGuid(),
                        TenantId = me.TenantId,
                        EventType = eventType,
                        InApp = item.InApp,
                        Email = item.Email,
                        IsLocked = item.IsLocked,
                        UpdatedAtUtc = now,
                        UpdatedBy = me.FullName
                    });
                }
                else if (row.InApp != item.InApp
                      || row.Email != item.Email
                      || row.IsLocked != item.IsLocked)
                {
                    row.InApp = item.InApp;
                    row.Email = item.Email;
                    row.IsLocked = item.IsLocked;
                    row.UpdatedAtUtc = now;
                    row.UpdatedBy = me.FullName;
                }
            }

            // NOTE: users' own rows are deliberately left alone when an event
            // is locked. They are ignored while the lock is on, and come back
            // if it is lifted. Deleting them would mean unlocking silently
            // reset everyone's choice — a destructive side effect of what
            // looks like a reversible toggle.
            await _db.SaveChangesAsync(ct);
        }
    }

    // =================================================================
    // THE EMAIL LOG
    // =================================================================

    public class GetOutboundMessagesHandler : ICommandHandler
    {
        private const int MaxPageSize = 100;

        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;
        private readonly EmailOptions _email;

        public GetOutboundMessagesHandler(
            FlowDbContext db, ICurrentUserService currentUser, IOptions<EmailOptions> email)
        {
            _db = db;
            _currentUser = currentUser;
            _email = email.Value;
        }

        public async Task<OutboundMessagePageDto> Handle(
            string? status = null,
            int skip = 0,
            int take = 25,
            CancellationToken ct = default)
        {
            take = Math.Clamp(take, 1, MaxPageSize);
            skip = Math.Max(0, skip);

            var me = await _currentUser.GetCurrentUserAsync();

            // No IgnoreQueryFilters here, unlike the worker: this IS a
            // tenant-scoped read for a signed-in person, so the global
            // filter is exactly what should apply. The explicit TenantId
            // predicate is the usual belt and braces.
            var all = _db.OutboundMessages.AsNoTracking()
                .Where(m => m.TenantId == me.TenantId);

            var totalCount = await all.CountAsync(ct);
            var pendingCount = await all.CountAsync(
                m => m.Status == OutboundStatus.Pending
                  || m.Status == OutboundStatus.Failed
                  || m.Status == OutboundStatus.Sending, ct);
            var deadCount = await all.CountAsync(m => m.Status == OutboundStatus.Dead, ct);

            var filtered = all;
            if (!string.IsNullOrWhiteSpace(status)
                && Enum.TryParse<OutboundStatus>(status, ignoreCase: true, out var wanted))
            {
                filtered = filtered.Where(m => m.Status == wanted);
            }

            var rows = await filtered
                .OrderByDescending(m => m.CreatedAtUtc)
                .Skip(skip)
                .Take(take + 1)          // one extra answers "is there more"
                .Select(m => new OutboundMessageDto(
                    m.Id,
                    m.Channel.ToString(),
                    m.Status.ToString(),
                    m.EventType.ToString(),
                    m.ToAddress,
                    m.ToName,
                    m.Subject,
                    m.AttemptCount,
                    m.CreatedAtUtc,
                    m.SentAtUtc,
                    m.NextAttemptAtUtc,
                    m.LastError,
                    m.ProviderMessageId,
                    m.EntityType,
                    m.EntityId))
                .ToListAsync(ct);

            var hasMore = rows.Count > take;
            if (hasMore) rows.RemoveAt(rows.Count - 1);

            return new OutboundMessagePageDto(
                rows, totalCount, pendingCount, deadCount, hasMore, _email.IsConfigured);
        }
    }

    /// <summary>
    /// Puts a Dead, Failed or Cancelled message back in the queue.
    ///
    /// AttemptCount is reset to zero, on purpose: a person retrying has
    /// normally just fixed the cause — a typo in an address, an expired API
    /// key — and should get the full backoff ladder again rather than one
    /// last try. The message keeps its Id, so the provider idempotency key
    /// is unchanged and a message Resend already accepted is still not sent
    /// twice.
    /// </summary>
    public class RetryOutboundMessageHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;

        public RetryOutboundMessageHandler(FlowDbContext db, ICurrentUserService currentUser)
        {
            _db = db;
            _currentUser = currentUser;
        }

        public async Task Handle(Guid messageId, CancellationToken ct = default)
        {
            var me = await _currentUser.GetCurrentUserAsync();

            var row = await _db.OutboundMessages
                .FirstOrDefaultAsync(m => m.Id == messageId && m.TenantId == me.TenantId, ct);

            if (row is null)
                throw new KeyNotFoundException("That message no longer exists.");

            if (row.Status == OutboundStatus.Sent)
                throw new InvalidOperationException(
                    "That message was already sent. Re-sending it would deliver a duplicate.");

            if (row.Status == OutboundStatus.Sending)
                throw new InvalidOperationException(
                    "That message is being sent right now. Give it a moment.");

            row.Status = OutboundStatus.Pending;
            row.AttemptCount = 0;
            row.NextAttemptAtUtc = null;     // due now
            row.LockedUntilUtc = null;
            row.LastError = null;
            row.UpdatedAtUtc = DateTime.UtcNow;
            row.UpdatedBy = me.FullName;

            await _db.SaveChangesAsync(ct);
        }
    }

    public class CancelOutboundMessageHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;

        public CancelOutboundMessageHandler(FlowDbContext db, ICurrentUserService currentUser)
        {
            _db = db;
            _currentUser = currentUser;
        }

        public async Task Handle(Guid messageId, CancellationToken ct = default)
        {
            var me = await _currentUser.GetCurrentUserAsync();

            var row = await _db.OutboundMessages
                .FirstOrDefaultAsync(m => m.Id == messageId && m.TenantId == me.TenantId, ct);

            if (row is null)
                throw new KeyNotFoundException("That message no longer exists.");

            if (row.Status == OutboundStatus.Sent)
                throw new InvalidOperationException("That message has already gone out.");

            if (row.Status == OutboundStatus.Sending)
                throw new InvalidOperationException(
                    "That message is being sent right now and can no longer be stopped.");

            row.Status = OutboundStatus.Cancelled;
            row.NextAttemptAtUtc = null;
            row.LockedUntilUtc = null;
            row.UpdatedAtUtc = DateTime.UtcNow;
            row.UpdatedBy = me.FullName;

            await _db.SaveChangesAsync(ct);
        }
    }
}
