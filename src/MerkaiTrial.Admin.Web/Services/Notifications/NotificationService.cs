// =====================================================================
// NotificationService.cs
// Location: MerkaiTrial.Admin.Web/Services/Notifications/NotificationService.cs
//
// NEW FILE (037).
//
// Thin wrapper over api/notifications — same pattern as
// QuoteApprovalService. Tenant id and user id are never sent; the API uses
// the caller's, which is the whole reason there is no route here that names
// a user.
//
// ONLY GetAsync / PostVoidAsync ARE USED. IApiService's proven surface is
// GetAsync<T>, PostVoidAsync and PutVoidAsync, so the dismiss and
// mark-all-read calls stay void — the page reloads its own list afterwards
// and the bell re-polls, so neither needs a return value.
//
// The bell dropdown needs to mark an item read AND then follow its link.
// It does that without a return value: its items link to
// /Notifications?open={id}, and the page marks the item read and resolves
// the link from the list it is already loading. So nothing here needs a
// Post-with-result helper.
//
// REGISTERED BY HAND. Admin.Web's typed clients are not scanned, unlike
// handlers — INotificationService is added to
// Startup/AdminWebServiceRegistration.cs in this round.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.Notifications;

public interface INotificationService
{
    /// <summary>
    /// The bell: unread count plus the newest few. Called by the layout's
    /// poll, so it must stay cheap.
    /// </summary>
    Task<NotificationSummaryDto> GetSummaryAsync();

    /// <summary>The notifications page.</summary>
    Task<NotificationPageDto> GetPageAsync(bool unreadOnly = false, int skip = 0, int take = 25);

    /// <summary>
    /// Marks one read. A notification that is not this user's is quietly
    /// ignored by the API — a stale click from a tab left open overnight is
    /// a no-op, not an error.
    /// </summary>
    Task MarkReadAsync(Guid notificationId);

    Task MarkAllReadAsync();
    Task DismissAsync(Guid notificationId);
    Task DismissReadAsync();
}

public class NotificationService : INotificationService
{
    private const string Base = "api/notifications";

    private readonly IApiService _api;

    public NotificationService(IApiService api) => _api = api;

    public async Task<NotificationSummaryDto> GetSummaryAsync()
        => await _api.GetAsync<NotificationSummaryDto>($"{Base}/summary");

    public async Task<NotificationPageDto> GetPageAsync(
        bool unreadOnly = false, int skip = 0, int take = 25)
        => await _api.GetAsync<NotificationPageDto>(
               $"{Base}?unreadOnly={(unreadOnly ? "true" : "false")}&skip={skip}&take={take}");

    // The API returns the notification from this endpoint, which is useful
    // for a caller that has nothing else to go on. Admin.Web does not need
    // it — the page resolves the link from the list it is already loading —
    // and IApiService has no Post-with-result helper, so this stays void
    // rather than pretending to return something.
    public async Task MarkReadAsync(Guid notificationId)
        => await _api.PostVoidAsync($"{Base}/{notificationId}/read", new { });

    public async Task MarkAllReadAsync()
        => await _api.PostVoidAsync($"{Base}/read-all", new { });

    public async Task DismissAsync(Guid notificationId)
        => await _api.PostVoidAsync($"{Base}/{notificationId}/dismiss", new { });

    public async Task DismissReadAsync()
        => await _api.PostVoidAsync($"{Base}/dismiss-read", new { });
}
