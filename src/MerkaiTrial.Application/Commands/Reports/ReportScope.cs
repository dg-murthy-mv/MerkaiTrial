// =====================================================================
// ReportScope.cs
// Location: MerkaiTrial.Application/Commands/Reports/ReportScope.cs
//
// NEW (111). What every report needs to know before it counts anything,
// worked out once, the same way, for all of them:
//
//   • the workspace (as a Guid — "TenantId.ToString() == …" made SQL
//     Server convert every row's id to text and read the whole table)
//   • the period: From 00:00 to the END of To
//   • the workspace's currency — money in any other currency is left out
//     of the sums, and counted, never added in
//   • the Vertical filter, as the vertical ids it means (null = no
//     filter). It used to be honoured by 2 reports of 10, and one of
//     those compared it with the wrong field.
//   • the workspace's pipeline stages and what each one MEANS: Open, Won
//     or Lost (PipelineStage.Category). Reports matched the names
//     "ClosedWon"/"Won", so a workspace's own won stage ("Contract
//     signed") counted as neither won nor open.
//
// CLOSED DATE. A deal's close date is ActualCloseDateUtc — set when it
// moves to a Won or Lost stage. Reports used UpdatedAtUtc, so fixing a
// typo on a deal won in March moved the win to today. Deals closed before
// ActualCloseDateUtc was filled in fall back to UpdatedAtUtc.
// =====================================================================

using MerkaiTrial.Application.Common;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.Reports
{
    public sealed record ReportStage(string Key, string Name, StageCategory Category, int SortOrder);

    public sealed class ReportScope
    {
        public Guid TenantId { get; private init; }
        public DateTime From { get; private init; }
        /// <summary>The last tick of the To date.</summary>
        public DateTime To { get; private init; }
        public string Currency { get; private init; } = string.Empty;
        /// <summary>Null: no Vertical filter. Empty: a vertical that doesn't exist — nothing matches.</summary>
        public List<Guid>? VerticalIds { get; private init; }

        public IReadOnlyDictionary<string, ReportStage> Stages { get; private init; } =
            new Dictionary<string, ReportStage>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Stage keys that mean Won / Lost / either (for IN (…) in SQL).</summary>
        public List<string> WonKeys { get; private init; } = new();
        public List<string> LostKeys { get; private init; } = new();
        public List<string> ClosedKeys { get; private init; } = new();

        public static async Task<ReportScope> CreateAsync(FlowDbContext db, ReportFilterDto filter,
            int monthsBack = 12, CancellationToken ct = default)
        {
            if (!Guid.TryParse(filter.TenantId, out var tenantId))
                throw new ArgumentException("The report has no workspace.", nameof(filter));

            var from = (filter.FromDate ?? DateTime.UtcNow.AddMonths(-monthsBack)).Date;
            var to = (filter.ToDate ?? DateTime.UtcNow).Date.AddDays(1).AddTicks(-1);

            var currency = await WorkspaceCurrency.ForAsync(db, tenantId, null, ct);

            List<Guid>? verticalIds = null;
            if (!string.IsNullOrWhiteSpace(filter.Vertical))
            {
                var name = filter.Vertical.Trim();
                verticalIds = await db.CompanyVerticals.AsNoTracking()
                    .Where(v => v.Name == name && !v.IsDeleted && (v.TenantId == null || v.TenantId == tenantId))
                    .Select(v => v.Id)
                    .ToListAsync(ct);
            }

            var rows = await db.PipelineStages.AsNoTracking()
                .Where(s => s.TenantId == tenantId)
                .Select(s => new { s.Key, s.Name, s.Category, s.SortOrder })
                .ToListAsync(ct);

            var stages = new Dictionary<string, ReportStage>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in rows)
                stages[s.Key] = new ReportStage(s.Key, string.IsNullOrWhiteSpace(s.Name) ? s.Key : s.Name, s.Category, s.SortOrder);

            // Keys some old deals still carry with no stage row behind them.
            void Legacy(string key, string name, StageCategory cat, int order)
            {
                if (!stages.ContainsKey(key)) stages[key] = new ReportStage(key, name, cat, order);
            }
            Legacy("ClosedWon", "Closed Won", StageCategory.Won, 900);
            Legacy("Won", "Won", StageCategory.Won, 901);
            Legacy("ClosedLost", "Closed Lost", StageCategory.Lost, 950);
            Legacy("Lost", "Lost", StageCategory.Lost, 951);

            var won = stages.Values.Where(s => s.Category == StageCategory.Won).Select(s => s.Key).ToList();
            var lost = stages.Values.Where(s => s.Category == StageCategory.Lost).Select(s => s.Key).ToList();

            return new ReportScope
            {
                TenantId = tenantId,
                From = from,
                To = to,
                Currency = currency,
                VerticalIds = verticalIds,
                Stages = stages,
                WonKeys = won,
                LostKeys = lost,
                ClosedKeys = won.Concat(lost).ToList(),
            };
        }

        /// <summary>What a stage key means. A key with no stage row is an open stage.</summary>
        public StageCategory CategoryOf(string? key) =>
            key is not null && Stages.TryGetValue(key, out var s) ? s.Category : StageCategory.Open;

        public string NameOf(string? key) =>
            key is not null && Stages.TryGetValue(key, out var s) ? s.Name : (key ?? "");

        public int OrderOf(string? key) =>
            key is not null && Stages.TryGetValue(key, out var s) ? s.SortOrder : 500;

        public bool InCurrency(string? code) =>
            string.IsNullOrWhiteSpace(code) || string.IsNullOrEmpty(Currency) ||
            string.Equals(code.Trim(), Currency, StringComparison.OrdinalIgnoreCase);

        /// <summary>Fills Currency and the "left out" note from the currencies seen.</summary>
        public void Note(ReportBaseDto dto, IEnumerable<(string? Currency, int Count)> seen)
        {
            dto.Currency = Currency;
            foreach (var (code, count) in seen)
            {
                if (InCurrency(code)) continue;
                dto.ExcludedOtherCurrency += count;
                var c = code!.Trim().ToUpperInvariant();
                if (!dto.ExcludedCurrencies.Contains(c)) dto.ExcludedCurrencies.Add(c);
            }
            dto.ExcludedCurrencies.Sort(StringComparer.Ordinal);
        }

        // ── query pieces ───────────────────────────────────────────────

        /// <summary>The workspace's deals the user may see, in the Vertical filter.</summary>
        public IQueryable<Deal> Deals(FlowDbContext db, RecordAccess access)
        {
            var tid = TenantId;
            var q = db.Deals.AsNoTracking().VisibleTo(access).Where(d => d.TenantId == tid && !d.IsDeleted);
            if (VerticalIds is not null)
            {
                var ids = VerticalIds;
                q = q.Where(d => d.VerticalId != null && ids.Contains(d.VerticalId.Value));
            }
            return q;
        }

        /// <summary>The workspace's leads the user may see, in the Vertical filter.</summary>
        public IQueryable<Lead> Leads(FlowDbContext db, RecordAccess access)
        {
            var tid = TenantId;
            var q = db.Leads.AsNoTracking().VisibleTo(access).Where(l => l.TenantId == tid && !l.IsDeleted);
            if (VerticalIds is not null)
            {
                var ids = VerticalIds;
                q = q.Where(l => l.VerticalId != null && ids.Contains(l.VerticalId.Value));
            }
            return q;
        }

        /// <summary>Quotes on visible deals, in the Vertical filter (through the deal).</summary>
        public IQueryable<Quote> Quotes(FlowDbContext db, RecordAccess dealAccess)
        {
            var tid = TenantId;
            var q = db.Quotes.AsNoTracking().WithVisibleDeal(db, dealAccess).Where(x => x.TenantId == tid && !x.IsDeleted);
            if (VerticalIds is not null)
            {
                var dealIds = Deals(db, dealAccess).Select(d => d.Id);
                q = q.Where(x => dealIds.Contains(x.DealId));
            }
            return q;
        }

        /// <summary>Invoices on visible deals, in the Vertical filter (through the deal or its quote).</summary>
        public IQueryable<Invoice> Invoices(FlowDbContext db, RecordAccess dealAccess)
        {
            var tid = TenantId;
            var q = db.Invoices.AsNoTracking().WithVisibleDeal(db, dealAccess).Where(i => i.TenantId == tid && !i.IsDeleted);
            if (VerticalIds is not null)
            {
                var dealIds = Deals(db, dealAccess).Select(d => d.Id);
                q = q.Where(i => (i.DealId != null && dealIds.Contains(i.DealId.Value)) ||
                                 (i.QuoteId != null && i.Quote != null && dealIds.Contains(i.Quote.DealId)));
            }
            return q;
        }
    }
}
