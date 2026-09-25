// =====================================================================
// QuoteApprovalHandlers.cs
// Location: MerkaiTrial.Application/Commands/Quotes/QuoteApprovalHandlers.cs
//
// COMPLETE FILE — replaces the 017/018 version.
//
// WHAT CHANGED (027)
//   Before: ONE setting per workspace (a discount limit and a total
//   limit) and ONE approver step, worked out at run time as "managers of
//   the deal owner's team, plus workspace admins". You could not create a
//   rule, you could not name a role as the approver, and a two-level
//   sign-off was impossible.
//
//   Now: named ApprovalRules with conditions, FIRST MATCH WINS in
//   SortOrder, each with an ordered chain of ApprovalSteps. A step names
//   who signs it — the deal owner's team managers (as before), everyone
//   holding a role, one named person, or any workspace admin. Step 2 is
//   only asked once step 1 approves. Every decision writes its own
//   QuoteApprovalDecision row, so the history shows the whole chain.
//
// WHAT DELIBERATELY DID NOT CHANGE
//   • The discount maths. LineDiscountPercent is byte-for-byte what it
//     was, including measuring catalog lines against LIST price so a
//     lower unit price can't dodge the limit.
//   • ApproversForDealAsync. Round 018's manual invoices and round 022's
//     pipeline transitions both call it; its signature and behaviour are
//     untouched.
//   • Every 017 DTO. QuoteApprovalStateDto, QuoteApprovalRequestDto,
//     PendingQuoteApprovalDto and friends are constructed with exactly
//     the same arguments in the same order, so Pages/Quotes/Detail.cshtml.cs
//     and anything else holding them compiles unchanged. The chain is
//     exposed through the NEW records in ApprovalChainDtos.cs.
//   • QuoteApprovalRules.Evaluate(settings, lines, total, currency) — the
//     old four-argument form is still here and still does exactly what it
//     did, in case something outside this file calls it. Nothing in the
//     quote path does any more.
//
// WHO CAN DECIDE A STEP
//   The people the step names — and any workspace admin, always. That
//   last part is the escape hatch that stops a quote getting stuck behind
//   somebody on leave; when it is used, the decision is stored with
//   WasAdminOverride = true so the history says "signed by the admin on
//   the Senior Manager's step" rather than pretending otherwise.
//   Nobody signs their own request unless the step allows it.
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

    /// <summary>
    /// The verdict on one quote. The last three are 027 additions and are
    /// optional, so any existing construction site still compiles.
    /// </summary>
    public sealed record QuoteRuleResult(
        bool RulesEnabled,
        bool RequiresApproval,
        List<string> Reasons,
        decimal MaxLineDiscountPercent,
        Guid? RuleId = null,
        string? RuleName = null,
        int TotalSteps = 1);

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

        /// <summary>
        /// LEGACY (017). The single-limit evaluation, kept verbatim in case
        /// anything outside this file still calls it. The quote path uses
        /// <see cref="RuleMatcher.Evaluate"/> instead.
        /// </summary>
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

    /// <summary>
    /// 027. Which rule, if any, this quote trips — and why, in words the
    /// rep can act on.
    ///
    /// FIRST MATCH WINS, in SortOrder. One quote is never governed by two
    /// rules at once: an "all matching rules combine" model produces
    /// chains nobody can predict from looking at the settings page, which
    /// is exactly the confusion this round exists to remove. The page says
    /// so, and orders the list so the tightest rule sits first.
    /// </summary>
    public static class RuleMatcher
    {
        public static bool Matches(ApprovalRule rule, decimal maxLineDiscountPct, decimal grandTotal)
        {
            var hasDiscount = rule.DiscountOverPercent.HasValue;
            var hasTotal = rule.TotalOverAmount.HasValue;

            // No condition at all = a catch-all: every quote needs this
            // chain. Legitimate, and flagged loudly on the settings page
            // because it is also what an unfinished rule looks like.
            if (!hasDiscount && !hasTotal) return true;

            var discountHit = hasDiscount && maxLineDiscountPct > rule.DiscountOverPercent!.Value;
            var totalHit = hasTotal && grandTotal > rule.TotalOverAmount!.Value;

            return rule.ConditionMode == ApprovalConditionMode.All
                ? (!hasDiscount || discountHit) && (!hasTotal || totalHit)
                : discountHit || totalHit;
        }

        public static QuoteRuleResult Evaluate(
            QuoteApprovalSettings settings,
            IReadOnlyList<ApprovalRule> activeRulesInOrder,
            IReadOnlyList<QuoteRuleLine> lines,
            decimal grandTotal,
            string currency)
        {
            var maxPct = lines.Count == 0 ? 0 : lines.Max(QuoteApprovalRules.LineDiscountPercent);

            if (!settings.IsEnabled)
                return new QuoteRuleResult(false, false, new List<string>(), maxPct);

            var rule = activeRulesInOrder.FirstOrDefault(r => Matches(r, maxPct, grandTotal));

            if (rule == null)
                return new QuoteRuleResult(true, false, new List<string>(), maxPct);

            var reasons = Explain(rule, lines, grandTotal, currency);

            // A rule with no step could never be approved. Treating it as
            // "one step" lets the engine fall back to the team managers it
            // always used, so a half-built rule slows a quote down instead
            // of stranding it. The settings page shouts about it separately.
            var steps = Math.Max(1, rule.Steps.Count);

            return new QuoteRuleResult(true, true, reasons, maxPct, rule.Id, rule.Name, steps);
        }

        /// <summary>Why this rule caught this quote, at most four lines.</summary>
        public static List<string> Explain(
            ApprovalRule rule, IReadOnlyList<QuoteRuleLine> lines, decimal grandTotal, string currency)
        {
            var inv = CultureInfo.InvariantCulture;
            var reasons = new List<string>();

            if (rule.DiscountOverPercent is decimal limit)
            {
                var over = lines
                    .Select(l => (l.Name, Pct: QuoteApprovalRules.LineDiscountPercent(l)))
                    .Where(x => x.Pct > limit)
                    .OrderByDescending(x => x.Pct)
                    .ToList();

                foreach (var x in over.Take(3))
                    reasons.Add($"\"{x.Name}\" is {x.Pct.ToString("0.##", inv)}% below list price (limit {limit.ToString("0.##", inv)}%).");

                if (over.Count > 3)
                    reasons.Add($"…and {over.Count - 3} more line(s) over the {limit.ToString("0.##", inv)}% limit.");
            }

            if (rule.TotalOverAmount is decimal maxTotal && grandTotal > maxTotal)
                reasons.Add($"Total {currency} {grandTotal.ToString("N2", inv)} is above the {currency} {maxTotal.ToString("N2", inv)} limit.");

            // A catch-all rule, or an "All" rule whose individual limits
            // each read as met but produced no line above — say something
            // rather than showing an empty box.
            if (reasons.Count == 0)
                reasons.Add($"The rule \"{rule.Name}\" applies to this quote.");

            return reasons;
        }

        /// <summary>"A line more than 10% below list, or a total above ฿500,000".</summary>
        public static string Describe(ApprovalRule rule, string currencySymbol)
        {
            var inv = CultureInfo.InvariantCulture;
            var parts = new List<string>();

            if (rule.DiscountOverPercent is decimal d)
                parts.Add($"a line more than {d.ToString("0.##", inv)}% below list price");

            if (rule.TotalOverAmount is decimal t)
                parts.Add($"a total above {currencySymbol}{t.ToString("N0", inv)}");

            if (parts.Count == 0) return "every quote";
            if (parts.Count == 1) return parts[0];

            return rule.ConditionMode == ApprovalConditionMode.All
                ? string.Join(" and ", parts)
                : string.Join(" or ", parts);
        }
    }

    // =================================================================
    // WORKFLOW (pure) — unchanged from 017
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
    // ENGINE — settings, rules, evaluation, approvers, access
    // =================================================================

    public sealed record QuoteApprover(Guid UserId, string Name, bool IsAdmin);

    /// <summary>A resolved step: the step itself plus who it comes to.</summary>
    public sealed record ResolvedStep(
        ApprovalStep? Step,
        int StepOrder,
        string StepName,
        string ApproverSummary,
        List<QuoteApprover> Approvers);

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

        // ── settings and rules ────────────────────────────────────────

        /// <summary>
        /// The tenant's row, or the defaults when it has never saved one.
        /// IsDefault matters: the defaults carry a property-initialised
        /// UpdatedAtUtc that never meant anything, and showing it renders
        /// "last changed by  on <today>" to a customer on their first visit.
        /// </summary>
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

        /// <summary>Active rules with their steps, in the order they are tried.</summary>
        public async Task<List<ApprovalRule>> GetActiveRulesAsync(Guid tenantId, CancellationToken ct = default)
            => await _db.ApprovalRules.AsNoTracking()
                .Where(r => r.TenantId == tenantId && r.IsActive)
                .Include(r => r.Steps)
                .OrderBy(r => r.SortOrder).ThenBy(r => r.Name)
                .ToListAsync(ct);

        /// <summary>One rule with its steps, active or not. Null when it has been deleted.</summary>
        public async Task<ApprovalRule?> GetRuleAsync(Guid tenantId, Guid ruleId, CancellationToken ct = default)
            => await _db.ApprovalRules.AsNoTracking()
                .Where(r => r.TenantId == tenantId && r.Id == ruleId)
                .Include(r => r.Steps)
                .FirstOrDefaultAsync(ct);

        // ── evaluation ────────────────────────────────────────────────

        /// <summary>Does the quote AS IT IS NOW trip a rule?</summary>
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
        /// Signature unchanged from 018; the logic behind it is now
        /// rule-based, so an invoice is measured against the same chain a
        /// quote would be.
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
            var rules = await GetActiveRulesAsync(tenantId, ct);

            return RuleMatcher.Evaluate(settings, rules, lines, grandTotal, currency);
        }

        // ── approvers ─────────────────────────────────────────────────

        /// <summary>
        /// UNCHANGED FROM 018. Who signs off for a deal under the original
        /// model: managers of the deal owner's team (or, when the deal has
        /// no owner, of <paramref name="teamFallbackUserId"/>'s team), plus
        /// every active workspace admin — minus
        /// <paramref name="excludeUserId"/>.
        ///
        /// Round 018's manual invoices and round 022's pipeline transitions
        /// both call this. It stays exactly as it was, and it is also what
        /// an ApproverKind.TeamManagers step resolves to.
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
                .Select(u => new
                {
                    u.Id,
                    First = u.FirstName ?? string.Empty,
                    Last = u.LastName ?? string.Empty,
                    Email = u.Email ?? string.Empty,
                    u.IsTenantAdmin
                })
                .ToListAsync(ct);

            return people
                .Where(u => u.Id != requesterId)
                .OrderBy(u => u.IsTenantAdmin)          // managers first
                .ThenBy(u => u.First)
                .Select(u => new QuoteApprover(u.Id, DisplayName(u.First, u.Last, u.Email), u.IsTenantAdmin))
                .ToList();
        }

        /// <summary>
        /// Who is asked to sign ONE step. A step naming a role or a person
        /// resolves to exactly those people — workspace admins are NOT
        /// folded in here, because "the Senior Manager signs it" should
        /// mean what it says. Admins can still decide any step (see
        /// DecideQuoteApprovalHandler); that is recorded as an override.
        /// </summary>
        public async Task<ResolvedStep> ResolveStepAsync(
            Guid tenantId, Guid? dealId, ApprovalStep? step, int stepOrder, Guid? requesterId, CancellationToken ct = default)
        {
            // No step at all — a rule saved without one, or a request that
            // predates 027. Fall back to what the app always did, so
            // nothing is ever stranded.
            if (step == null)
            {
                var fallback = await ApproversForDealAsync(tenantId, dealId, requesterId, requesterId, ct);
                return new ResolvedStep(null, stepOrder, $"Step {stepOrder}",
                    "The deal owner's team managers, and workspace admins", fallback);
            }

            var exclude = step.AllowSelfApproval ? (Guid?)null : requesterId;
            List<QuoteApprover> approvers;
            string summary;

            switch (step.ApproverKind)
            {
                case ApproverKind.Role:
                {
                    // Hoisted into a plain local before it goes near an
                    // expression tree: EF translates a captured local
                    // cleanly, while member access on a captured ENTITY is
                    // the kind of thing that quietly becomes client-side
                    // evaluation or a translation failure.
                    var roleId = step.ApproverRoleId;

                    var roleName = roleId.HasValue
                        ? await _db.Roles.AsNoTracking()
                            .Where(r => r.Id == roleId.Value && !r.IsDeleted)
                            .Select(r => r.DisplayName ?? string.Empty)
                            .FirstOrDefaultAsync(ct)
                        : null;

                    if (roleId.HasValue)
                    {
                        var id = roleId.Value;
                        approvers = await PeopleAsync(tenantId, exclude,
                            u => u.UserRoles.Any(ur => ur.RoleId == id), ct);
                    }
                    else
                    {
                        approvers = new List<QuoteApprover>();
                    }

                    summary = roleName == null
                        ? "a role that no longer exists"
                        : $"everyone with the role \"{roleName}\"";
                    break;
                }

                case ApproverKind.User:
                {
                    var userId = step.ApproverUserId;

                    if (userId.HasValue)
                    {
                        var id = userId.Value;
                        approvers = await PeopleAsync(tenantId, exclude, u => u.Id == id, ct);
                    }
                    else
                    {
                        approvers = new List<QuoteApprover>();
                    }

                    summary = approvers.Count > 0
                        ? approvers[0].Name
                        : "somebody who is no longer active here";
                    break;
                }

                case ApproverKind.WorkspaceAdmin:
                {
                    approvers = await PeopleAsync(tenantId, exclude, u => u.IsTenantAdmin, ct);
                    summary = "any workspace admin";
                    break;
                }

                default:   // ApproverKind.TeamManagers
                {
                    approvers = await ApproversForDealAsync(tenantId, dealId, exclude, requesterId, ct);
                    summary = "the deal owner's team managers, and workspace admins";
                    break;
                }
            }

            return new ResolvedStep(step, step.StepOrder, step.EffectiveName, summary, approvers);
        }

        /// <summary>
        /// The step a quote is waiting on right now — or, when nothing is
        /// pending, step 1 of whichever rule the quote would trip.
        /// </summary>
        public async Task<ResolvedStep?> CurrentStepAsync(
            Guid tenantId, Guid quoteId, Guid? requesterId, CancellationToken ct = default)
        {
            var quote = await _db.Quotes.AsNoTracking()
                .Where(q => q.Id == quoteId && q.TenantId == tenantId)
                .Select(q => new { DealId = (Guid?)q.DealId })
                .FirstOrDefaultAsync(ct);

            if (quote == null) return null;

            var pending = await _db.QuoteApprovalRequests.AsNoTracking()
                .Where(r => r.QuoteId == quoteId && r.TenantId == tenantId &&
                            r.Status == QuoteApprovalRequestStatus.Pending)
                .Select(r => new { r.ApprovalRuleId, r.CurrentStepOrder, r.RequestedByUserId })
                .FirstOrDefaultAsync(ct);

            if (pending != null)
            {
                var step = pending.ApprovalRuleId.HasValue
                    ? await _db.ApprovalSteps.AsNoTracking()
                        .FirstOrDefaultAsync(s => s.TenantId == tenantId &&
                                                  s.ApprovalRuleId == pending.ApprovalRuleId.Value &&
                                                  s.StepOrder == pending.CurrentStepOrder, ct)
                    : null;

                return await ResolveStepAsync(
                    tenantId, quote.DealId, step, pending.CurrentStepOrder, pending.RequestedByUserId, ct);
            }

            // Nothing pending: what WOULD happen if it were submitted now.
            var verdict = await EvaluateAsync(tenantId, quoteId, ct);
            if (!verdict.RequiresApproval || verdict.RuleId == null) return null;

            var first = await _db.ApprovalSteps.AsNoTracking()
                .Where(s => s.TenantId == tenantId && s.ApprovalRuleId == verdict.RuleId.Value)
                .OrderBy(s => s.StepOrder)
                .FirstOrDefaultAsync(ct);

            return await ResolveStepAsync(tenantId, quote.DealId, first, first?.StepOrder ?? 1, requesterId, ct);
        }

        /// <summary>
        /// 017 compatibility. "Who can approve this quote" now means "who
        /// is being asked at the step it is waiting on" — which is the same
        /// answer as before for a single-step chain.
        /// </summary>
        public async Task<List<QuoteApprover>> ApproversAsync(
            Guid tenantId, Guid quoteId, Guid? requesterId, CancellationToken ct = default)
        {
            var step = await CurrentStepAsync(tenantId, quoteId, requesterId, ct);
            return step?.Approvers ?? new List<QuoteApprover>();
        }

        /// <summary>Active workspace admins, minus one person. The always-available fallback.</summary>
        public Task<List<QuoteApprover>> AdminsAsync(Guid tenantId, Guid? exclude, CancellationToken ct = default)
            => PeopleAsync(tenantId, exclude, u => u.IsTenantAdmin, ct);

        private async Task<List<QuoteApprover>> PeopleAsync(
            Guid tenantId,
            Guid? exclude,
            System.Linq.Expressions.Expression<Func<User, bool>> predicate,
            CancellationToken ct)
        {
            // Users is not tenant-filtered — the TenantId predicate IS the
            // isolation. Names are coalesced inside the projection: a NULL
            // column otherwise throws SqlNullValueException in the reader.
            var people = await _db.Users.AsNoTracking()
                .Where(u => u.TenantId == tenantId && !u.IsDeleted && u.IsActive)
                .Where(predicate)
                .Select(u => new
                {
                    u.Id,
                    First = u.FirstName ?? string.Empty,
                    Last = u.LastName ?? string.Empty,
                    Email = u.Email ?? string.Empty,
                    u.IsTenantAdmin
                })
                .ToListAsync(ct);

            return people
                .Where(u => u.Id != exclude)
                .OrderBy(u => u.First).ThenBy(u => u.Last)
                .Select(u => new QuoteApprover(u.Id, DisplayName(u.First, u.Last, u.Email), u.IsTenantAdmin))
                .ToList();
        }

        internal static string DisplayName(string? first, string? last, string? email)
        {
            var name = $"{first ?? string.Empty} {last ?? string.Empty}".Trim();
            if (name.Length > 0) return name;
            return string.IsNullOrWhiteSpace(email) ? "(no name)" : email!;
        }

        // ── access ────────────────────────────────────────────────────

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
        /// plus the people asked to approve the step it is waiting on, and
        /// anyone who has already decided a step on it. A manager whose own
        /// visibility is "Own" can still open the quote they were asked to
        /// approve.
        /// </summary>
        public async Task<bool> CanReadQuoteAsync(Guid tenantId, Guid quoteId, CancellationToken ct = default)
        {
            if (await CanWriteQuoteAsync(tenantId, quoteId, ct)) return true;

            var me = _currentUser.GetCurrentUserId();

            var requests = await _db.QuoteApprovalRequests.AsNoTracking()
                .Where(r => r.QuoteId == quoteId && r.TenantId == tenantId)
                .Select(r => new { r.Id, r.Status, r.RequestedByUserId, r.DecidedByUserId })
                .ToListAsync(ct);

            if (requests.Count == 0) return false;

            if (requests.Any(r => r.DecidedByUserId == me)) return true;

            // 027: somebody who signed an earlier step keeps their view of
            // the quote even though they are no longer the current approver.
            var requestIds = requests.Select(r => r.Id).ToList();
            if (await _db.QuoteApprovalDecisions.AsNoTracking()
                    .AnyAsync(d => d.TenantId == tenantId && requestIds.Contains(d.RequestId) && d.DecidedByUserId == me, ct))
                return true;

            var pending = requests.FirstOrDefault(r => r.Status == QuoteApprovalRequestStatus.Pending);
            if (pending == null) return false;

            // A workspace admin can decide any step, so they can see it.
            var who = await _currentUser.GetCurrentUserAsync();
            if (who.IsTenantAdmin) return true;

            var step = await CurrentStepAsync(tenantId, quoteId, pending.RequestedByUserId, ct);
            return step?.Approvers.Any(a => a.UserId == me) == true;
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
    // SETTINGS — the master switch and the legacy limits
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

    /// <summary>
    /// 027: this now saves the MASTER SWITCH only. The limits moved to
    /// ApprovalRules, and the two legacy columns are left exactly as they
    /// are so the record of where a workspace's first rule came from
    /// survives.
    /// </summary>
    public class SaveQuoteApprovalSettingsHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly IAuditService _audit;

        public SaveQuoteApprovalSettingsHandler(FlowDbContext db, IAuditService audit)
        {
            _db = db;
            _audit = audit;
        }

        public async Task Handle(Guid tenantId, bool isEnabled, string updatedBy, CancellationToken ct = default)
        {
            var row = await _db.QuoteApprovalSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
            if (row == null)
            {
                row = new QuoteApprovalSettings
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    MaxDiscountPercent = null,
                    MaxQuoteTotal = null
                };
                _db.QuoteApprovalSettings.Add(row);
            }

            if (row.IsEnabled == isEnabled) return;

            row.IsEnabled = isEnabled;
            row.UpdatedAtUtc = DateTime.UtcNow;
            row.UpdatedBy = updatedBy;

            await _db.SaveChangesAsync(ct);

            await _audit.WriteAsync(
                "QuoteApprovalsToggled", "QuoteApprovalSettings", row.Id, tenantId,
                new { isEnabled, by = updatedBy }, ct);
        }

        /// <summary>
        /// LEGACY (017) entry point, kept so an older caller still
        /// compiles. Only the master switch is applied; the limits it
        /// carries are ignored, because rules decide those now.
        /// </summary>
        public Task Handle(Guid tenantId, SaveQuoteApprovalSettingsDto dto, string updatedBy, CancellationToken ct = default)
            => Handle(tenantId, dto.IsEnabled, updatedBy, ct);
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

            var step = await _engine.CurrentStepAsync(
                tenantId, quoteId, pending?.RequestedByUserId ?? me.UserId, ct);

            var approvers = step?.Approvers ?? new List<QuoteApprover>();

            var isExempt = me.IsTenantAdmin;
            var preSend = QuoteWorkflow.IsPreSend(status);

            // Somebody has to be able to sign it. The named approvers, or —
            // failing that — a workspace admin, who can always decide.
            var anyAdmin = await _engine.AdminsAsync(tenantId, pending?.RequestedByUserId ?? me.UserId, ct);
            var someoneCanDecide = approvers.Count > 0 || anyAdmin.Count > 0;

            var canSubmit = canWrite && !isExempt && preSend && rules.RequiresApproval && pending == null
                            && someoneCanDecide;

            // Admins can decide any step. That is the escape hatch, and it
            // is recorded as an override when used. Nobody decides their own
            // request unless the step explicitly allows it — which a
            // one-person workspace needs, or its quotes can never be sent.
            var allowsSelf = step?.Step?.AllowSelfApproval == true;

            var canApprove = pending != null &&
                             (approvers.Any(a => a.UserId == me.UserId) || me.IsTenantAdmin) &&
                             (pending.RequestedByUserId != me.UserId || allowsSelf);

            var canRecall = pending != null && (pending.RequestedByUserId == me.UserId || me.IsTenantAdmin);
            var canSend = canWrite && (status == QuoteStatus.Approved || (preSend && (!rules.RequiresApproval || isExempt)));

            // Names shown to the rep — PEOPLE ONLY.
            //
            // An earlier draft prefixed this list with "[Step 2 of 3 — …]" so
            // the panel would show the chain. That was wrong twice over:
            // _QuoteApprovalPanel.cshtml branches on ApproverNames being EMPTY
            // to say "nobody is set up to approve this quote", and a label in
            // the list made that honest message unreachable — and it rendered
            // "can approve: [Step 2 of 3 — …], Priya" to the user. The step
            // context belongs on the approvals queue, which has fields for it.
            var names = approvers
                .Select(a => a.IsAdmin ? $"{a.Name} (admin)" : a.Name)
                .ToList();

            // Same 14 arguments, same order, as 017 — Detail.cshtml.cs and
            // _QuoteApprovalPanel.cshtml are untouched by this round.
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
                names,
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

            var verdict = await _engine.EvaluateAsync(tenantId, quoteId, ct);
            if (!verdict.RequiresApproval)
                throw new InvalidOperationException("This quote is within your workspace's limits, so it doesn't need approval. You can send it.");

            var step = await _engine.CurrentStepAsync(tenantId, quoteId, me.UserId, ct);
            var approvers = step?.Approvers ?? new List<QuoteApprover>();

            if (approvers.Count == 0)
            {
                // Nobody named — but an admin can always decide, so only
                // refuse when there is not even one of those.
                var admins = await _engine.AdminsAsync(tenantId, me.UserId, ct);
                if (admins.Count == 0)
                    throw new InvalidOperationException(
                        $"Nobody can approve this quote. The rule \"{verdict.RuleName}\" asks for {step?.ApproverSummary ?? "an approver"}, " +
                        "and there is nobody here who fits. Ask your workspace admin to check the approval rules.");
            }

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
                Reasons = string.Join("\n", verdict.Reasons),
                QuoteTotal = quote.GrandTotal,
                MaxLineDiscountPercent = verdict.MaxLineDiscountPercent,
                Currency = quote.Currency ?? string.Empty,

                // The chain, snapshotted. Editing the rule tomorrow must not
                // move the goalposts for a request already in flight.
                ApprovalRuleId = verdict.RuleId,
                RuleName = verdict.RuleName,
                CurrentStepOrder = step?.StepOrder ?? 1,
                TotalSteps = Math.Max(1, verdict.TotalSteps)
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
                new
                {
                    number = quote.Number,
                    from = from.ToString(),
                    to = quote.Status.ToString(),
                    via = "SubmitForApproval",
                    rule = verdict.RuleName,
                    steps = request.TotalSteps,
                    firstStep = step?.StepName,
                    reasons = verdict.Reasons
                },
                ct);

            _logger.LogInformation(
                "Quote {Number} submitted for approval by {User} — rule {Rule}, {Steps} step(s)",
                quote.Number, me.FullName, verdict.RuleName, request.TotalSteps);
        }
    }

    // =================================================================
    // APPROVE / REQUEST CHANGES — walks the chain
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

        /// <summary>
        /// Returns what happened, so the page can say "approved — now with
        /// Anand for step 2 of 3" instead of the flatly wrong "the rep can
        /// send it now". Returning a value where 017 returned void is
        /// source-compatible with a caller that ignores the result.
        /// </summary>
        public async Task<ApprovalOutcomeDto> Handle(
            Guid tenantId, Guid quoteId, bool approve, string? comment, CancellationToken ct = default)
        {
            // Read-only loads: the writes go through conditional UPDATEs,
            // and nothing here must be left tracked for a later SaveChanges
            // (e.g. the audit service's) to write back.
            var request = await _db.QuoteApprovalRequests.AsNoTracking()
                .FirstOrDefaultAsync(r => r.QuoteId == quoteId && r.TenantId == tenantId &&
                                          r.Status == QuoteApprovalRequestStatus.Pending, ct)
                ?? throw new InvalidOperationException("There is no approval request waiting on this quote.");

            var me = await _currentUser.GetCurrentUserAsync();

            var step = await _engine.CurrentStepAsync(tenantId, quoteId, request.RequestedByUserId, ct);
            var approvers = step?.Approvers ?? new List<QuoteApprover>();
            var isNamed = approvers.Any(a => a.UserId == me.UserId);

            // A workspace admin can decide any step — the escape hatch for
            // an approver who has left or is on leave. Recorded as such.
            var isOverride = !isNamed && me.IsTenantAdmin;

            if (!isNamed && !isOverride)
                throw new UnauthorizedAccessException(
                    $"You're not one of the approvers for this step ({step?.ApproverSummary ?? "unknown"}).");

            // Self-approval is separate from being named: a step can allow
            // it, and a one-person workspace needs that.
            var allowsSelf = step?.Step?.AllowSelfApproval == true;
            if (request.RequestedByUserId == me.UserId && !allowsSelf)
                throw new UnauthorizedAccessException("You can't approve your own request.");

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

            var decidedStep = request.CurrentStepOrder;
            var isFinalStep = decidedStep >= request.TotalSteps;
            var advances = approve && !isFinalStep;

            var decision = new QuoteApprovalDecision
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                RequestId = request.Id,
                StepOrder = decidedStep,
                StepName = step?.StepName,
                Decision = approve ? QuoteApprovalDecisionKind.Approved : QuoteApprovalDecisionKind.ChangesRequested,
                DecidedByUserId = me.UserId,
                DecidedByName = me.FullName,
                DecidedAtUtc = DateTime.UtcNow,
                Comment = cleanComment,
                WasAdminOverride = isOverride
            };

            if (advances)
            {
                // Two approvers clicking at once: the UPDATE only matches
                // while the request is still pending AT THIS STEP, so
                // exactly one of them moves the chain on.
                await QuoteApprovalWrites.AdvanceStepAsync(
                    _db, tenantId, request.Id, decidedStep, decision, ct);
            }
            else
            {
                var newRequestStatus = approve
                    ? QuoteApprovalRequestStatus.Approved
                    : QuoteApprovalRequestStatus.ChangesRequested;

                var newQuoteStatus = approve ? QuoteStatus.Approved : QuoteStatus.Draft;

                await QuoteApprovalWrites.ClosePendingAsync(
                    _db, tenantId, quoteId, request.Id, decidedStep, newRequestStatus, newQuoteStatus,
                    me.UserId, me.FullName, cleanComment, decision, ct);
            }

            // ── what the next step looks like, for the message ─────────
            var nextApprovers = new List<string>();
            string? nextStepName = null;
            int? nextOrder = null;

            if (advances)
            {
                nextOrder = decidedStep + 1;

                var nextStep = request.ApprovalRuleId.HasValue
                    ? await _db.ApprovalSteps.AsNoTracking()
                        .FirstOrDefaultAsync(s => s.TenantId == tenantId &&
                                                  s.ApprovalRuleId == request.ApprovalRuleId.Value &&
                                                  s.StepOrder == nextOrder.Value, ct)
                    : null;

                var dealId = await _db.Quotes.AsNoTracking()
                    .Where(q => q.Id == quoteId && q.TenantId == tenantId)
                    .Select(q => (Guid?)q.DealId)
                    .FirstOrDefaultAsync(ct);

                var resolved = await _engine.ResolveStepAsync(
                    tenantId, dealId, nextStep, nextOrder.Value, request.RequestedByUserId, ct);

                nextStepName = resolved.StepName;
                nextApprovers = resolved.Approvers.Select(a => a.Name).ToList();
            }

            await _audit.WriteAsync(
                AuditAction.QuoteStatusChanged, AuditEntityType.Quote, quoteId, tenantId,
                new
                {
                    number = quote.Number,
                    from = QuoteStatus.PendingApproval.ToString(),
                    to = advances ? QuoteStatus.PendingApproval.ToString()
                                  : (approve ? QuoteStatus.Approved.ToString() : QuoteStatus.Draft.ToString()),
                    via = approve ? (advances ? "StepApproved" : "Approved") : "ChangesRequested",
                    rule = request.RuleName,
                    step = decidedStep,
                    ofSteps = request.TotalSteps,
                    stepName = step?.StepName,
                    adminOverride = isOverride,
                    requestedBy = request.RequestedByName,
                    comment = cleanComment
                },
                ct);

            _logger.LogInformation(
                "Quote {Number} step {Step}/{Total} {Decision} by {User}{Override}",
                quote.Number, decidedStep, request.TotalSteps,
                approve ? "approved" : "sent back for changes", me.FullName,
                isOverride ? " (admin override)" : "");

            var message = !approve
                ? $"Sent back to {request.RequestedByName} with your comment."
                : advances
                    ? $"Step {decidedStep} of {request.TotalSteps} approved. It now goes to {(nextApprovers.Count > 0 ? string.Join(", ", nextApprovers.Take(3)) : "a workspace admin")} for {nextStepName}."
                    : request.TotalSteps > 1
                        ? $"Final step approved — all {request.TotalSteps} steps are done. {request.RequestedByName} can send the quote now."
                        : $"Approved. {request.RequestedByName} can send the quote now.";

            return new ApprovalOutcomeDto(
                ChainComplete: approve && !advances,
                SentBack: !approve,
                StepJustDecided: decidedStep,
                TotalSteps: request.TotalSteps,
                NextStepOrder: nextOrder,
                NextStepName: nextStepName,
                NextApprovers: nextApprovers,
                Message: message);
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

            // A recall is a withdrawal, not a decision, so no
            // QuoteApprovalDecision row: the steps already approved stay in
            // the history exactly as they happened.
            await QuoteApprovalWrites.ClosePendingAsync(
                _db, tenantId, quoteId, request.Id, request.CurrentStepOrder,
                QuoteApprovalRequestStatus.Recalled, QuoteStatus.Draft,
                me.UserId, me.FullName, comment: null, decision: null, ct);

            await _audit.WriteAsync(
                AuditAction.QuoteStatusChanged, AuditEntityType.Quote, quoteId, tenantId,
                new
                {
                    number,
                    from = QuoteStatus.PendingApproval.ToString(),
                    to = QuoteStatus.Draft.ToString(),
                    via = "Recalled",
                    atStep = request.CurrentStepOrder,
                    ofSteps = request.TotalSteps
                },
                ct);
        }
    }

    // =================================================================
    // WAITING FOR MY APPROVAL
    // =================================================================

    /// <summary>
    /// Shared by both pending queries: which of these requests may I decide?
    /// </summary>
    internal sealed record PendingRow(
        QuoteApprovalRequest Request,
        string QuoteNumber,
        string DealTitle,
        string? Owner,
        string ContactName);

    /// <summary>
    /// 017 SHAPE, UNCHANGED. Still returns PendingQuoteApprovalDto, so the
    /// "waiting for me" badge on the Quotes list keeps working without a
    /// change to that page. The richer version is below.
    /// </summary>
    public class GetPendingQuoteApprovalsHandler : ICommandHandler
    {
        private readonly PendingApprovalReader _reader;
        public GetPendingQuoteApprovalsHandler(PendingApprovalReader reader) => _reader = reader;

        public async Task<List<PendingQuoteApprovalDto>> Handle(Guid tenantId, CancellationToken ct = default)
        {
            var rows = await _reader.ForMeAsync(tenantId, ct);

            return rows.Select(x => new PendingQuoteApprovalDto(
                    x.Row.Request.Id,
                    x.Row.Request.QuoteId,
                    x.Row.QuoteNumber,
                    x.Row.DealTitle,
                    x.Row.ContactName,
                    x.Row.Request.RequestedByName,
                    x.Row.Request.RequestedAtUtc,
                    x.Row.Request.RequestComment,
                    x.Row.Request.QuoteTotal,
                    x.Row.Request.Currency,
                    x.Row.Request.MaxLineDiscountPercent,
                    QuoteApprovalEngine.SplitReasons(x.Row.Request.Reasons)))
                .ToList();
        }
    }

    /// <summary>
    /// 027. The same queue with the chain context: which step, what it is
    /// called, who it names, whether I'm standing in as an admin, and what
    /// the earlier steps decided.
    /// </summary>
    public class GetPendingApprovalChainsHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly PendingApprovalReader _reader;

        public GetPendingApprovalChainsHandler(FlowDbContext db, PendingApprovalReader reader)
        {
            _db = db;
            _reader = reader;
        }

        public async Task<List<PendingApprovalChainDto>> Handle(Guid tenantId, CancellationToken ct = default)
        {
            var rows = await _reader.ForMeAsync(tenantId, ct);
            if (rows.Count == 0) return new List<PendingApprovalChainDto>();

            var requestIds = rows.Select(r => r.Row.Request.Id).ToList();

            var decisions = (await _db.QuoteApprovalDecisions.AsNoTracking()
                    .Where(d => d.TenantId == tenantId && requestIds.Contains(d.RequestId))
                    .OrderBy(d => d.StepOrder).ThenBy(d => d.DecidedAtUtc)
                    .ToListAsync(ct))
                .GroupBy(d => d.RequestId)
                .ToDictionary(g => g.Key, g => g.ToList());

            return rows.Select(x => new PendingApprovalChainDto(
                    x.Row.Request.Id,
                    x.Row.Request.QuoteId,
                    x.Row.QuoteNumber,
                    x.Row.DealTitle,
                    x.Row.ContactName,
                    x.Row.Request.RequestedByName,
                    x.Row.Request.RequestedAtUtc,
                    x.Row.Request.RequestComment,
                    x.Row.Request.QuoteTotal,
                    x.Row.Request.Currency,
                    x.Row.Request.MaxLineDiscountPercent,
                    QuoteApprovalEngine.SplitReasons(x.Row.Request.Reasons),
                    x.Row.Request.RuleName,
                    x.Row.Request.CurrentStepOrder,
                    x.Row.Request.TotalSteps,
                    x.StepName,
                    x.ApproverSummary,
                    x.IsAdminOverride,
                    decisions.GetValueOrDefault(x.Row.Request.Id, new List<QuoteApprovalDecision>())
                        .Select(d => new ApprovalDecisionDto(
                            d.StepOrder, d.StepName, d.Decision, d.DecidedByName,
                            d.DecidedAtUtc, d.Comment, d.WasAdminOverride))
                        .ToList()))
                .ToList();
        }
    }

    /// <summary>
    /// Works out which pending requests the signed-in user may decide,
    /// for every approver kind, without a query per request.
    ///
    ///   WorkspaceAdmin step → me, if I'm an admin
    ///   User step           → me, if it names me
    ///   Role step           → me, if I hold that role
    ///   TeamManagers step   → the deal owner (or requester) is in a team
    ///                         I manage — the 017 rule, in bulk
    ///   any step            → me, if I'm an admin (the override)
    ///
    /// Registered by the Scrutor ICommandHandler scan like everything else.
    /// </summary>
    public class PendingApprovalReader : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;

        public PendingApprovalReader(FlowDbContext db, ICurrentUserService currentUser)
        {
            _db = db;
            _currentUser = currentUser;
        }

        internal sealed record Eligible(PendingRow Row, string StepName, string ApproverSummary, bool IsAdminOverride);

        internal async Task<List<Eligible>> ForMeAsync(Guid tenantId, CancellationToken ct)
        {
            var me = await _currentUser.GetCurrentUserAsync();

            var raw = await _db.QuoteApprovalRequests.AsNoTracking()
                .Where(r => r.TenantId == tenantId && r.Status == QuoteApprovalRequestStatus.Pending)
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

            if (raw.Count == 0) return new List<Eligible>();

            var rows = raw.Select(x => new PendingRow(
                    x.Request,
                    x.QuoteNumber ?? string.Empty,
                    x.DealTitle ?? string.Empty,
                    x.Owner,
                    string.Join(" ", new[] { x.ContactFirst, x.ContactLast }
                        .Where(s => !string.IsNullOrWhiteSpace(s)))))
                .ToList();

            // ── the steps these requests are waiting on ────────────────
            var ruleIds = rows
                .Where(r => r.Request.ApprovalRuleId.HasValue)
                .Select(r => r.Request.ApprovalRuleId!.Value)
                .Distinct()
                .ToList();

            var steps = ruleIds.Count == 0
                ? new List<ApprovalStep>()
                : await _db.ApprovalSteps.AsNoTracking()
                    .Where(s => s.TenantId == tenantId && ruleIds.Contains(s.ApprovalRuleId))
                    .ToListAsync(ct);

            var stepBy = steps.ToDictionary(s => (s.ApprovalRuleId, s.StepOrder));

            // ── what I am, once ───────────────────────────────────────
            var myRoleIds = (await _db.Users.AsNoTracking()
                    .Where(u => u.Id == me.UserId && u.TenantId == tenantId)
                    .SelectMany(u => u.UserRoles.Select(ur => ur.RoleId))
                    .ToListAsync(ct))
                .ToHashSet();

            var managedTeamIds = (await _db.TeamManagers.AsNoTracking()
                    .Where(m => m.TenantId == tenantId && m.UserId == me.UserId)
                    .Select(m => m.TeamId)
                    .ToListAsync(ct))
                .ToHashSet();

            var teamMemberIds = managedTeamIds.Count == 0
                ? new HashSet<Guid>()
                : (await _db.Users.AsNoTracking()
                        .Where(u => u.TenantId == tenantId && !u.IsDeleted &&
                                    u.TeamId.HasValue && managedTeamIds.Contains(u.TeamId.Value))
                        .Select(u => u.Id)
                        .ToListAsync(ct))
                    .ToHashSet();

            var roleNames = await _db.Roles.AsNoTracking()
                .Where(r => !r.IsDeleted)
                .Select(r => new { r.Id, DisplayName = r.DisplayName ?? string.Empty })
                .ToDictionaryAsync(r => r.Id, r => r.DisplayName, ct);

            var namedUserIds = steps
                .Where(s => s.ApproverUserId.HasValue)
                .Select(s => s.ApproverUserId!.Value)
                .Distinct()
                .ToList();

            var namedUsers = namedUserIds.Count == 0
                ? new Dictionary<Guid, string>()
                : (await _db.Users.AsNoTracking()
                        .Where(u => u.TenantId == tenantId && !u.IsDeleted && namedUserIds.Contains(u.Id))
                        .Select(u => new
                        {
                            u.Id,
                            First = u.FirstName ?? string.Empty,
                            Last = u.LastName ?? string.Empty,
                            Email = u.Email ?? string.Empty
                        })
                        .ToListAsync(ct))
                    .ToDictionary(u => u.Id, u => QuoteApprovalEngine.DisplayName(u.First, u.Last, u.Email));

            var result = new List<Eligible>();

            foreach (var row in rows)
            {
                var req = row.Request;

                ApprovalStep? step = null;
                if (req.ApprovalRuleId.HasValue)
                    stepBy.TryGetValue((req.ApprovalRuleId.Value, req.CurrentStepOrder), out step);

                var allowsSelf = step?.AllowSelfApproval == true;

                // Nobody decides their own request unless the step says so.
                if (req.RequestedByUserId == me.UserId && !allowsSelf) continue;

                var (named, summary) = Eligibility(
                    step, me.UserId, me.IsTenantAdmin, myRoleIds, teamMemberIds, row, roleNames, namedUsers);

                if (!named && !me.IsTenantAdmin) continue;

                result.Add(new Eligible(
                    row,
                    step?.EffectiveName ?? $"Step {req.CurrentStepOrder}",
                    summary,
                    IsAdminOverride: !named && me.IsTenantAdmin));
            }

            return result;
        }

        private static (bool Named, string Summary) Eligibility(
            ApprovalStep? step,
            Guid meId,
            bool meIsAdmin,
            HashSet<Guid> myRoleIds,
            HashSet<Guid> teamMemberIds,
            PendingRow row,
            IReadOnlyDictionary<Guid, string> roleNames,
            IReadOnlyDictionary<Guid, string> namedUsers)
        {
            // No step: the pre-027 rule — the deal's owner, or failing that
            // the requester, must be in a team I manage.
            if (step == null)
                return (InManagedTeam(row, meId, teamMemberIds), "the deal owner's team managers, and workspace admins");

            switch (step.ApproverKind)
            {
                case ApproverKind.Role:
                {
                    var name = step.ApproverRoleId.HasValue && roleNames.TryGetValue(step.ApproverRoleId.Value, out var rn)
                        ? rn
                        : null;

                    var named = step.ApproverRoleId.HasValue && myRoleIds.Contains(step.ApproverRoleId.Value);
                    return (named, name == null ? "a role that no longer exists" : $"everyone with the role \"{name}\"");
                }

                case ApproverKind.User:
                {
                    var named = step.ApproverUserId == meId;
                    var who = step.ApproverUserId.HasValue && namedUsers.TryGetValue(step.ApproverUserId.Value, out var un)
                        ? un
                        : "somebody who is no longer active here";
                    return (named, who);
                }

                case ApproverKind.WorkspaceAdmin:
                    // An admin IS the named approver for this step, not
                    // somebody standing in for one. Reporting false here made
                    // the queue show "standing in as admin" while
                    // DecideQuoteApprovalHandler stored WasAdminOverride =
                    // false — the page and the permanent history disagreeing
                    // on every admin step.
                    return (meIsAdmin, "any workspace admin");

                default:
                    return (InManagedTeam(row, meId, teamMemberIds), "the deal owner's team managers, and workspace admins");
            }
        }

        private static bool InManagedTeam(PendingRow row, Guid meId, HashSet<Guid> teamMemberIds)
        {
            if (teamMemberIds.Count == 0) return false;

            return Guid.TryParse(row.Owner, out var ownerId)
                ? teamMemberIds.Contains(ownerId)
                : teamMemberIds.Contains(row.Request.RequestedByUserId);
        }
    }

    // =================================================================
    // CONCURRENCY-SAFE WRITES
    // =================================================================

    internal static class QuoteApprovalWrites
    {
        /// <summary>
        /// Moves a request on to the next step. The UPDATE only matches
        /// while the request is still pending AT THIS STEP, so two
        /// approvers clicking at once produce exactly one advance and the
        /// loser gets a clear message. Runs inside the context's execution
        /// strategy, so it works with or without EnableRetryOnFailure.
        /// </summary>
        public static async Task AdvanceStepAsync(
            FlowDbContext db, Guid tenantId, Guid requestId, int fromStep,
            QuoteApprovalDecision decision, CancellationToken ct)
        {
            var strategy = db.Database.CreateExecutionStrategy();

            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);

                var moved = await db.QuoteApprovalRequests
                    .Where(r => r.Id == requestId && r.TenantId == tenantId &&
                                r.Status == QuoteApprovalRequestStatus.Pending &&
                                r.CurrentStepOrder == fromStep)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(r => r.CurrentStepOrder, fromStep + 1), ct);

                if (moved == 0)
                    throw new InvalidOperationException(
                        "Someone else has already dealt with this step. Refresh to see where it stands.");

                db.QuoteApprovalDecisions.Add(decision);
                await db.SaveChangesAsync(ct);

                await tx.CommitAsync(ct);
            });
        }

        /// <summary>
        /// Closes a pending request and moves the quote out of
        /// PendingApproval — both as conditional UPDATEs in one
        /// transaction. If either row has already moved on (someone else
        /// decided, recalled, or it was sent), nothing changes and the
        /// caller gets "no longer waiting".
        /// </summary>
        public static async Task ClosePendingAsync(
            FlowDbContext db, Guid tenantId, Guid quoteId, Guid requestId, int atStep,
            string newRequestStatus, QuoteStatus newQuoteStatus,
            Guid byUserId, string byName, string? comment,
            QuoteApprovalDecision? decision, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var strategy = db.Database.CreateExecutionStrategy();

            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);

                var closed = await db.QuoteApprovalRequests
                    .Where(r => r.Id == requestId && r.TenantId == tenantId &&
                                r.Status == QuoteApprovalRequestStatus.Pending &&
                                r.CurrentStepOrder == atStep)
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

                if (decision != null)
                {
                    db.QuoteApprovalDecisions.Add(decision);
                    await db.SaveChangesAsync(ct);
                }

                await tx.CommitAsync(ct);
            });
        }
    }
}
