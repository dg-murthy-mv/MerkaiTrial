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

        /// <summary>044. The third channel. Off unless someone chose it.</summary>
        bool WhatsApp,

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
        string? MyEmailAddress,

        // ── 044: WhatsApp ────────────────────────────────────────────

        /// <summary>
        /// False when no WhatsApp access token is configured on this
        /// environment. The column still renders, explained and disabled —
        /// a missing column would leave someone hunting for a feature they
        /// were told exists.
        /// </summary>
        bool WhatsAppEnabled = false,

        /// <summary>
        /// This user's mobile in E.164, or null. What they would be
        /// messaged on, shown back to them so a wrong country code is
        /// visible before it matters.
        /// </summary>
        string? MyMobileE164 = null,

        /// <summary>Whether they have agreed to receive WhatsApp messages.</summary>
        bool WhatsAppOptedIn = false,

        /// <summary>
        /// The workspace's country dial code ("+91"), for the hint under
        /// the box. A number typed without one is resolved against this.
        /// </summary>
        string? DefaultDialCode = null);

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

        /// <summary>044. The workspace's starting position for WhatsApp.</summary>
        bool WhatsApp,

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
        int OverriddenCount,

        /// <summary>
        /// 044. Active users who will get this on WhatsApp as things
        /// stand — wanting it, with a number, and opted in. All three.
        /// </summary>
        int WhatsAppRecipientCount = 0,

        /// <summary>
        /// 044. Users who want WhatsApp for this event but have no mobile
        /// number or have not opted in, so nothing can reach them.
        ///
        /// The same reasoning as MissingEmailAddressCount, and more
        /// necessary: an admin switching WhatsApp on for the workspace has
        /// no way to know how many people never filled the number in, and
        /// silence looks identical to a broken feature.
        /// </summary>
        int MissingMobileCount = 0);

    public record TenantNotificationDefaultsDto(
        List<TenantNotificationDefaultDto> Items,
        int ActiveUserCount,
        bool EmailEnabled,

        /// <summary>044. False when this environment has no WhatsApp token.</summary>
        bool WhatsAppEnabled = false);

    public record SaveTenantNotificationDefaultDto(
        string EventType,
        bool InApp,
        bool Email,
        bool IsLocked,

        /// <summary>044. Trailing with a default, so an older caller still compiles.</summary>
        bool WhatsApp = false);

    public record SaveTenantNotificationDefaultsDto(
        List<SaveTenantNotificationDefaultDto> Items);

    /// <summary>One row's worth of saved choice. The page posts the whole set.</summary>
    public record SaveNotificationPreferenceDto(
        string EventType,
        bool InApp,
        bool Email,

        /// <summary>044. Trailing with a default, so an older caller still compiles.</summary>
        bool WhatsApp = false);

    /// <summary>
    /// 044: the page saves the channel grid AND this person's WhatsApp
    /// contact details in one post, because on screen they are one form
    /// with one Save button. Two endpoints would mean a page that can end
    /// up half saved.
    /// </summary>
    public record SaveNotificationPreferencesDto(
        List<SaveNotificationPreferenceDto> Items,

        /// <summary>
        /// The mobile number exactly as typed. Normalised to E.164 on the
        /// server — never trust the browser to have done it. Blank clears
        /// the number, and clearing the number withdraws the opt-in with
        /// it: consent is given for a number, not in the abstract.
        /// </summary>
        string? MobileNumber = null,

        /// <summary>Whether the "yes, message me on WhatsApp" box is ticked.</summary>
        bool WhatsAppOptIn = false);

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
