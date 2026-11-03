// =====================================================================
// ReportQueries.cs
// Location: MerkaiTrial.Application/Commands/Reports/ReportQueries.cs
//
// CHANGES (111 — reports that add up)
//   Every report now starts from ReportScope (see that file), so they all
//   agree on the workspace, the period, the currency, the Vertical filter
//   and what each stage means.
//
//   ✅ Won / lost / open come from each stage's CATEGORY, never its name.
//      A workspace's own won stage ("Contract signed") counts as won.
//   ✅ A deal's close date is ActualCloseDateUtc (falls back to
//      UpdatedAtUtc for deals closed before it was recorded). Editing a
//      deal no longer moves its win to today.
//   ✅ Open pipeline is EVERY open deal, today — not only deals created in
//      the period. A deal opened 14 months ago and still being worked is
//      in the pipeline. Won and lost are the deals CLOSED in the period.
//   ✅ Revenue: invoiced by the invoice's issue month; COLLECTED by the
//      month the money arrived (captured payments), not the invoice's.
//   ✅ The Vertical filter applies to every report it can (all but the
//      activity leaderboard, which has no vertical).
//   ✅ Money is in the workspace's currency only. Records in any other
//      currency are left out of the sums and counted in the report's note
//      (ReportBaseDto) — never added to them.
//   ✅ Counting and summing happen in SQL (GROUP BY), on the workspace's
//      Guid — no "TenantId.ToString()" (a full-table scan), no whole tables
//      pulled into memory.
//   ✅ Deal velocity: lead → deal and lead → close are worked out (they
//      were hard-coded 0), and stages show their names.
//   ✅ Pipeline: stages in the workspace's own order, with their names.
//
// CHANGES (earlier, still true)
//   • 105/109: owner names match ids whatever their case, looked up by Guid.
//   • Every report respects record visibility (Own / Team / All).
//   • Drafts and void invoices are never revenue or outstanding.
// =====================================================================

using System.Globalization;
using MerkaiTrial.Application.Commands.Activities;
using MerkaiTrial.Application.Commands.LeadStatuses;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Reports
{
    // ==================== shared ====================

    internal static class ReportOwners
    {
        public const string Unassigned = "Unassigned";

        /// <summary>Owner id (any case) → "First Last".</summary>
        public static async Task<Dictionary<string, string>> NamesAsync(
            FlowDbContext db, IEnumerable<string?> ownerIds, CancellationToken ct)
        {
            var guids = ownerIds
                .Select(o => Guid.TryParse(o, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .Distinct().ToList();
            if (guids.Count == 0) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var rows = await db.Users.AsNoTracking()
                .Where(u => guids.Contains(u.Id))
                .Select(u => new { u.Id, u.FirstName, u.LastName })
                .ToListAsync(ct);

            return rows.ToDictionary(u => u.Id.ToString(), u => $"{u.FirstName} {u.LastName}".Trim(),
                StringComparer.OrdinalIgnoreCase);
        }

        public static string Name(Dictionary<string, string> names, string? ownerId) =>
            string.IsNullOrWhiteSpace(ownerId) ? Unassigned
            : names.TryGetValue(ownerId, out var n) && n.Length > 0 ? n : Unassigned;

        /// <summary>A grouping key that treats "abc" and "ABC" as the same owner.</summary>
        public static string Key(string? ownerId) =>
            string.IsNullOrWhiteSpace(ownerId) ? "" : ownerId.Trim().ToLowerInvariant();
    }

    internal static class Pct
    {
        public static double Of(double part, double whole) => whole == 0 ? 0 : Math.Round(part * 100.0 / whole, 1);
    }

    // ==================== 1. SALES FUNNEL ====================

    public class GetSalesFunnelHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetSalesFunnelHandler> _logger;
        private readonly ILeadStatusResolver _statuses;
        private readonly IRecordScopeService _scope;

        public GetSalesFunnelHandler(FlowDbContext db, ILogger<GetSalesFunnelHandler> logger,
            ILeadStatusResolver statuses, IRecordScopeService scope)
        {
            _db = db; _logger = logger; _statuses = statuses; _scope = scope;
        }

        public async Task<SalesFunnelReportDto> HandleAsync(ReportFilterDto filter, CancellationToken ct = default)
        {
            var s = await ReportScope.CreateAsync(_db, filter, 12, ct);
            var leadAccess = await _scope.GetAsync(RecordModules.Leads, ct);
            var dealAccess = await _scope.GetAsync(RecordModules.Deals, ct);
            DateTime from = s.From, to = s.To;

            // Leads created in the period, counted by status in SQL.
            var leadRows = await s.Leads(_db, leadAccess)
                .Where(l => l.CreatedAtUtc >= from && l.CreatedAtUtc <= to)
                .GroupBy(l => new { l.Status, l.IsConverted })
                .Select(g => new { g.Key.Status, g.Key.IsConverted, Count = g.Count() })
                .ToListAsync(ct);

            var statuses = await _statuses.GetAsync(s.TenantId, ct);
            var qualifiedOrBetter = statuses.All
                .Where(x => x.Category is LeadStatusCategory.Qualified or LeadStatusCategory.Converted)
                .Select(x => x.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var totalLeads = leadRows.Sum(r => r.Count);
            var qualifiedLeads = leadRows.Where(r => qualifiedOrBetter.Contains(r.Status ?? "") || r.IsConverted).Sum(r => r.Count);
            var convertedLeads = leadRows.Where(r => r.IsConverted).Sum(r => r.Count);

            var deals = s.Deals(_db, dealAccess).Where(d => d.CreatedAtUtc >= from && d.CreatedAtUtc <= to);
            var quotes = s.Quotes(_db, dealAccess).Where(q => q.CreatedAtUtc >= from && q.CreatedAtUtc <= to);
            var invoices = s.Invoices(_db, dealAccess)
                .Where(i => i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Cancelled &&
                            i.IssueDateUtc >= from && i.IssueDateUtc <= to);

            var totalDeals = await deals.CountAsync(ct);
            var dealsWithQuotes = await deals.CountAsync(d => quotes.Any(q => q.DealId == d.Id), ct);
            var totalQuotes = await quotes.CountAsync(ct);
            var acceptedQuotes = await quotes.CountAsync(q => q.Status == QuoteStatus.Accepted, ct);
            var totalInvoices = await invoices.CountAsync(ct);
            var paidInvoices = await invoices.CountAsync(i => i.Status == InvoiceStatus.Paid, ct);

            var dto = new SalesFunnelReportDto
            {
                TotalLeads = totalLeads,
                QualifiedLeads = qualifiedLeads,
                ConvertedLeads = convertedLeads,
                TotalDeals = totalDeals,
                DealsWithQuotes = dealsWithQuotes,
                TotalQuotes = totalQuotes,
                AcceptedQuotes = acceptedQuotes,
                TotalInvoices = totalInvoices,
                PaidInvoices = paidInvoices,
                LeadToQualifiedPct = Pct.Of(qualifiedLeads, totalLeads),
                LeadToConvertedPct = Pct.Of(convertedLeads, totalLeads),
                DealToQuotePct = Pct.Of(dealsWithQuotes, totalDeals),
                QuoteToAcceptedPct = Pct.Of(acceptedQuotes, totalQuotes),
                InvoiceToPaidPct = Pct.Of(paidInvoices, totalInvoices),
            };
            s.Note(dto, Array.Empty<(string?, int)>());   // counts only — no money here
            return dto;
        }
    }

    // ==================== 2. PIPELINE SUMMARY ====================

    public class GetPipelineSummaryHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetPipelineSummaryHandler> _logger;
        private readonly IRecordScopeService _scope;

        public GetPipelineSummaryHandler(FlowDbContext db, ILogger<GetPipelineSummaryHandler> logger, IRecordScopeService scope)
        {
            _db = db; _logger = logger; _scope = scope;
        }

        public async Task<PipelineSummaryReportDto> HandleAsync(ReportFilterDto filter, CancellationToken ct = default)
        {
            var s = await ReportScope.CreateAsync(_db, filter, 12, ct);
            var dealAccess = await _scope.GetAsync(RecordModules.Deals, ct);
            DateTime from = s.From, to = s.To;
            var closed = s.ClosedKeys;

            // Open: every open deal, today. Closed: closed in the period.
            var rows = await s.Deals(_db, dealAccess)
                .Where(d => !closed.Contains(d.Stage) ||
                            ((d.ActualCloseDateUtc ?? d.UpdatedAtUtc) >= from &&
                             (d.ActualCloseDateUtc ?? d.UpdatedAtUtc) <= to))
                .GroupBy(d => new { d.Stage, d.Currency })
                .Select(g => new
                {
                    g.Key.Stage,
                    g.Key.Currency,
                    Count = g.Count(),
                    Value = g.Sum(d => d.ExpectedValue),
                    Probability = g.Sum(d => d.Probability),
                    Weighted = g.Sum(d => d.ExpectedValue * d.Probability / 100m),
                })
                .ToListAsync(ct);

            var dto = new PipelineSummaryReportDto();
            s.Note(dto, rows.Select(r => ((string?)r.Currency, r.Count)));

            var mine = rows.Where(r => s.InCurrency(r.Currency)).ToList();
            var byStage = mine.GroupBy(r => r.Stage ?? "", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (Count: g.Sum(x => x.Count), Value: g.Sum(x => x.Value),
                                                Prob: g.Sum(x => x.Probability), Weighted: g.Sum(x => x.Weighted)),
                              StringComparer.OrdinalIgnoreCase);

            // Every stage the workspace has, in its order, plus any key a deal
            // still carries (old stages with no row, shown only when used).
            var keys = s.Stages.Values.Where(x => x.SortOrder < 900).Select(x => x.Key)
                .Concat(byStage.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            dto.Stages = keys
                .Select(k =>
                {
                    byStage.TryGetValue(k, out var t);
                    return new PipelineStageItem
                    {
                        Stage = k,
                        StageName = s.NameOf(k),
                        Category = s.CategoryOf(k).ToString(),
                        SortOrder = s.OrderOf(k),
                        Count = t.Count,
                        TotalValue = t.Value,
                        AvgProbability = t.Count == 0 ? 0 : (int)Math.Round((double)t.Prob / t.Count),
                    };
                })
                .OrderBy(x => x.SortOrder).ThenBy(x => x.StageName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var open = dto.Stages.Where(x => x.Category == nameof(StageCategory.Open)).ToList();
            dto.TotalActiveDeals = open.Sum(x => x.Count);
            dto.TotalPipelineValue = open.Sum(x => x.TotalValue);
            dto.AvgDealSize = dto.TotalActiveDeals == 0 ? 0 : Math.Round(dto.TotalPipelineValue / dto.TotalActiveDeals, 2);
            dto.WeightedPipelineValue = Math.Round(byStage
                .Where(kv => s.CategoryOf(kv.Key) == StageCategory.Open)
                .Sum(kv => kv.Value.Weighted), 2);

            var won = dto.Stages.Where(x => x.Category == nameof(StageCategory.Won)).ToList();
            dto.WonCount = won.Sum(x => x.Count);
            dto.WonValue = won.Sum(x => x.TotalValue);
            dto.LostCount = dto.Stages.Where(x => x.Category == nameof(StageCategory.Lost)).Sum(x => x.Count);
            return dto;
        }
    }

    // ==================== 3. REVENUE BY PERIOD ====================

    public class GetRevenueByPeriodHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetRevenueByPeriodHandler> _logger;
        private readonly IRecordScopeService _scope;

        public GetRevenueByPeriodHandler(FlowDbContext db, ILogger<GetRevenueByPeriodHandler> logger, IRecordScopeService scope)
        {
            _db = db; _logger = logger; _scope = scope;
        }

        public async Task<RevenueByPeriodReportDto> HandleAsync(ReportFilterDto filter, CancellationToken ct = default)
        {
            var s = await ReportScope.CreateAsync(_db, filter, 11, ct);
            var dealAccess = await _scope.GetAsync(RecordModules.Deals, ct);
            DateTime from = s.From, to = s.To;
            var tid = s.TenantId;

            // Invoiced: by the month the invoice was issued.
            var invoiced = await s.Invoices(_db, dealAccess)
                .Where(i => i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Cancelled &&
                            i.IssueDateUtc >= from && i.IssueDateUtc <= to)
                .GroupBy(i => new { i.IssueDateUtc.Year, i.IssueDateUtc.Month, i.Currency })
                .Select(g => new
                {
                    g.Key.Year, g.Key.Month, g.Key.Currency,
                    Count = g.Count(),
                    Total = g.Sum(i => i.Total),
                    Balance = g.Sum(i => i.Balance),
                })
                .ToListAsync(ct);

            // Collected: by the month the money arrived, on invoices the user can see.
            var visibleInvoiceIds = s.Invoices(_db, dealAccess).Select(i => i.Id);
            var collected = await _db.Payments.AsNoTracking()
                .Where(p => p.TenantId == tid && !p.IsDeleted &&
                            p.Status == PaymentStatusNames.Captured &&
                            p.PaidAtUtc >= from && p.PaidAtUtc <= to &&
                            visibleInvoiceIds.Contains(p.InvoiceId))
                .GroupBy(p => new { p.PaidAtUtc.Year, p.PaidAtUtc.Month, p.Currency })
                .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Currency, Count = g.Count(), Amount = g.Sum(p => p.Amount) })
                .ToListAsync(ct);

            var dto = new RevenueByPeriodReportDto();
            s.Note(dto, invoiced.Select(r => ((string?)r.Currency, r.Count))
                .Concat(collected.Select(r => ((string?)r.Currency, r.Count))));

            var inv = invoiced.Where(r => s.InCurrency(r.Currency)).ToList();
            var col = collected.Where(r => s.InCurrency(r.Currency)).ToList();

            // Every month in the period, empty ones too.
            var periods = new List<RevenuePeriodItem>();
            for (var m = new DateTime(from.Year, from.Month, 1); m <= to; m = m.AddMonths(1))
            {
                periods.Add(new RevenuePeriodItem
                {
                    Label = m.ToString("MMM yyyy", CultureInfo.InvariantCulture),
                    Invoiced = inv.Where(r => r.Year == m.Year && r.Month == m.Month).Sum(r => r.Total),
                    Collected = col.Where(r => r.Year == m.Year && r.Month == m.Month).Sum(r => r.Amount),
                });
            }

            dto.Periods = periods;
            dto.TotalInvoiced = inv.Sum(r => r.Total);
            dto.TotalCollected = col.Sum(r => r.Amount);
            dto.TotalOutstanding = inv.Sum(r => r.Balance);
            return dto;
        }
    }

    // ==================== 4. WIN/LOSS ANALYSIS ====================

    public class GetWinLossHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetWinLossHandler> _logger;
        private readonly IRecordScopeService _scope;

        public GetWinLossHandler(FlowDbContext db, ILogger<GetWinLossHandler> logger, IRecordScopeService scope)
        {
            _db = db; _logger = logger; _scope = scope;
        }

        public async Task<WinLossReportDto> HandleAsync(ReportFilterDto filter, CancellationToken ct = default)
        {
            var s = await ReportScope.CreateAsync(_db, filter, 12, ct);
            var dealAccess = await _scope.GetAsync(RecordModules.Deals, ct);
            DateTime from = s.From, to = s.To;
            var closed = s.ClosedKeys;

            // Deals CLOSED in the period, by owner and stage.
            var rows = await s.Deals(_db, dealAccess)
                .Where(d => closed.Contains(d.Stage) &&
                            (d.ActualCloseDateUtc ?? d.UpdatedAtUtc) >= from &&
                            (d.ActualCloseDateUtc ?? d.UpdatedAtUtc) <= to)
                .GroupBy(d => new { d.OwnerUserId, d.Stage, d.Currency })
                .Select(g => new { g.Key.OwnerUserId, g.Key.Stage, g.Key.Currency, Count = g.Count(), Value = g.Sum(d => d.ExpectedValue) })
                .ToListAsync(ct);

            var dto = new WinLossReportDto();
            s.Note(dto, rows.Select(r => ((string?)r.Currency, r.Count)));
            rows = rows.Where(r => s.InCurrency(r.Currency)).ToList();   // other currencies: left out, counted in the note
            var names = await ReportOwners.NamesAsync(_db, rows.Select(r => r.OwnerUserId), ct);

            bool Won(string? k) => s.CategoryOf(k) == StageCategory.Won;

            dto.ByOwner = rows
                .GroupBy(r => ReportOwners.Key(r.OwnerUserId))
                .Select(g =>
                {
                    var won = g.Where(r => Won(r.Stage)).Sum(r => r.Count);
                    var lost = g.Where(r => !Won(r.Stage)).Sum(r => r.Count);
                    return new WinLossOwnerItem
                    {
                        OwnerName = ReportOwners.Name(names, g.First().OwnerUserId),
                        Won = won,
                        Lost = lost,
                        WinRate = Pct.Of(won, won + lost),
                        WonValue = g.Where(r => Won(r.Stage)).Sum(r => r.Value),
                    };
                })
                .OrderByDescending(o => o.Won).ThenBy(o => o.OwnerName)
                .ToList();

            dto.TotalWon = dto.ByOwner.Sum(o => o.Won);
            dto.TotalLost = dto.ByOwner.Sum(o => o.Lost);
            dto.OverallWinRate = Pct.Of(dto.TotalWon, dto.TotalWon + dto.TotalLost);
            dto.TotalWonValue = dto.ByOwner.Sum(o => o.WonValue);
            return dto;
        }
    }

    // ==================== 5. OUTSTANDING INVOICES (AGING) ====================

    public class GetOutstandingInvoicesHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetOutstandingInvoicesHandler> _logger;
        private readonly IRecordScopeService _scope;

        public GetOutstandingInvoicesHandler(FlowDbContext db, ILogger<GetOutstandingInvoicesHandler> logger, IRecordScopeService scope)
        {
            _db = db; _logger = logger; _scope = scope;
        }

        public async Task<OutstandingInvoicesReportDto> HandleAsync(ReportFilterDto filter, CancellationToken ct = default)
        {
            var s = await ReportScope.CreateAsync(_db, filter, 12, ct);
            var dealAccess = await _scope.GetAsync(RecordModules.Deals, ct);
            var today = DateTime.UtcNow.Date;

            // Everything owed TODAY — the period doesn't apply to what is still owed.
            var rows = await s.Invoices(_db, dealAccess)
                .Where(i => i.Balance > 0 &&
                            i.Status != InvoiceStatus.Paid &&
                            i.Status != InvoiceStatus.Cancelled &&
                            i.Status != InvoiceStatus.Draft)
                .Select(i => new
                {
                    i.Number, i.Total, i.Balance, i.DueDateUtc, i.Status, i.Currency,
                    DealTitle = i.Deal != null ? i.Deal.Title : "",
                    ContactName = i.Deal != null && i.Deal.Contact != null
                        ? i.Deal.Contact.FirstName + " " + i.Deal.Contact.LastName
                        : "",
                })
                .ToListAsync(ct);

            var dto = new OutstandingInvoicesReportDto();
            s.Note(dto, rows.GroupBy(r => r.Currency).Select(g => ((string?)g.Key, g.Count())));

            var items = rows.Where(r => s.InCurrency(r.Currency)).Select(i =>
            {
                var daysOverdue = i.DueDateUtc.HasValue ? (int)(today - i.DueDateUtc.Value.Date).TotalDays : 0;
                return new OutstandingInvoiceItem
                {
                    Number = i.Number,
                    ContactName = (i.ContactName ?? "").Trim(),
                    DealTitle = i.DealTitle ?? "",
                    GrandTotal = i.Total,
                    Balance = i.Balance,
                    DueDateUtc = i.DueDateUtc,
                    DaysOverdue = Math.Max(0, daysOverdue),
                    Status = i.Status == InvoiceStatus.PartiallyPaid ? "PartiallyPaid" : "Unpaid",
                };
            }).OrderByDescending(i => i.DaysOverdue).ThenBy(i => i.Number).ToList();

            AgingBucketItem Bucket(string label, Func<OutstandingInvoiceItem, bool> pick) => new()
            {
                Label = label,
                Count = items.Count(pick),
                Amount = items.Where(pick).Sum(i => i.Balance),
            };

            dto.Buckets = new List<AgingBucketItem>
            {
                Bucket("Current (not due)", i => i.DaysOverdue == 0),
                Bucket("1–30 days", i => i.DaysOverdue is >= 1 and <= 30),
                Bucket("31–60 days", i => i.DaysOverdue is >= 31 and <= 60),
                Bucket("61–90 days", i => i.DaysOverdue is >= 61 and <= 90),
                Bucket("90+ days", i => i.DaysOverdue > 90),
            };
            dto.Invoices = items;
            dto.TotalOutstanding = items.Sum(i => i.Balance);
            dto.TotalCount = items.Count;
            return dto;
        }
    }

    // ==================== 6. REVENUE BY VERTICAL ====================

    public class GetRevenueByVerticalHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetRevenueByVerticalHandler> _logger;
        private readonly IRecordScopeService _scope;

        public GetRevenueByVerticalHandler(FlowDbContext db, ILogger<GetRevenueByVerticalHandler> logger, IRecordScopeService scope)
        {
            _db = db; _logger = logger; _scope = scope;
        }

        public async Task<RevenueByVerticalReportDto> HandleAsync(ReportFilterDto filter, CancellationToken ct = default)
        {
            var s = await ReportScope.CreateAsync(_db, filter, 12, ct);
            var dealAccess = await _scope.GetAsync(RecordModules.Deals, ct);
            DateTime from = s.From, to = s.To;
            var closed = s.ClosedKeys;

            // Open deals today + deals closed in the period, by vertical and stage.
            var deals = await s.Deals(_db, dealAccess)
                .Where(d => !closed.Contains(d.Stage) ||
                            ((d.ActualCloseDateUtc ?? d.UpdatedAtUtc) >= from &&
                             (d.ActualCloseDateUtc ?? d.UpdatedAtUtc) <= to))
                .GroupBy(d => new { d.VerticalId, d.Stage, d.Currency })
                .Select(g => new { g.Key.VerticalId, g.Key.Stage, g.Key.Currency, Count = g.Count(), Value = g.Sum(d => d.ExpectedValue) })
                .ToListAsync(ct);

            // Invoices issued in the period, by their deal's vertical.
            var issued = s.Invoices(_db, dealAccess)
                .Where(i => i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Cancelled &&
                            i.IssueDateUtc >= from && i.IssueDateUtc <= to && i.DealId != null);
            var invoices = await (
                    from i in issued
                    join d in _db.Deals on i.DealId equals (Guid?)d.Id
                    group new { i.Total, i.Balance } by new { d.VerticalId, i.Currency } into g
                    select new
                    {
                        g.Key.VerticalId, g.Key.Currency,
                        Count = g.Count(),
                        Total = g.Sum(x => x.Total),
                        Paid = g.Sum(x => x.Total - x.Balance),
                    })
                .ToListAsync(ct);

            var dto = new RevenueByVerticalReportDto();
            s.Note(dto, deals.Select(r => ((string?)r.Currency, r.Count))
                .Concat(invoices.Select(r => ((string?)r.Currency, r.Count))));
            deals = deals.Where(d => s.InCurrency(d.Currency)).ToList();
            invoices = invoices.Where(i => s.InCurrency(i.Currency)).ToList();

            var ids = deals.Select(d => d.VerticalId).Concat(invoices.Select(i => i.VerticalId))
                .Where(v => v.HasValue).Select(v => v!.Value).Distinct().ToList();
            var verticalNames = await _db.CompanyVerticals.AsNoTracking()
                .Where(v => ids.Contains(v.Id))
                .Select(v => new { v.Id, v.Name })
                .ToDictionaryAsync(v => v.Id, v => v.Name, ct);

            string NameOf(Guid? id) => id is null ? "Not assigned"
                : verticalNames.TryGetValue(id.Value, out var n) ? n : "Unknown";

            var groups = deals.Select(d => d.VerticalId).Concat(invoices.Select(i => i.VerticalId)).Distinct().ToList();
            dto.Verticals = groups.Select(v =>
                {
                    var ds = deals.Where(d => d.VerticalId == v).ToList();
                    var mine = ds;
                    var inv = invoices.Where(i => i.VerticalId == v).ToList();
                    var won = ds.Where(d => s.CategoryOf(d.Stage) == StageCategory.Won).Sum(d => d.Count);
                    var lost = ds.Where(d => s.CategoryOf(d.Stage) == StageCategory.Lost).Sum(d => d.Count);
                    return new VerticalRevenueItem
                    {
                        VerticalName = NameOf(v),
                        DealCount = ds.Sum(d => d.Count),
                        WonDeals = won,
                        PipelineValue = mine.Where(d => s.CategoryOf(d.Stage) == StageCategory.Open).Sum(d => d.Value),
                        WonValue = mine.Where(d => s.CategoryOf(d.Stage) == StageCategory.Won).Sum(d => d.Value),
                        InvoicedValue = inv.Sum(i => i.Total),
                        CollectedValue = inv.Sum(i => i.Paid),
                        WinRate = Pct.Of(won, won + lost),
                    };
                })
                .OrderByDescending(v => v.WonValue + v.PipelineValue).ThenBy(v => v.VerticalName)
                .ToList();

            dto.TotalRevenue = dto.Verticals.Sum(v => v.WonValue);
            dto.TotalInvoiced = dto.Verticals.Sum(v => v.InvoicedValue);
            dto.TotalDeals = dto.Verticals.Sum(v => v.DealCount);
            return dto;
        }
    }

    // ==================== 7. SALES REP PERFORMANCE ====================

    public class GetSalesRepPerformanceHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetSalesRepPerformanceHandler> _logger;
        private readonly IRecordScopeService _scope;

        public GetSalesRepPerformanceHandler(FlowDbContext db, ILogger<GetSalesRepPerformanceHandler> logger, IRecordScopeService scope)
        {
            _db = db; _logger = logger; _scope = scope;
        }

        public async Task<SalesRepPerformanceReportDto> HandleAsync(ReportFilterDto filter, CancellationToken ct = default)
        {
            var s = await ReportScope.CreateAsync(_db, filter, 12, ct);
            var leadAccess = await _scope.GetAsync(RecordModules.Leads, ct);
            var dealAccess = await _scope.GetAsync(RecordModules.Deals, ct);
            DateTime from = s.From, to = s.To;
            var closed = s.ClosedKeys;

            // Each rep's open deals today, and deals closed in the period.
            var deals = await s.Deals(_db, dealAccess)
                .Where(d => !closed.Contains(d.Stage) ||
                            ((d.ActualCloseDateUtc ?? d.UpdatedAtUtc) >= from &&
                             (d.ActualCloseDateUtc ?? d.UpdatedAtUtc) <= to))
                .GroupBy(d => new { d.OwnerUserId, d.Stage, d.Currency })
                .Select(g => new { g.Key.OwnerUserId, g.Key.Stage, g.Key.Currency, Count = g.Count(), Value = g.Sum(d => d.ExpectedValue) })
                .ToListAsync(ct);

            // Leads each rep was given in the period, and how many converted.
            var leads = await s.Leads(_db, leadAccess)
                .Where(l => l.CreatedAtUtc >= from && l.CreatedAtUtc <= to)
                .GroupBy(l => new { l.OwnerUserId, l.IsConverted })
                .Select(g => new { g.Key.OwnerUserId, g.Key.IsConverted, Count = g.Count() })
                .ToListAsync(ct);

            var dto = new SalesRepPerformanceReportDto();
            s.Note(dto, deals.Select(r => ((string?)r.Currency, r.Count)));
            deals = deals.Where(d => s.InCurrency(d.Currency)).ToList();   // other currencies: left out, counted in the note
            var names = await ReportOwners.NamesAsync(_db,
                deals.Select(d => d.OwnerUserId).Concat(leads.Select(l => l.OwnerUserId)), ct);

            var owners = deals.Select(d => ReportOwners.Key(d.OwnerUserId))
                .Concat(leads.Select(l => ReportOwners.Key(l.OwnerUserId)))
                .Distinct().ToList();

            dto.Reps = owners.Select(o =>
                {
                    var ds = deals.Where(d => ReportOwners.Key(d.OwnerUserId) == o).ToList();
                    var ls = leads.Where(l => ReportOwners.Key(l.OwnerUserId) == o).ToList();
                    var anyId = ds.Select(d => d.OwnerUserId).Concat(ls.Select(l => l.OwnerUserId)).FirstOrDefault();
                    int Count(StageCategory c) => ds.Where(d => s.CategoryOf(d.Stage) == c).Sum(d => d.Count);
                    decimal Value(StageCategory c) => ds.Where(d => s.CategoryOf(d.Stage) == c).Sum(d => d.Value);
                    var won = Count(StageCategory.Won);
                    var lost = Count(StageCategory.Lost);
                    var active = Count(StageCategory.Open);
                    return new SalesRepItem
                    {
                        OwnerName = ReportOwners.Name(names, anyId),
                        TotalDeals = won + lost + active,
                        Won = won,
                        Lost = lost,
                        Active = active,
                        WinRate = Pct.Of(won, won + lost),
                        WonValue = Value(StageCategory.Won),
                        PipelineValue = Value(StageCategory.Open),
                        LeadsOwned = ls.Sum(l => l.Count),
                        LeadsConverted = ls.Where(l => l.IsConverted).Sum(l => l.Count),
                    };
                })
                .OrderByDescending(r => r.WonValue).ThenByDescending(r => r.PipelineValue).ThenBy(r => r.OwnerName)
                .ToList();

            dto.TotalDeals = dto.Reps.Sum(r => r.TotalDeals);
            dto.TotalWon = dto.Reps.Sum(r => r.Won);
            dto.TotalWonValue = dto.Reps.Sum(r => r.WonValue);
            dto.OverallWinRate = Pct.Of(dto.TotalWon, dto.TotalWon + dto.Reps.Sum(r => r.Lost));
            return dto;
        }
    }

    // ==================== 8. LEAD SOURCE ANALYSIS ====================

    public class GetLeadSourceHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetLeadSourceHandler> _logger;
        private readonly ILeadStatusResolver _statuses;
        private readonly IRecordScopeService _scope;

        public GetLeadSourceHandler(FlowDbContext db, ILogger<GetLeadSourceHandler> logger,
            ILeadStatusResolver statuses, IRecordScopeService scope)
        {
            _db = db; _logger = logger; _statuses = statuses; _scope = scope;
        }

        public async Task<LeadSourceReportDto> HandleAsync(ReportFilterDto filter, CancellationToken ct = default)
        {
            var s = await ReportScope.CreateAsync(_db, filter, 12, ct);
            var leadAccess = await _scope.GetAsync(RecordModules.Leads, ct);
            DateTime from = s.From, to = s.To;

            var rows = await s.Leads(_db, leadAccess)
                .Where(l => l.CreatedAtUtc >= from && l.CreatedAtUtc <= to)
                .GroupBy(l => new { l.ChannelId, l.SourceId, l.Status, l.IsConverted, l.Currency })
                .Select(g => new
                {
                    g.Key.ChannelId, g.Key.SourceId, g.Key.Status, g.Key.IsConverted, g.Key.Currency,
                    Count = g.Count(),
                    Value = g.Sum(l => l.EstimatedValue ?? 0),
                })
                .ToListAsync(ct);

            var dto = new LeadSourceReportDto();
            s.Note(dto, rows.Select(r => ((string?)r.Currency, r.Count)));
            rows = rows.Where(r => s.InCurrency(r.Currency)).ToList();   // other currencies: left out, counted in the note

            var channelIds = rows.Where(r => r.ChannelId.HasValue).Select(r => r.ChannelId!.Value).Distinct().ToList();
            var channelMap = await _db.LeadChannels.AsNoTracking()
                .Where(c => channelIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Name })
                .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

            var sourceIds = rows.Where(r => r.SourceId.HasValue).Select(r => r.SourceId!.Value).Distinct().ToList();
            var sourceMap = await _db.LeadSources.AsNoTracking()
                .Where(x => sourceIds.Contains(x.Id))
                .Select(x => new { x.Id, x.Name })
                .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

            var statuses = await _statuses.GetAsync(s.TenantId, ct);
            var qualifiedOrBetter = statuses.All
                .Where(x => x.Category is LeadStatusCategory.Qualified or LeadStatusCategory.Converted)
                .Select(x => x.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            LeadSourceItem Item(string name, IEnumerable<int> idx)
            {
                var g = idx.Select(i => rows[i]).ToList();
                var total = g.Sum(r => r.Count);
                var conv = g.Where(r => r.IsConverted).Sum(r => r.Count);
                return new LeadSourceItem
                {
                    Name = name,
                    TotalLeads = total,
                    Qualified = g.Where(r => r.IsConverted || qualifiedOrBetter.Contains(r.Status ?? "")).Sum(r => r.Count),
                    Converted = conv,
                    ConversionRate = Pct.Of(conv, total),
                    EstimatedValue = g.Sum(r => r.Value),
                };
            }

            var index = Enumerable.Range(0, rows.Count).ToList();

            dto.ByChannel = index
                .GroupBy(i => rows[i].ChannelId.HasValue
                    ? (channelMap.TryGetValue(rows[i].ChannelId!.Value, out var n) ? n : "Unknown")
                    : "Not set")
                .Select(g => Item(g.Key, g))
                .OrderByDescending(x => x.TotalLeads).ThenBy(x => x.Name)
                .ToList();

            dto.BySource = index
                .GroupBy(i => rows[i].SourceId.HasValue
                    ? (sourceMap.TryGetValue(rows[i].SourceId!.Value, out var n) ? n : "Unknown")
                    : "Not set")
                .Select(g => Item(g.Key, g))
                .OrderByDescending(x => x.TotalLeads).ThenBy(x => x.Name)
                .ToList();

            dto.TotalLeads = rows.Sum(r => r.Count);
            dto.TotalConverted = rows.Where(r => r.IsConverted).Sum(r => r.Count);
            dto.OverallConversionRate = Pct.Of(dto.TotalConverted, dto.TotalLeads);
            return dto;
        }
    }

    // ==================== 9. DEAL VELOCITY ====================

    public class GetDealVelocityHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetDealVelocityHandler> _logger;
        private readonly IRecordScopeService _scope;

        public GetDealVelocityHandler(FlowDbContext db, ILogger<GetDealVelocityHandler> logger, IRecordScopeService scope)
        {
            _db = db; _logger = logger; _scope = scope;
        }

        public async Task<DealVelocityReportDto> HandleAsync(ReportFilterDto filter, CancellationToken ct = default)
        {
            var s = await ReportScope.CreateAsync(_db, filter, 12, ct);
            var dealAccess = await _scope.GetAsync(RecordModules.Deals, ct);
            DateTime from = s.From, to = s.To;
            var closed = s.ClosedKeys;

            // Deals CLOSED in the period.
            var closedQ = s.Deals(_db, dealAccess)
                .Where(d => closed.Contains(d.Stage) &&
                            (d.ActualCloseDateUtc ?? d.UpdatedAtUtc) >= from &&
                            (d.ActualCloseDateUtc ?? d.UpdatedAtUtc) <= to);

            var deals = await closedQ
                .Select(d => new
                {
                    d.Id,
                    d.CreatedAtUtc,
                    ClosedAtUtc = d.ActualCloseDateUtc ?? d.UpdatedAtUtc,
                    LeadCreatedAtUtc = d.Lead != null ? (DateTime?)d.Lead.CreatedAtUtc : null,
                })
                .ToListAsync(ct);

            var closedIds = closedQ.Select(d => d.Id);
            var history = await _db.DealStageHistory.AsNoTracking()
                .Where(h => closedIds.Contains(h.DealId))
                // 111-update: FromStage is NULL on a deal's first history row (the
                // column allows it, the entity says string) — read as "".
                .Select(h => new { h.DealId, FromStage = h.FromStage ?? "", ToStage = h.ToStage ?? "", h.ChangedAtUtc })
                .ToListAsync(ct);

            // Time spent between one stage change and the next, per deal.
            var transitions = new List<(string From, string To, double Days)>();
            foreach (var g in history.GroupBy(h => h.DealId))
            {
                var steps = g.OrderBy(h => h.ChangedAtUtc).ToList();
                for (var i = 1; i < steps.Count; i++)
                {
                    var days = (steps[i].ChangedAtUtc - steps[i - 1].ChangedAtUtc).TotalDays;
                    if (days < 0) continue;
                    var fromKey = string.IsNullOrEmpty(steps[i].FromStage) ? steps[i - 1].ToStage : steps[i].FromStage;
                    transitions.Add((s.NameOf(fromKey), s.NameOf(steps[i].ToStage), days));
                }
            }

            static double Avg(IEnumerable<double> xs)
            {
                var l = xs.ToList();
                return l.Count == 0 ? 0 : Math.Round(l.Average(), 1);
            }

            var dto = new DealVelocityReportDto
            {
                Stages = transitions
                    .GroupBy(t => new { t.From, t.To })
                    .Select(g => new StageVelocityItem
                    {
                        FromStage = g.Key.From,
                        ToStage = g.Key.To,
                        AvgDays = Math.Round(g.Average(t => t.Days), 1),
                        MinDays = Math.Round(g.Min(t => t.Days), 1),
                        MaxDays = Math.Round(g.Max(t => t.Days), 1),
                        Transitions = g.Count(),
                    })
                    .OrderBy(x => x.AvgDays)
                    .ToList(),
                AvgDaysDealToClose = Avg(deals.Select(d => (d.ClosedAtUtc - d.CreatedAtUtc).TotalDays).Where(x => x >= 0)),
                AvgDaysLeadToDeal = Avg(deals.Where(d => d.LeadCreatedAtUtc.HasValue)
                    .Select(d => (d.CreatedAtUtc - d.LeadCreatedAtUtc!.Value).TotalDays).Where(x => x >= 0)),
                AvgDaysLeadToClose = Avg(deals.Where(d => d.LeadCreatedAtUtc.HasValue)
                    .Select(d => (d.ClosedAtUtc - d.LeadCreatedAtUtc!.Value).TotalDays).Where(x => x >= 0)),
                DealsAnalyzed = deals.Count,
            };
            s.Note(dto, Array.Empty<(string?, int)>());   // days, not money
            return dto;
        }
    }

    // ==================== 10. ACTIVITY LEADERBOARD ====================

    public class GetActivityLeaderboardHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetActivityLeaderboardHandler> _logger;
        private readonly IRecordScopeService _scope;

        public GetActivityLeaderboardHandler(FlowDbContext db, ILogger<GetActivityLeaderboardHandler> logger, IRecordScopeService scope)
        {
            _db = db; _logger = logger; _scope = scope;
        }

        public async Task<ActivityLeaderboardReportDto> HandleAsync(ReportFilterDto filter, CancellationToken ct = default)
        {
            if (!Guid.TryParse(filter.TenantId, out _))
                return new ActivityLeaderboardReportDto();

            var s = await ReportScope.CreateAsync(_db, filter, 12, ct);
            var tenantId = s.TenantId;
            DateTime from = s.From, to = s.To;

            // Count only work logged on leads and deals the user can see.
            // Activities on other record types (contacts, companies…) are not
            // owner-scoped yet, so they count as before.
            var leadAccess = await _scope.GetAsync(RecordModules.Leads, ct);
            var dealAccess = await _scope.GetAsync(RecordModules.Deals, ct);

            var visibleLeadIds = _db.Leads.VisibleTo(leadAccess).Select(l => l.Id);
            var visibleDealIds = _db.Deals.VisibleTo(dealAccess).Select(d => d.Id);
            var leadsAll = leadAccess.SeesAll;
            var dealsAll = dealAccess.SeesAll;
            var leadType = ActivityEntityType.Lead;
            var dealType = ActivityEntityType.Deal;

            var dealNotes = await _db.DealNotes
                .Where(n => n.TenantId == tenantId && !n.IsDeleted &&
                            n.CreatedAtUtc >= from && n.CreatedAtUtc <= to &&
                            (dealsAll || visibleDealIds.Contains(n.DealId)))
                .GroupBy(n => n.CreatedBy)
                .Select(g => new { Who = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var leadNotes = await _db.LeadNotes
                .Where(n => n.TenantId == tenantId && !n.IsDeleted &&
                            n.CreatedAtUtc >= from && n.CreatedAtUtc <= to &&
                            (leadsAll || visibleLeadIds.Contains(n.LeadId)))
                .GroupBy(n => n.CreatedBy)
                .Select(g => new { Who = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var activityRows = await _db.Activities
                .Where(a => a.TenantId == tenantId && !a.IsDeleted &&
                            a.CreatedAtUtc >= from && a.CreatedAtUtc <= to &&
                            (a.EntityType == leadType ? (leadsAll || visibleLeadIds.Contains(a.EntityId))
                           : a.EntityType == dealType ? (dealsAll || visibleDealIds.Contains(a.EntityId))
                           : true))
                .GroupBy(a => new { a.IsTask, a.CreatedBy })
                .Select(g => new { g.Key.IsTask, Who = g.Key.CreatedBy, Count = g.Count() })
                .ToListAsync(ct);

            // Ids → names, so "dd100001-…" and "Somchai Wiriya" become one rep.
            var names = await ActivityReadModel.UserNamesAsync(_db, tenantId,
                dealNotes.Select(x => x.Who).Concat(leadNotes.Select(x => x.Who)).Concat(activityRows.Select(r => r.Who)), ct);

            string? Who(string? stored) =>
                string.IsNullOrWhiteSpace(stored) ? null : ActivityReadModel.Lookup(names, stored) ?? stored;

            var tally = new Dictionary<string, ActivityRepItem>(StringComparer.Ordinal);
            void Add(string? stored, int count, Action<ActivityRepItem, int> into)
            {
                var who = Who(stored);
                if (string.IsNullOrEmpty(who)) return;
                if (!tally.TryGetValue(who, out var item)) tally[who] = item = new ActivityRepItem { OwnerName = who };
                into(item, count);
            }

            foreach (var n in dealNotes.Concat(leadNotes)) Add(n.Who, n.Count, (i, c) => i.Notes += c);
            foreach (var a in activityRows.Where(r => !r.IsTask)) Add(a.Who, a.Count, (i, c) => i.Activities += c);
            foreach (var a in activityRows.Where(r => r.IsTask)) Add(a.Who, a.Count, (i, c) => i.Reminders += c);
            foreach (var i in tally.Values) i.Total = i.Notes + i.Activities + i.Reminders;

            var dto = new ActivityLeaderboardReportDto
            {
                Reps = tally.Values.OrderByDescending(r => r.Total).ThenBy(r => r.OwnerName).ToList(),
                TotalNotes = dealNotes.Concat(leadNotes).Where(n => Who(n.Who) != null).Sum(n => n.Count),
                TotalActivities = activityRows.Where(r => !r.IsTask && Who(r.Who) != null).Sum(r => r.Count),
                TotalReminders = activityRows.Where(r => r.IsTask && Who(r.Who) != null).Sum(r => r.Count),
            };
            s.Note(dto, Array.Empty<(string?, int)>());
            return dto;
        }
    }
}
