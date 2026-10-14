// =====================================================================
// InboundEmailService.cs
// Location: MerkaiTrial.Admin.Web/Services/InboundEmails/InboundEmailService.cs
//
// NEW FILE (083). Thin wrapper over /api/inbound-emails, in the shape of
// CustomFieldService. No tenant id anywhere — the API reads the workspace
// from the signed token.
//
// READS THROW (like CustomFieldService), so each page can tell "no emails"
// apart from "emails could not be loaded" and say which. A 404 from the
// API — an email that does not exist or that this person may not see —
// comes back as KeyNotFoundException from GetAsync / GetForRecordAsync, so
// a page can redirect with "not found" instead of "something broke".
//
// Writes surface the API's own sentence through IApiService: a 400/403
// with { error } becomes an InvalidOperationException carrying it.
//
// Registered in AdminWebServiceRegistration.AddAdminWebModuleServices.
//
// CHANGES (085)
//   + GetSentForRecordAsync — the quote and invoice emails WE sent for a
//     deal / contact / lead (api/sent-emails/for/…), so the record's
//     Emails tab shows both directions. It lives here rather than in
//     SentEmailService so the three detail pages, which already hand
//     this service to RecordEmailsVm.LoadAsync, need no change at all.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.InboundEmails
{
    public interface IInboundEmailService
    {
        Task<InboundEmailPageDto> GetInboxAsync(string? status, string? search, int skip, int take);
        Task<int> GetUnmatchedCountAsync();
        Task<InboundEmailDetailDto> GetAsync(Guid id);
        Task<RecordEmailsDto> GetForRecordAsync(string entityType, Guid entityId);

        /// <summary>085. Emails we sent about the record's quotes and invoices. Throws on failure.</summary>
        Task<SentEmailsDto> GetSentForRecordAsync(string entityType, Guid entityId);
        Task<List<InboundLinkTargetDto>> SearchTargetsAsync(Guid emailId, string? search);

        Task LinkAsync(Guid id, string entityType, Guid entityId);
        Task UnlinkAsync(Guid id);
        Task DismissAsync(Guid id);
        Task RestoreAsync(Guid id);
        Task DeleteAsync(Guid id);

        Task<InboundEmailSettingsDto> GetSettingsAsync();
        Task<InboundEmailSettingsDto> NewAddressAsync();
    }

    public class InboundEmailService : IInboundEmailService
    {
        private readonly IApiService _api;
        private readonly ILogger<InboundEmailService> _logger;

        public InboundEmailService(IApiService api, ILogger<InboundEmailService> logger)
        {
            _api = api;
            _logger = logger;
        }

        public async Task<InboundEmailPageDto> GetInboxAsync(string? status, string? search, int skip, int take)
        {
            var url = $"api/inbound-emails?status={Uri.EscapeDataString(status ?? "unmatched")}" +
                      $"&skip={skip}&take={take}";
            if (!string.IsNullOrWhiteSpace(search)) url += $"&search={Uri.EscapeDataString(search.Trim())}";

            return await Read(() => _api.GetAsync<InboundEmailPageDto>(url), "the inbox")
                   ?? new InboundEmailPageDto(new(), 0, 0, 0, false);
        }

        /// <summary>For the menu badge. Never throws — a missing badge is not worth an error.</summary>
        public async Task<int> GetUnmatchedCountAsync()
        {
            try
            {
                return (await _api.GetAsync<InboundCountDto>("api/inbound-emails/count"))?.Unmatched ?? 0;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Inbox count unavailable");
                return 0;
            }
        }

        public async Task<InboundEmailDetailDto> GetAsync(Guid id)
            => await Read(() => _api.GetAsync<InboundEmailDetailDto>($"api/inbound-emails/{id}"), "an email")
               ?? throw new KeyNotFoundException("That email could not be found.");

        public async Task<RecordEmailsDto> GetForRecordAsync(string entityType, Guid entityId)
            => await Read(() => _api.GetAsync<RecordEmailsDto>(
                   $"api/inbound-emails/for/{Uri.EscapeDataString(entityType)}/{entityId}"), "a record's emails")
               ?? new RecordEmailsDto(new(), null, false);

        public async Task<SentEmailsDto> GetSentForRecordAsync(string entityType, Guid entityId)
            => await Read(() => _api.GetAsync<SentEmailsDto>(
                   $"api/sent-emails/for/{Uri.EscapeDataString(entityType)}/{entityId}"), "a record's sent emails")
               ?? new SentEmailsDto(new(), false);

        public async Task<List<InboundLinkTargetDto>> SearchTargetsAsync(Guid emailId, string? search)
        {
            var url = $"api/inbound-emails/{emailId}/link-targets";
            if (!string.IsNullOrWhiteSpace(search)) url += $"?search={Uri.EscapeDataString(search.Trim())}";

            return (await Read(() => _api.GetAsync<InboundLinkTargetsDto>(url), "records to link to"))?.Items
                   ?? new List<InboundLinkTargetDto>();
        }

        public Task LinkAsync(Guid id, string entityType, Guid entityId)
            => _api.PostAsync<OkResponse>($"api/inbound-emails/{id}/link", new LinkInboundEmailDto(entityType, entityId));

        public Task UnlinkAsync(Guid id) => _api.PostAsync<OkResponse>($"api/inbound-emails/{id}/unlink", new { });
        public Task DismissAsync(Guid id) => _api.PostAsync<OkResponse>($"api/inbound-emails/{id}/dismiss", new { });
        public Task RestoreAsync(Guid id) => _api.PostAsync<OkResponse>($"api/inbound-emails/{id}/restore", new { });
        public Task DeleteAsync(Guid id) => _api.DeleteAsync($"api/inbound-emails/{id}");

        public async Task<InboundEmailSettingsDto> GetSettingsAsync()
            => await Read(() => _api.GetAsync<InboundEmailSettingsDto>("api/inbound-emails/settings"), "inbound email settings")
               ?? throw new InvalidOperationException("The inbound email settings could not be loaded.");

        public async Task<InboundEmailSettingsDto> NewAddressAsync()
            => await _api.PostAsync<InboundEmailSettingsDto>("api/inbound-emails/settings/new-address", new { })
               ?? throw new InvalidOperationException("A new address could not be created. Please try again.");

        /// <summary>Logs and re-throws, so the page can say what failed.</summary>
        private async Task<T?> Read<T>(Func<Task<T?>> call, string what)
        {
            try
            {
                return await call();
            }
            catch (Exception ex) when (ex is not KeyNotFoundException)
            {
                // A 404 surfaces as an HttpRequestException (status set, or
                // "NotFound" in the message — ContactService checks the same)
                // or as the API's own "could not be found" sentence. Either
                // way the page should say "not found", not "something broke".
                var notFound =
                    (ex is HttpRequestException http && http.StatusCode == System.Net.HttpStatusCode.NotFound)
                    || ex.Message.Contains("NotFound", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("404")
                    || ex.Message.Contains("could not be found", StringComparison.OrdinalIgnoreCase);

                if (notFound)
                    throw new KeyNotFoundException("That could not be found.", ex);

                _logger.LogError(ex, "Failed to load {What}", what);
                throw;
            }
        }

        private sealed class OkResponse
        {
            public bool Ok { get; set; }
        }
    }
}
