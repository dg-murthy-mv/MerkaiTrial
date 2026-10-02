using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.Quotes
{
    public interface IQuoteService
    {
        Task<List<QuoteListItem>> GetAllAsync(
            Guid tenantId,
            Guid? dealId = null,
            string? status = null,
            DateTime? fromDate = null,
            DateTime? toDate = null);

        Task<QuoteDto> GetByIdAsync(Guid tenantId, Guid quoteId);
        Task<QuoteDto> CreateAsync(CreateQuoteDto dto);
        Task UpdateAsync(Guid tenantId, Guid quoteId, UpdateQuoteDto dto);
        Task DeleteAsync(Guid tenantId, Guid quoteId);

        /// <summary>
        /// 066: OBSOLETE. Marked here as well as on the implementation —
        /// almost every call goes through IQuoteService, so a warning only
        /// on the class would point at nothing.
        ///
        /// NOTE WHY THIS OVERLOAD EVER WINS. The four-argument version
        /// below has `string? baseUrl = null`, so a three-argument call is
        /// applicable to BOTH. C# prefers the candidate that does not need
        /// a default value filled in — so every three-argument call
        /// silently chose this one, which never sent BaseUrl, and the
        /// quote was stored with a relative customer link.
        /// </summary>
        [Obsolete("Pass baseUrl — the 3-argument overload stores a relative " +
                  "customer link that does not work in an email. Use " +
                  "UpdateStatusAsync(tenantId, quoteId, status, baseUrl).")]
        Task UpdateStatusAsync(Guid tenantId, Guid quoteId, string status);

        /// <summary>
        /// 043a: the two dates are optional and TRAILING, so every existing
        /// GetStatisticsAsync(tenantId) call still compiles and still means
        /// all time. Half-open — toExclusive is the first instant NOT
        /// included. Pass IFiscalYearService.DateOnlyRange, which is what the
        /// issue-date column holds since 034.
        /// </summary>
        Task<QuoteStatisticsDto> GetStatisticsAsync(
            Guid tenantId, DateTime? fromUtc = null, DateTime? toExclusiveUtc = null);

        Task<List<AttachmentDto>> GetAttachmentsAsync(Guid tenantId, Guid quoteId);
        Task<AttachmentDto> UploadAttachmentAsync(Guid tenantId, Guid quoteId, IFormFile file);
        Task DeleteAttachmentAsync(Guid tenantId, Guid attachmentId);

        Task<byte[]> DownloadPdfAsync(Guid tenantId, Guid quoteId);
        Task UpdateStatusAsync(Guid tenantId, Guid quoteId, string status, string? baseUrl = null);

        Task<QuoteDto?> GetByTokenAsync(string token);
        Task<bool> UpdateStatusByTokenAsync(string token, string newStatus);

        /// <summary>
        /// 061. Emails the quote to the customer again. The FIRST email
        /// goes out automatically when the quote moves to Sent.
        ///
        /// Throws InvalidOperationException carrying the API's own sentence
        /// when it cannot be sent — "Worapong Thongsuk has no email address
        /// on file…" — so the page shows that rather than a generic error.
        ///
        /// baseUrl is this host's public address, which the API cannot know
        /// for itself. Same value the status call already passes.
        /// </summary>
        Task SendToCustomerAsync(Guid tenantId, Guid quoteId, string? baseUrl = null);
    }

    public class QuoteService : IQuoteService
    {
        private readonly IApiService _apiService;
        private readonly ILogger<QuoteService> _logger;

        public QuoteService(
            IApiService apiService,
            ILogger<QuoteService> logger)
        {
            _apiService = apiService;
            _logger = logger;
        }

        public async Task<List<QuoteListItem>> GetAllAsync(
            Guid tenantId,
            Guid? dealId = null,
            string? status = null,
            DateTime? fromDate = null,
            DateTime? toDate = null)
        {
            try
            {
                var url = $"api/quotes?tenantId={tenantId}";

                if (dealId.HasValue)
                    url += $"&dealId={dealId.Value}";

                if (!string.IsNullOrEmpty(status))
                    url += $"&status={status}";

                if (fromDate.HasValue)
                    url += $"&fromDate={fromDate.Value:yyyy-MM-ddTHH:mm:ss}";

                if (toDate.HasValue)
                    url += $"&toDate={toDate.Value:yyyy-MM-ddTHH:mm:ss}";

                var quotes = await _apiService.GetAsync<List<QuoteListItem>>(url);
                return quotes ?? new List<QuoteListItem>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get quotes for tenant {TenantId}", tenantId);
                return new List<QuoteListItem>();
            }
        }

        public async Task<QuoteDto> GetByIdAsync(Guid tenantId, Guid quoteId)
        {
            try
            {
                var url = $"api/quotes/{quoteId}?tenantId={tenantId}";
                return await _apiService.GetAsync<QuoteDto>(url);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get quote {QuoteId}", quoteId);
                throw;
            }
        }

        public async Task<QuoteDto> CreateAsync(CreateQuoteDto dto)
        {
            try
            {
                return await _apiService.PostAsync<QuoteDto>("api/quotes", dto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create quote for deal {DealId}", dto.DealId);
                throw;
            }
        }

        public async Task UpdateAsync(Guid tenantId, Guid quoteId, UpdateQuoteDto dto)
        {
            try
            {
                var url = $"api/quotes/{quoteId}?tenantId={tenantId}";
                await _apiService.PutAsync<QuoteDto>(url, dto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update quote {QuoteId}", quoteId);
                throw;
            }
        }

        public async Task DeleteAsync(Guid tenantId, Guid quoteId)
        {
            try
            {
                var url = $"api/quotes/{quoteId}?tenantId={tenantId}";
                await _apiService.DeleteAsync(url);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete quote {QuoteId}", quoteId);
                throw;
            }
        }

        /// <summary>
        /// 066: NOW DELEGATES. It used to be a second, independent
        /// implementation that built its own UpdateQuoteStatusDto and
        /// never set BaseUrl.
        ///
        /// WHY THAT MATTERED. UpdateQuoteStatusHandler uses BaseUrl to
        /// build the customer's public link:
        ///
        ///     var baseUrl = !string.IsNullOrEmpty(dto.BaseUrl) ? dto.BaseUrl : "";
        ///     quote.PaymentLinkUrl = $"{baseUrl}/q/{token}";
        ///
        /// With it null the quote is stored with a RELATIVE link,
        /// "/q/abc123…", which is useless in an email and useless in the
        /// Copy link box. Anything that happened to call this three-
        /// argument overload would send a quote whose link went nowhere,
        /// and nothing would report a failure — the status change
        /// succeeded, the token was generated, the link was simply wrong.
        ///
        /// [Obsolete] rather than deleted: deleting it would break any
        /// caller I cannot see, and a build warning naming the call site is
        /// exactly the signal wanted. The warning points at the fix.
        /// </summary>
        [Obsolete("Pass baseUrl — the 3-argument overload stores a relative " +
                  "customer link that does not work in an email. Use " +
                  "UpdateStatusAsync(tenantId, quoteId, status, baseUrl) with " +
                  "$\"{Request.Scheme}://{Request.Host}\".")]
        public Task UpdateStatusAsync(Guid tenantId, Guid quoteId, string status)
            => UpdateStatusAsync(tenantId, quoteId, status, baseUrl: null);

        public async Task<QuoteStatisticsDto> GetStatisticsAsync(
            Guid tenantId, DateTime? fromUtc = null, DateTime? toExclusiveUtc = null)
        {
            try
            {
                var url = $"api/quotes/statistics?tenantId={tenantId}";

                if (fromUtc.HasValue)        url += $"&from={Utc(fromUtc.Value)}";
                if (toExclusiveUtc.HasValue) url += $"&toExclusive={Utc(toExclusiveUtc.Value)}";

                var stats = await _apiService.GetAsync<QuoteStatisticsDto>(url);
                return stats ?? new QuoteStatisticsDto();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get quote statistics for tenant {TenantId}", tenantId);
                return new QuoteStatisticsDto();
            }
        }

        /// <summary>
        /// 043a. An instant on the wire, with the Z that says so.
        ///
        /// The older filters in this file send "yyyy-MM-ddTHH:mm:ss" with no
        /// designator, which leaves the API to guess. These two are financial
        /// year boundaries: guessed wrongly, a whole day of quotes moves from
        /// one year's numbers to another's. The API normalises whatever it
        /// receives, and this end says plainly what it meant.
        /// </summary>
        private static string Utc(DateTime value)
        {
            var utc = value.Kind switch
            {
                DateTimeKind.Utc   => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _                  => DateTime.SpecifyKind(value, DateTimeKind.Utc)
            };

            return Uri.EscapeDataString(utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
        }

        public async Task<List<AttachmentDto>> GetAttachmentsAsync(Guid tenantId, Guid quoteId)
        {
            try
            {
                var url = $"api/quotes/{quoteId}/attachments?tenantId={tenantId}";
                var result = await _apiService.GetAsync<List<AttachmentDto>>(url);
                return result ?? new List<AttachmentDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get attachments for quote {QuoteId}", quoteId);
                return new List<AttachmentDto>();
            }
        }

        public async Task<AttachmentDto> UploadAttachmentAsync(
            Guid tenantId, Guid quoteId, IFormFile file)
        {
            var url = $"api/quotes/{quoteId}/attachments?tenantId={tenantId}";

            var content = new MultipartFormDataContent();
            await using var stream = file.OpenReadStream();
            var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType);
            content.Add(fileContent, "file", file.FileName);

            return await _apiService.PostMultipartAsync<AttachmentDto>(url, content);
        }

        public async Task DeleteAttachmentAsync(Guid tenantId, Guid attachmentId)
        {
            // Use the direct endpoint — no placeholder quoteId needed
            var url = $"api/quotes/attachments/{attachmentId}?tenantId={tenantId}";
            await _apiService.DeleteAsync(url);
        }

        public async Task<byte[]> DownloadPdfAsync(Guid tenantId, Guid quoteId)
        {
            var url = $"api/quotes/{quoteId}/pdf?tenantId={tenantId}";
            return await _apiService.GetBytesAsync(url);
        }

        public async Task UpdateStatusAsync(Guid tenantId, Guid quoteId, string status, string? baseUrl = null)
        {
            try
            {
                var url = $"api/quotes/{quoteId}/status?tenantId={tenantId}";
                var dto = new UpdateQuoteStatusDto
                {
                    Status = status,
                    BaseUrl = baseUrl   // ← pass through
                };
                await _apiService.PutAsync<object>(url, dto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update quote status {QuoteId}", quoteId);
                throw;
            }
        }

        // ── QuoteService — add these two implementations ─────────────────────
        public async Task<QuoteDto?> GetByTokenAsync(string token)
        {
            try
            {
                // Calls GET api/quotes/public/{token}  [AllowAnonymous]
                return await _apiService.GetAsync<QuoteDto>($"api/quotes/public/{token}");
            }
            catch (Exception ex)
            {
                // 061: the token is NOT logged. It is the whole security of
                // the /q/{token} URL, and a log is not a key store.
                _logger.LogWarning(ex, "Quote not found for token {Token}", Mask(token));
                return null;
            }
        }

        public async Task<bool> UpdateStatusByTokenAsync(string token, string newStatus)
        {
            try
            {
                // Calls PUT api/quotes/public/{token}/status  [AllowAnonymous]
                var dto = new UpdateQuoteStatusDto { Status = newStatus, UpdatedBy = "Customer" };
                await _apiService.PutAsync<object>($"api/quotes/public/{token}/status", dto);
                return true;
            }
            catch (Exception ex)
            {
                // 061: masked, same reason as above.
                _logger.LogError(ex, "Failed to update quote status by token {Token}", Mask(token));
                return false;
            }
        }

        // ── 061: email the quote to the customer ─────────────────────────
        public async Task SendToCustomerAsync(Guid tenantId, Guid quoteId, string? baseUrl = null)
        {
            // No try/catch that swallows. IApiService turns a 400
            // { error = "…" } into InvalidOperationException carrying that
            // message, and the message is the entire value here — "this
            // contact has no email address" is what the rep needs to read.
            // Catching it to log a generic line would throw that away.
            await _apiService.PostVoidAsync(
                $"api/quotes/{quoteId}/send?tenantId={tenantId}",
                new { BaseUrl = baseUrl });
        }

        /// <summary>
        /// 061. Last four characters of a public quote token, for the log.
        /// Enough to find the row; useless for opening the quote.
        /// </summary>
        private static string Mask(string? token)
            => string.IsNullOrWhiteSpace(token) || token.Length < 8
                ? "****"
                : "****" + token[^4..];
    }
}
