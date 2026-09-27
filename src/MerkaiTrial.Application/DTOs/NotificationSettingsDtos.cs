// =====================================================================
// NotificationSettingsDtos.cs
// Location: MerkaiTrial.Application/DTOs/NotificationSettingsDtos.cs
//
// NEW FILE (038). 037's NotificationDtos.cs is untouched.
//
// Covers both halves of the Settings → Notifications page: the per-user
// channel preferences everyone sees, and the email log workspace admins
// see.
// =====================================================================

namespace MerkaiTrial.Application.DTOs
{
    // ── Per-user channel preferences ──────────────────────────────────

    /// <summary>
    /// One row of the preferences matrix.
    ///
    /// IsDefault says the user has never saved a choice for this event and
    /// is on the built-in default. Worth showing: "you have not changed
    /// this" reads very differently from "you turned this off", especially
    /// when someone is wondering why they are not getting an email.
    /// </summary>
    public record NotificationPreferenceDto(
        string EventType,
        string Label,
        string Group,
        bool InApp,
        bool Email,
        bool IsDefault,

        /// <summary>
        /// 040. The workspace admin has locked this event, so these values
        /// are the workspace's and the user cannot change them. The page
        /// renders the row disabled; the API refuses an override too, so
        /// hiding it is a courtesy rather than the control.
        /// </summary>
        bool IsLocked = false);

    public record NotificationPreferencesDto(
        List<NotificationPreferenceDto> Items,

        /// <summary>
        /// False when no Resend key is configured on this environment. The
        /// page says so rather than letting someone tick Email and wonder
        /// why nothing arrives.
        /// </summary>
        bool EmailEnabled,

        /// <summary>Where their email would go. Blank if they have no address.</summary>
        string? MyEmailAddress);

    // ── Workspace defaults (040, admin only) ──────────────────────────

    /// <summary>
    /// One event on the workspace defaults tab, with the answer to the
    /// question that made this feature necessary: how many people actually
    /// receive it.
    /// </summary>
    public record TenantNotificationDefaultDto(
        string EventType,
        string Label,
        string Group,
        bool InApp,
        bool Email,
        bool IsLocked,

        /// <summary>False when no row has been saved for this event yet.</summary>
        bool IsConfigured,

        /// <summary>Active users who will get this in the app, as things stand.</summary>
        int InAppRecipientCount,

        /// <summary>Active users who will get this by email, as things stand.</summary>
        int EmailRecipientCount,

        /// <summary>
        /// Users who want the email but have no address on their account, so
        /// it silently cannot reach them. Worth surfacing: it is invisible
        /// otherwise and looks exactly like a broken feature.
        /// </summary>
        int MissingEmailAddressCount,

        /// <summary>Users who have overridden this event themselves.</summary>
        int OverriddenCount);

    public record TenantNotificationDefaultsDto(
        List<TenantNotificationDefaultDto> Items,
        int ActiveUserCount,
        bool EmailEnabled);

    public record SaveTenantNotificationDefaultDto(
        string EventType,
        bool InApp,
        bool Email,
        bool IsLocked);

    public record SaveTenantNotificationDefaultsDto(
        List<SaveTenantNotificationDefaultDto> Items);

    /// <summary>One row's worth of saved choice. The page posts the whole set.</summary>
    public record SaveNotificationPreferenceDto(
        string EventType,
        bool InApp,
        bool Email);

    public record SaveNotificationPreferencesDto(
        List<SaveNotificationPreferenceDto> Items);

    // ── The email log (workspace admins) ──────────────────────────────

    public record OutboundMessageDto(
        Guid Id,
        string Channel,
        string Status,
        string EventType,
        string ToAddress,
        string? ToName,
        string Subject,
        int AttemptCount,
        DateTime CreatedAtUtc,
        DateTime? SentAtUtc,
        DateTime? NextAttemptAtUtc,
        string? LastError,
        string? ProviderMessageId,
        string? EntityType,
        Guid? EntityId)
    {
        public bool CanRetry => Status is "Dead" or "Failed" or "Cancelled";
        public bool CanCancel => Status is "Pending" or "Failed";

        public string StatusClass => Status switch
        {
            "Sent"      => "bg-success-subtle text-success-emphasis",
            "Pending"   => "bg-secondary-subtle text-secondary-emphasis",
            "Sending"   => "bg-info-subtle text-info-emphasis",
            "Failed"    => "bg-warning-subtle text-warning-emphasis",
            "Dead"      => "bg-danger-subtle text-danger-emphasis",
            "Cancelled" => "bg-light text-muted",
            _           => "bg-light text-muted"
        };

        /// <summary>Plain-language status, for the row's tooltip.</summary>
        public string StatusHint => Status switch
        {
            "Sent"      => "Accepted by the email provider.",
            "Pending"   => "Waiting for the worker to pick it up.",
            "Sending"   => "Being sent right now.",
            "Failed"    => "Failed, and will be tried again automatically.",
            "Dead"      => "Out of automatic attempts — retry it by hand once the cause is fixed.",
            "Cancelled" => "Cancelled before it was sent.",
            _           => Status
        };
    }

    public record OutboundMessagePageDto(
        List<OutboundMessageDto> Items,
        int TotalCount,
        int PendingCount,
        int DeadCount,
        bool HasMore,

        /// <summary>False when no provider key is configured here.</summary>
        bool EmailEnabled);
}
