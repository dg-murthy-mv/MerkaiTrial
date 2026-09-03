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
        Task UpdateStatusAsync(Guid tenantId, Guid quoteId, string status);
        Task<QuoteStatisticsDto> GetStatisticsAsync(Guid tenantId);

        Task<List<AttachmentDto>> GetAttachmentsAsync(Guid tenantId, Guid quoteId);
        Task<AttachmentDto> UploadAttachmentAsync(Guid tenantId, Guid quoteId, IFormFile file);
        Task DeleteAttachmentAsync(Guid tenantId, Guid attachmentId);

        Task<byte[]> DownloadPdfAsync(Guid tenantId, Guid quoteId);
        Task UpdateStatusAsync(Guid tenantId, Guid quoteId, string status, string? baseUrl = null);

        Task<QuoteDto?> GetByTokenAsync(string token);
        Task<bool> UpdateStatusByTokenAsync(string token, string newStatus);
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

        public async Task UpdateStatusAsync(Guid tenantId, Guid quoteId, string status)
        {
            try
            {
                var url = $"api/quotes/{quoteId}/status?tenantId={tenantId}";
                var dto = new UpdateQuoteStatusDto { Status = status };
                await _apiService.PutAsync<object>(url, dto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update quote status {QuoteId}", quoteId);
                throw;
            }
        }

        public async Task<QuoteStatisticsDto> GetStatisticsAsync(Guid tenantId)
        {
            try
            {
                var url = $"api/quotes/statistics?tenantId={tenantId}";
                var stats = await _apiService.GetAsync<QuoteStatisticsDto>(url);
                return stats ?? new QuoteStatisticsDto();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get quote statistics for tenant {TenantId}", tenantId);
                return new QuoteStatisticsDto();
            }
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
                _logger.LogWarning(ex, "Quote not found for token {Token}", token);
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
                _logger.LogError(ex, "Failed to update quote status by token {Token}", token);
                return false;
            }
        }
    }
}
