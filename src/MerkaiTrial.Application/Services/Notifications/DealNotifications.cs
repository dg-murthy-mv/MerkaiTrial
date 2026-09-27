// =====================================================================
// DealNotifications.cs
// Location: MerkaiTrial.Application/Services/Notifications/DealNotifications.cs
//
// NEW FILE (039).
//
// WHY THIS EXISTS
//   DealStageChanged can happen on THREE paths:
//
//     UpdateDealHandler          the Edit page / API callers
//     UpdateDealStageHandler     the kanban drag and the deal page buttons
//     TransitionDealStageHandler a quote accepted, an invoice paid
//
//   Three copies of the same wording is exactly how the two terminal
//   checks drifted apart in 036 — one listed four stage names, the other
//   listed none, and nobody noticed until a customer could wreck an
//   invoiced deal. The same thing would happen here: someone improves the
//   message on the kanban path and the other two quietly keep the old one.
//
//   So the message is built in ONE place and the three callers pass what
//   they know.
//
// THESE BUILD A REQUEST — THEY DO NOT SEND ANYTHING
//   Each returns a NotificationRequest. The caller hands it to
//   INotificationDispatcher BEFORE its own SaveChangesAsync, so the
//   notification commits in the same transaction as the change it
//   describes. See INotificationDispatcher's header for why that matters.
// =====================================================================

using MerkaiTrial.Domain.Entities;

namespace MerkaiTrial.Application.Services.Notifications;

public static class DealNotifications
{
    // ── Stage changed ─────────────────────────────────────────────────

    /// <summary>
    /// "Deal X moved from A to B". Goes to the deal's OWNER, and the
    /// dispatcher drops them if they are the one who moved it — nobody
    /// needs telling about their own drag.
    ///
    /// Stage NAMES, never keys: the owner reads "Commercials", not
    /// "stage_3". The caller has TenantStages already, so resolving the
    /// name costs nothing.
    /// </summary>
    public static NotificationRequest StageChanged(
        Guid tenantId,
        Guid dealId,
        string dealTitle,
        string? fromStageName,
        string toStageName,
        bool isClosing,
        bool isReopen,
        Guid? actorUserId,
        string? actorName,
        string? note = null)
    {
        // Three different events in the owner's eyes, so three different
        // sentences. "Moved to Closed Won" and "moved to Demo" should not
        // read the same in a list of twenty notifications.
        var title = isReopen
            ? $"{dealTitle} was reopened — now {toStageName}"
            : isClosing
                ? $"{dealTitle} was closed as {toStageName}"
                : $"{dealTitle} moved to {toStageName}";

        // The note is the lost reason or the transition's own prompt. It is
        // the single most useful thing on the notification when there is
        // one, so it becomes the body rather than being dropped.
        var body = !string.IsNullOrWhiteSpace(note)
            ? note.Trim()
            : string.IsNullOrWhiteSpace(fromStageName)
                ? null
                : $"From {fromStageName}";

        return new NotificationRequest(
            TenantId: tenantId,
            EventType: NotificationEventType.DealStageChanged,
            Title: title,
            Body: body,
            EntityType: "Deal",
            EntityId: dealId,
            ActorUserId: actorUserId,

            // Null actor means the system moved it — a quote was accepted
            // or an invoice was paid. "the system" is honest and stops the
            // email reading "by".
            ActorName: actorName);
    }

    // ── Assigned ──────────────────────────────────────────────────────

    /// <summary>
    /// "X assigned you a deal". Goes to the NEW owner only. The previous
    /// owner is deliberately not told: "this was taken off you" is a
    /// conversation, not a notification, and a reassignment during a
    /// handover would produce a stream of them.
    /// </summary>
    public static NotificationRequest DealAssigned(
        Guid tenantId,
        Guid dealId,
        string dealTitle,
        string? stageName,
        decimal expectedValue,
        string? currency,
        Guid? actorUserId,
        string? actorName)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(stageName))
            parts.Add(stageName!);

        // Currency CODE, not a symbol: this layer has no view of the
        // tenant's formatting, and "THB 95,000.00" is unambiguous where a
        // bare number is not.
        if (expectedValue > 0)
            parts.Add($"{currency} {expectedValue:N2}".Trim());

        return new NotificationRequest(
            TenantId: tenantId,
            EventType: NotificationEventType.DealAssigned,
            Title: $"You were assigned the deal {dealTitle}",
            Body: parts.Count > 0 ? string.Join(" · ", parts) : null,
            EntityType: "Deal",
            EntityId: dealId,
            ActorUserId: actorUserId,
            ActorName: actorName);
    }

    public static NotificationRequest LeadAssigned(
        Guid tenantId,
        Guid leadId,
        string leadName,
        string? companyName,
        decimal? estimatedValue,
        string? currency,
        Guid? actorUserId,
        string? actorName)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(companyName))
            parts.Add(companyName!);

        if (estimatedValue is > 0)
            parts.Add($"{currency} {estimatedValue.Value:N2}".Trim());

        return new NotificationRequest(
            TenantId: tenantId,
            EventType: NotificationEventType.LeadAssigned,
            Title: $"You were assigned the lead {leadName}",
            Body: parts.Count > 0 ? string.Join(" · ", parts) : null,
            EntityType: "Lead",
            EntityId: leadId,
            ActorUserId: actorUserId,
            ActorName: actorName);
    }
}
