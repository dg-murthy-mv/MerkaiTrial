// =====================================================================
// ReportDtos.cs
// Location: MerkaiTrial.Application/DTOs/ReportDtos.cs
//
// CHANGES (111 — reports that add up)
//   ✅ ReportBaseDto: every report says which currency its money is in
//      (the workspace's), and how many records in another currency were
//      left out of its sums rather than added to them.
//   ✅ PipelineStageItem: StageName (what people see, not the key),
//      Category (Open / Won / Lost) and SortOrder (the workspace's order).
//   ✅ PipelineSummaryReportDto: what was won and lost in the period.
//   Nothing removed or renamed, so older callers keep working.
//
// CHANGES (112 — the priority reports)
//   ✅ Stalled Deals, Lead Funnel & Conversion, Quote Performance,
//      Invoice & Collections, Revenue Forecast (11–15 below).
//   ✅ ReportFilterDto: Days (stalled after) and Months (forecast ahead).
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

        // 112 — Stalled Deals: a deal is stalled after this many days (default 30).
        public int?      Days      { get; set; }
        // 112 — Revenue Forecast: how many months ahead (default 6).
        public int?      Months    { get; set; }
    }

    // ── 111: what every report carries ───────────────────────────────
    public class ReportBaseDto
    {
        /// <summary>The workspace's currency: every money figure is in it.</summary>
        public string       Currency               { get; set; } = string.Empty;
        /// <summary>Records in another currency, left OUT of the sums.</summary>
        public int          ExcludedOtherCurrency  { get; set; }
        /// <summary>Which other currencies those were ("THB", "USD").</summary>
        public List<string> ExcludedCurrencies     { get; set; } = new();
    }

    // ── 1. Sales Funnel ───────────────────────────────────────────────
    public class SalesFunnelReportDto : ReportBaseDto
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
    public class PipelineSummaryReportDto : ReportBaseDto
    {
        public List<PipelineStageItem> Stages { get; set; } = new();
        public decimal TotalPipelineValue     { get; set; }
        public int     TotalActiveDeals       { get; set; }
        public decimal AvgDealSize            { get; set; }

        // 111 — closed in the period (by the date the deal was closed)
        public int     WonCount               { get; set; }
        public decimal WonValue               { get; set; }
        public int     LostCount              { get; set; }
        public decimal WeightedPipelineValue  { get; set; }
    }

    public class PipelineStageItem
    {
        public string  Stage      { get; set; } = string.Empty;   // the key stored on the deal
        public string  StageName  { get; set; } = string.Empty;   // 111 — what people see
        public string  Category   { get; set; } = "Open";         // 111 — Open / Won / Lost
        public int     SortOrder  { get; set; }                   // 111 — the workspace's order
        public int     Count      { get; set; }
        public decimal TotalValue { get; set; }
        public int     AvgProbability { get; set; }
    }

    // ── 3. Revenue by Period ──────────────────────────────────────────
    public class RevenueByPeriodReportDto : ReportBaseDto
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
    public class WinLossReportDto : ReportBaseDto
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
    public class OutstandingInvoicesReportDto : ReportBaseDto
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
    public class RevenueByVerticalReportDto : ReportBaseDto
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
    public class SalesRepPerformanceReportDto : ReportBaseDto
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
    public class LeadSourceReportDto : ReportBaseDto
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
    public class DealVelocityReportDto : ReportBaseDto
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
    public class ActivityLeaderboardReportDto : ReportBaseDto
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
    // =================================================================
    // 112 — the priority reports
    // =================================================================

    // ── 11. Stalled Deals ─────────────────────────────────────────────
    public class StalledDealsReportDto : ReportBaseDto
    {
        public int     StalledDays       { get; set; }
        public int     OpenDeals         { get; set; }
        public int     StalledCount      { get; set; }
        public decimal StalledValue      { get; set; }
        public int     OverdueCloseCount { get; set; }
        public List<StalledDealItem>  Deals   { get; set; } = new();
        public List<StalledOwnerItem> ByOwner { get; set; } = new();
    }

    public class StalledDealItem
    {
        public Guid     DealId          { get; set; }
        public string   Title           { get; set; } = string.Empty;
        public string   OwnerName       { get; set; } = string.Empty;
        public string   Stage           { get; set; } = string.Empty;
        public string   StageName       { get; set; } = string.Empty;
        public decimal  Value           { get; set; }
        public int      DaysInStage     { get; set; }
        public int      DaysSinceTouch  { get; set; }
        public DateTime ExpectedCloseDateUtc { get; set; }
        public int      DaysPastClose   { get; set; }
        public List<string> Reasons     { get; set; } = new();
    }

    public class StalledOwnerItem
    {
        public string  OwnerName { get; set; } = string.Empty;
        public int     Count     { get; set; }
        public decimal Value     { get; set; }
    }

    // ── 12. Lead Funnel & Conversion ─────────────────────────────────
    public class LeadFunnelReportDto : ReportBaseDto
    {
        public int    TotalLeads        { get; set; }
        public int    QualifiedOrBetter { get; set; }
        public int    Converted         { get; set; }
        public int    Disqualified      { get; set; }
        public double QualifiedRate     { get; set; }
        public double ConversionRate    { get; set; }
        public double AvgDaysToConvert  { get; set; }
        public List<LeadStatusCountItem> ByStatus { get; set; } = new();
        public List<LeadMonthItem>       ByMonth  { get; set; } = new();
        public List<LeadOwnerItem>       ByOwner  { get; set; } = new();
    }

    public class LeadStatusCountItem
    {
        public string Key      { get; set; } = string.Empty;
        public string Name     { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;   // Open / Qualified / Disqualified / Converted
        public int    Count    { get; set; }
        public double Percent  { get; set; }
    }

    public class LeadMonthItem
    {
        public string Label          { get; set; } = string.Empty;
        public int    Created        { get; set; }
        public int    Converted      { get; set; }
        public double ConversionRate { get; set; }
    }

    public class LeadOwnerItem
    {
        public string OwnerName      { get; set; } = string.Empty;
        public int    Leads          { get; set; }
        public int    Qualified      { get; set; }
        public int    Converted      { get; set; }
        public double ConversionRate { get; set; }
    }

    // ── 13. Quote Performance ─────────────────────────────────────────
    public class QuotePerformanceReportDto : ReportBaseDto
    {
        public int     TotalQuotes     { get; set; }
        public int     SentQuotes      { get; set; }   // sent to the customer: Sent, Viewed, Accepted, Rejected, Expired
        public int     Accepted        { get; set; }
        public int     Rejected        { get; set; }
        public int     Expired         { get; set; }
        public int     Open            { get; set; }   // with the customer now: Sent, Viewed
        public int     NotSent         { get; set; }   // drafts, waiting for approval, approved
        public double  AcceptanceRate  { get; set; }   // Accepted ÷ SentQuotes
        public decimal QuotedValue     { get; set; }   // sent quotes
        public decimal AcceptedValue   { get; set; }
        public decimal OpenValue       { get; set; }
        public double  AvgDiscountPct  { get; set; }   // discount ÷ list value, sent quotes
        public int     ExpiringSoon    { get; set; }   // open, expires in the next 7 days
        public int     PastExpiry      { get; set; }   // open, expiry date already passed
        public List<QuoteStatusItem>    ByStatus       { get; set; } = new();
        public List<QuoteOwnerItem>     ByOwner        { get; set; } = new();
        public List<QuoteAttentionItem> NeedsAttention { get; set; } = new();
    }

    public class QuoteStatusItem
    {
        public string  Status { get; set; } = string.Empty;
        public int     Count  { get; set; }
        public decimal Value  { get; set; }
    }

    public class QuoteOwnerItem
    {
        public string  OwnerName      { get; set; } = string.Empty;
        public int     Sent           { get; set; }
        public int     Accepted       { get; set; }
        public double  AcceptanceRate { get; set; }
        public decimal AcceptedValue  { get; set; }
    }

    public class QuoteAttentionItem
    {
        public Guid     QuoteId      { get; set; }
        public string   Number       { get; set; } = string.Empty;
        public string   DealTitle    { get; set; } = string.Empty;
        public string   Status       { get; set; } = string.Empty;
        public decimal  Value        { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public string   Reason       { get; set; } = string.Empty;
    }

    // ── 14. Invoice & Collections ─────────────────────────────────────
    public class InvoiceCollectionsReportDto : ReportBaseDto
    {
        public int     InvoicesIssued   { get; set; }
        public decimal Invoiced         { get; set; }
        public decimal PaidOnThese      { get; set; }   // collected so far on the invoices issued in the period
        public double  CollectionRate   { get; set; }   // PaidOnThese ÷ Invoiced
        public decimal Collected        { get; set; }   // every payment that arrived in the period
        public int     PaidInvoices     { get; set; }   // issued in the period and paid in full
        public double  AvgDaysToPay     { get; set; }
        public double  PaidOnTimePct    { get; set; }
        public int     OverdueCount     { get; set; }   // today
        public decimal OverdueAmount    { get; set; }   // today
        public List<PaymentMethodItem>   ByMethod         { get; set; } = new();
        public List<OverdueCustomerItem> OverdueCustomers { get; set; } = new();
    }

    public class PaymentMethodItem
    {
        public string  Method { get; set; } = string.Empty;
        public int     Count  { get; set; }
        public decimal Amount { get; set; }
    }

    public class OverdueCustomerItem
    {
        public string  CustomerName      { get; set; } = string.Empty;
        public int     Invoices          { get; set; }
        public decimal Amount            { get; set; }
        public int     OldestDaysOverdue { get; set; }
    }

    // ── 15. Revenue Forecast ──────────────────────────────────────────
    public class RevenueForecastReportDto : ReportBaseDto
    {
        public int     Months        { get; set; }
        public int     OpenDeals     { get; set; }
        public decimal TotalValue    { get; set; }
        public decimal WeightedValue { get; set; }
        public decimal WonThisMonth  { get; set; }
        public List<ForecastBucketItem> Buckets { get; set; } = new();
        public List<ForecastOwnerItem>  ByOwner { get; set; } = new();
    }

    public class ForecastBucketItem
    {
        public string  Key      { get; set; } = string.Empty;   // "overdue", "2026-11", "later"
        public string  Label    { get; set; } = string.Empty;
        public int     Count    { get; set; }
        public decimal Value    { get; set; }
        public decimal Weighted { get; set; }
    }

    public class ForecastOwnerItem
    {
        public string  OwnerName { get; set; } = string.Empty;
        public int     Count     { get; set; }
        public decimal Value     { get; set; }
        public decimal Weighted  { get; set; }
    }
}
