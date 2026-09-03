// =====================================================================
// ReportQueries.cs
// Location: MerkaiTrial.Application/Commands/Reports/ReportQueries.cs
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Reports
{
    // ==================== 1. SALES FUNNEL ====================

    public class GetSalesFunnelHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetSalesFunnelHandler> _logger;

        public GetSalesFunnelHandler(FlowDbContext db, ILogger<GetSalesFunnelHandler> logger)
        {
            _db     = db;
            _logger = logger;
        }

        public async Task<SalesFunnelReportDto> HandleAsync(
            ReportFilterDto filter, CancellationToken ct = default)
        {
           
            var tenantId = filter.TenantId;
            var from     = filter.FromDate ?? DateTime.UtcNow.AddMonths(-12);
            var to       = (filter.ToDate ?? DateTime.UtcNow).Date.AddDays(1).AddTicks(-1);

            var leads = await _db.Leads
                .Where(l => l.TenantId.ToString() == tenantId &&
                            !l.IsDeleted &&
                            l.CreatedAtUtc >= from && l.CreatedAtUtc <= to &&
                            (filter.Vertical == null || l.Vertical.ToString() == filter.Vertical))
                .ToListAsync(ct);

            var deals = await _db.Deals
                .Where(d => d.TenantId.ToString() == tenantId &&
                            !d.IsDeleted &&
                            d.CreatedAtUtc >= from && d.CreatedAtUtc <= to)
                .ToListAsync(ct);

            var quotes = await _db.Quotes
                .Where(q => q.TenantId.ToString() == tenantId &&
                            !q.IsDeleted &&
                            q.CreatedAtUtc >= from && q.CreatedAtUtc <= to)
                .ToListAsync(ct);

            var invoices = await _db.Invoices
                .Where(i => i.TenantId.ToString() == tenantId &&
                            !i.IsDeleted &&
                            i.CreatedAtUtc >= from && i.CreatedAtUtc <= to)
                .ToListAsync(ct);

            var totalLeads     = leads.Count;
            var qualifiedLeads = leads.Count(l => l.Status.ToString() == "Qualified" ||
                                                   (int)l.Status >= 2);
            var convertedLeads = leads.Count(l => l.IsConverted);
            var totalDeals     = deals.Count;
            var dealsWithQuotes = deals.Count(d => quotes.Any(q => q.DealId == d.Id));
            var totalQuotes    = quotes.Count;
            var acceptedQuotes = quotes.Count(q => q.Status.ToString() == "Accepted");
            var totalInvoices  = invoices.Count;
            var paidInvoices   = invoices.Count(i => i.Status != InvoiceStatus.Paid); // 4 = Paid

            return new SalesFunnelReportDto
            {
                TotalLeads         = totalLeads,
                QualifiedLeads     = qualifiedLeads,
                ConvertedLeads     = convertedLeads,
                TotalDeals         = totalDeals,
                DealsWithQuotes    = dealsWithQuotes,
                TotalQuotes        = totalQuotes,
                AcceptedQuotes     = acceptedQuotes,
                TotalInvoices      = totalInvoices,
                PaidInvoices       = paidInvoices,
                LeadToQualifiedPct = totalLeads  == 0 ? 0 : Math.Round(qualifiedLeads  * 100.0 / totalLeads,  1),
                LeadToConvertedPct = totalLeads  == 0 ? 0 : Math.Round(convertedLeads  * 100.0 / totalLeads,  1),
                DealToQuotePct     = totalDeals  == 0 ? 0 : Math.Round(dealsWithQuotes * 100.0 / totalDeals,  1),
                QuoteToAcceptedPct = totalQuotes == 0 ? 0 : Math.Round(acceptedQuotes  * 100.0 / totalQuotes, 1),
                InvoiceToPaidPct   = totalInvoices == 0 ? 0 : Math.Round(paidInvoices  * 100.0 / totalInvoices, 1),
            };
        }
    }

    // ==================== 2. PIPELINE SUMMARY ====================

    public class GetPipelineSummaryHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetPipelineSummaryHandler> _logger;

        public GetPipelineSummaryHandler(FlowDbContext db, ILogger<GetPipelineSummaryHandler> logger)
        {
            _db     = db;
            _logger = logger;
        }

        public async Task<PipelineSummaryReportDto> HandleAsync(
            ReportFilterDto filter, CancellationToken ct = default)
        {
            var tenantId = filter.TenantId;
            var from     = filter.FromDate ?? DateTime.UtcNow.AddMonths(-12);
            var to       = (filter.ToDate ?? DateTime.UtcNow).Date.AddDays(1).AddTicks(-1);

            var stages = await _db.Deals
                .Where(d => d.TenantId.ToString() == tenantId &&
                            !d.IsDeleted &&
                            d.CreatedAtUtc >= from && d.CreatedAtUtc <= to &&
                            (filter.Vertical == null ||
                             _db.CompanyVerticals
                                .Where(v => v.Id == d.VerticalId)
                                .Select(v => v.Name)
                                .FirstOrDefault() == filter.Vertical))
                .GroupBy(d => d.Stage)
                .Select(g => new PipelineStageItem
                {
                    Stage          = g.Key,
                    Count          = g.Count(),
                    TotalValue     = g.Sum(d => d.ExpectedValue),
                    AvgProbability = (int)g.Average(d => d.Probability)
                })
                .ToListAsync(ct);

            // Enforce stage display order
            var order = new[] { "Discovery", "Qualification", "Proposal",
                                "Negotiation", "ClosedWon", "ClosedLost" };
            stages = stages
                .OrderBy(s => Array.IndexOf(order, s.Stage) < 0
                    ? 99 : Array.IndexOf(order, s.Stage))
                .ToList();

            var activeDeals = stages
                .Where(s => s.Stage != "ClosedWon" && s.Stage != "ClosedLost")
                .ToList();

            var totalValue  = activeDeals.Sum(s => s.TotalValue);
            var totalActive = activeDeals.Sum(s => s.Count);

            return new PipelineSummaryReportDto
            {
                Stages             = stages,
                TotalPipelineValue = totalValue,
                TotalActiveDeals   = totalActive,
                AvgDealSize        = totalActive == 0 ? 0 :
                                     Math.Round(totalValue / totalActive, 2)
            };
        }
    }

    // ==================== 3. REVENUE BY PERIOD ====================

    public class GetRevenueByPeriodHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetRevenueByPeriodHandler> _logger;

        public GetRevenueByPeriodHandler(FlowDbContext db, ILogger<GetRevenueByPeriodHandler> logger)
        {
            _db     = db;
            _logger = logger;
        }

        public async Task<RevenueByPeriodReportDto> HandleAsync(
            ReportFilterDto filter, CancellationToken ct = default)
        {
            var tenantId = filter.TenantId;
            var from     = filter.FromDate ?? DateTime.UtcNow.AddMonths(-11);
            var to       = (filter.ToDate ?? DateTime.UtcNow).Date.AddDays(1).AddTicks(-1);

            var invoices = await _db.Invoices
                .Where(i => i.TenantId.ToString() == tenantId &&
                            !i.IsDeleted &&
                            i.IssueDateUtc >= from && i.IssueDateUtc <= to)
                .Select(i => new
                {
                    i.IssueDateUtc,
                    i.Total,
                    i.TotalPaid,
                    i.Balance
                })
                .ToListAsync(ct);

            // Group by month
            var grouped = invoices
                .GroupBy(i => new { i.IssueDateUtc.Year, i.IssueDateUtc.Month })
                .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
                .Select(g => new RevenuePeriodItem
                {
                    Label     = new DateTime(g.Key.Year, g.Key.Month, 1)
                                    .ToString("MMM yyyy"),
                    Invoiced  = g.Sum(i => i.Total),
                    Collected = g.Sum(i => i.TotalPaid)
                })
                .ToList();

            return new RevenueByPeriodReportDto
            {
                Periods         = grouped,
                TotalInvoiced   = invoices.Sum(i => i.Total),
                TotalCollected  = invoices.Sum(i => i.TotalPaid),
                TotalOutstanding = invoices.Sum(i => i.Balance)
            };
        }
    }

    // ==================== 4. WIN/LOSS ANALYSIS ====================

    public class GetWinLossHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetWinLossHandler> _logger;

        public GetWinLossHandler(FlowDbContext db, ILogger<GetWinLossHandler> logger)
        {
            _db     = db;
            _logger = logger;
        }

        public async Task<WinLossReportDto> HandleAsync(
            ReportFilterDto filter, CancellationToken ct = default)
        {
            var tenantId = filter.TenantId;
            var from     = filter.FromDate ?? DateTime.UtcNow.AddMonths(-12);
            var to       = (filter.ToDate ?? DateTime.UtcNow).Date.AddDays(1).AddTicks(-1);

            var deals = await _db.Deals
                .Where(d => d.TenantId.ToString() == tenantId &&
                            !d.IsDeleted &&
                            d.UpdatedAtUtc >= from && d.UpdatedAtUtc <= to &&
                            (d.Stage == "ClosedWon" || d.Stage == "ClosedLost" ||
                             d.Stage == "Won"       || d.Stage == "Lost"))
                .ToListAsync(ct);

            // Resolve owner names
            var ownerIds = deals
                .Where(d => !string.IsNullOrEmpty(d.OwnerUserId))
                .Select(d => d.OwnerUserId!)
                .Distinct().ToList();

            var ownerMap = await _db.Users
                .Where(u => ownerIds.Contains(u.Id.ToString()))
                .Select(u => new { Id = u.Id.ToString(), Name = $"{u.FirstName} {u.LastName}".Trim() })
                .ToDictionaryAsync(u => u.Id, u => u.Name, ct);

            var byOwner = deals
                .GroupBy(d => d.OwnerUserId ?? "Unassigned")
                .Select(g =>
                {
                    var won  = g.Count(d => d.Stage == "ClosedWon" || d.Stage == "Won");
                    var lost = g.Count(d => d.Stage == "ClosedLost" || d.Stage == "Lost");
                    return new WinLossOwnerItem
                    {
                        OwnerName = ownerMap.TryGetValue(g.Key, out var n) ? n : "Unassigned",
                        Won       = won,
                        Lost      = lost,
                        WinRate   = (won + lost) == 0 ? 0 :
                                    Math.Round(won * 100.0 / (won + lost), 1),
                        WonValue  = g.Where(d => d.Stage == "ClosedWon" || d.Stage == "Won")
                                     .Sum(d => d.ExpectedValue)
                    };
                })
                .OrderByDescending(o => o.Won)
                .ToList();

            var totalWon  = deals.Count(d => d.Stage == "ClosedWon" || d.Stage == "Won");
            var totalLost = deals.Count(d => d.Stage == "ClosedLost" || d.Stage == "Lost");

            return new WinLossReportDto
            {
                ByOwner        = byOwner,
                TotalWon       = totalWon,
                TotalLost      = totalLost,
                OverallWinRate = (totalWon + totalLost) == 0 ? 0 :
                                 Math.Round(totalWon * 100.0 / (totalWon + totalLost), 1),
                TotalWonValue  = deals
                    .Where(d => d.Stage == "ClosedWon" || d.Stage == "Won")
                    .Sum(d => d.ExpectedValue)
            };
        }
    }

    // ==================== 5. OUTSTANDING INVOICES (AGING) ====================

    public class GetOutstandingInvoicesHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetOutstandingInvoicesHandler> _logger;

        public GetOutstandingInvoicesHandler(FlowDbContext db, ILogger<GetOutstandingInvoicesHandler> logger)
        {
            _db     = db;
            _logger = logger;
        }

        public async Task<OutstandingInvoicesReportDto> HandleAsync(
            ReportFilterDto filter, CancellationToken ct = default)
        {
            var tenantId = filter.TenantId;
            var today    = DateTime.UtcNow.Date;

            var invoices = await _db.Invoices
                .Where(i => i.TenantId.ToString() == tenantId &&
                            !i.IsDeleted &&
                            i.Balance > 0 &&
                            i.Status != InvoiceStatus.Paid &&
                            i.Status != InvoiceStatus.Cancelled)
                .Select(i => new
                {
                    i.Id,
                    i.Number,
                    i.Total,
                    i.Balance,
                    i.DueDateUtc,
                    i.Status,
                    DealTitle   = i.Deal != null ? i.Deal.Title : "",
                    ContactName = i.Deal != null && i.Deal.Contact != null
                        ? i.Deal.Contact.FirstName + " " + i.Deal.Contact.LastName
                        : ""
                })
                .ToListAsync(ct);

            var items = invoices.Select(i =>
            {
                var daysOverdue = i.DueDateUtc.HasValue
                    ? (int)(today - i.DueDateUtc.Value.Date).TotalDays
                    : 0;
                return new OutstandingInvoiceItem
                {
                    Number       = i.Number,
                    ContactName  = i.ContactName.Trim(),
                    DealTitle    = i.DealTitle,
                    GrandTotal   = i.Total,
                    Balance      = i.Balance,
                    DueDateUtc   = i.DueDateUtc,
                    DaysOverdue  = Math.Max(0, daysOverdue),
                    Status       = i.Status == InvoiceStatus.PartiallyPaid ? "PartiallyPaid" : "Unpaid"
                };
            }).OrderByDescending(i => i.DaysOverdue).ToList();

            // Aging buckets
            var buckets = new List<AgingBucketItem>
            {
                new() { Label = "Current (not due)",
                    Count  = items.Count(i => i.DaysOverdue == 0),
                    Amount = items.Where(i => i.DaysOverdue == 0).Sum(i => i.Balance) },
                new() { Label = "1–30 days",
                    Count  = items.Count(i => i.DaysOverdue is >= 1 and <= 30),
                    Amount = items.Where(i => i.DaysOverdue is >= 1 and <= 30).Sum(i => i.Balance) },
                new() { Label = "31–60 days",
                    Count  = items.Count(i => i.DaysOverdue is >= 31 and <= 60),
                    Amount = items.Where(i => i.DaysOverdue is >= 31 and <= 60).Sum(i => i.Balance) },
                new() { Label = "61–90 days",
                    Count  = items.Count(i => i.DaysOverdue is >= 61 and <= 90),
                    Amount = items.Where(i => i.DaysOverdue is >= 61 and <= 90).Sum(i => i.Balance) },
                new() { Label = "90+ days",
                    Count  = items.Count(i => i.DaysOverdue > 90),
                    Amount = items.Where(i => i.DaysOverdue > 90).Sum(i => i.Balance) },
            };

            return new OutstandingInvoicesReportDto
            {
                Buckets          = buckets,
                Invoices         = items,
                TotalOutstanding = items.Sum(i => i.Balance),
                TotalCount       = items.Count
            };
        }
    }

    // ==================== 6. REVENUE BY VERTICAL ====================

    public class GetRevenueByVerticalHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetRevenueByVerticalHandler> _logger;

        public GetRevenueByVerticalHandler(
            FlowDbContext db,
            ILogger<GetRevenueByVerticalHandler> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task<RevenueByVerticalReportDto> HandleAsync(
            ReportFilterDto filter, CancellationToken ct = default)
        {
            var tenantId = filter.TenantId;
            var from = filter.FromDate ?? DateTime.UtcNow.AddMonths(-12);
            var to = (filter.ToDate ?? DateTime.UtcNow).Date.AddDays(1).AddTicks(-1);

            // ── Pull all deals in range ───────────────────────────────
            var deals = await _db.Deals
                .Where(d => d.TenantId.ToString() == tenantId &&
                            !d.IsDeleted &&
                            d.CreatedAtUtc >= from && d.CreatedAtUtc <= to)
                .ToListAsync(ct);

            // ── Resolve VerticalId → Name for each deal ───────────────
            var verticalIds = deals
                .Where(d => d.VerticalId.HasValue)
                .Select(d => d.VerticalId!.Value)
                .Distinct()
                .ToList();

            var verticalMap = await _db.CompanyVerticals
                .Where(v => verticalIds.Contains(v.Id) && !v.IsDeleted)
                .Select(v => new { v.Id, v.Name })
                .ToDictionaryAsync(v => v.Id, v => v.Name, ct);

            // ── Pull invoices linked to deals in range ────────────────
            var dealIds = deals.Select(d => d.Id).ToList();

            var invoices = await _db.Invoices
                .Where(i => i.TenantId.ToString() == tenantId &&
                            !i.IsDeleted &&
                            i.DealId.HasValue &&
                            dealIds.Contains(i.DealId.Value))
                .Select(i => new
                {
                    i.DealId,
                    i.Total,
                    i.TotalPaid
                })
                .ToListAsync(ct);

            // ── Group deals by vertical ───────────────────────────────
            var grouped = deals
                .GroupBy(d => d.VerticalId.HasValue
                    ? (verticalMap.TryGetValue(d.VerticalId.Value, out var n) ? n : "Generic")
                    : "Not Assigned")
                .Select(g =>
                {
                    var won = g.Count(d => d.Stage is "ClosedWon" or "Won");
                    var lost = g.Count(d => d.Stage is "ClosedLost" or "Lost");
                    var total = won + lost;

                    var groupDealIds = g.Select(d => d.Id).ToList();
                    var groupInvoices = invoices
                        .Where(i => i.DealId.HasValue &&
                                    groupDealIds.Contains(i.DealId.Value))
                        .ToList();

                    return new VerticalRevenueItem
                    {
                        VerticalName = g.Key,
                        DealCount = g.Count(),
                        WonDeals = won,
                        PipelineValue = g
                            .Where(d => d.Stage is not ("ClosedWon" or "Won"
                                                     or "ClosedLost" or "Lost"))
                            .Sum(d => d.ExpectedValue),
                        WonValue = g
                            .Where(d => d.Stage is "ClosedWon" or "Won")
                            .Sum(d => d.ExpectedValue),
                        InvoicedValue = groupInvoices.Sum(i => i.Total),
                        CollectedValue = groupInvoices.Sum(i => i.TotalPaid),
                        WinRate = total == 0 ? 0 :
                                         Math.Round(won * 100.0 / total, 1)
                    };
                })
                .OrderByDescending(v => v.WonValue + v.PipelineValue)
                .ToList();

            return new RevenueByVerticalReportDto
            {
                Verticals = grouped,
                TotalRevenue = grouped.Sum(v => v.WonValue),
                TotalInvoiced = grouped.Sum(v => v.InvoicedValue),
                TotalDeals = deals.Count
            };
        }
    }

    // ==================== 7. SALES REP PERFORMANCE ====================

    public class GetSalesRepPerformanceHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetSalesRepPerformanceHandler> _logger;

        public GetSalesRepPerformanceHandler(FlowDbContext db,
            ILogger<GetSalesRepPerformanceHandler> logger)
        { _db = db; _logger = logger; }

        public async Task<SalesRepPerformanceReportDto> HandleAsync(
            ReportFilterDto filter, CancellationToken ct = default)
        {
            var tenantId = filter.TenantId;
            var from = filter.FromDate ?? DateTime.UtcNow.AddMonths(-12);
            var to = (filter.ToDate ?? DateTime.UtcNow).Date.AddDays(1).AddTicks(-1);

            var deals = await _db.Deals
                .Where(d => d.TenantId.ToString() == tenantId && !d.IsDeleted &&
                            d.CreatedAtUtc >= from && d.CreatedAtUtc <= to)
                .ToListAsync(ct);

            var leads = await _db.Leads
                .Where(l => l.TenantId.ToString() == tenantId && !l.IsDeleted &&
                            l.CreatedAtUtc >= from && l.CreatedAtUtc <= to)
                .ToListAsync(ct);

            var ownerIds = deals.Where(d => !string.IsNullOrEmpty(d.OwnerUserId))
                .Select(d => d.OwnerUserId!).Distinct().ToList();

            var ownerMap = await _db.Users
                .Where(u => ownerIds.Contains(u.Id.ToString()))
                .Select(u => new { Id = u.Id.ToString(), Name = $"{u.FirstName} {u.LastName}".Trim() })
                .ToDictionaryAsync(u => u.Id, u => u.Name, ct);

            var reps = deals
                .GroupBy(d => d.OwnerUserId ?? "Unassigned")
                .Select(g =>
                {
                    var won = g.Count(d => d.Stage is "ClosedWon" or "Won");
                    var lost = g.Count(d => d.Stage is "ClosedLost" or "Lost");
                    var active = g.Count(d => d.Stage is not
                        ("ClosedWon" or "Won" or "ClosedLost" or "Lost"));
                    var closed = won + lost;
                    var repLeads = leads.Where(l => l.OwnerUserId == g.Key).ToList();

                    return new SalesRepItem
                    {
                        OwnerName = ownerMap.TryGetValue(g.Key, out var n) ? n : "Unassigned",
                        TotalDeals = g.Count(),
                        Won = won,
                        Lost = lost,
                        Active = active,
                        WinRate = closed == 0 ? 0 : Math.Round(won * 100.0 / closed, 1),
                        WonValue = g.Where(d => d.Stage is "ClosedWon" or "Won")
                                          .Sum(d => d.ExpectedValue),
                        PipelineValue = g.Where(d => d.Stage is not
                                            ("ClosedWon" or "Won" or "ClosedLost" or "Lost"))
                                          .Sum(d => d.ExpectedValue),
                        LeadsOwned = repLeads.Count,
                        LeadsConverted = repLeads.Count(l => l.IsConverted)
                    };
                })
                .OrderByDescending(r => r.WonValue)
                .ToList();

            var totalWon = reps.Sum(r => r.Won);
            var totalLost = reps.Sum(r => r.Lost);
            var totalClosed = totalWon + totalLost;

            return new SalesRepPerformanceReportDto
            {
                Reps = reps,
                TotalDeals = reps.Sum(r => r.TotalDeals),
                TotalWon = totalWon,
                TotalWonValue = reps.Sum(r => r.WonValue),
                OverallWinRate = totalClosed == 0 ? 0 :
                    Math.Round(totalWon * 100.0 / totalClosed, 1)
            };
        }
    }

    // ==================== 8. LEAD SOURCE ANALYSIS ====================

    public class GetLeadSourceHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetLeadSourceHandler> _logger;

        public GetLeadSourceHandler(FlowDbContext db,
            ILogger<GetLeadSourceHandler> logger)
        { _db = db; _logger = logger; }

        public async Task<LeadSourceReportDto> HandleAsync(
            ReportFilterDto filter, CancellationToken ct = default)
        {
            var tenantId = filter.TenantId;
            var from = filter.FromDate ?? DateTime.UtcNow.AddMonths(-12);
            var to = (filter.ToDate ?? DateTime.UtcNow).Date.AddDays(1).AddTicks(-1);

            var leads = await _db.Leads
                .Where(l => l.TenantId.ToString() == tenantId && !l.IsDeleted &&
                            l.CreatedAtUtc >= from && l.CreatedAtUtc <= to)
                .ToListAsync(ct);

            var channelIds = leads.Where(l => l.ChannelId.HasValue)
                .Select(l => l.ChannelId!.Value).Distinct().ToList();

            var channelMap = await _db.LeadChannels
                .Where(c => channelIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Name })
                .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

            var sourceIds = leads.Where(l => l.SourceId.HasValue)
                .Select(l => l.SourceId!.Value).Distinct().ToList();

            var sourceMap = await _db.LeadSources
                .Where(s => sourceIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Name })
                .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

            LeadSourceItem BuildItem(IGrouping<string, dynamic> g) =>
                new()
                {
                    Name = g.Key,
                    TotalLeads = g.Count(),
                    Qualified = g.Count(l => (int)l.Status >= 2),
                    Converted = g.Count(l => (bool)l.IsConverted),
                    ConversionRate = g.Count() == 0 ? 0 :
                        Math.Round(g.Count(l => (bool)l.IsConverted) * 100.0 / g.Count(), 1),
                    EstimatedValue = g.Sum(l => (decimal?)l.EstimatedValue ?? 0m)
                };

            var byChannel = leads
                .GroupBy(l => l.ChannelId.HasValue
                    ? (channelMap.TryGetValue(l.ChannelId.Value, out var n) ? n : "Unknown")
                    : "Not Set")
                .Select(g =>
                {
                    var conv = g.Count(l => l.IsConverted);
                    return new LeadSourceItem
                    {
                        Name = g.Key,
                        TotalLeads = g.Count(),
                        Qualified = g.Count(l => (int)l.Status >= 2),
                        Converted = conv,
                        ConversionRate = g.Count() == 0 ? 0 :
                            Math.Round(conv * 100.0 / g.Count(), 1),
                        EstimatedValue = g.Sum(l => l.EstimatedValue ?? 0)
                    };
                })
                .OrderByDescending(c => c.TotalLeads).ToList();

            var bySource = leads
                .GroupBy(l => l.SourceId.HasValue
                    ? (sourceMap.TryGetValue(l.SourceId.Value, out var n) ? n : "Unknown")
                    : "Not Set")
                .Select(g =>
                {
                    var conv = g.Count(l => l.IsConverted);
                    return new LeadSourceItem
                    {
                        Name = g.Key,
                        TotalLeads = g.Count(),
                        Qualified = g.Count(l => (int)l.Status >= 2),
                        Converted = conv,
                        ConversionRate = g.Count() == 0 ? 0 :
                            Math.Round(conv * 100.0 / g.Count(), 1),
                        EstimatedValue = g.Sum(l => l.EstimatedValue ?? 0)
                    };
                })
                .OrderByDescending(c => c.TotalLeads).ToList();

            var totalConverted = leads.Count(l => l.IsConverted);
            return new LeadSourceReportDto
            {
                ByChannel = byChannel,
                BySource = bySource,
                TotalLeads = leads.Count,
                TotalConverted = totalConverted,
                OverallConversionRate = leads.Count == 0 ? 0 :
                    Math.Round(totalConverted * 100.0 / leads.Count, 1)
            };
        }
    }

    // ==================== 9. DEAL VELOCITY ====================

    public class GetDealVelocityHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetDealVelocityHandler> _logger;

        public GetDealVelocityHandler(FlowDbContext db,
            ILogger<GetDealVelocityHandler> logger)
        { _db = db; _logger = logger; }

        public async Task<DealVelocityReportDto> HandleAsync(
            ReportFilterDto filter, CancellationToken ct = default)
        {
            var tenantId = filter.TenantId;
            var from = filter.FromDate ?? DateTime.UtcNow.AddMonths(-12);
            var to = (filter.ToDate ?? DateTime.UtcNow).Date.AddDays(1).AddTicks(-1);

            var closedDealIds = await _db.Deals
                .Where(d => d.TenantId.ToString() == tenantId && !d.IsDeleted &&
                            (d.Stage == "ClosedWon" || d.Stage == "Won" ||
                             d.Stage == "ClosedLost" || d.Stage == "Lost") &&
                            d.UpdatedAtUtc >= from && d.UpdatedAtUtc <= to)
                .Select(d => d.Id)
                .ToListAsync(ct);

            var history = await _db.DealStageHistory
                .Where(h => closedDealIds.Contains(h.DealId))
                .OrderBy(h => h.DealId).ThenBy(h => h.ChangedAtUtc)
                .ToListAsync(ct);

            // Build per-deal ordered history → compute stage durations
            var stageTransitions = new List<(string From, string To, double Days)>();

            foreach (var dealId in closedDealIds)
            {
                var dealHistory = history
                    .Where(h => h.DealId == dealId)
                    .OrderBy(h => h.ChangedAtUtc)
                    .ToList();

                for (int i = 1; i < dealHistory.Count; i++)
                {
                    var days = (dealHistory[i].ChangedAtUtc -
                                dealHistory[i - 1].ChangedAtUtc).TotalDays;
                    if (days >= 0)
                        stageTransitions.Add((
                            dealHistory[i].FromStage ?? dealHistory[i - 1].ToStage ?? "Start",
                            dealHistory[i].ToStage ?? "End",
                            Math.Round(days, 1)
                        ));
                }
            }

            var stages = stageTransitions
                .GroupBy(t => new { t.From, t.To })
                .Where(g => g.Count() >= 1)
                .Select(g => new StageVelocityItem
                {
                    FromStage = g.Key.From,
                    ToStage = g.Key.To,
                    AvgDays = Math.Round(g.Average(t => t.Days), 1),
                    MinDays = Math.Round(g.Min(t => t.Days), 1),
                    MaxDays = Math.Round(g.Max(t => t.Days), 1),
                    Transitions = g.Count()
                })
                .OrderBy(s => s.AvgDays)
                .ToList();

            // Overall deal → close (created to updated)
            var dealTimes = await _db.Deals
                .Where(d => d.TenantId.ToString() == tenantId && !d.IsDeleted &&
                            closedDealIds.Contains(d.Id))
                .Select(d => new { d.CreatedAtUtc, d.UpdatedAtUtc })
                .ToListAsync(ct);

            var avgDealToClose = dealTimes.Any()
                ? Math.Round(dealTimes.Average(
                    d => (d.UpdatedAtUtc - d.CreatedAtUtc).TotalDays), 1)
                : 0;

            return new DealVelocityReportDto
            {
                Stages = stages,
                AvgDaysLeadToClose = 0,   // populated if lead data joined
                AvgDaysLeadToDeal = 0,
                AvgDaysDealToClose = avgDealToClose,
                DealsAnalyzed = closedDealIds.Count
            };
        }
    }

    // ==================== 10. ACTIVITY LEADERBOARD ====================

    public class GetActivityLeaderboardHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetActivityLeaderboardHandler> _logger;

        public GetActivityLeaderboardHandler(FlowDbContext db,
            ILogger<GetActivityLeaderboardHandler> logger)
        { _db = db; _logger = logger; }

        public async Task<ActivityLeaderboardReportDto> HandleAsync(
            ReportFilterDto filter, CancellationToken ct = default)
        {
            var tenantId = filter.TenantId;
            var from = filter.FromDate ?? DateTime.UtcNow.AddMonths(-12);
            var to = (filter.ToDate ?? DateTime.UtcNow).Date.AddDays(1).AddTicks(-1);

            var notes = await _db.DealNotes
                .Where(n => n.TenantId.ToString() == tenantId &&
                            n.CreatedAtUtc >= from && n.CreatedAtUtc <= to)
                .Select(n => n.CreatedBy).ToListAsync(ct);

            var activities = await _db.DealActivities
                .Where(a => a.TenantId.ToString() == tenantId &&
                            a.CreatedAtUtc >= from && a.CreatedAtUtc <= to)
                .Select(a => a.CreatedBy).ToListAsync(ct);

            var reminders = await _db.DealReminders
                .Where(r => r.TenantId.ToString() == tenantId &&
                            r.CreatedAtUtc >= from && r.CreatedAtUtc <= to)
                .Select(r => r.CreatedBy).ToListAsync(ct);

            var allReps = notes.Concat(activities).Concat(reminders)
                .Where(r => !string.IsNullOrEmpty(r))
                .Distinct().ToList();

            var reps = allReps.Select(rep =>
            {
                var n = notes.Count(x => x == rep);
                var a = activities.Count(x => x == rep);
                var r = reminders.Count(x => x == rep);
                return new ActivityRepItem
                {
                    OwnerName = rep!,
                    Notes = n,
                    Activities = a,
                    Reminders = r,
                    Total = n + a + r
                };
            })
            .OrderByDescending(r => r.Total)
            .ToList();

            return new ActivityLeaderboardReportDto
            {
                Reps = reps,
                TotalNotes = notes.Count,
                TotalActivities = activities.Count,
                TotalReminders = reminders.Count
            };
        }
    }
}
