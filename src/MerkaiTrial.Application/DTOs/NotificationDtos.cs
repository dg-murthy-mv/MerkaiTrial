// =====================================================================
// NotificationDtos.cs
// Location: MerkaiTrial.Application/DTOs/NotificationDtos.cs
//
// NEW FILE (037).
//
// The API never returns the entity. Two reasons that matter here rather
// than as a general principle:
//
//   1. Notification carries RecipientUserId and ActorUserId. Neither
//      belongs on the wire — the caller is the recipient by definition,
//      and the actor is shown by NAME.
//   2. Icon and link are presentation. They are computed once, here, so
//      the bell dropdown and the notifications page cannot disagree about
//      what a QuoteAccepted looks like.
// =====================================================================

using MerkaiTrial.Domain.Entities;

namespace MerkaiTrial.Application.DTOs
{
    public record NotificationDto(
        Guid Id,
        string EventType,
        string Title,
        string? Body,
        string? EntityType,
        Guid? EntityId,
        string? ActorName,
        DateTime CreatedAtUtc,
        bool IsRead)
    {
        /// <summary>
        /// Bootstrap Icons name, chosen from the event type. Computed rather
        /// than stored: it is a look, not a fact, and a restyle should not
        /// need a migration.
        /// </summary>
        public string Icon => EventType switch
        {
            nameof(NotificationEventType.QuoteSent)               => "bi-send",
            nameof(NotificationEventType.QuoteAccepted)           => "bi-check-circle",
            nameof(NotificationEventType.QuoteRejected)           => "bi-x-circle",
            nameof(NotificationEventType.QuoteApprovalRequested)  => "bi-hourglass-split",
            nameof(NotificationEventType.QuoteApprovalDecided)    => "bi-clipboard-check",
            nameof(NotificationEventType.DealStageChanged)        => "bi-arrow-right-circle",
            nameof(NotificationEventType.LeadAssigned)            => "bi-person-plus",
            nameof(NotificationEventType.DealAssigned)            => "bi-briefcase",
            _                                                     => "bi-bell"
        };

        /// <summary>A muted Bootstrap text colour for the icon.</summary>
        public string IconClass => EventType switch
        {
            nameof(NotificationEventType.QuoteAccepted)          => "text-success",
            nameof(NotificationEventType.QuoteRejected)          => "text-danger",
            nameof(NotificationEventType.QuoteApprovalRequested) => "text-warning",
            _                                                    => "text-primary"
        };

        /// <summary>
        /// Where clicking it goes, or null when the notification points at
        /// nothing. Built from EntityType so a route change is one edit here
        /// rather than a data migration.
        /// </summary>
        public string? Url => (EntityType, EntityId) switch
        {
            ("Quote", { } id) => $"/Quotes/Detail/{id}",
            ("Deal",  { } id) => $"/Pipeline/Detail/{id}",
            ("Lead",  { } id) => $"/Leads/Detail/{id}",
            _                 => null
        };
    }

    /// <summary>
    /// What the bell asks for every 45 seconds: the count, plus enough of
    /// the newest items to fill the dropdown. One request rather than two,
    /// because this is the most frequently called endpoint in the app.
    /// </summary>
    public record NotificationSummaryDto(
        int UnreadCount,
        List<NotificationDto> Recent);

    /// <summary>The notifications page.</summary>
    public record NotificationPageDto(
        List<NotificationDto> Items,
        int UnreadCount,
        int TotalCount,
        bool HasMore);
}
