using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.Invoices
{
    public interface IInvoiceService
    {
        Task<List<InvoiceListItem>> GetAllAsync(
            Guid tenantId,
            Guid? quoteId = null,
            Guid? dealId = null,
            string? status = null,
            DateTime? fromDate = null,
            DateTime? toDate = null);

        Task<InvoiceDto> GetByIdAsync(Guid tenantId, Guid id);

        Task<InvoiceStatisticsDto> GetStatisticsAsync(Guid tenantId);

        Task<InvoiceDto> CreateAsync(CreateInvoiceDto dto);

        Task<InvoiceDto> CreateFromQuoteAsync(CreateInvoiceFromQuoteDto dto);

        Task<InvoiceDto> UpdateAsync(Guid tenantId, Guid id, UpdateInvoiceDto dto);

        Task UpdateStatusAsync(Guid tenantId, Guid id, string status);

        Task<PaymentDto> AddPaymentAsync(CreatePaymentDto dto);

        Task DeleteAsync(Guid tenantId, Guid id);

        Task<byte[]> DownloadPdfAsync(Guid tenantId, Guid invoiceId);
    }
    public class InvoiceService : IInvoiceService
    {
        private readonly IApiService _apiService;
        private readonly ILogger<InvoiceService> _logger;

        public InvoiceService(IApiService apiService, ILogger<InvoiceService> logger)
        {
            _apiService = apiService;
            _logger = logger;
        }

        public async Task<List<InvoiceListItem>> GetAllAsync(
            Guid tenantId,
            Guid? quoteId = null,
            Guid? dealId = null,
            string? status = null,
            DateTime? fromDate = null,
            DateTime? toDate = null)
        {
            try
            {
                var url = $"api/invoices?tenantId={tenantId}";

                if (quoteId.HasValue) url += $"&quoteId={quoteId.Value}";
                if (dealId.HasValue) url += $"&dealId={dealId.Value}";
                if (!string.IsNullOrWhiteSpace(status)) url += $"&status={status}";
                if (fromDate.HasValue) url += $"&fromDate={fromDate.Value:yyyy-MM-ddTHH:mm:ss}";
                if (toDate.HasValue) url += $"&toDate={toDate.Value:yyyy-MM-ddTHH:mm:ss}";

                var list = await _apiService.GetAsync<List<InvoiceListItem>>(url);
                return list ?? new List<InvoiceListItem>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get invoices for tenant {TenantId}", tenantId);
                return new List<InvoiceListItem>();
            }
        }

        public async Task<InvoiceDto> GetByIdAsync(Guid tenantId, Guid id)
        {
            var url = $"api/invoices/{id}?tenantId={tenantId}";
            return await _apiService.GetAsync<InvoiceDto>(url);
        }

        public async Task<InvoiceStatisticsDto> GetStatisticsAsync(Guid tenantId)
        {
            try
            {
                var url = $"api/invoices/statistics?tenantId={tenantId}";
                var stats = await _apiService.GetAsync<InvoiceStatisticsDto>(url);
                return stats ?? new InvoiceStatisticsDto();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get invoice statistics for tenant {TenantId}", tenantId);
                return new InvoiceStatisticsDto();
            }
        }

        public async Task<InvoiceDto> CreateAsync(CreateInvoiceDto dto)
        {
            // API reads TenantId from body for create (dto has TenantId)
            return await _apiService.PostAsync<InvoiceDto>("api/invoices", dto);
        }

        public async Task<InvoiceDto> CreateFromQuoteAsync(CreateInvoiceFromQuoteDto dto)
        {
            // This will now hit https://localhost:61091 via ApiService base address
            return await _apiService.PostAsync<InvoiceDto>("api/invoices/from-quote", dto);
        }

        public async Task<InvoiceDto> UpdateAsync(Guid tenantId, Guid id, UpdateInvoiceDto dto)
        {
            var url = $"api/invoices/{id}?tenantId={tenantId}";
            return await _apiService.PutAsync<InvoiceDto>(url, dto);
        }

        public async Task UpdateStatusAsync(Guid tenantId, Guid id, string status)
        {
            // ✅ FIXED: matches controller PUT endpoint with UpdateInvoiceStatusDto body
            var url = $"api/invoices/{id}/status?tenantId={tenantId}";
            var dto = new UpdateInvoiceStatusDto
            {
                Id        = id,
                TenantId  = tenantId,
                Status    = status,
                UpdatedBy = "User"  // resolved on API side from ICurrentUserService
            };
            await _apiService.PutVoidAsync(url, dto);
        }

        public async Task<PaymentDto> AddPaymentAsync(CreatePaymentDto dto)
        {
            // tenantId is in dto; API can also require tenantId in query depending on your controller.
            // If your API requires tenantId in query, add: ?tenantId={dto.TenantId}
            var url = $"api/invoices/{dto.InvoiceId}/payments?tenantId={dto.TenantId}";
            return await _apiService.PostAsync<PaymentDto>(url, dto);
        }

        public async Task DeleteAsync(Guid tenantId, Guid id)
        {
            var url = $"api/invoices/{id}?tenantId={tenantId}";
            await _apiService.DeleteAsync(url);
        }

        public async Task<byte[]> DownloadPdfAsync(Guid tenantId, Guid invoiceId)
        {
            var url = $"api/invoices/{invoiceId}/pdf?tenantId={tenantId}";

            // Use raw HttpClient to get the binary response
            // IApiService.GetBytesAsync needs to be added OR use HttpClient directly
            return await _apiService.GetBytesAsync(url);
        }
    }
}
