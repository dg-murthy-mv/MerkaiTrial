// =====================================================================
// TaskNotifications.cs
// Location: MerkaiTrial.Application/Services/Notifications/TaskNotifications.cs
//
// NEW FILE (091).
//
// WHY THIS EXISTS
//   A task reaches someone on THREE paths:
//
//     CreateActivityHandler    a task given to a colleague when it is made
//                              (lead, deal, contact or company page)
//     ReassignActivityHandler  "Give to" on the Tasks page
//     UpdateActivityHandler    the record page's edit form, Assign to
//
//   Same reason as DealNotifications: one wording, written once, so the
//   three paths cannot drift apart.
//
// THIS BUILDS A REQUEST — IT DOES NOT SEND ANYTHING
//   The caller hands it to INotificationDispatcher BEFORE its own
//   SaveChangesAsync, so the notification commits with the task. The
//   dispatcher drops the person who did it, so giving a task to yourself
//   tells nobody.
//
// THE DUE DATE IS WRITTEN IN THE WORKSPACE'S TIME
//   "Due 17 Oct 2026, 15:30", in the workspace's time zone. Spelled-out
//   month on purpose: this layer has no view of the workspace's date
//   format, and "17 Oct" cannot be misread the way 10/17 and 17/10 can.
//   The title is stored once and never recomputed (see Notification), so
//   the time must be right when it is written.
// =====================================================================

using System.Globalization;
using MerkaiTrial.Domain.Entities;

namespace MerkaiTrial.Application.Services.Notifications;

public static class TaskNotifications
{
    /// <summary>
    /// "You were given a task: Send the revised proposal". Goes to the
    /// task's assignee.
    ///
    /// The link: a task on a LEAD or a DEAL opens that record — the task is
    /// listed on its Tasks tab. A task on a contact or a company points at
    /// the task ("Activity"), which opens the person's Tasks page.
    /// </summary>
    /// <param name="recordType">The task's EntityType: Lead, Deal, Contact, Company.</param>
    /// <param name="recordName">The lead's name, the deal's title… Null when unknown.</param>
    /// <param name="activityType">Task, Call, Meeting… "Task" itself is not repeated in the body.</param>
    /// <param name="dueUtc">The task's due date, UTC.</param>
    /// <param name="timeZoneId">The workspace's time zone ("Asia/Kolkata"). Null or unknown → UTC, said so.</param>
    public static NotificationRequest Assigned(
        Guid tenantId,
        Guid activityId,
        string subject,
        string activityType,
        string recordType,
        Guid recordId,
        string? recordName,
        DateTime? dueUtc,
        string? timeZoneId,
        Guid? actorUserId,
        string? actorName)
    {
        var parts = new List<string>();

        // "Call · Due …" for a scheduled call; a plain to-do needs no label.
        if (!string.IsNullOrWhiteSpace(activityType)
            && !string.Equals(activityType, "Task", StringComparison.OrdinalIgnoreCase))
            parts.Add(activityType);

        if (dueUtc is { } due)
            parts.Add("Due " + FormatDue(due, timeZoneId));

        if (!string.IsNullOrWhiteSpace(recordName))
            parts.Add($"{recordType}: {recordName!.Trim()}");

        var linksToRecord = recordType is "Lead" or "Deal";

        return new NotificationRequest(
            TenantId: tenantId,
            EventType: NotificationEventType.TaskAssigned,
            Title: $"You were given a task: {subject.Trim()}",
            Body: parts.Count > 0 ? string.Join(" · ", parts) : null,
            EntityType: linksToRecord ? recordType : "Activity",
            EntityId: linksToRecord ? recordId : activityId,
            ActorUserId: actorUserId,
            ActorName: actorName,

            // WhatsApp's {{headline}}: the task itself, without the verb.
            Headline: subject.Trim());
    }

    /// <summary>
    /// "17 Oct 2026, 15:30" in the workspace's time zone. Falls back to UTC
    /// — and says "UTC" — when the zone is missing or not known to this
    /// server, rather than printing a time that is silently hours out.
    /// </summary>
    public static string FormatDue(DateTime dueUtc, string? timeZoneId)
    {
        var utc = dueUtc.Kind == DateTimeKind.Utc
            ? dueUtc
            : DateTime.SpecifyKind(dueUtc, DateTimeKind.Utc);

        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
                return local.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        return utc.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture) + " UTC";
    }
}
