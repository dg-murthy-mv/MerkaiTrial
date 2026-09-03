// =====================================================================
// ReportService.cs
// Location: MerkaiTrial.Admin.Web/Services/Reports/ReportService.cs
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.Reports
{
    public interface IReportService
    {
        Task<SalesFunnelReportDto>         GetSalesFunnelAsync(DateTime? from, DateTime? to, string? vertical);
        Task<PipelineSummaryReportDto>     GetPipelineSummaryAsync(DateTime? from, DateTime? to, string? vertical);
        Task<RevenueByPeriodReportDto>     GetRevenueByPeriodAsync(DateTime? from, DateTime? to, string? vertical);
        Task<WinLossReportDto>             GetWinLossAsync(DateTime? from, DateTime? to, string? vertical);
        Task<OutstandingInvoicesReportDto> GetOutstandingInvoicesAsync(DateTime? from, DateTime? to, string? vertical);
        Task<RevenueByVerticalReportDto> GetRevenueByVerticalAsync(DateTime? from, DateTime? to, string? vertical);
        Task<SalesRepPerformanceReportDto> GetRepPerformanceAsync(DateTime? from, DateTime? to, string? vertical);
        Task<LeadSourceReportDto> GetLeadSourcesAsync(DateTime? from, DateTime? to, string? vertical);
        Task<DealVelocityReportDto> GetDealVelocityAsync(DateTime? from, DateTime? to, string? vertical);
        Task<ActivityLeaderboardReportDto> GetActivityLeaderboardAsync(DateTime? from, DateTime? to, string? vertical);
        Task<byte[]> ExportPipelineAsync(DateTime? from, DateTime? to, string? vertical);
        Task<byte[]> ExportRevenueAsync(DateTime? from, DateTime? to, string? vertical);
        Task<byte[]> ExportWinLossAsync(DateTime? from, DateTime? to, string? vertical);
        Task<byte[]> ExportOutstandingAsync(DateTime? from, DateTime? to, string? vertical);
        Task<byte[]> ExportRevenueByVerticalAsync(DateTime? from, DateTime? to, string? vertical);
        Task<byte[]> ExportRepPerformanceAsync(DateTime? from, DateTime? to, string? vertical);
        Task<byte[]> ExportLeadSourcesAsync(DateTime? from, DateTime? to, string? vertical);
        Task<byte[]> ExportDealVelocityAsync(DateTime? from, DateTime? to, string? vertical);
    }

    public class ReportService : IReportService
    {
        private readonly IApiService _api;
        private readonly ILogger<ReportService> _logger;

        public ReportService(IApiService api, ILogger<ReportService> logger)
        {
            _api    = api;
            _logger = logger;
        }

        private string BuildQuery(DateTime? from, DateTime? to, string? vertical)
        {
            var parts = new List<string>();
            if (from.HasValue)
                parts.Add($"from={from.Value:yyyy-MM-dd}");
            if (to.HasValue)
                parts.Add($"to={to.Value:yyyy-MM-dd}");
            if (!string.IsNullOrWhiteSpace(vertical))
                parts.Add($"vertical={Uri.EscapeDataString(vertical)}");
            return parts.Any() ? "?" + string.Join("&", parts) : "";
        }

        public async Task<SalesFunnelReportDto> GetSalesFunnelAsync(
            DateTime? from, DateTime? to, string? vertical)
        {
            try
            {
                return await _api.GetAsync<SalesFunnelReportDto>(
                    $"/api/reports/sales-funnel{BuildQuery(from, to, vertical)}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting sales funnel report");
                throw;
            }
        }

        public async Task<PipelineSummaryReportDto> GetPipelineSummaryAsync(
            DateTime? from, DateTime? to, string? vertical)
        {
            try
            {
                return await _api.GetAsync<PipelineSummaryReportDto>(
                    $"/api/reports/pipeline-summary{BuildQuery(from, to, vertical)}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting pipeline summary report");
                throw;
            }
        }

        public async Task<RevenueByPeriodReportDto> GetRevenueByPeriodAsync(
            DateTime? from, DateTime? to, string? vertical)
        {
            try
            {
                return await _api.GetAsync<RevenueByPeriodReportDto>(
                    $"/api/reports/revenue-by-period{BuildQuery(from, to, vertical)}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting revenue by period report");
                throw;
            }
        }

        public async Task<WinLossReportDto> GetWinLossAsync(
            DateTime? from, DateTime? to, string? vertical)
        {
            try
            {
                return await _api.GetAsync<WinLossReportDto>(
                    $"/api/reports/win-loss{BuildQuery(from, to, vertical)}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting win/loss report");
                throw;
            }
        }

        public async Task<OutstandingInvoicesReportDto> GetOutstandingInvoicesAsync(
            DateTime? from, DateTime? to, string? vertical)
        {
            try
            {
                return await _api.GetAsync<OutstandingInvoicesReportDto>(
                    $"/api/reports/outstanding-invoices{BuildQuery(from, to, vertical)}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting outstanding invoices report");
                throw;
            }
        }
        public async Task<SalesRepPerformanceReportDto> GetRepPerformanceAsync(
            DateTime? from, DateTime? to, string? vertical)
        {
            try
            {
                return await _api.GetAsync<SalesRepPerformanceReportDto>(
                $"/api/reports/rep-performance{BuildQuery(from, to, vertical)}");
            }
            catch (Exception ex) { _logger.LogError(ex, "Error getting rep performance"); throw; }
        }

        public async Task<LeadSourceReportDto> GetLeadSourcesAsync(
            DateTime? from, DateTime? to, string? vertical)
        {
            try
            {
                return await _api.GetAsync<LeadSourceReportDto>(
                $"/api/reports/lead-sources{BuildQuery(from, to, vertical)}");
            }
            catch (Exception ex) { _logger.LogError(ex, "Error getting lead sources"); throw; }
        }

        public async Task<DealVelocityReportDto> GetDealVelocityAsync(
            DateTime? from, DateTime? to, string? vertical)
        {
            try
            {
                return await _api.GetAsync<DealVelocityReportDto>(
                $"/api/reports/deal-velocity{BuildQuery(from, to, vertical)}");
            }
            catch (Exception ex) { _logger.LogError(ex, "Error getting deal velocity"); throw; }
        }

        public async Task<ActivityLeaderboardReportDto> GetActivityLeaderboardAsync(
            DateTime? from, DateTime? to, string? vertical)
        {
            try
            {
                return await _api.GetAsync<ActivityLeaderboardReportDto>(
                $"/api/reports/activity-leaderboard{BuildQuery(from, to, vertical)}");
            }
            catch (Exception ex) { _logger.LogError(ex, "Error getting activity leaderboard"); throw; }
        }
        public async Task<byte[]> ExportPipelineAsync(DateTime? from, DateTime? to, string? vertical)
            => await _api.GetBytesAsync($"/api/reports/pipeline-summary/export{BuildQuery(from, to, vertical)}");

        public async Task<byte[]> ExportRevenueAsync(DateTime? from, DateTime? to, string? vertical)
            => await _api.GetBytesAsync($"/api/reports/revenue-by-period/export{BuildQuery(from, to, vertical)}");

        public async Task<byte[]> ExportWinLossAsync(DateTime? from, DateTime? to, string? vertical)
            => await _api.GetBytesAsync($"/api/reports/win-loss/export{BuildQuery(from, to, vertical)}");

        public async Task<byte[]> ExportOutstandingAsync(DateTime? from, DateTime? to, string? vertical)
            => await _api.GetBytesAsync($"/api/reports/outstanding-invoices/export{BuildQuery(from, to, vertical)}");

        public async Task<RevenueByVerticalReportDto> GetRevenueByVerticalAsync(
           DateTime? from, DateTime? to, string? vertical)
        {
            try
            {
                return await _api.GetAsync<RevenueByVerticalReportDto>(
                    $"/api/reports/revenue-by-vertical{BuildQuery(from, to, vertical)}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting revenue by vertical report");
                throw;
            }
        }
        public async Task<byte[]> ExportRepPerformanceAsync(DateTime? from, DateTime? to, string? vertical)
          => await _api.GetBytesAsync($"/api/reports/rep-performance/export{BuildQuery(from, to, vertical)}");

        public async Task<byte[]> ExportLeadSourcesAsync(DateTime? from, DateTime? to, string? vertical)
            => await _api.GetBytesAsync($"/api/reports/lead-sources/export{BuildQuery(from, to, vertical)}");

        public async Task<byte[]> ExportDealVelocityAsync(DateTime? from, DateTime? to, string? vertical)
            => await _api.GetBytesAsync($"/api/reports/deal-velocity/export{BuildQuery(from, to, vertical)}");

        public async Task<byte[]> ExportRevenueByVerticalAsync(
            DateTime? from, DateTime? to, string? vertical)
            => await _api.GetBytesAsync(
                $"/api/reports/revenue-by-vertical/export{BuildQuery(from, to, vertical)}");

       
    }
}
