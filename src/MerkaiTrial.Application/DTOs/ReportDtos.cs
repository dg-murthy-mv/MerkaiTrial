// =====================================================================
// ReportDtos.cs
// Location: MerkaiTrial.Application/DTOs/ReportDtos.cs
// =====================================================================

namespace MerkaiTrial.Application.DTOs
{
    // ── Shared filter ─────────────────────────────────────────────────
    public class ReportFilterDto
    {
        public DateTime? FromDate  { get; set; }
        public DateTime? ToDate    { get; set; }
        public string?   Vertical  { get; set; }
        public string    TenantId  { get; set; } = string.Empty;
    }

    // ── 1. Sales Funnel ───────────────────────────────────────────────
    public class SalesFunnelReportDto
    {
        public int     TotalLeads         { get; set; }
        public int     QualifiedLeads     { get; set; }
        public int     ConvertedLeads     { get; set; }
        public int     TotalDeals         { get; set; }
        public int     DealsWithQuotes    { get; set; }
        public int     TotalQuotes        { get; set; }
        public int     AcceptedQuotes     { get; set; }
        public int     TotalInvoices      { get; set; }
        public int     PaidInvoices       { get; set; }

        // Conversion rates (%)
        public double  LeadToQualifiedPct  { get; set; }
        public double  LeadToConvertedPct  { get; set; }
        public double  DealToQuotePct      { get; set; }
        public double  QuoteToAcceptedPct  { get; set; }
        public double  InvoiceToPaidPct    { get; set; }
    }

    // ── 2. Pipeline Summary ───────────────────────────────────────────
    public class PipelineSummaryReportDto
    {
        public List<PipelineStageItem> Stages { get; set; } = new();
        public decimal TotalPipelineValue     { get; set; }
        public int     TotalActiveDeals       { get; set; }
        public decimal AvgDealSize            { get; set; }
    }

    public class PipelineStageItem
    {
        public string  Stage      { get; set; } = string.Empty;
        public int     Count      { get; set; }
        public decimal TotalValue { get; set; }
        public int     AvgProbability { get; set; }
    }

    // ── 3. Revenue by Period ──────────────────────────────────────────
    public class RevenueByPeriodReportDto
    {
        public List<RevenuePeriodItem> Periods { get; set; } = new();
        public decimal TotalInvoiced  { get; set; }
        public decimal TotalCollected { get; set; }
        public decimal TotalOutstanding { get; set; }
    }

    public class RevenuePeriodItem
    {
        public string  Label     { get; set; } = string.Empty;  // "Jan 2026"
        public decimal Invoiced  { get; set; }
        public decimal Collected { get; set; }
    }

    // ── 4. Win/Loss Analysis ──────────────────────────────────────────
    public class WinLossReportDto
    {
        public List<WinLossOwnerItem> ByOwner  { get; set; } = new();
        public int    TotalWon      { get; set; }
        public int    TotalLost     { get; set; }
        public double OverallWinRate { get; set; }
        public decimal TotalWonValue { get; set; }
    }

    public class WinLossOwnerItem
    {
        public string  OwnerName { get; set; } = string.Empty;
        public int     Won       { get; set; }
        public int     Lost      { get; set; }
        public double  WinRate   { get; set; }
        public decimal WonValue  { get; set; }
    }

    // ── 5. Outstanding Invoices (Aging) ───────────────────────────────
    public class OutstandingInvoicesReportDto
    {
        public List<AgingBucketItem> Buckets  { get; set; } = new();
        public List<OutstandingInvoiceItem> Invoices { get; set; } = new();
        public decimal TotalOutstanding { get; set; }
        public int     TotalCount       { get; set; }
    }

    public class AgingBucketItem
    {
        public string  Label  { get; set; } = string.Empty;  // "0-30 days"
        public int     Count  { get; set; }
        public decimal Amount { get; set; }
    }

    public class OutstandingInvoiceItem
    {
        public string  Number      { get; set; } = string.Empty;
        public string  ContactName { get; set; } = string.Empty;
        public string  DealTitle   { get; set; } = string.Empty;
        public decimal GrandTotal  { get; set; }
        public decimal Balance     { get; set; }
        public DateTime? DueDateUtc { get; set; }
        public int     DaysOverdue  { get; set; }
        public string  Status       { get; set; } = string.Empty;
    }

    // ── 6. Revenue by Vertical ───────────────────────────────────────
    public class RevenueByVerticalReportDto
    {
        public List<VerticalRevenueItem> Verticals { get; set; } = new();
        public decimal TotalRevenue { get; set; }
        public decimal TotalInvoiced { get; set; }
        public int TotalDeals { get; set; }
    }

    public class VerticalRevenueItem
    {
        public string VerticalName { get; set; } = string.Empty;
        public int DealCount { get; set; }
        public int WonDeals { get; set; }
        public decimal PipelineValue { get; set; }  // active deals
        public decimal WonValue { get; set; }  // closed won deals
        public decimal InvoicedValue { get; set; }  // actual invoices raised
        public decimal CollectedValue { get; set; }  // actually paid
        public double WinRate { get; set; }
    }

    // ── 7. Sales Rep Performance ──────────────────────────────────────
    public class SalesRepPerformanceReportDto
    {
        public List<SalesRepItem> Reps { get; set; } = new();
        public int TotalDeals { get; set; }
        public int TotalWon { get; set; }
        public decimal TotalWonValue { get; set; }
        public double OverallWinRate { get; set; }
    }

    public class SalesRepItem
    {
        public string OwnerName { get; set; } = string.Empty;
        public int TotalDeals { get; set; }
        public int Won { get; set; }
        public int Lost { get; set; }
        public int Active { get; set; }
        public double WinRate { get; set; }
        public decimal WonValue { get; set; }
        public decimal PipelineValue { get; set; }
        public int LeadsOwned { get; set; }
        public int LeadsConverted { get; set; }
    }

    // ── 8. Lead Source Analysis ───────────────────────────────────────
    public class LeadSourceReportDto
    {
        public List<LeadSourceItem> ByChannel { get; set; } = new();
        public List<LeadSourceItem> BySource { get; set; } = new();
        public int TotalLeads { get; set; }
        public int TotalConverted { get; set; }
        public double OverallConversionRate { get; set; }
    }

    public class LeadSourceItem
    {
        public string Name { get; set; } = string.Empty;
        public int TotalLeads { get; set; }
        public int Qualified { get; set; }
        public int Converted { get; set; }
        public double ConversionRate { get; set; }
        public decimal EstimatedValue { get; set; }
    }

    // ── 9. Deal Velocity ──────────────────────────────────────────────
    public class DealVelocityReportDto
    {
        public List<StageVelocityItem> Stages { get; set; } = new();
        public double AvgDaysLeadToClose { get; set; }
        public double AvgDaysLeadToDeal { get; set; }
        public double AvgDaysDealToClose { get; set; }
        public int DealsAnalyzed { get; set; }
    }

    public class StageVelocityItem
    {
        public string FromStage { get; set; } = string.Empty;
        public string ToStage { get; set; } = string.Empty;
        public double AvgDays { get; set; }
        public double MinDays { get; set; }
        public double MaxDays { get; set; }
        public int Transitions { get; set; }
    }

    // ── 10. Activity Leaderboard ──────────────────────────────────────
    public class ActivityLeaderboardReportDto
    {
        public List<ActivityRepItem> Reps { get; set; } = new();
        public int TotalNotes { get; set; }
        public int TotalActivities { get; set; }
        public int TotalReminders { get; set; }
    }

    public class ActivityRepItem
    {
        public string OwnerName { get; set; } = string.Empty;
        public int Notes { get; set; }
        public int Activities { get; set; }
        public int Reminders { get; set; }
        public int Total { get; set; }
    }
}
