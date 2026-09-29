// =====================================================================
// WhatsAppOptions.cs
// Location: MerkaiTrial.Application/Services/Notifications/WhatsAppOptions.cs
//
// NEW FILE (044). The settings the WhatsApp sender will need in 045, and
// the IsConfigured flag the settings page needs NOW so it can explain
// itself before anything can send.
//
// ── THE ACCESS TOKEN NEVER GOES IN appsettings.json ──────────────────
//
// Same rule as the Resend key, for the same reason and with sharper
// teeth: a WhatsApp access token can send messages from your verified
// business number to anyone. Leaked, it is your brand in a stranger's
// hands, and Meta's remedy is to disable the number.
//
//   Locally:
//     dotnet user-secrets set "WhatsApp:AccessToken" "EAAG..."
//     dotnet user-secrets set "WhatsApp:PhoneNumberId" "123456789012345"
//
//   Azure App Service → Configuration:
//     WhatsApp__AccessToken     (double underscore, not a colon)
//     WhatsApp__PhoneNumberId
//
// appsettings.json may hold ApiVersion and the non-secret ids only.
// =====================================================================

namespace MerkaiTrial.Application.Services.Notifications;

public class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";

    /// <summary>
    /// A permanent access token from a System User in Meta Business
    /// Manager — NOT the temporary 24-hour token the Getting Started page
    /// hands out, which is the single most common reason a working
    /// integration stops working the next morning.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// The Phone Number ID from Business Manager. A long number, and NOT
    /// the phone number itself — "+66812345678" here fails with a message
    /// that does not mention phone numbers at all.
    /// </summary>
    public string? PhoneNumberId { get; set; }

    /// <summary>
    /// 045. The WhatsApp Business Account that OWNS the templates — the
    /// "WhatsApp Business account ID" shown beside the Phone number ID on
    /// Meta's API Setup page.
    ///
    /// Not needed to send. It is what the template screen uses to ask Meta
    /// which templates exist and what state they are in, which is the
    /// difference between reading the approval status in the app and
    /// keeping a browser tab open on Business Manager.
    /// </summary>
    public string? BusinessAccountId { get; set; }

    /// <summary>
    /// Meta's Graph API version, "v21.0". Pinned rather than floating:
    /// Meta deprecates versions on a published schedule, and finding out
    /// from a 400 in production is the wrong way to learn a new one exists.
    /// </summary>
    public string ApiVersion { get; set; } = "v21.0";

    /// <summary>
    /// 045. Graph's host. Configurable only so a test can point it
    /// somewhere else; there is no reason to change it in production.
    /// </summary>
    public string ApiBaseUrl { get; set; } = "https://graph.facebook.com";

    /// <summary>
    /// 045. Per-request timeout. Clamped to 5–120 at registration, like
    /// the email client's.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// The number messages come FROM, in E.164, for display only — the
    /// settings page says "we'll message you from +66…" so a recipient
    /// knows who is about to appear in their chat list.
    /// </summary>
    public string? FromDisplayNumber { get; set; }

    /// <summary>
    /// Off by default. Even with a token configured, WhatsApp does
    /// nothing until this is true — so a shared staging configuration
    /// cannot start messaging real people's phones because it happened to
    /// inherit a token.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Usable: switched on, with both the pieces a send needs. The
    /// settings page reads this to decide between "choose your events"
    /// and "WhatsApp isn't set up on this environment yet".
    /// </summary>
    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(AccessToken)
        && !string.IsNullOrWhiteSpace(PhoneNumberId);
}
