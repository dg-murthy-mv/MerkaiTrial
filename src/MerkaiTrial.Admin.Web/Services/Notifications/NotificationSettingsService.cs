// =====================================================================
// NotificationSettingsService.cs
// Location: MerkaiTrial.Admin.Web/Services/Notifications/NotificationSettingsService.cs
//
// NEW FILE (038).
//
// Thin wrapper over api/notification-settings. Both tabs of the Settings →
// Notifications page, one service, because they are one page.
//
// GetAsync / PutVoidAsync / PostVoidAsync only — IApiService's proven
// surface. A 403 from the outbound endpoints ("Only workspace admins can
// see the email log") arrives as InvalidOperationException carrying that
// message, so the page can show it as written.
//
// REGISTERED BY HAND in Startup/AdminWebServiceRegistration.cs, like every
// other typed client here.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.Notifications;

public interface INotificationSettingsService
{
    // ── Personal ──────────────────────────────────────────────────────
    Task<NotificationPreferencesDto> GetPreferencesAsync();
    Task SavePreferencesAsync(SaveNotificationPreferencesDto dto);

    // ── Workspace admins ──────────────────────────────────────────────

    /// <summary>040. The workspace defaults tab, with recipient counts.</summary>
    Task<TenantNotificationDefaultsDto> GetDefaultsAsync();
    Task SaveDefaultsAsync(SaveTenantNotificationDefaultsDto dto);

    Task<OutboundMessagePageDto> GetOutboundAsync(string? status = null, int skip = 0, int take = 25);
    Task RetryAsync(Guid messageId);
    Task CancelAsync(Guid messageId);
}

public class NotificationSettingsService : INotificationSettingsService
{
    private const string Base = "api/notification-settings";

    private readonly IApiService _api;

    public NotificationSettingsService(IApiService api) => _api = api;

    public async Task<NotificationPreferencesDto> GetPreferencesAsync()
        => await _api.GetAsync<NotificationPreferencesDto>($"{Base}/preferences");

    public async Task SavePreferencesAsync(SaveNotificationPreferencesDto dto)
        => await _api.PutVoidAsync($"{Base}/preferences", dto);

    public async Task<TenantNotificationDefaultsDto> GetDefaultsAsync()
        => await _api.GetAsync<TenantNotificationDefaultsDto>($"{Base}/defaults");

    public async Task SaveDefaultsAsync(SaveTenantNotificationDefaultsDto dto)
        => await _api.PutVoidAsync($"{Base}/defaults", dto);

    public async Task<OutboundMessagePageDto> GetOutboundAsync(
        string? status = null, int skip = 0, int take = 25)
    {
        var query = $"{Base}/outbound?skip={skip}&take={take}";

        if (!string.IsNullOrWhiteSpace(status))
            query += $"&status={Uri.EscapeDataString(status)}";

        return await _api.GetAsync<OutboundMessagePageDto>(query);
    }

    public async Task RetryAsync(Guid messageId)
        => await _api.PostVoidAsync($"{Base}/outbound/{messageId}/retry", new { });

    public async Task CancelAsync(Guid messageId)
        => await _api.PostVoidAsync($"{Base}/outbound/{messageId}/cancel", new { });
}
