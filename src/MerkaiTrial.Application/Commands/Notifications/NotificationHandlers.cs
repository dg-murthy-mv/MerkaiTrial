// =====================================================================
// NotificationHandlers.cs
// Location: MerkaiTrial.Application/Commands/Notifications/NotificationHandlers.cs
//
// NEW FILE (037).
//
// Reading and clearing notifications. Registered by the Scrutor scan
// through ICommandHandler, so nothing to add to Program.cs.
//
// EVERY QUERY IS SCOPED TO THE CALLER, TWICE
//   The global query filter gives TenantId. RecipientUserId is added at
//   every single call site here, because it is the only thing standing
//   between one colleague's notifications and another's — and unlike the
//   tenant filter, nothing in the model enforces it. There is no "read
//   someone else's notifications" path, not even for a workspace admin:
//   these are personal, and an admin who needs to know what happened has
//   the audit log.
//
// NO MODULE PERMISSION
//   Deliberate. Every signed-in user sees their own notifications,
//   including one whose role grants almost nothing. Gating this behind a
//   module permission would hide the bell from exactly the read-only users
//   who most need to be told when something is waiting for them.
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.Notifications
{
    /// <summary>
    /// Shared projection, so the bell dropdown and the page can never show
    /// the same notification differently.
    /// </summary>
    internal static class NotificationProjection
    {
        public static IQueryable<NotificationDto> Project(IQueryable<Notification> q) =>
            q.Select(n => new NotificationDto(
                n.Id,
                n.EventType.ToString(),
                n.Title,
                n.Body,
                n.EntityType,
                n.EntityId,
                n.ActorName,
                n.CreatedAtUtc,
                n.ReadAtUtc != null));

        /// <summary>
        /// This person's live notifications: theirs, not dismissed. The
        /// RecipientUserId predicate is the important one — see the header.
        /// </summary>
        public static IQueryable<Notification> Mine(
            FlowDbContext db, Guid tenantId, Guid userId) =>
            db.Notifications
              .Where(n => n.TenantId == tenantId
                       && n.RecipientUserId == userId
                       && n.DismissedAtUtc == null);
    }

    // =================================================================
    // THE BELL — the most frequently called endpoint in the app
    // =================================================================

    /// <summary>
    /// Count plus the newest few, in one round trip. Every open browser asks
    /// for this every 45 seconds, so it is two cheap queries against
    /// IX_Notifications_Unread and IX_Notifications_Feed and nothing else.
    /// </summary>
    public class GetNotificationSummaryHandler : ICommandHandler
    {
        private const int DropdownSize = 6;

        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;

        public GetNotificationSummaryHandler(FlowDbContext db, ICurrentUserService currentUser)
        {
            _db = db;
            _currentUser = currentUser;
        }

        public async Task<NotificationSummaryDto> Handle(CancellationToken ct = default)
        {
            var me = await _currentUser.GetCurrentUserAsync();
            var mine = NotificationProjection.Mine(_db, me.TenantId, me.UserId).AsNoTracking();

            var unread = await mine.CountAsync(n => n.ReadAtUtc == null, ct);

            var recent = await NotificationProjection
                .Project(mine.OrderByDescending(n => n.CreatedAtUtc).Take(DropdownSize))
                .ToListAsync(ct);

            return new NotificationSummaryDto(unread, recent);
        }
    }

    // =================================================================
    // THE PAGE
    // =================================================================

    public class GetNotificationsHandler : ICommandHandler
    {
        private const int MaxPageSize = 100;

        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;

        public GetNotificationsHandler(FlowDbContext db, ICurrentUserService currentUser)
        {
            _db = db;
            _currentUser = currentUser;
        }

        public async Task<NotificationPageDto> Handle(
            bool unreadOnly = false,
            int skip = 0,
            int take = 25,
            CancellationToken ct = default)
        {
            // Clamped rather than trusted. take comes off a query string.
            take = Math.Clamp(take, 1, MaxPageSize);
            skip = Math.Max(0, skip);

            var me = await _currentUser.GetCurrentUserAsync();
            var mine = NotificationProjection.Mine(_db, me.TenantId, me.UserId).AsNoTracking();

            // Both counts describe the WHOLE list, not the page, so the tabs
            // read correctly while you are on page three.
            var unreadCount = await mine.CountAsync(n => n.ReadAtUtc == null, ct);
            var totalCount = await mine.CountAsync(ct);

            var filtered = unreadOnly ? mine.Where(n => n.ReadAtUtc == null) : mine;

            // take + 1 rather than a third COUNT: one extra row answers
            // "is there more" for free.
            var rows = await NotificationProjection
                .Project(filtered.OrderByDescending(n => n.CreatedAtUtc)
                                 .Skip(skip)
                                 .Take(take + 1))
                .ToListAsync(ct);

            var hasMore = rows.Count > take;
            if (hasMore) rows.RemoveAt(rows.Count - 1);

            return new NotificationPageDto(rows, unreadCount, totalCount, hasMore);
        }
    }

    // =================================================================
    // MARK READ
    // =================================================================

    public class MarkNotificationReadHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;

        public MarkNotificationReadHandler(FlowDbContext db, ICurrentUserService currentUser)
        {
            _db = db;
            _currentUser = currentUser;
        }

        /// <summary>
        /// Returns the notification so the caller can follow its link. Null
        /// when it is not this person's — deliberately not an error: clicking
        /// a stale item from a browser tab left open overnight should be a
        /// no-op, not a 404 page.
        /// </summary>
        public async Task<NotificationDto?> Handle(Guid notificationId, CancellationToken ct = default)
        {
            var me = await _currentUser.GetCurrentUserAsync();

            var row = await _db.Notifications
                .FirstOrDefaultAsync(n => n.Id == notificationId
                                       && n.TenantId == me.TenantId
                                       && n.RecipientUserId == me.UserId, ct);

            if (row is null) return null;

            // Idempotent: clicking twice keeps the first read time.
            if (row.ReadAtUtc is null)
            {
                row.ReadAtUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }

            return new NotificationDto(
                row.Id, row.EventType.ToString(), row.Title, row.Body,
                row.EntityType, row.EntityId, row.ActorName, row.CreatedAtUtc, true);
        }
    }

    public class MarkAllNotificationsReadHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;

        public MarkAllNotificationsReadHandler(FlowDbContext db, ICurrentUserService currentUser)
        {
            _db = db;
            _currentUser = currentUser;
        }

        /// <summary>Returns how many were still unread.</summary>
        public async Task<int> Handle(CancellationToken ct = default)
        {
            var me = await _currentUser.GetCurrentUserAsync();
            var now = DateTime.UtcNow;

            // ExecuteUpdate: one UPDATE statement instead of loading every
            // unread row into the change tracker. A user coming back from
            // leave can have hundreds.
            return await _db.Notifications
                .Where(n => n.TenantId == me.TenantId
                         && n.RecipientUserId == me.UserId
                         && n.DismissedAtUtc == null
                         && n.ReadAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, now), ct);
        }
    }

    // =================================================================
    // DISMISS
    // =================================================================

    public class DismissNotificationHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;

        public DismissNotificationHandler(FlowDbContext db, ICurrentUserService currentUser)
        {
            _db = db;
            _currentUser = currentUser;
        }

        public async Task Handle(Guid notificationId, CancellationToken ct = default)
        {
            var me = await _currentUser.GetCurrentUserAsync();
            var now = DateTime.UtcNow;

            // Dismissing also marks it read. Leaving a dismissed row unread
            // would keep it in the bell count while hiding it from the list,
            // which is a count nobody can ever clear.
            await _db.Notifications
                .Where(n => n.Id == notificationId
                         && n.TenantId == me.TenantId
                         && n.RecipientUserId == me.UserId
                         && n.DismissedAtUtc == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(n => n.DismissedAtUtc, now)
                    .SetProperty(n => n.ReadAtUtc, n => n.ReadAtUtc ?? now), ct);
        }
    }

    public class DismissReadNotificationsHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;

        public DismissReadNotificationsHandler(FlowDbContext db, ICurrentUserService currentUser)
        {
            _db = db;
            _currentUser = currentUser;
        }

        /// <summary>
        /// "Clear read" — hides everything already read, leaving anything
        /// unread alone. Deliberately not "clear all": clearing something you
        /// have not looked at is how people miss an approval request.
        /// </summary>
        public async Task<int> Handle(CancellationToken ct = default)
        {
            var me = await _currentUser.GetCurrentUserAsync();
            var now = DateTime.UtcNow;

            return await _db.Notifications
                .Where(n => n.TenantId == me.TenantId
                         && n.RecipientUserId == me.UserId
                         && n.DismissedAtUtc == null
                         && n.ReadAtUtc != null)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.DismissedAtUtc, now), ct);
        }
    }
}
