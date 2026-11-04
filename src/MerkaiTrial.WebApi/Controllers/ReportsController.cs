// =====================================================================
// ReportsController.cs
// Location: MerkaiTrial.WebApi/Controllers/ReportsController.cs
//
// 113 — every endpoint needs reports.read (it had no permission check at
// all: any signed-in person could call it). The Revenue & Finance ones —
// revenue-by-period, outstanding-invoices (and their exports),
// quote-performance, invoice-collections — need financereports.read as
// well. Workspace admins bypass, as everywhere. What a person may see
// INSIDE a report is still their record scope (own / team / all).
// =====================================================================

using MerkaiTrial.Application.Commands.Reports;
using MerkaiTrial.Application.Common;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Application.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using System.Drawing;
using MerkaiTrial.Application.Configuration;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
    [Route("api/reports")]
    [Authorize(Policy = Policies.ReportsRead)]   // 113
    [RequiresPlanFeature(PlanFeatureKeys.BasicReports, PlanFeatureKeys.AdvancedReports)]   // 114
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
        // 112 — the priority reports
        private readonly GetStalledDealsHandler        _stalledDeals;
        private readonly GetLeadFunnelHandler          _leadFunnel;
        private readonly GetQuotePerformanceHandler    _quotePerformance;
        private readonly GetInvoiceCollectionsHandler  _invoiceCollections;
        private readonly GetRevenueForecastHandler     _revenueForecast;
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
            GetStalledDealsHandler stalledDeals,
            GetLeadFunnelHandler leadFunnel,
            GetQuotePerformanceHandler quotePerformance,
            GetInvoiceCollectionsHandler invoiceCollections,
            GetRevenueForecastHandler revenueForecast,
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
            _stalledDeals = stalledDeals;
            _leadFunnel = leadFunnel;
            _quotePerformance = quotePerformance;
            _invoiceCollections = invoiceCollections;
            _revenueForecast = revenueForecast;
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
        [Authorize(Policy = Policies.FinanceReportsRead)]   // 113
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
        [Authorize(Policy = Policies.FinanceReportsRead)]   // 113
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

            // 111: the stage's NAME and what it means (open today / won or
            // lost in the period), in the workspace's order.
            ws.Cells[1, 1].Value = "Stage";
            ws.Cells[1, 2].Value = "Type";
            ws.Cells[1, 3].Value = "Deal Count";
            ws.Cells[1, 4].Value = $"Total Value ({data.Currency})";
            ws.Cells[1, 5].Value = "Avg Probability %";
            StyleHeader(ws, 1, 5);

            for (int i = 0; i < data.Stages.Count; i++)
            {
                var s = data.Stages[i];
                ws.Cells[i + 2, 1].Value = string.IsNullOrEmpty(s.StageName) ? s.Stage : s.StageName;
                ws.Cells[i + 2, 2].Value = s.Category == "Open" ? "Open (today)" : s.Category + " (in period)";
                ws.Cells[i + 2, 3].Value = s.Count;
                ws.Cells[i + 2, 4].Value = s.TotalValue;
                ws.Cells[i + 2, 5].Value = s.AvgProbability;
            }
            ws.Cells.AutoFitColumns();
            return ExcelFile(pkg, "PipelineSummary");
        }

        [HttpGet("revenue-by-period/export")]
        [Authorize(Policy = Policies.FinanceReportsRead)]   // 113
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
        [Authorize(Policy = Policies.FinanceReportsRead)]   // 113
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
        [RequiresPlanFeature(PlanFeatureKeys.AdvancedReports)]   // 114
        public async Task<IActionResult> GetRepPerformance(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] string? vertical, CancellationToken ct)
        {
            try { return Ok(await _repPerformance.HandleAsync(BuildFilter(from, to, vertical), ct)); }
            catch (Exception ex) { _logger.LogError(ex, "Error getting rep performance"); return StatusCode(500, new { error = "Failed" }); }
        }

        [HttpGet("rep-performance/export")]
        [RequiresPlanFeature(PlanFeatureKeys.AdvancedReports)]   // 114
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
        [RequiresPlanFeature(PlanFeatureKeys.AdvancedReports)]   // 114
        public async Task<IActionResult> GetLeadSources(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] string? vertical, CancellationToken ct)
        {
            try { return Ok(await _leadSource.HandleAsync(BuildFilter(from, to, vertical), ct)); }
            catch (Exception ex) { _logger.LogError(ex, "Error getting lead sources"); return StatusCode(500, new { error = "Failed" }); }
        }

        [HttpGet("lead-sources/export")]
        [RequiresPlanFeature(PlanFeatureKeys.AdvancedReports)]   // 114
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
        [RequiresPlanFeature(PlanFeatureKeys.AdvancedReports)]   // 114
        public async Task<IActionResult> GetDealVelocity(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] string? vertical, CancellationToken ct)
        {
            try { return Ok(await _dealVelocity.HandleAsync(BuildFilter(from, to, vertical), ct)); }
            catch (Exception ex) { _logger.LogError(ex, "Error getting deal velocity"); return StatusCode(500, new { error = "Failed" }); }
        }

        [HttpGet("deal-velocity/export")]
        [RequiresPlanFeature(PlanFeatureKeys.AdvancedReports)]   // 114
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
        [RequiresPlanFeature(PlanFeatureKeys.AdvancedReports)]   // 114
        public async Task<IActionResult> GetActivityLeaderboard(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] string? vertical, CancellationToken ct)
        {
            try { return Ok(await _activityLeaderboard.HandleAsync(BuildFilter(from, to, vertical), ct)); }
            catch (Exception ex) { _logger.LogError(ex, "Error getting activity leaderboard"); return StatusCode(500, new { error = "Failed" }); }
        }

        // ── 112: the priority reports ─────────────────────────────────

        // 11. Stalled Deals — open deals today; days = stalled after (default 30)
        [HttpGet("stalled-deals")]
        [RequiresPlanFeature(PlanFeatureKeys.AdvancedReports)]   // 114
        public async Task<IActionResult> GetStalledDeals(
            [FromQuery] string? vertical, [FromQuery] int? days, CancellationToken ct)
        {
            try
            {
                var f = BuildFilter(null, null, vertical);
                f.Days = days;
                return Ok(await _stalledDeals.HandleAsync(f, ct));
            }
            catch (Exception ex) { _logger.LogError(ex, "Error getting stalled deals"); return StatusCode(500, new { error = "Failed" }); }
        }

        // 12. Lead Funnel & Conversion — leads received in the period
        [HttpGet("lead-funnel")]
        public async Task<IActionResult> GetLeadFunnel(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] string? vertical, CancellationToken ct)
        {
            try { return Ok(await _leadFunnel.HandleAsync(BuildFilter(from, to, vertical), ct)); }
            catch (Exception ex) { _logger.LogError(ex, "Error getting lead funnel"); return StatusCode(500, new { error = "Failed" }); }
        }

        // 13. Quote Performance — quotes issued in the period
        [HttpGet("quote-performance")]
        [Authorize(Policy = Policies.FinanceReportsRead)]   // 113
        public async Task<IActionResult> GetQuotePerformance(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] string? vertical, CancellationToken ct)
        {
            try { return Ok(await _quotePerformance.HandleAsync(BuildFilter(from, to, vertical), ct)); }
            catch (Exception ex) { _logger.LogError(ex, "Error getting quote performance"); return StatusCode(500, new { error = "Failed" }); }
        }

        // 14. Invoice & Collections — issued / paid in the period; overdue today
        [HttpGet("invoice-collections")]
        [Authorize(Policy = Policies.FinanceReportsRead)]   // 113
        public async Task<IActionResult> GetInvoiceCollections(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] string? vertical, CancellationToken ct)
        {
            try { return Ok(await _invoiceCollections.HandleAsync(BuildFilter(from, to, vertical), ct)); }
            catch (Exception ex) { _logger.LogError(ex, "Error getting invoice collections"); return StatusCode(500, new { error = "Failed" }); }
        }

        // 15. Revenue Forecast — open deals today by expected close month; months ahead (default 6)
        [HttpGet("revenue-forecast")]
        [RequiresPlanFeature(PlanFeatureKeys.AdvancedReports)]   // 114
        public async Task<IActionResult> GetRevenueForecast(
            [FromQuery] string? vertical, [FromQuery] int? months, CancellationToken ct)
        {
            try
            {
                var f = BuildFilter(null, null, vertical);
                f.Months = months;
                return Ok(await _revenueForecast.HandleAsync(f, ct));
            }
            catch (Exception ex) { _logger.LogError(ex, "Error getting revenue forecast"); return StatusCode(500, new { error = "Failed" }); }
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
            // 105 — VUL-15: owner names, stage names, deal titles, contact
            // names… are people's text. Any that starts like a formula
            // (= + - @) is marked quote-prefix, so Excel keeps it as text
            // even after an edit. Values are unchanged. (A copy re-saved as
            // CSV by hand loses the mark — the agreed limit.)
            foreach (var ws in pkg.Workbook.Worksheets)
            {
                if (ws.Dimension is null) continue;
                foreach (var cell in ws.Cells[ws.Dimension.Address])
                    if (cell.Value is string text && SpreadsheetText.LooksLikeFormula(text))
                        cell.Style.QuotePrefix = true;
            }

            var bytes = pkg.GetAsByteArray();
            return File(bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"{name}_{DateTime.UtcNow:yyyyMMdd}.xlsx");
        }
    }
}
