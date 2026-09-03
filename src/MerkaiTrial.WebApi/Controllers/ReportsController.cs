// =====================================================================
// ReportsController.cs
// Location: MerkaiTrial.WebApi/Controllers/ReportsController.cs
// =====================================================================

using MerkaiTrial.Application.Commands.Reports;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using System.Drawing;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
    [Route("api/reports")]
    public class ReportsController : ControllerBase
    {
        private readonly GetSalesFunnelHandler        _salesFunnel;
        private readonly GetPipelineSummaryHandler    _pipeline;
        private readonly GetRevenueByPeriodHandler    _revenue;
        private readonly GetWinLossHandler            _winLoss;
        private readonly GetOutstandingInvoicesHandler _outstanding;
        private readonly GetRevenueByVerticalHandler _revenueByVertical;
        private readonly GetSalesRepPerformanceHandler _repPerformance;
        private readonly GetLeadSourceHandler _leadSource;
        private readonly GetDealVelocityHandler _dealVelocity;
        private readonly GetActivityLeaderboardHandler _activityLeaderboard;
        private readonly ICurrentUserService          _currentUserService;
        private readonly ILogger<ReportsController>   _logger;

        public ReportsController(
            GetSalesFunnelHandler        salesFunnel,
            GetPipelineSummaryHandler    pipeline,
            GetRevenueByPeriodHandler    revenue,
            GetWinLossHandler            winLoss,
            GetOutstandingInvoicesHandler outstanding,
            GetRevenueByVerticalHandler revenueByVertical,
            GetSalesRepPerformanceHandler repPerformance,
            GetLeadSourceHandler leadSource,
            GetDealVelocityHandler dealVelocity,
            GetActivityLeaderboardHandler activityLeaderboard,
            ICurrentUserService          currentUserService,
            ILogger<ReportsController>   logger)
        {
            _salesFunnel        = salesFunnel;
            _pipeline           = pipeline;
            _revenue            = revenue;
            _winLoss            = winLoss;
            _outstanding        = outstanding;
            _revenueByVertical = revenueByVertical;
            _repPerformance = repPerformance;
            _leadSource = leadSource;
            _dealVelocity = dealVelocity;
            _activityLeaderboard = activityLeaderboard;
            _currentUserService = currentUserService;
            _logger             = logger;
        }

        private ReportFilterDto BuildFilter(
            DateTime? from, DateTime? to, string? vertical)
        {
            return new ReportFilterDto
            {
                TenantId = _currentUserService.GetCurrentTenantId().ToString(),
                FromDate  = from,
                ToDate    = to,
                Vertical  = string.IsNullOrWhiteSpace(vertical) ? null : vertical
            };
        }

        // ── 1. Sales Funnel ───────────────────────────────────────────

        [HttpGet("sales-funnel")]
        public async Task<IActionResult> GetSalesFunnel(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] string?   vertical,
            CancellationToken ct)
        {
            try
            {
                var result = await _salesFunnel.HandleAsync(BuildFilter(from, to, vertical), ct);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting sales funnel report");
                return StatusCode(500, new { error = "Failed to generate report" });
            }
        }

        // ── 2. Pipeline Summary ───────────────────────────────────────

        [HttpGet("pipeline-summary")]
        public async Task<IActionResult> GetPipelineSummary(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] string?   vertical,
            CancellationToken ct)
        {
            try
            {
                var result = await _pipeline.HandleAsync(BuildFilter(from, to, vertical), ct);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting pipeline summary report");
                return StatusCode(500, new { error = "Failed to generate report" });
            }
        }

        // ── 3. Revenue by Period ──────────────────────────────────────

        [HttpGet("revenue-by-period")]
        public async Task<IActionResult> GetRevenueByPeriod(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] string?   vertical,
            CancellationToken ct)
        {
            try
            {
                var result = await _revenue.HandleAsync(BuildFilter(from, to, vertical), ct);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting revenue by period report");
                return StatusCode(500, new { error = "Failed to generate report" });
            }
        }

        // ── 4. Win/Loss Analysis ──────────────────────────────────────

        [HttpGet("win-loss")]
        public async Task<IActionResult> GetWinLoss(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] string?   vertical,
            CancellationToken ct)
        {
            try
            {
                var result = await _winLoss.HandleAsync(BuildFilter(from, to, vertical), ct);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting win/loss report");
                return StatusCode(500, new { error = "Failed to generate report" });
            }
        }

        // ── 5. Outstanding Invoices ───────────────────────────────────

        [HttpGet("outstanding-invoices")]
        public async Task<IActionResult> GetOutstandingInvoices(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] string?   vertical,
            CancellationToken ct)
        {
            try
            {
                var result = await _outstanding.HandleAsync(BuildFilter(from, to, vertical), ct);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting outstanding invoices report");
                return StatusCode(500, new { error = "Failed to generate report" });
            }
        }

        // ── EXCEL EXPORTS ─────────────────────────────────────────────

        [HttpGet("pipeline-summary/export")]
        public async Task<IActionResult> ExportPipeline(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] string? vertical, CancellationToken ct)
        {
            var data = await _pipeline.HandleAsync(BuildFilter(from, to, vertical), ct);
            using var pkg = new ExcelPackage();
            var ws = pkg.Workbook.Worksheets.Add("Pipeline Summary");

            ws.Cells[1, 1].Value = "Stage";
            ws.Cells[1, 2].Value = "Deal Count";
            ws.Cells[1, 3].Value = "Total Value";
            ws.Cells[1, 4].Value = "Avg Probability %";
            StyleHeader(ws, 1, 4);

            for (int i = 0; i < data.Stages.Count; i++)
            {
                var s = data.Stages[i];
                ws.Cells[i + 2, 1].Value = s.Stage;
                ws.Cells[i + 2, 2].Value = s.Count;
                ws.Cells[i + 2, 3].Value = s.TotalValue;
                ws.Cells[i + 2, 4].Value = s.AvgProbability;
            }
            ws.Cells.AutoFitColumns();
            return ExcelFile(pkg, "PipelineSummary");
        }

        [HttpGet("revenue-by-period/export")]
        public async Task<IActionResult> ExportRevenue(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] string? vertical, CancellationToken ct)
        {
            var data = await _revenue.HandleAsync(BuildFilter(from, to, vertical), ct);
            using var pkg = new ExcelPackage();
            var ws = pkg.Workbook.Worksheets.Add("Revenue by Period");

            ws.Cells[1, 1].Value = "Period";
            ws.Cells[1, 2].Value = "Invoiced";
            ws.Cells[1, 3].Value = "Collected";
            StyleHeader(ws, 1, 3);

            for (int i = 0; i < data.Periods.Count; i++)
            {
                var p = data.Periods[i];
                ws.Cells[i + 2, 1].Value = p.Label;
                ws.Cells[i + 2, 2].Value = p.Invoiced;
                ws.Cells[i + 2, 3].Value = p.Collected;
            }
            ws.Cells.AutoFitColumns();
            return ExcelFile(pkg, "RevenueByPeriod");
        }

        [HttpGet("win-loss/export")]
        public async Task<IActionResult> ExportWinLoss(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] string? vertical, CancellationToken ct)
        {
            var data = await _winLoss.HandleAsync(BuildFilter(from, to, vertical), ct);
            using var pkg = new ExcelPackage();
            var ws = pkg.Workbook.Worksheets.Add("Win Loss Analysis");

            ws.Cells[1, 1].Value = "Owner";
            ws.Cells[1, 2].Value = "Won";
            ws.Cells[1, 3].Value = "Lost";
            ws.Cells[1, 4].Value = "Win Rate %";
            ws.Cells[1, 5].Value = "Won Value";
            StyleHeader(ws, 1, 5);

            for (int i = 0; i < data.ByOwner.Count; i++)
            {
                var o = data.ByOwner[i];
                ws.Cells[i + 2, 1].Value = o.OwnerName;
                ws.Cells[i + 2, 2].Value = o.Won;
                ws.Cells[i + 2, 3].Value = o.Lost;
                ws.Cells[i + 2, 4].Value = o.WinRate;
                ws.Cells[i + 2, 5].Value = o.WonValue;
            }
            ws.Cells.AutoFitColumns();
            return ExcelFile(pkg, "WinLossAnalysis");
        }

        [HttpGet("outstanding-invoices/export")]
        public async Task<IActionResult> ExportOutstanding(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] string? vertical, CancellationToken ct)
        {
            var data = await _outstanding.HandleAsync(BuildFilter(from, to, vertical), ct);
            using var pkg = new ExcelPackage();
            var ws = pkg.Workbook.Worksheets.Add("Outstanding Invoices");

            ws.Cells[1, 1].Value = "Invoice #";
            ws.Cells[1, 2].Value = "Contact";
            ws.Cells[1, 3].Value = "Deal";
            ws.Cells[1, 4].Value = "Total";
            ws.Cells[1, 5].Value = "Balance";
            ws.Cells[1, 6].Value = "Due Date";
            ws.Cells[1, 7].Value = "Days Overdue";
            ws.Cells[1, 8].Value = "Status";
            StyleHeader(ws, 1, 8);

            for (int i = 0; i < data.Invoices.Count; i++)
            {
                var inv = data.Invoices[i];
                ws.Cells[i + 2, 1].Value = inv.Number;
                ws.Cells[i + 2, 2].Value = inv.ContactName;
                ws.Cells[i + 2, 3].Value = inv.DealTitle;
                ws.Cells[i + 2, 4].Value = inv.GrandTotal;
                ws.Cells[i + 2, 5].Value = inv.Balance;
                ws.Cells[i + 2, 6].Value = inv.DueDateUtc?.ToString("dd/MM/yyyy");
                ws.Cells[i + 2, 7].Value = inv.DaysOverdue;
                ws.Cells[i + 2, 8].Value = inv.Status;
            }
            ws.Cells.AutoFitColumns();
            return ExcelFile(pkg, "OutstandingInvoices");
        }

        // ── 6. Revenue by Vertical ────────────────────────────────────

        [HttpGet("revenue-by-vertical")]
        public async Task<IActionResult> GetRevenueByVertical(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] string? vertical,
            CancellationToken ct)
        {
            try
            {
                var result = await _revenueByVertical.HandleAsync(
                    BuildFilter(from, to, vertical), ct);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting revenue by vertical report");
                return StatusCode(500, new { error = "Failed to generate report" });
            }
        }

        [HttpGet("revenue-by-vertical/export")]
        public async Task<IActionResult> ExportRevenueByVertical(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] string? vertical,
            CancellationToken ct)
        {
            var data = await _revenueByVertical.HandleAsync(
                BuildFilter(from, to, vertical), ct);

            using var pkg = new ExcelPackage();
            var ws = pkg.Workbook.Worksheets.Add("Revenue by Vertical");

            ws.Cells[1, 1].Value = "Vertical";
            ws.Cells[1, 2].Value = "Total Deals";
            ws.Cells[1, 3].Value = "Won Deals";
            ws.Cells[1, 4].Value = "Win Rate %";
            ws.Cells[1, 5].Value = "Pipeline Value";
            ws.Cells[1, 6].Value = "Won Value";
            ws.Cells[1, 7].Value = "Invoiced";
            ws.Cells[1, 8].Value = "Collected";
            StyleHeader(ws, 1, 8);

            for (int i = 0; i < data.Verticals.Count; i++)
            {
                var v = data.Verticals[i];
                ws.Cells[i + 2, 1].Value = v.VerticalName;
                ws.Cells[i + 2, 2].Value = v.DealCount;
                ws.Cells[i + 2, 3].Value = v.WonDeals;
                ws.Cells[i + 2, 4].Value = v.WinRate;
                ws.Cells[i + 2, 5].Value = v.PipelineValue;
                ws.Cells[i + 2, 6].Value = v.WonValue;
                ws.Cells[i + 2, 7].Value = v.InvoicedValue;
                ws.Cells[i + 2, 8].Value = v.CollectedValue;
            }
            ws.Cells.AutoFitColumns();
            return ExcelFile(pkg, "RevenueByVertical");
        }

        // ── 7. Sales Rep Performance ──────────────────────────────────
        [HttpGet("rep-performance")]
        public async Task<IActionResult> GetRepPerformance(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] string? vertical, CancellationToken ct)
        {
            try { return Ok(await _repPerformance.HandleAsync(BuildFilter(from, to, vertical), ct)); }
            catch (Exception ex) { _logger.LogError(ex, "Error getting rep performance"); return StatusCode(500, new { error = "Failed" }); }
        }

        [HttpGet("rep-performance/export")]
        public async Task<IActionResult> ExportRepPerformance(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] string? vertical, CancellationToken ct)
        {
            var data = await _repPerformance.HandleAsync(BuildFilter(from, to, vertical), ct);
            using var pkg = new ExcelPackage();
            var ws = pkg.Workbook.Worksheets.Add("Rep Performance");
            ws.Cells[1, 1].Value = "Owner"; ws.Cells[1, 2].Value = "Deals"; ws.Cells[1, 3].Value = "Won";
            ws.Cells[1, 4].Value = "Lost"; ws.Cells[1, 5].Value = "Win Rate%"; ws.Cells[1, 6].Value = "Won Value";
            ws.Cells[1, 7].Value = "Pipeline"; ws.Cells[1, 8].Value = "Leads"; ws.Cells[1, 9].Value = "Converted";
            StyleHeader(ws, 1, 9);
            for (int i = 0; i < data.Reps.Count; i++)
            {
                var r = data.Reps[i];
                ws.Cells[i + 2, 1].Value = r.OwnerName; ws.Cells[i + 2, 2].Value = r.TotalDeals;
                ws.Cells[i + 2, 3].Value = r.Won; ws.Cells[i + 2, 4].Value = r.Lost;
                ws.Cells[i + 2, 5].Value = r.WinRate; ws.Cells[i + 2, 6].Value = r.WonValue;
                ws.Cells[i + 2, 7].Value = r.PipelineValue; ws.Cells[i + 2, 8].Value = r.LeadsOwned;
                ws.Cells[i + 2, 9].Value = r.LeadsConverted;
            }
            ws.Cells.AutoFitColumns();
            return ExcelFile(pkg, "RepPerformance");
        }

        // ── 8. Lead Source Analysis ───────────────────────────────────
        [HttpGet("lead-sources")]
        public async Task<IActionResult> GetLeadSources(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] string? vertical, CancellationToken ct)
        {
            try { return Ok(await _leadSource.HandleAsync(BuildFilter(from, to, vertical), ct)); }
            catch (Exception ex) { _logger.LogError(ex, "Error getting lead sources"); return StatusCode(500, new { error = "Failed" }); }
        }

        [HttpGet("lead-sources/export")]
        public async Task<IActionResult> ExportLeadSources(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] string? vertical, CancellationToken ct)
        {
            var data = await _leadSource.HandleAsync(BuildFilter(from, to, vertical), ct);
            using var pkg = new ExcelPackage();
            var ws = pkg.Workbook.Worksheets.Add("By Channel");
            ws.Cells[1, 1].Value = "Channel"; ws.Cells[1, 2].Value = "Leads"; ws.Cells[1, 3].Value = "Qualified";
            ws.Cells[1, 4].Value = "Converted"; ws.Cells[1, 5].Value = "Conv Rate%"; ws.Cells[1, 6].Value = "Est Value";
            StyleHeader(ws, 1, 6);
            for (int i = 0; i < data.ByChannel.Count; i++)
            {
                var c = data.ByChannel[i];
                ws.Cells[i + 2, 1].Value = c.Name; ws.Cells[i + 2, 2].Value = c.TotalLeads;
                ws.Cells[i + 2, 3].Value = c.Qualified; ws.Cells[i + 2, 4].Value = c.Converted;
                ws.Cells[i + 2, 5].Value = c.ConversionRate; ws.Cells[i + 2, 6].Value = c.EstimatedValue;
            }
            ws.Cells.AutoFitColumns();
            return ExcelFile(pkg, "LeadSourceAnalysis");
        }

        // ── 9. Deal Velocity ──────────────────────────────────────────
        [HttpGet("deal-velocity")]
        public async Task<IActionResult> GetDealVelocity(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] string? vertical, CancellationToken ct)
        {
            try { return Ok(await _dealVelocity.HandleAsync(BuildFilter(from, to, vertical), ct)); }
            catch (Exception ex) { _logger.LogError(ex, "Error getting deal velocity"); return StatusCode(500, new { error = "Failed" }); }
        }

        [HttpGet("deal-velocity/export")]
        public async Task<IActionResult> ExportDealVelocity(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] string? vertical, CancellationToken ct)
        {
            var data = await _dealVelocity.HandleAsync(BuildFilter(from, to, vertical), ct);
            using var pkg = new ExcelPackage();
            var ws = pkg.Workbook.Worksheets.Add("Deal Velocity");
            ws.Cells[1, 1].Value = "From Stage"; ws.Cells[1, 2].Value = "To Stage";
            ws.Cells[1, 3].Value = "Avg Days"; ws.Cells[1, 4].Value = "Min Days";
            ws.Cells[1, 5].Value = "Max Days"; ws.Cells[1, 6].Value = "Transitions";
            StyleHeader(ws, 1, 6);
            for (int i = 0; i < data.Stages.Count; i++)
            {
                var s = data.Stages[i];
                ws.Cells[i + 2, 1].Value = s.FromStage; ws.Cells[i + 2, 2].Value = s.ToStage;
                ws.Cells[i + 2, 3].Value = s.AvgDays; ws.Cells[i + 2, 4].Value = s.MinDays;
                ws.Cells[i + 2, 5].Value = s.MaxDays; ws.Cells[i + 2, 6].Value = s.Transitions;
            }
            ws.Cells.AutoFitColumns();
            return ExcelFile(pkg, "DealVelocity");
        }

        // ── 10. Activity Leaderboard ──────────────────────────────────
        [HttpGet("activity-leaderboard")]
        public async Task<IActionResult> GetActivityLeaderboard(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] string? vertical, CancellationToken ct)
        {
            try { return Ok(await _activityLeaderboard.HandleAsync(BuildFilter(from, to, vertical), ct)); }
            catch (Exception ex) { _logger.LogError(ex, "Error getting activity leaderboard"); return StatusCode(500, new { error = "Failed" }); }
        }

        // ── HELPERS ───────────────────────────────────────────────────

        private static void StyleHeader(ExcelWorksheet ws, int row, int cols)
        {
            using var range = ws.Cells[row, 1, row, cols];
            range.Style.Font.Bold = true;
            range.Style.Fill.PatternType = ExcelFillStyle.Solid;
            range.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(79, 70, 229)); // indigo
            range.Style.Font.Color.SetColor(Color.White);
        }

        private FileContentResult ExcelFile(ExcelPackage pkg, string name)
        {
            var bytes = pkg.GetAsByteArray();
            return File(bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"{name}_{DateTime.UtcNow:yyyyMMdd}.xlsx");
        }
    }
}
