// =====================================================================
// SentEmailService.cs
// Location: MerkaiTrial.Admin.Web/Services/Emails/SentEmailService.cs
//
// NEW FILE (085). Thin wrapper over /api/sent-emails — the emails WE sent.
// No tenant id anywhere: the API reads the workspace from the signed token
// and decides, per email, whether this person may see it.
//
//   GetForEntityAsync  a quote's or an invoice's email history (its card)
//   GetAsync           one email in full (/Emails/Sent/{id})
//   RetryAsync         put a failed one back in the queue
//
// READS THROW, like InboundEmailService, so a page can tell "none yet"
// from "could not be loaded" and say which. A 404 — an email that does
// not exist or that this person may not see — becomes KeyNotFoundException
// so the page can say "not found" rather than "something broke".
//
// Writes surface the API's own sentence: a 400/403 { error } arrives as an
// InvalidOperationException carrying it.
//
// Registered in AdminWebServiceRegistration.AddAdminWebModuleServices.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.Emails
{
    public interface ISentEmailService
    {
        /// <summary>"quote" or "invoice". Throws on failure.</summary>
        Task<SentEmailsDto> GetForEntityAsync(string entityType, Guid entityId);

        /// <summary>Throws KeyNotFoundException when missing or not visible.</summary>
        Task<SentEmailDetailDto> GetAsync(Guid id);

        Task RetryAsync(Guid id);
    }

    public class SentEmailService : ISentEmailService
    {
        private readonly IApiService _api;
        private readonly ILogger<SentEmailService> _logger;

        public SentEmailService(IApiService api, ILogger<SentEmailService> logger)
        {
            _api = api;
            _logger = logger;
        }

        public async Task<SentEmailsDto> GetForEntityAsync(string entityType, Guid entityId)
            => await Read(() => _api.GetAsync<SentEmailsDto>(
                   $"api/sent-emails/for-entity/{Uri.EscapeDataString(entityType)}/{entityId}"), "a document's emails")
               ?? new SentEmailsDto(new(), false);

        public async Task<SentEmailDetailDto> GetAsync(Guid id)
            => await Read(() => _api.GetAsync<SentEmailDetailDto>($"api/sent-emails/{id}"), "a sent email")
               ?? throw new KeyNotFoundException("That email could not be found.");

        public Task RetryAsync(Guid id)
            => _api.PostAsync<OkResponse>($"api/sent-emails/{id}/retry", new { });

        /// <summary>Logs and re-throws, so the page can say what failed.</summary>
        private async Task<T?> Read<T>(Func<Task<T?>> call, string what)
        {
            try
            {
                return await call();
            }
            catch (Exception ex) when (ex is not KeyNotFoundException)
            {
                // Same test as InboundEmailService: a 404 arrives as an
                // HttpRequestException with the status, or as the API's own
                // "could not be found" sentence.
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
