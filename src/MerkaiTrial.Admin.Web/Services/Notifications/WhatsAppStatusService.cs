// =====================================================================
// WhatsAppStatusService.cs
// Location: MerkaiTrial.Admin.Web/Services/Notifications/WhatsAppStatusService.cs
//
// NEW FILE (046). Asks the WebApi whether WhatsApp is configured and what
// Meta says about the templates.
//
// WHY A ROUND TRIP FOR A BADGE
//   The token lives in the API's configuration and belongs nowhere else.
//   Copying it into Admin.Web's user secrets so a page could render
//   "Ready" would put a credential in a second place for a decorative
//   reason — and it would still be a guess, because the API is what
//   actually sends.
//
// NEVER THROWS. A status panel that takes the page down with it would be
// a poor trade. A failure comes back as "not reachable" with the reason,
// and the screen renders your own settings exactly as it did before this
// existed.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.Services.Notifications;

namespace MerkaiTrial.Admin.Web.Services.Notifications;

public interface IWhatsAppStatusService
{
    Task<WhatsAppStatusView> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// The API's answer, or an explanation of why there isn't one.
/// </summary>
public sealed record WhatsAppStatusView(
    bool Available,
    string? Error,
    bool Configured,
    string? FromDisplayNumber,
    string? PhoneNumberId,
    string? BusinessAccountId,
    bool DirectoryReachable,
    string? DirectoryError,
    IReadOnlyList<MetaTemplate> Templates)
{
    public static WhatsAppStatusView Unavailable(string error)
        => new(false, error, false, null, null, null, false, null, Array.Empty<MetaTemplate>());

    public MetaTemplate? Find(string? name, string? language)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        return Templates.FirstOrDefault(t =>
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(language)
                || string.Equals(t.Language, language, StringComparison.OrdinalIgnoreCase)));
    }
}

public sealed class WhatsAppStatusService : IWhatsAppStatusService
{
    private readonly IApiService _api;
    private readonly ILogger<WhatsAppStatusService> _logger;

    public WhatsAppStatusService(IApiService api, ILogger<WhatsAppStatusService> logger)
    {
        _api = api;
        _logger = logger;
    }

    public async Task<WhatsAppStatusView> GetAsync(CancellationToken ct = default)
    {
        try
        {
            var dto = await _api.GetAsync<ApiStatus>("api/whatsapp/status");

            if (dto is null)
                return WhatsAppStatusView.Unavailable("The API returned nothing.");

            return new WhatsAppStatusView(
                Available: true,
                Error: null,
                Configured: dto.Configured,
                FromDisplayNumber: dto.FromDisplayNumber,
                PhoneNumberId: dto.PhoneNumberId,
                BusinessAccountId: dto.BusinessAccountId,
                DirectoryReachable: dto.DirectoryReachable,
                DirectoryError: dto.DirectoryError,
                Templates: dto.Templates ?? new List<MetaTemplate>());
        }
        catch (Exception ex)
        {
            // Includes the 404 the API returns to anyone who is not a super
            // admin — which, on a page only super admins can open, means
            // the API disagrees about who you are. Worth the log line.
            _logger.LogWarning(ex, "Could not read the WhatsApp status from the API");

            return WhatsAppStatusView.Unavailable(
                "Couldn't reach the API to check whether WhatsApp is configured.");
        }
    }

    /// <summary>
    /// Mirrors WhatsAppStatusDto on the API side. A separate type on
    /// purpose: this one is deserialised from JSON, and tying the page to
    /// the controller's record would make a field rename on one side a
    /// silent null on the other.
    /// </summary>
    private sealed record ApiStatus(
        bool Configured,
        string? FromDisplayNumber,
        string? PhoneNumberId,
        string? BusinessAccountId,
        bool DirectoryReachable,
        string? DirectoryError,
        List<MetaTemplate>? Templates);
}
