// =====================================================================
// TenantNotificationDefault.cs
// Location: MerkaiTrial.Domain/Entities/TenantNotificationDefault.cs
//
// NEW FILE (040).
//
// THE PROBLEM THIS SOLVES
//   Preferences are per-user, which is right — "how do I want to be
//   contacted" belongs to the person. But it left a workspace admin with
//   no lever at all:
//
//     • A new user has no rows, so they get whatever we hardcoded. An
//       admin who wants approval emails on for their managers had no way
//       to arrange it.
//     • A manager could turn approval emails off, and quotes would then
//       sit unapproved with nobody finding out for a week.
//
//   Neither is acceptable in a product the admin is accountable for.
//
// TWO SETTINGS, DOING DIFFERENT JOBS
//
//   InApp / Email    the STARTING POSITION for this workspace. Replaces
//                    the hardcoded default. A user can still change their
//                    own.
//
//   IsLocked         the user CANNOT change it. Their settings page shows
//                    the row greyed, reading "Set by your workspace
//                    admin", and the API refuses an override server-side
//                    rather than trusting the page to hide it.
//
// USE THE LOCK SPARINGLY
//   Lock the approval events, where a missed notification stops someone
//   else working. Do not lock "a lead was assigned to me". Someone who
//   feels shouted at stops reading notifications altogether — and then the
//   locked ones get ignored too, which costs you the exact thing the lock
//   was for.
//
// RESOLVED AT READ TIME, NOT COPIED ONTO USERS
//   No rows are seeded onto users when this changes, and none are seeded
//   at provisioning. The dispatcher resolves the answer each time a
//   notification is raised:
//
//       locked?    -> the tenant's value, full stop
//       otherwise  -> the user's row, else this row, else NotificationDefaults
//
//   That is what lets a ninth event type added next year pick up a
//   sensible default for everyone immediately. Seeding rows would leave
//   every existing user with no row for it, and therefore silently
//   nothing.
// =====================================================================

namespace MerkaiTrial.Domain.Entities
{
    public class TenantNotificationDefault
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid TenantId { get; set; }

        public NotificationEventType EventType { get; set; }

        /// <summary>The workspace's starting position for the bell.</summary>
        public bool InApp { get; set; } = true;

        /// <summary>The workspace's starting position for email.</summary>
        public bool Email { get; set; }

        /// <summary>
        /// When true, InApp and Email above are FORCED: a user's own row is
        /// ignored for this event, and the API refuses to save one.
        /// </summary>
        public bool IsLocked { get; set; }

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public string? UpdatedBy { get; set; }

        // Navigation
        public Tenant? Tenant { get; set; }
    }

    /// <summary>
    /// How one person's setting for one event is decided, once the tenant
    /// default and the user's own row have both been looked up.
    ///
    /// Kept here, beside the entity, so the dispatcher and the settings
    /// page cannot disagree about what "locked" means — the page has to
    /// render exactly what the dispatcher will do, or the whole feature
    /// lies to the user.
    /// </summary>
    public static class NotificationResolution
    {
        public readonly record struct Resolved(bool InApp, bool Email, bool IsLocked, bool IsUserChoice);

        public static Resolved For(
            NotificationEventType eventType,
            TenantNotificationDefault? tenantDefault,
            UserNotificationPreference? userPreference)
        {
            // Locked: the tenant's answer, whatever the user saved. Their
            // row may well still exist from before the lock — it is ignored,
            // not deleted, so unlocking restores their choice rather than
            // silently resetting everyone.
            if (tenantDefault is { IsLocked: true })
                return new Resolved(tenantDefault.InApp, tenantDefault.Email, true, false);

            var inApp = userPreference?.InApp
                     ?? tenantDefault?.InApp
                     ?? NotificationDefaults.InAppFor(eventType);

            var email = userPreference?.Email
                     ?? tenantDefault?.Email
                     ?? NotificationDefaults.EmailFor(eventType);

            return new Resolved(inApp, email, false, userPreference is not null);
        }
    }
}
