// =====================================================================
// PriorityReports.cs
// Location: MerkaiTrial.Application/Commands/Reports/PriorityReports.cs
//
// NEW (112). Five reports, built on the same rules as the other ten
// (ReportScope: the workspace by Guid, the period to the end of its last
// day, the workspace's currency only, the Vertical filter, and what each
// pipeline stage MEANS rather than what it is called).
//
//   11. Stalled Deals          — open deals going nowhere, and why
//   12. Lead Funnel & Conversion — leads by status, how many convert, how fast
//   13. Quote Performance      — sent, accepted, rejected, expiring, discount
//   14. Invoice & Collections  — invoiced, collected, how fast, who is late
//   15. Revenue Forecast       — open deals by the month they should close
//
// What "today" means: DateTime.UtcNow.Date. What the period means is said
// on each report's page, under the filter (_ReportFilter.cshtml).
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
    // ==================== 11. STALLED DEALS ====================

    /// <summary>
    /// Open deals (today) that look stuck. A deal is stalled when ANY of:
    ///   • it has sat in the same stage for Days days or more
    ///   • nothing has been logged on it (a note, a call, a meeting, a stage
    ///     change) for Days days or more
    ///   • its expected close date has passed
    /// Days defaults to 30. The period doesn't apply — this is about now.
    /// </summary>
    public class GetStalledDealsHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetStalledDealsHandler> _logger;
        private readonly IRecordScopeService _scope;

        public GetStalledDealsHandler(FlowDbContext db, ILogger<GetStalledDealsHandler> logger, IRecordScopeService scope)
        {
            _db = db; _logger = logger; _scope = scope;
        }

        public const int MaxListed = 200;

        public async Task<StalledDealsReportDto> HandleAsync(ReportFilterDto filter, CancellationToken ct = default)
        {
            var s = await ReportScope.CreateAsync(_db, filter, 12, ct);
            var dealAccess = await _scope.GetAsync(RecordModules.Deals, ct);
            var days = Math.Clamp(filter.Days ?? 30, 1, 365);
            var now = DateTime.UtcNow;
            var today = now.Date;
            var closed = s.ClosedKeys;
            var dealType = ActivityEntityType.Deal;

            var rows = await s.Deals(_db, dealAccess)
                .Where(d => !closed.Contains(d.Stage))
                .Select(d => new
                {
                    d.Id, d.Title, d.OwnerUserId, d.Stage, d.ExpectedValue, d.ExpectedCloseDateUtc, d.CreatedAtUtc, d.Currency,
                    LastStage = _db.DealStageHistory.Where(h => h.DealId == d.Id).Max(h => (DateTime?)h.ChangedAtUtc),
                    LastActivity = _db.Activities
                        .Where(a => a.EntityType == dealType && a.EntityId == d.Id && !a.IsDeleted && !a.IsTask)
                        .Max(a => (DateTime?)a.ActivityDate),
                    LastNote = _db.DealNotes.Where(n => n.DealId == d.Id && !n.IsDeleted).Max(n => (DateTime?)n.CreatedAtUtc),
                })
                .ToListAsync(ct);

            var dto = new StalledDealsReportDto { StalledDays = days };
            s.Note(dto, rows.GroupBy(r => r.Currency).Select(g => ((string?)g.Key, g.Count())));
            var mine = rows.Where(r => s.InCurrency(r.Currency)).ToList();
            dto.OpenDeals = mine.Count;

            var names = await ReportOwners.NamesAsync(_db, mine.Select(r => r.OwnerUserId), ct);

            static int DaysSince(DateTime from, DateTime now) => Math.Max(0, (int)Math.Floor((now - from).TotalDays));

            var stalled = new List<StalledDealItem>();
            foreach (var r in mine)
            {
                var stageSince = r.LastStage ?? r.CreatedAtUtc;
                var touched = new[] { stageSince, r.LastActivity ?? DateTime.MinValue, r.LastNote ?? DateTime.MinValue }
                    .Where(t => t <= now).DefaultIfEmpty(stageSince).Max();

                var item = new StalledDealItem
                {
                    DealId = r.Id,
                    Title = r.Title,
                    OwnerName = ReportOwners.Name(names, r.OwnerUserId),
                    Stage = r.Stage,
                    StageName = s.NameOf(r.Stage),
                    Value = r.ExpectedValue,
                    DaysInStage = DaysSince(stageSince, now),
                    DaysSinceTouch = DaysSince(touched, now),
                    ExpectedCloseDateUtc = r.ExpectedCloseDateUtc,
                    DaysPastClose = r.ExpectedCloseDateUtc.Date < today ? (today - r.ExpectedCloseDateUtc.Date).Days : 0,
                };

                if (item.DaysInStage >= days) item.Reasons.Add($"In {item.StageName} for {item.DaysInStage} days");
                if (item.DaysSinceTouch >= days) item.Reasons.Add($"Nothing logged for {item.DaysSinceTouch} days");
                if (item.DaysPastClose > 0) item.Reasons.Add($"Close date passed {item.DaysPastClose} day{(item.DaysPastClose == 1 ? "" : "s")} ago");

                if (item.Reasons.Count > 0) stalled.Add(item);
            }

            dto.StalledCount = stalled.Count;
            dto.StalledValue = stalled.Sum(x => x.Value);
            dto.OverdueCloseCount = stalled.Count(x => x.DaysPastClose > 0);
            dto.Deals = stalled
                .OrderByDescending(x => x.Value).ThenByDescending(x => x.DaysSinceTouch).ThenBy(x => x.Title)
                .Take(MaxListed)
                .ToList();
            dto.ByOwner = stalled
                .GroupBy(x => x.OwnerName)
                .Select(g => new StalledOwnerItem { OwnerName = g.Key, Count = g.Count(), Value = g.Sum(x => x.Value) })
                .OrderByDescending(x => x.Value).ThenBy(x => x.OwnerName)
                .ToList();
            return dto;
        }
    }

    // ==================== 12. LEAD FUNNEL & CONVERSION ====================

    /// <summary>
    /// Leads received in the period: where they are now (by the workspace's
    /// own statuses), how many qualified and converted, how long converting
    /// took, month by month and person by person.
    /// </summary>
    public class GetLeadFunnelHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetLeadFunnelHandler> _logger;
        private readonly IRecordScopeService _scope;

        public GetLeadFunnelHandler(FlowDbContext db, ILogger<GetLeadFunnelHandler> logger, IRecordScopeService scope)
        {
            _db = db; _logger = logger; _scope = scope;
        }

        public async Task<LeadFunnelReportDto> HandleAsync(ReportFilterDto filter, CancellationToken ct = default)
        {
            var s = await ReportScope.CreateAsync(_db, filter, 12, ct);
            var leadAccess = await _scope.GetAsync(RecordModules.Leads, ct);
            DateTime from = s.From, to = s.To;
            var tid = s.TenantId;

            var leads = s.Leads(_db, leadAccess).Where(l => l.CreatedAtUtc >= from && l.CreatedAtUtc <= to);

            var rows = await leads
                .GroupBy(l => new { l.Status, l.IsConverted, l.OwnerUserId, l.CreatedAtUtc.Year, l.CreatedAtUtc.Month })
                .Select(g => new { g.Key.Status, g.Key.IsConverted, g.Key.OwnerUserId, g.Key.Year, g.Key.Month, Count = g.Count() })
                .ToListAsync(ct);

            var converted = await leads
                .Where(l => l.IsConverted && l.ConvertedAtUtc != null)
                .Select(l => new { l.CreatedAtUtc, ConvertedAtUtc = l.ConvertedAtUtc!.Value })
                .ToListAsync(ct);

            var statuses = await _db.LeadStatusDefinitions.AsNoTracking()
                .Where(x => x.TenantId == tid)
                .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
                .Select(x => new { x.Key, x.Name, x.Category, x.IsActive })
                .ToListAsync(ct);
            var byKey = statuses.GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            LeadStatusCategory CategoryOf(string? status, bool isConverted) =>
                isConverted ? LeadStatusCategory.Converted
                : status is not null && byKey.TryGetValue(status, out var d) ? d.Category
                : LeadStatusCategory.Open;

            var dto = new LeadFunnelReportDto();
            s.Note(dto, Array.Empty<(string?, int)>());   // counts, not money

            var total = rows.Sum(r => r.Count);
            int InCategory(params LeadStatusCategory[] cats) =>
                rows.Where(r => cats.Contains(CategoryOf(r.Status, r.IsConverted))).Sum(r => r.Count);

            dto.TotalLeads = total;
            dto.Converted = rows.Where(r => r.IsConverted).Sum(r => r.Count);
            dto.QualifiedOrBetter = InCategory(LeadStatusCategory.Qualified, LeadStatusCategory.Converted);
            dto.Disqualified = InCategory(LeadStatusCategory.Disqualified);
            dto.QualifiedRate = Pct.Of(dto.QualifiedOrBetter, total);
            dto.ConversionRate = Pct.Of(dto.Converted, total);
            var spans = converted.Select(c => (c.ConvertedAtUtc - c.CreatedAtUtc).TotalDays).Where(x => x >= 0).ToList();
            dto.AvgDaysToConvert = spans.Count == 0 ? 0 : Math.Round(spans.Average(), 1);

            // Where the leads are now, in the workspace's status order.
            var counts = rows.GroupBy(r => r.Status ?? "", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Count), StringComparer.OrdinalIgnoreCase);
            var keys = statuses.Where(x => x.IsActive || counts.ContainsKey(x.Key)).Select(x => x.Key)
                .Concat(counts.Keys.Where(k => !byKey.ContainsKey(k)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            dto.ByStatus = keys.Select(k =>
            {
                counts.TryGetValue(k, out var n);
                var known = byKey.TryGetValue(k, out var def);
                return new LeadStatusCountItem
                {
                    Key = k,
                    Name = known ? def!.Name : (k.Length == 0 ? "(no status)" : k),
                    Category = (known ? def!.Category : LeadStatusCategory.Open).ToString(),
                    Count = n,
                    Percent = Pct.Of(n, total),
                };
            }).ToList();

            // Month by month, by the month the lead came in.
            var months = new List<LeadMonthItem>();
            for (var m = new DateTime(from.Year, from.Month, 1); m <= to; m = m.AddMonths(1))
            {
                var inMonth = rows.Where(r => r.Year == m.Year && r.Month == m.Month).ToList();
                var created = inMonth.Sum(r => r.Count);
                var conv = inMonth.Where(r => r.IsConverted).Sum(r => r.Count);
                months.Add(new LeadMonthItem
                {
                    Label = m.ToString("MMM yyyy", CultureInfo.InvariantCulture),
                    Created = created,
                    Converted = conv,
                    ConversionRate = Pct.Of(conv, created),
                });
            }
            dto.ByMonth = months;

            var names = await ReportOwners.NamesAsync(_db, rows.Select(r => r.OwnerUserId), ct);
            dto.ByOwner = rows.GroupBy(r => ReportOwners.Key(r.OwnerUserId))
                .Select(g =>
                {
                    var n = g.Sum(r => r.Count);
                    var conv = g.Where(r => r.IsConverted).Sum(r => r.Count);
                    return new LeadOwnerItem
                    {
                        OwnerName = ReportOwners.Name(names, g.First().OwnerUserId),
                        Leads = n,
                        Qualified = g.Where(r => CategoryOf(r.Status, r.IsConverted) is LeadStatusCategory.Qualified or LeadStatusCategory.Converted).Sum(r => r.Count),
                        Converted = conv,
                        ConversionRate = Pct.Of(conv, n),
                    };
                })
                .OrderByDescending(o => o.Converted).ThenByDescending(o => o.Leads).ThenBy(o => o.OwnerName)
                .ToList();
            return dto;
        }
    }

    // ==================== 13. QUOTE PERFORMANCE ====================

    /// <summary>
    /// Quotes issued in the period. "Sent" means it reached the customer
    /// (Sent, Viewed, Accepted, Rejected or Expired); acceptance rate is
    /// Accepted ÷ Sent. Open means with the customer now (Sent, Viewed).
    /// Discount is the sent quotes' discount ÷ their value before discount.
    /// </summary>
    public class GetQuotePerformanceHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetQuotePerformanceHandler> _logger;
        private readonly IRecordScopeService _scope;

        public GetQuotePerformanceHandler(FlowDbContext db, ILogger<GetQuotePerformanceHandler> logger, IRecordScopeService scope)
        {
            _db = db; _logger = logger; _scope = scope;
        }

        public const int ExpiringWithinDays = 7;

        private static readonly QuoteStatus[] SentStatuses =
            { QuoteStatus.Sent, QuoteStatus.Viewed, QuoteStatus.Accepted, QuoteStatus.Rejected, QuoteStatus.Expired };
        private static readonly QuoteStatus[] OpenStatuses = { QuoteStatus.Sent, QuoteStatus.Viewed };
        private static readonly QuoteStatus[] NotSentStatuses =
            { QuoteStatus.Draft, QuoteStatus.PendingApproval, QuoteStatus.Approved };

        public async Task<QuotePerformanceReportDto> HandleAsync(ReportFilterDto filter, CancellationToken ct = default)
        {
            var s = await ReportScope.CreateAsync(_db, filter, 12, ct);
            var dealAccess = await _scope.GetAsync(RecordModules.Deals, ct);
            DateTime from = s.From, to = s.To;
            var today = DateTime.UtcNow.Date;

            var rows = await s.Quotes(_db, dealAccess)
                .Where(q => q.IssueDateUtc >= from && q.IssueDateUtc <= to)
                .Select(q => new
                {
                    q.Id, q.Number, q.Status, q.GrandTotal, q.Subtotal, q.DiscountTotal, q.ExpiresAtUtc, q.Currency,
                    DealTitle = q.Deal != null ? q.Deal.Title : "",
                    Owner = q.Deal != null ? q.Deal.OwnerUserId : null,
                })
                .ToListAsync(ct);

            var dto = new QuotePerformanceReportDto();
            s.Note(dto, rows.GroupBy(r => r.Currency).Select(g => ((string?)g.Key, g.Count())));
            var mine = rows.Where(r => s.InCurrency(r.Currency)).ToList();

            var sent = mine.Where(r => SentStatuses.Contains(r.Status)).ToList();
            var open = mine.Where(r => OpenStatuses.Contains(r.Status)).ToList();

            dto.TotalQuotes = mine.Count;
            dto.SentQuotes = sent.Count;
            dto.Accepted = mine.Count(r => r.Status == QuoteStatus.Accepted);
            dto.Rejected = mine.Count(r => r.Status == QuoteStatus.Rejected);
            dto.Expired = mine.Count(r => r.Status == QuoteStatus.Expired);
            dto.Open = open.Count;
            dto.NotSent = mine.Count(r => NotSentStatuses.Contains(r.Status));
            dto.AcceptanceRate = Pct.Of(dto.Accepted, dto.SentQuotes);
            dto.QuotedValue = sent.Sum(r => r.GrandTotal);
            dto.AcceptedValue = mine.Where(r => r.Status == QuoteStatus.Accepted).Sum(r => r.GrandTotal);
            dto.OpenValue = open.Sum(r => r.GrandTotal);
            var listValue = sent.Sum(r => r.Subtotal);
            dto.AvgDiscountPct = listValue == 0 ? 0 : Math.Round((double)(sent.Sum(r => r.DiscountTotal) / listValue) * 100.0, 1);

            var soon = today.AddDays(ExpiringWithinDays);
            var expiring = open.Where(r => r.ExpiresAtUtc.Date >= today && r.ExpiresAtUtc.Date <= soon).ToList();
            var past = open.Where(r => r.ExpiresAtUtc.Date < today).ToList();
            dto.ExpiringSoon = expiring.Count;
            dto.PastExpiry = past.Count;

            dto.ByStatus = mine.GroupBy(r => r.Status)
                .OrderBy(g => g.Key)
                .Select(g => new QuoteStatusItem { Status = g.Key.ToString(), Count = g.Count(), Value = g.Sum(r => r.GrandTotal) })
                .ToList();

            var names = await ReportOwners.NamesAsync(_db, sent.Select(r => r.Owner), ct);
            dto.ByOwner = sent.GroupBy(r => ReportOwners.Key(r.Owner))
                .Select(g =>
                {
                    var acc = g.Count(r => r.Status == QuoteStatus.Accepted);
                    return new QuoteOwnerItem
                    {
                        OwnerName = ReportOwners.Name(names, g.First().Owner),
                        Sent = g.Count(),
                        Accepted = acc,
                        AcceptanceRate = Pct.Of(acc, g.Count()),
                        AcceptedValue = g.Where(r => r.Status == QuoteStatus.Accepted).Sum(r => r.GrandTotal),
                    };
                })
                .OrderByDescending(o => o.AcceptedValue).ThenBy(o => o.OwnerName)
                .ToList();

            dto.NeedsAttention = past.Select(r => (r, Reason: $"Expired {(today - r.ExpiresAtUtc.Date).Days} day(s) ago — still open"))
                .Concat(expiring.Select(r => (r, Reason: (r.ExpiresAtUtc.Date - today).Days == 0
                    ? "Expires today" : $"Expires in {(r.ExpiresAtUtc.Date - today).Days} day(s)")))
                .Select(x => new QuoteAttentionItem
                {
                    QuoteId = x.r.Id, Number = x.r.Number, DealTitle = x.r.DealTitle ?? "", Status = x.r.Status.ToString(),
                    Value = x.r.GrandTotal, ExpiresAtUtc = x.r.ExpiresAtUtc, Reason = x.Reason,
                })
                .OrderBy(x => x.ExpiresAtUtc)
                .Take(50)
                .ToList();
            return dto;
        }
    }

    // ==================== 14. INVOICE & COLLECTIONS ====================

    /// <summary>
    /// How well the money comes in. Invoices issued in the period: their
    /// total and how much of it is paid. Payments that ARRIVED in the
    /// period, by method. For invoices issued in the period and paid in
    /// full: days from issue to the last payment, and how many were paid by
    /// their due date. And what is overdue today, by customer.
    /// </summary>
    public class GetInvoiceCollectionsHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetInvoiceCollectionsHandler> _logger;
        private readonly IRecordScopeService _scope;

        public GetInvoiceCollectionsHandler(FlowDbContext db, ILogger<GetInvoiceCollectionsHandler> logger, IRecordScopeService scope)
        {
            _db = db; _logger = logger; _scope = scope;
        }

        public async Task<InvoiceCollectionsReportDto> HandleAsync(ReportFilterDto filter, CancellationToken ct = default)
        {
            var s = await ReportScope.CreateAsync(_db, filter, 12, ct);
            var dealAccess = await _scope.GetAsync(RecordModules.Deals, ct);
            DateTime from = s.From, to = s.To;
            var today = DateTime.UtcNow.Date;
            var tid = s.TenantId;
            var captured = PaymentStatusNames.Captured;

            var visible = s.Invoices(_db, dealAccess);

            var issued = await visible
                .Where(i => i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Cancelled &&
                            i.IssueDateUtc >= from && i.IssueDateUtc <= to)
                .Select(i => new { i.Id, i.Total, i.Balance, i.IssueDateUtc, i.DueDateUtc, i.Status, i.Currency })
                .ToListAsync(ct);

            var visibleIds = visible.Select(i => i.Id);
            var payments = await _db.Payments.AsNoTracking()
                .Where(p => p.TenantId == tid && !p.IsDeleted && p.Status == captured &&
                            p.PaidAtUtc >= from && p.PaidAtUtc <= to && visibleIds.Contains(p.InvoiceId))
                .GroupBy(p => new { p.Method, p.Currency })
                .Select(g => new { g.Key.Method, g.Key.Currency, Count = g.Count(), Amount = g.Sum(p => p.Amount) })
                .ToListAsync(ct);

            var paidIds = issued.Where(i => i.Status == InvoiceStatus.Paid).Select(i => i.Id).ToList();
            var lastPayments = await _db.Payments.AsNoTracking()
                .Where(p => p.TenantId == tid && !p.IsDeleted && p.Status == captured && paidIds.Contains(p.InvoiceId))
                .GroupBy(p => p.InvoiceId)
                .Select(g => new { InvoiceId = g.Key, Last = g.Max(p => p.PaidAtUtc) })
                .ToListAsync(ct);

            var overdue = await visible
                .Where(i => i.Balance > 0 && i.DueDateUtc != null && i.DueDateUtc < today &&
                            i.Status != InvoiceStatus.Paid && i.Status != InvoiceStatus.Cancelled && i.Status != InvoiceStatus.Draft)
                .Select(i => new
                {
                    i.Balance, DueDateUtc = i.DueDateUtc!.Value, i.Currency,
                    Customer = i.Deal != null && i.Deal.Contact != null
                        ? i.Deal.Contact.FirstName + " " + i.Deal.Contact.LastName
                        : (i.Deal != null && i.Deal.Company != null ? i.Deal.Company.Name : null),
                })
                .ToListAsync(ct);

            var dto = new InvoiceCollectionsReportDto();
            s.Note(dto, issued.GroupBy(i => i.Currency).Select(g => ((string?)g.Key, g.Count()))
                .Concat(payments.Select(p => ((string?)p.Currency, p.Count)))
                .Concat(overdue.GroupBy(o => o.Currency).Select(g => ((string?)g.Key, g.Count()))));

            var inv = issued.Where(i => s.InCurrency(i.Currency)).ToList();
            var pay = payments.Where(p => s.InCurrency(p.Currency)).ToList();
            var due = overdue.Where(o => s.InCurrency(o.Currency)).ToList();

            dto.InvoicesIssued = inv.Count;
            dto.Invoiced = inv.Sum(i => i.Total);
            dto.PaidOnThese = inv.Sum(i => i.Total - i.Balance);
            dto.CollectionRate = dto.Invoiced == 0 ? 0 : Math.Round((double)(dto.PaidOnThese / dto.Invoiced) * 100.0, 1);
            dto.Collected = pay.Sum(p => p.Amount);

            var last = lastPayments.ToDictionary(x => x.InvoiceId, x => x.Last);
            var paid = inv.Where(i => i.Status == InvoiceStatus.Paid && last.ContainsKey(i.Id))
                .Select(i => new { i.IssueDateUtc, i.DueDateUtc, PaidAt = last[i.Id] })
                .ToList();
            dto.PaidInvoices = paid.Count;
            dto.AvgDaysToPay = paid.Count == 0 ? 0
                : Math.Round(paid.Average(p => Math.Max(0, (p.PaidAt.Date - p.IssueDateUtc.Date).TotalDays)), 1);
            dto.PaidOnTimePct = paid.Count == 0 ? 0
                : Pct.Of(paid.Count(p => p.DueDateUtc is null || p.PaidAt.Date <= p.DueDateUtc.Value.Date), paid.Count);

            dto.OverdueCount = due.Count;
            dto.OverdueAmount = due.Sum(o => o.Balance);

            dto.ByMethod = pay.GroupBy(p => string.IsNullOrWhiteSpace(p.Method) ? "Other" : p.Method)
                .Select(g => new PaymentMethodItem { Method = g.Key, Count = g.Sum(x => x.Count), Amount = g.Sum(x => x.Amount) })
                .OrderByDescending(x => x.Amount).ThenBy(x => x.Method)
                .ToList();

            dto.OverdueCustomers = due
                .GroupBy(o => string.IsNullOrWhiteSpace(o.Customer) ? "(no customer)" : o.Customer!.Trim())
                .Select(g => new OverdueCustomerItem
                {
                    CustomerName = g.Key,
                    Invoices = g.Count(),
                    Amount = g.Sum(x => x.Balance),
                    OldestDaysOverdue = g.Max(x => (today - x.DueDateUtc.Date).Days),
                })
                .OrderByDescending(x => x.Amount).ThenBy(x => x.CustomerName)
                .Take(20)
                .ToList();
            return dto;
        }
    }

    // ==================== 15. REVENUE FORECAST ====================

    /// <summary>
    /// Every open deal (today), by the month it is expected to close, for
    /// the next Months months (default 6). Each month: how many, their
    /// value, and their WEIGHTED value (value × the deal's probability) —
    /// the honest forecast. Deals whose close date has already passed sit
    /// in "Overdue"; beyond the horizon, in "Later". The period doesn't
    /// apply. Won this month is shown alongside, for context.
    /// </summary>
    public class GetRevenueForecastHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ILogger<GetRevenueForecastHandler> _logger;
        private readonly IRecordScopeService _scope;

        public GetRevenueForecastHandler(FlowDbContext db, ILogger<GetRevenueForecastHandler> logger, IRecordScopeService scope)
        {
            _db = db; _logger = logger; _scope = scope;
        }

        public async Task<RevenueForecastReportDto> HandleAsync(ReportFilterDto filter, CancellationToken ct = default)
        {
            var s = await ReportScope.CreateAsync(_db, filter, 12, ct);
            var dealAccess = await _scope.GetAsync(RecordModules.Deals, ct);
            var months = Math.Clamp(filter.Months ?? 6, 1, 12);
            var today = DateTime.UtcNow.Date;
            var thisMonth = new DateTime(today.Year, today.Month, 1);
            var nextMonth = thisMonth.AddMonths(1);
            var closed = s.ClosedKeys;
            var won = s.WonKeys;

            var open = await s.Deals(_db, dealAccess)
                .Where(d => !closed.Contains(d.Stage))
                .Select(d => new { d.ExpectedCloseDateUtc, d.ExpectedValue, d.Probability, d.OwnerUserId, d.Currency })
                .ToListAsync(ct);

            var wonThisMonth = await s.Deals(_db, dealAccess)
                .Where(d => won.Contains(d.Stage) &&
                            (d.ActualCloseDateUtc ?? d.UpdatedAtUtc) >= thisMonth &&
                            (d.ActualCloseDateUtc ?? d.UpdatedAtUtc) < nextMonth)
                .GroupBy(d => d.Currency)
                .Select(g => new { Currency = g.Key, Count = g.Count(), Value = g.Sum(d => d.ExpectedValue) })
                .ToListAsync(ct);

            var dto = new RevenueForecastReportDto { Months = months };
            s.Note(dto, open.GroupBy(o => o.Currency).Select(g => ((string?)g.Key, g.Count()))
                .Concat(wonThisMonth.Select(w => ((string?)w.Currency, w.Count))));

            var mine = open.Where(o => s.InCurrency(o.Currency)).ToList();
            static decimal Weighted(decimal value, int probability) => Math.Round(value * Math.Clamp(probability, 0, 100) / 100m, 2);

            var horizonEnd = thisMonth.AddMonths(months);
            string KeyOf(DateTime close) =>
                close.Date < today ? "overdue"
                : close.Date >= horizonEnd ? "later"
                : close.ToString("yyyy-MM", CultureInfo.InvariantCulture);

            var grouped = mine.GroupBy(o => KeyOf(o.ExpectedCloseDateUtc))
                .ToDictionary(g => g.Key, g => g.ToList());

            ForecastBucketItem Bucket(string key, string label)
            {
                grouped.TryGetValue(key, out var list);
                list ??= new();
                return new ForecastBucketItem
                {
                    Key = key, Label = label, Count = list.Count,
                    Value = list.Sum(x => x.ExpectedValue),
                    Weighted = list.Sum(x => Weighted(x.ExpectedValue, x.Probability)),
                };
            }

            var buckets = new List<ForecastBucketItem> { Bucket("overdue", "Overdue (close date passed)") };
            for (var m = thisMonth; m < horizonEnd; m = m.AddMonths(1))
                buckets.Add(Bucket(m.ToString("yyyy-MM", CultureInfo.InvariantCulture), m.ToString("MMM yyyy", CultureInfo.InvariantCulture)));
            buckets.Add(Bucket("later", $"Later (after {horizonEnd.AddDays(-1):MMM yyyy})"));
            dto.Buckets = buckets;

            dto.OpenDeals = mine.Count;
            dto.TotalValue = mine.Sum(x => x.ExpectedValue);
            dto.WeightedValue = mine.Sum(x => Weighted(x.ExpectedValue, x.Probability));
            dto.WonThisMonth = wonThisMonth.Where(w => s.InCurrency(w.Currency)).Sum(w => w.Value);

            var names = await ReportOwners.NamesAsync(_db, mine.Select(x => x.OwnerUserId), ct);
            dto.ByOwner = mine.GroupBy(x => ReportOwners.Key(x.OwnerUserId))
                .Select(g => new ForecastOwnerItem
                {
                    OwnerName = ReportOwners.Name(names, g.First().OwnerUserId),
                    Count = g.Count(),
                    Value = g.Sum(x => x.ExpectedValue),
                    Weighted = g.Sum(x => Weighted(x.ExpectedValue, x.Probability)),
                })
                .OrderByDescending(x => x.Weighted).ThenBy(x => x.OwnerName)
                .ToList();
            return dto;
        }
    }
}
