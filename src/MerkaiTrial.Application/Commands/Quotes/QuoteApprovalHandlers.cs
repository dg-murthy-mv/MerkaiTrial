// =====================================================================
// QuoteApprovalHandlers.cs
// Location: MerkaiTrial.Application/Commands/Quotes/QuoteApprovalHandlers.cs
//
// NEW FILE (017). Everything about quote approvals in one place:
//
//   QuoteApprovalRules      — pure: does this quote break a rule, and why
//   QuoteWorkflow           — pure: which status moves are allowed
//   QuoteApprovalEngine     — settings, evaluation, who approves, who may
//                             read/write a quote (record visibility)
//   Get/Save settings, Get state, Submit, Approve / Request changes,
//   Recall, Pending-for-me   — the handlers behind api/quote-approvals
//
// All are ICommandHandler, so the Scrutor scan registers them — nothing
// to add to Program.cs.
//
// THE DISCOUNT RULE, AND WHY IT LOOKS AT LIST PRICE
//   A limit on the "Discount" column alone is easy to get round: type a
//   lower unit price and leave the discount at 0. So for catalog items the
//   discount is measured against the product's LIST price:
//
//       reference = ListPrice × Qty        (custom items: UnitPrice × Qty)
//       charged   = UnitPrice × Qty − LineDiscount
//       discount% = (reference − charged) ÷ reference
//
//   Any single line over the limit triggers approval. Checking lines, not
//   the quote average, stops one 50%-off line hiding inside a big quote.
//
// WHO APPROVES — see QuoteApproval.cs. Managers of the deal owner's team,
// plus workspace admins, never the requester. Admins are exempt from the
// rules altogether.
//
// 018 (invoice round): the engine also serves MANUAL invoices — the same
// limits apply, so a rep can't skip quote approval by invoicing straight
// from the deal. New: EvaluateLinesAsync (any set of lines) and
// ApproversForDealAsync (who signs off for a deal). The quote versions
// now call these; behaviour for quotes is unchanged.
// =====================================================================

using System.Globalization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Quotes
{
    // =================================================================
    // RULES (pure)
    // =================================================================

    public sealed record QuoteRuleLine(string Name, decimal UnitPrice, int Quantity, decimal LineDiscount, decimal? ListPrice);

    /// <summary>A line as stored on a quote or invoice — list price not yet looked up.</summary>
    public sealed record PricedLine(string Name, decimal UnitPrice, int Quantity, decimal LineDiscount, Guid? ProductId);

    public sealed record QuoteRuleResult(bool RulesEnabled, bool RequiresApproval, List<string> Reasons, decimal MaxLineDiscountPercent);

    public static class QuoteApprovalRules
    {
        public static decimal LineDiscountPercent(QuoteRuleLine l)
        {
            var qty = Math.Max(0, l.Quantity);
            var reference = (l.ListPrice is > 0 ? l.ListPrice.Value : l.UnitPrice) * qty;
            if (reference <= 0) return 0;

            var charged = (l.UnitPrice * qty) - l.LineDiscount;
            var pct = (reference - charged) / reference * 100m;
            return pct <= 0 ? 0 : Math.Round(pct, 2);
        }

        public static QuoteRuleResult Evaluate(
            QuoteApprovalSettings settings, IReadOnlyList<QuoteRuleLine> lines, decimal grandTotal, string currency)
        {
            var maxPct = lines.Count == 0 ? 0 : lines.Max(LineDiscountPercent);

            if (!settings.IsEnabled)
                return new QuoteRuleResult(false, false, new List<string>(), maxPct);

            var reasons = new List<string>();
            var inv = CultureInfo.InvariantCulture;

            if (settings.MaxDiscountPercent is decimal limit)
            {
                var over = lines
                    .Select(l => (l.Name, Pct: LineDiscountPercent(l)))
                    .Where(x => x.Pct > limit)
                    .OrderByDescending(x => x.Pct)
                    .ToList();

                foreach (var x in over.Take(3))
                    reasons.Add($"\"{x.Name}\" is {x.Pct.ToString("0.##", inv)}% below list price (limit {limit.ToString("0.##", inv)}%).");

                if (over.Count > 3)
                    reasons.Add($"…and {over.Count - 3} more line(s) over the {limit.ToString("0.##", inv)}% limit.");
            }

            if (settings.MaxQuoteTotal is decimal maxTotal && grandTotal > maxTotal)
                reasons.Add($"Total {currency} {grandTotal.ToString("N2", inv)} is above the {currency} {maxTotal.ToString("N2", inv)} limit.");

            return new QuoteRuleResult(true, reasons.Count > 0, reasons, maxPct);
        }
    }

    // =================================================================
    // WORKFLOW (pure)
    // =================================================================

    public static class QuoteWorkflow
    {
        /// <summary>
        /// Moves allowed through the STATUS endpoint (PUT api/quotes/{id}/status,
        /// and the customer's public link). PendingApproval and Approved are
        /// reached only through api/quote-approvals; Draft only by editing,
        /// recalling or "request changes".
        /// </summary>
        public static bool CanMove(QuoteStatus from, QuoteStatus to) => (from, to) switch
        {
            (QuoteStatus.Draft, QuoteStatus.Sent) => true,
            (QuoteStatus.Revised, QuoteStatus.Sent) => true,
            (QuoteStatus.Approved, QuoteStatus.Sent) => true,

            (QuoteStatus.Sent, QuoteStatus.Viewed or QuoteStatus.Accepted or QuoteStatus.Rejected or QuoteStatus.Expired) => true,
            (QuoteStatus.Viewed, QuoteStatus.Accepted or QuoteStatus.Rejected or QuoteStatus.Expired) => true,

            (QuoteStatus.Rejected or QuoteStatus.Expired, QuoteStatus.Revised) => true,
            _ => false
        };

        /// <summary>Line items and dates can be changed only in these.</summary>
        public static bool IsEditable(QuoteStatus s)
            => s is QuoteStatus.Draft or QuoteStatus.Revised or QuoteStatus.Approved;

        /// <summary>A quote in one of these goes out once it passes (or is exempt from) the rules.</summary>
        public static bool IsPreSend(QuoteStatus s)
            => s is QuoteStatus.Draft or QuoteStatus.Revised;

        public static string Label(QuoteStatus s) => s switch
        {
            QuoteStatus.PendingApproval => "pending-approval",
            _ => s.ToString().ToLowerInvariant()
        };
    }

    // =================================================================
    // ENGINE — settings, evaluation, approvers, access
    // =================================================================

    public sealed record QuoteApprover(Guid UserId, string Name, bool IsAdmin);

    public class QuoteApprovalEngine : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly IRecordScopeService _scope;
        private readonly ICurrentUserService _currentUser;

        public QuoteApprovalEngine(FlowDbContext db, IRecordScopeService scope, ICurrentUserService currentUser)
        {
            _db = db;
            _scope = scope;
            _currentUser = currentUser;
        }

        /// <summary>The tenant's row, or the defaults when it has never saved one.</summary>
        public async Task<(QuoteApprovalSettings Settings, bool IsDefault)> GetSettingsAsync(Guid tenantId, CancellationToken ct = default)
        {
            var row = await _db.QuoteApprovalSettings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

            return row != null
                ? (row, false)
                : (new QuoteApprovalSettings
                {
                    TenantId = tenantId,
                    IsEnabled = QuoteApprovalDefaults.IsEnabled,
                    MaxDiscountPercent = QuoteApprovalDefaults.MaxDiscountPercent,
                    MaxQuoteTotal = null
                }, true);
        }

        /// <summary>Does the quote AS IT IS NOW break a rule?</summary>
        public async Task<QuoteRuleResult> EvaluateAsync(Guid tenantId, Guid quoteId, CancellationToken ct = default)
        {
            var quote = await _db.Quotes.AsNoTracking()
                .Where(q => q.Id == quoteId && q.TenantId == tenantId && !q.IsDeleted)
                .Select(q => new
                {
                    q.GrandTotal,
                    q.Currency,
                    Items = q.Items
                        .Where(i => !i.IsDeleted)
                        .Select(i => new PricedLine(i.Name, i.UnitPrice, i.Quantity, i.LineDiscount, i.ProductId))
                        .ToList()
                })
                .FirstOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException($"Quote {quoteId} not found");

            return await EvaluateLinesAsync(tenantId, quote.Items, quote.GrandTotal, quote.Currency ?? string.Empty, ct);
        }

        /// <summary>
        /// The same rules for any set of priced lines — quotes, and manual
        /// invoices (018). List prices are looked up for catalog lines.
        /// </summary>
        public async Task<QuoteRuleResult> EvaluateLinesAsync(
            Guid tenantId, IReadOnlyList<PricedLine> items, decimal grandTotal, string currency, CancellationToken ct = default)
        {
            var productIds = items
                .Where(i => i.ProductId.HasValue)
                .Select(i => i.ProductId!.Value)
                .Distinct()
                .ToList();

            var listPrices = productIds.Count == 0
                ? new Dictionary<Guid, decimal?>()
                : await _db.Products.AsNoTracking()
                    .Where(p => productIds.Contains(p.Id))
                    .ToDictionaryAsync(p => p.Id, p => (decimal?)p.ListPrice, ct);

            var lines = items
                .Select(i => new QuoteRuleLine(
                    i.Name, i.UnitPrice, i.Quantity, i.LineDiscount,
                    i.ProductId.HasValue && listPrices.TryGetValue(i.ProductId.Value, out var lp) ? lp : null))
                .ToList();

            var (settings, _) = await GetSettingsAsync(tenantId, ct);
            return QuoteApprovalRules.Evaluate(settings, lines, grandTotal, currency);
        }

        /// <summary>
        /// Who may approve this quote: managers of the deal owner's team (or
        /// of the requester's team when the deal has no owner), plus every
        /// active workspace admin — minus the requester. Managers first.
        /// </summary>
        public async Task<List<QuoteApprover>> ApproversAsync(
            Guid tenantId, Guid quoteId, Guid? requesterId, CancellationToken ct = default)
        {
            var dealId = await _db.Quotes.AsNoTracking()
                .Where(q => q.Id == quoteId && q.TenantId == tenantId)
                .Select(q => (Guid?)q.DealId)
                .FirstOrDefaultAsync(ct);

            return await ApproversForDealAsync(tenantId, dealId, excludeUserId: requesterId, teamFallbackUserId: requesterId, ct);
        }

        /// <summary>
        /// Who signs off for a deal: managers of the deal owner's team (or,
        /// when the deal has no owner, of <paramref name="teamFallbackUserId"/>'s
        /// team), plus every active workspace admin — minus
        /// <paramref name="excludeUserId"/> (the requester, for quotes; nobody,
        /// for invoices, where the issuer may be the approver).
        /// </summary>
        public async Task<List<QuoteApprover>> ApproversForDealAsync(
            Guid tenantId, Guid? dealId, Guid? excludeUserId, Guid? teamFallbackUserId, CancellationToken ct = default)
        {
            var requesterId = excludeUserId;
            var owner = dealId.HasValue
                ? await _db.Deals.AsNoTracking()
                    .Where(d => d.Id == dealId.Value && d.TenantId == tenantId)
                    .Select(d => d.OwnerUserId)
                    .FirstOrDefaultAsync(ct)
                : null;

            Guid? teamUserId = Guid.TryParse(owner, out var ownerId) ? ownerId : teamFallbackUserId;

            Guid? teamId = null;
            if (teamUserId.HasValue)
            {
                // Users is not tenant-filtered — scope explicitly.
                teamId = await _db.Users.AsNoTracking()
                    .Where(u => u.Id == teamUserId.Value && u.TenantId == tenantId && !u.IsDeleted)
                    .Select(u => u.TeamId)
                    .FirstOrDefaultAsync(ct);
            }

            var managerIds = teamId.HasValue
                ? await _db.TeamManagers.AsNoTracking()
                    .Where(m => m.TeamId == teamId.Value)
                    .Select(m => m.UserId)
                    .ToListAsync(ct)
                : new List<Guid>();

            var people = await _db.Users.AsNoTracking()
                .Where(u => u.TenantId == tenantId && !u.IsDeleted && u.IsActive &&
                            (u.IsTenantAdmin || managerIds.Contains(u.Id)))
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.IsTenantAdmin })
                .ToListAsync(ct);

            return people
                .Where(u => u.Id != requesterId)
                .OrderBy(u => u.IsTenantAdmin)          // managers first
                .ThenBy(u => u.FirstName)
                .Select(u => new QuoteApprover(u.Id, $"{u.FirstName} {u.LastName}".Trim(), u.IsTenantAdmin))
                .ToList();
        }

        /// <summary>
        /// Can the current user change this quote? Same rule as its deal:
        /// quotes follow deal visibility.
        /// </summary>
        public async Task<bool> CanWriteQuoteAsync(Guid tenantId, Guid quoteId, CancellationToken ct = default)
        {
            var dealAccess = await _scope.GetAsync(RecordModules.Deals, ct);

            return await _db.Quotes.AsNoTracking()
                .Where(q => q.Id == quoteId && q.TenantId == tenantId && !q.IsDeleted)
                .WithVisibleDeal(_db, dealAccess)
                .AnyAsync(ct);
        }

        /// <summary>
        /// Can the current user see this quote? Anyone who can change it —
        /// plus the people asked to approve it while it is pending, and
        /// anyone who decided an approval on it. A manager whose own
        /// visibility is "Own" can still open the quote they were asked to
        /// approve.
        /// </summary>
        public async Task<bool> CanReadQuoteAsync(Guid tenantId, Guid quoteId, CancellationToken ct = default)
        {
            if (await CanWriteQuoteAsync(tenantId, quoteId, ct)) return true;

            var me = _currentUser.GetCurrentUserId();

            var requests = await _db.QuoteApprovalRequests.AsNoTracking()
                .Where(r => r.QuoteId == quoteId && r.TenantId == tenantId)
                .Select(r => new { r.Status, r.RequestedByUserId, r.DecidedByUserId })
                .ToListAsync(ct);

            if (requests.Any(r => r.DecidedByUserId == me)) return true;

            var pending = requests.FirstOrDefault(r => r.Status == QuoteApprovalRequestStatus.Pending);
            if (pending == null) return false;

            var approvers = await ApproversAsync(tenantId, quoteId, pending.RequestedByUserId, ct);
            return approvers.Any(a => a.UserId == me);
        }

        /// <summary>
        /// Can the current user delete this attachment? Resolved from the
        /// attachment itself (the quote it hangs on must be one they can
        /// change) — NOT from the {id} in the URL, which the Admin.Web
        /// client may not fill with the real quote id.
        /// </summary>
        public async Task<bool> CanWriteQuoteAttachmentAsync(Guid tenantId, Guid attachmentId, CancellationToken ct = default)
        {
            var quoteId = await _db.Attachments.AsNoTracking()
                .Where(a => a.Id == attachmentId && a.TenantId == tenantId && !a.IsDeleted)
                .Select(a => (Guid?)a.EntityId)
                .FirstOrDefaultAsync(ct);

            return quoteId.HasValue && await CanWriteQuoteAsync(tenantId, quoteId.Value, ct);
        }

        public static List<string> SplitReasons(string? reasons)
            => string.IsNullOrWhiteSpace(reasons)
                ? new List<string>()
                : reasons.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        public static QuoteApprovalRequestDto ToDto(QuoteApprovalRequest r) => new(
            r.Id, r.QuoteId, r.Status, r.RequestedByName, r.RequestedAtUtc, r.RequestComment,
            SplitReasons(r.Reasons), r.QuoteTotal, r.MaxLineDiscountPercent, r.Currency,
            r.DecidedByName, r.DecidedAtUtc, r.DecisionComment);

        internal static bool IsDuplicateKey(DbUpdateException ex)
        {
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                if (inner is SqlException sql && (sql.Number == 2601 || sql.Number == 2627))
                    return true;
            return false;
        }
    }

    // =================================================================
    // SETTINGS
    // =================================================================

    public class GetQuoteApprovalSettingsHandler : ICommandHandler
    {
        private readonly QuoteApprovalEngine _engine;
        public GetQuoteApprovalSettingsHandler(QuoteApprovalEngine engine) => _engine = engine;

        public async Task<QuoteApprovalSettingsDto> Handle(Guid tenantId, CancellationToken ct = default)
        {
            var (s, isDefault) = await _engine.GetSettingsAsync(tenantId, ct);
            return new QuoteApprovalSettingsDto(
                s.IsEnabled, s.MaxDiscountPercent, s.MaxQuoteTotal, isDefault,
                isDefault ? null : s.UpdatedAtUtc, isDefault ? null : s.UpdatedBy);
        }
    }

    public class SaveQuoteApprovalSettingsHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly IAuditService _audit;

        public SaveQuoteApprovalSettingsHandler(FlowDbContext db, IAuditService audit)
        {
            _db = db;
            _audit = audit;
        }

        public async Task Handle(Guid tenantId, SaveQuoteApprovalSettingsDto dto, string updatedBy, CancellationToken ct = default)
        {
            if (dto.MaxDiscountPercent is < 0 or > 100)
                throw new InvalidOperationException("The discount limit must be between 0 and 100%.");

            if (dto.MaxQuoteTotal is <= 0)
                throw new InvalidOperationException("The quote total limit must be more than zero — or leave it empty for no limit.");

            if (dto.IsEnabled && dto.MaxDiscountPercent == null && dto.MaxQuoteTotal == null)
                throw new InvalidOperationException("Set a discount limit, a total limit, or both — or switch approvals off.");

            var row = await _db.QuoteApprovalSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
            if (row == null)
            {
                row = new QuoteApprovalSettings { Id = Guid.NewGuid(), TenantId = tenantId };
                _db.QuoteApprovalSettings.Add(row);
            }

            row.IsEnabled = dto.IsEnabled;
            row.MaxDiscountPercent = dto.MaxDiscountPercent;
            row.MaxQuoteTotal = dto.MaxQuoteTotal;
            row.UpdatedAtUtc = DateTime.UtcNow;
            row.UpdatedBy = updatedBy;

            await _db.SaveChangesAsync(ct);

            await _audit.WriteAsync(
                "QuoteApprovalRulesChanged", "QuoteApprovalSettings", row.Id, tenantId,
                new { dto.IsEnabled, dto.MaxDiscountPercent, dto.MaxQuoteTotal }, ct);
        }
    }

    // =================================================================
    // STATE (what the Detail page shows, and what I may do)
    // =================================================================

    public class GetQuoteApprovalStateHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly QuoteApprovalEngine _engine;
        private readonly ICurrentUserService _currentUser;

        public GetQuoteApprovalStateHandler(FlowDbContext db, QuoteApprovalEngine engine, ICurrentUserService currentUser)
        {
            _db = db;
            _engine = engine;
            _currentUser = currentUser;
        }

        public async Task<QuoteApprovalStateDto> Handle(Guid tenantId, Guid quoteId, CancellationToken ct = default)
        {
            var status = await _db.Quotes.AsNoTracking()
                .Where(q => q.Id == quoteId && q.TenantId == tenantId && !q.IsDeleted)
                .Select(q => (QuoteStatus?)q.Status)
                .FirstOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException($"Quote {quoteId} not found");

            var me = await _currentUser.GetCurrentUserAsync();
            var rules = await _engine.EvaluateAsync(tenantId, quoteId, ct);
            var canWrite = await _engine.CanWriteQuoteAsync(tenantId, quoteId, ct);

            var requests = await _db.QuoteApprovalRequests.AsNoTracking()
                .Where(r => r.QuoteId == quoteId && r.TenantId == tenantId)
                .OrderByDescending(r => r.RequestedAtUtc)
                .ToListAsync(ct);

            var pending = requests.FirstOrDefault(r => r.Status == QuoteApprovalRequestStatus.Pending);
            var approvers = await _engine.ApproversAsync(tenantId, quoteId, pending?.RequestedByUserId ?? me.UserId, ct);

            var isExempt = me.IsTenantAdmin;
            var preSend = QuoteWorkflow.IsPreSend(status);

            var canSubmit = canWrite && !isExempt && preSend && rules.RequiresApproval && pending == null
                            && approvers.Count > 0;
            var canApprove = pending != null && approvers.Any(a => a.UserId == me.UserId);
            var canRecall = pending != null && (pending.RequestedByUserId == me.UserId || me.IsTenantAdmin);
            var canSend = canWrite && (status == QuoteStatus.Approved || (preSend && (!rules.RequiresApproval || isExempt)));

            return new QuoteApprovalStateDto(
                quoteId,
                status.ToString(),
                rules.RulesEnabled,
                rules.RequiresApproval,
                rules.Reasons,
                rules.MaxLineDiscountPercent,
                isExempt,
                canSubmit,
                canApprove,
                canRecall,
                canSend,
                approvers.Select(a => a.IsAdmin ? $"{a.Name} (admin)" : a.Name).ToList(),
                pending == null ? null : QuoteApprovalEngine.ToDto(pending),
                requests.Where(r => r.Status != QuoteApprovalRequestStatus.Pending).Select(QuoteApprovalEngine.ToDto).ToList());
        }
    }

    // =================================================================
    // SUBMIT
    // =================================================================

    public class SubmitQuoteForApprovalHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly QuoteApprovalEngine _engine;
        private readonly ICurrentUserService _currentUser;
        private readonly IAuditService _audit;
        private readonly ILogger<SubmitQuoteForApprovalHandler> _logger;

        public SubmitQuoteForApprovalHandler(
            FlowDbContext db, QuoteApprovalEngine engine, ICurrentUserService currentUser,
            IAuditService audit, ILogger<SubmitQuoteForApprovalHandler> logger)
        {
            _db = db;
            _engine = engine;
            _currentUser = currentUser;
            _audit = audit;
            _logger = logger;
        }

        public async Task Handle(Guid tenantId, Guid quoteId, string? comment, CancellationToken ct = default)
        {
            if (!await _engine.CanWriteQuoteAsync(tenantId, quoteId, ct))
                throw new KeyNotFoundException($"Quote {quoteId} not found");

            var quote = await _db.Quotes
                .FirstOrDefaultAsync(q => q.Id == quoteId && q.TenantId == tenantId && !q.IsDeleted, ct)
                ?? throw new KeyNotFoundException($"Quote {quoteId} not found");

            if (!QuoteWorkflow.IsPreSend(quote.Status))
                throw new InvalidOperationException(quote.Status == QuoteStatus.PendingApproval
                    ? "This quote is already waiting for approval."
                    : $"Only a draft quote can be sent for approval. This one is {QuoteWorkflow.Label(quote.Status)}.");

            var me = await _currentUser.GetCurrentUserAsync();
            if (me.IsTenantAdmin)
                throw new InvalidOperationException("You're a workspace admin, so this quote doesn't need approval — you can send it directly.");

            var rules = await _engine.EvaluateAsync(tenantId, quoteId, ct);
            if (!rules.RequiresApproval)
                throw new InvalidOperationException("This quote is within your workspace's limits, so it doesn't need approval. You can send it.");

            var approvers = await _engine.ApproversAsync(tenantId, quoteId, me.UserId, ct);
            if (approvers.Count == 0)
                throw new InvalidOperationException("Nobody can approve this quote — ask your workspace admin to check the team managers.");

            var cleanComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
            if (cleanComment?.Length > 1000) cleanComment = cleanComment[..1000];

            var request = new QuoteApprovalRequest
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                QuoteId = quoteId,
                Status = QuoteApprovalRequestStatus.Pending,
                RequestedByUserId = me.UserId,
                RequestedByName = me.FullName,
                RequestedAtUtc = DateTime.UtcNow,
                RequestComment = cleanComment,
                Reasons = string.Join("\n", rules.Reasons),
                QuoteTotal = quote.GrandTotal,
                MaxLineDiscountPercent = rules.MaxLineDiscountPercent,
                Currency = quote.Currency ?? string.Empty
            };

            _db.QuoteApprovalRequests.Add(request);

            var from = quote.Status;
            quote.Status = QuoteStatus.PendingApproval;
            quote.UpdatedAtUtc = DateTime.UtcNow;
            quote.UpdatedBy = me.FullName;

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (QuoteApprovalEngine.IsDuplicateKey(ex))
            {
                // Two clicks at once — the filtered unique index allows one
                // pending request per quote.
                throw new InvalidOperationException("This quote is already waiting for approval.");
            }

            await _audit.WriteAsync(
                AuditAction.QuoteStatusChanged, AuditEntityType.Quote, quoteId, tenantId,
                new { number = quote.Number, from = from.ToString(), to = quote.Status.ToString(), via = "SubmitForApproval", reasons = rules.Reasons },
                ct);

            _logger.LogInformation("Quote {Number} submitted for approval by {User}", quote.Number, me.FullName);
        }
    }

    // =================================================================
    // APPROVE / REQUEST CHANGES
    // =================================================================

    public class DecideQuoteApprovalHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly QuoteApprovalEngine _engine;
        private readonly ICurrentUserService _currentUser;
        private readonly IAuditService _audit;
        private readonly ILogger<DecideQuoteApprovalHandler> _logger;

        public DecideQuoteApprovalHandler(
            FlowDbContext db, QuoteApprovalEngine engine, ICurrentUserService currentUser,
            IAuditService audit, ILogger<DecideQuoteApprovalHandler> logger)
        {
            _db = db;
            _engine = engine;
            _currentUser = currentUser;
            _audit = audit;
            _logger = logger;
        }

        public async Task Handle(Guid tenantId, Guid quoteId, bool approve, string? comment, CancellationToken ct = default)
        {
            // Read-only loads: the writes go through ClosePendingAsync's
            // conditional UPDATEs, and nothing here must be left tracked for
            // a later SaveChanges (e.g. the audit service's) to write back.
            var request = await _db.QuoteApprovalRequests.AsNoTracking()
                .FirstOrDefaultAsync(r => r.QuoteId == quoteId && r.TenantId == tenantId &&
                                          r.Status == QuoteApprovalRequestStatus.Pending, ct)
                ?? throw new InvalidOperationException("There is no approval request waiting on this quote.");

            var me = await _currentUser.GetCurrentUserAsync();

            if (request.RequestedByUserId == me.UserId)
                throw new UnauthorizedAccessException("You can't approve your own request.");

            var approvers = await _engine.ApproversAsync(tenantId, quoteId, request.RequestedByUserId, ct);
            if (!approvers.Any(a => a.UserId == me.UserId))
                throw new UnauthorizedAccessException("You're not one of the approvers for this quote.");

            var cleanComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
            if (cleanComment?.Length > 1000) cleanComment = cleanComment[..1000];

            if (!approve && cleanComment == null)
                throw new InvalidOperationException("Say what needs to change, so the rep knows what to fix.");

            var quote = await _db.Quotes.AsNoTracking()
                .Where(q => q.Id == quoteId && q.TenantId == tenantId && !q.IsDeleted)
                .Select(q => new { q.Number, q.Status })
                .FirstOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException($"Quote {quoteId} not found");

            if (quote.Status != QuoteStatus.PendingApproval)
                throw new InvalidOperationException("This quote is no longer waiting for approval.");

            var newRequestStatus = approve ? QuoteApprovalRequestStatus.Approved : QuoteApprovalRequestStatus.ChangesRequested;
            var newQuoteStatus = approve ? QuoteStatus.Approved : QuoteStatus.Draft;

            // Two approvers clicking at once, or an approve racing a recall:
            // both UPDATEs only match while the row is still pending, so
            // exactly one decision wins and the other gets a clear message.
            await QuoteApprovalWrites.ClosePendingAsync(
                _db, tenantId, quoteId, request.Id, newRequestStatus, newQuoteStatus,
                me.UserId, me.FullName, cleanComment, ct);

            await _audit.WriteAsync(
                AuditAction.QuoteStatusChanged, AuditEntityType.Quote, quoteId, tenantId,
                new
                {
                    number = quote.Number,
                    from = QuoteStatus.PendingApproval.ToString(),
                    to = newQuoteStatus.ToString(),
                    via = approve ? "Approved" : "ChangesRequested",
                    requestedBy = request.RequestedByName,
                    comment = cleanComment
                },
                ct);

            _logger.LogInformation("Quote {Number} {Decision} by {User}",
                quote.Number, approve ? "approved" : "sent back for changes", me.FullName);
        }
    }

    // =================================================================
    // RECALL
    // =================================================================

    public class RecallQuoteApprovalHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;
        private readonly IAuditService _audit;

        public RecallQuoteApprovalHandler(FlowDbContext db, ICurrentUserService currentUser, IAuditService audit)
        {
            _db = db;
            _currentUser = currentUser;
            _audit = audit;
        }

        public async Task Handle(Guid tenantId, Guid quoteId, CancellationToken ct = default)
        {
            // Read-only loads: the writes go through ClosePendingAsync's
            // conditional UPDATEs, and nothing here must be left tracked for
            // a later SaveChanges (e.g. the audit service's) to write back.
            var request = await _db.QuoteApprovalRequests.AsNoTracking()
                .FirstOrDefaultAsync(r => r.QuoteId == quoteId && r.TenantId == tenantId &&
                                          r.Status == QuoteApprovalRequestStatus.Pending, ct)
                ?? throw new InvalidOperationException("There is no approval request waiting on this quote.");

            var me = await _currentUser.GetCurrentUserAsync();
            if (request.RequestedByUserId != me.UserId && !me.IsTenantAdmin)
                throw new UnauthorizedAccessException("Only the person who asked for approval (or an admin) can recall it.");

            var number = await _db.Quotes.AsNoTracking()
                .Where(q => q.Id == quoteId && q.TenantId == tenantId && !q.IsDeleted)
                .Select(q => q.Number)
                .FirstOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException($"Quote {quoteId} not found");

            await QuoteApprovalWrites.ClosePendingAsync(
                _db, tenantId, quoteId, request.Id, QuoteApprovalRequestStatus.Recalled, QuoteStatus.Draft,
                me.UserId, me.FullName, comment: null, ct);

            await _audit.WriteAsync(
                AuditAction.QuoteStatusChanged, AuditEntityType.Quote, quoteId, tenantId,
                new { number, from = QuoteStatus.PendingApproval.ToString(), to = QuoteStatus.Draft.ToString(), via = "Recalled" },
                ct);
        }
    }

    // =================================================================
    // WAITING FOR MY APPROVAL
    // =================================================================

    public class GetPendingQuoteApprovalsHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;

        public GetPendingQuoteApprovalsHandler(FlowDbContext db, ICurrentUserService currentUser)
        {
            _db = db;
            _currentUser = currentUser;
        }

        /// <summary>
        /// Requests the current user can decide. Same rule as
        /// QuoteApprovalEngine.ApproversAsync, done in bulk:
        ///   admin   → every pending request except their own
        ///   manager → requests whose deal owner (or, for unassigned deals,
        ///             whose requester) is in a team they manage
        /// </summary>
        public async Task<List<PendingQuoteApprovalDto>> Handle(Guid tenantId, CancellationToken ct = default)
        {
            var me = await _currentUser.GetCurrentUserAsync();

            var rows = await _db.QuoteApprovalRequests.AsNoTracking()
                .Where(r => r.TenantId == tenantId && r.Status == QuoteApprovalRequestStatus.Pending &&
                            r.RequestedByUserId != me.UserId)
                .OrderBy(r => r.RequestedAtUtc)
                .Select(r => new
                {
                    Request = r,
                    QuoteNumber = r.Quote!.Number,
                    DealTitle = r.Quote.Deal.Title,
                    Owner = r.Quote.Deal.OwnerUserId,
                    ContactFirst = r.Quote.Deal.Contact != null ? r.Quote.Deal.Contact.FirstName : null,
                    ContactLast = r.Quote.Deal.Contact != null ? r.Quote.Deal.Contact.LastName : null
                })
                .ToListAsync(ct);

            if (rows.Count == 0) return new List<PendingQuoteApprovalDto>();

            if (!me.IsTenantAdmin)
            {
                var managed = await _db.TeamManagers.AsNoTracking()
                    .Where(m => m.UserId == me.UserId)
                    .Select(m => m.TeamId)
                    .ToListAsync(ct);

                if (managed.Count == 0) return new List<PendingQuoteApprovalDto>();

                var members = (await _db.Users.AsNoTracking()
                        .Where(u => u.TenantId == tenantId && !u.IsDeleted &&
                                    u.TeamId.HasValue && managed.Contains(u.TeamId.Value))
                        .Select(u => u.Id)
                        .ToListAsync(ct))
                    .ToHashSet();

                rows = rows
                    .Where(x => Guid.TryParse(x.Owner, out var ownerId)
                        ? members.Contains(ownerId)
                        : members.Contains(x.Request.RequestedByUserId))
                    .ToList();
            }

            return rows.Select(x => new PendingQuoteApprovalDto(
                    x.Request.Id,
                    x.Request.QuoteId,
                    x.QuoteNumber,
                    x.DealTitle,
                    string.Join(" ", new[] { x.ContactFirst, x.ContactLast }.Where(s => !string.IsNullOrWhiteSpace(s))),
                    x.Request.RequestedByName,
                    x.Request.RequestedAtUtc,
                    x.Request.RequestComment,
                    x.Request.QuoteTotal,
                    x.Request.Currency,
                    x.Request.MaxLineDiscountPercent,
                    QuoteApprovalEngine.SplitReasons(x.Request.Reasons)))
                .ToList();
        }
    }

    // =================================================================
    // CONCURRENCY-SAFE CLOSE
    // =================================================================

    internal static class QuoteApprovalWrites
    {
        /// <summary>
        /// Closes a pending request and moves the quote out of
        /// PendingApproval — both as conditional UPDATEs in one transaction.
        /// If either row has already moved on (someone else decided,
        /// recalled, or it was sent), nothing changes and the caller gets
        /// "no longer waiting". Runs inside the context's execution strategy,
        /// so it works with or without EnableRetryOnFailure.
        /// </summary>
        public static async Task ClosePendingAsync(
            FlowDbContext db, Guid tenantId, Guid quoteId, Guid requestId,
            string newRequestStatus, QuoteStatus newQuoteStatus,
            Guid byUserId, string byName, string? comment, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var strategy = db.Database.CreateExecutionStrategy();

            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);

                var closed = await db.QuoteApprovalRequests
                    .Where(r => r.Id == requestId && r.TenantId == tenantId &&
                                r.Status == QuoteApprovalRequestStatus.Pending)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(r => r.Status, newRequestStatus)
                        .SetProperty(r => r.DecidedByUserId, (Guid?)byUserId)
                        .SetProperty(r => r.DecidedByName, byName)
                        .SetProperty(r => r.DecidedAtUtc, (DateTime?)now)
                        .SetProperty(r => r.DecisionComment, comment), ct);

                if (closed == 0)
                    throw new InvalidOperationException("Someone else has already dealt with this approval request. Refresh to see where it stands.");

                var moved = await db.Quotes
                    .Where(q => q.Id == quoteId && q.TenantId == tenantId && !q.IsDeleted &&
                                q.Status == QuoteStatus.PendingApproval)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(q => q.Status, newQuoteStatus)
                        .SetProperty(q => q.UpdatedAtUtc, (DateTime?)now)
                        .SetProperty(q => q.UpdatedBy, byName), ct);

                if (moved == 0)
                    throw new InvalidOperationException("This quote is no longer waiting for approval. Refresh to see where it stands.");

                await tx.CommitAsync(ct);
            });
        }
    }
}
