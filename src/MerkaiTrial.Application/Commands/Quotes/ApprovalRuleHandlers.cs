// =====================================================================
// ApprovalRuleHandlers.cs
// Location: MerkaiTrial.Application/Commands/Quotes/ApprovalRuleHandlers.cs
//
// NEW FILE (027). The rules behind Settings → Quote approval rules:
// read the whole screen, save one rule and its chain, reorder, delete,
// and flip the master switch.
//
// A RULE IS SAVED WITH ITS WHOLE CHAIN. Steps are replaced, not diffed.
// A chain is only meaningful as a whole — a half-applied save that left
// step 2 pointing at a deleted role, or a gap at step 2 of 3, is a quote
// that can never be approved. Replacing is one decision, atomically.
//
// WHAT THIS REFUSES, AND WHY
//   • A rule with no step. A quote matching it could never be approved.
//   • A "named role" step with no role, or "named person" with no person.
//     Resolves to nobody.
//   • A role or person from another workspace. Checked, not trusted.
//   • A duplicate rule name. The page identifies rules by name.
//   • A discount outside 0–100, or a total at or below zero.
//   • More than ApprovalStep.MaxStepsPerRule steps.
//
// WHAT IT ALLOWS BUT WARNS ABOUT (on the page, not here)
//   • A catch-all rule with no condition — every quote needs approval.
//     Legitimate, and also what an unfinished rule looks like.
//   • A step naming somebody who can't sign in. A workspace admin can
//     still decide it, so the quote isn't stranded; the page says so.
//
// All handlers are ICommandHandler, so the Scrutor scan registers them.
// =====================================================================

using System.Globalization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Quotes;

// ── READ: the whole screen in one call ────────────────────────────────

public class GetApprovalRulesHandler : ICommandHandler
{
    private const int MaxRulesPerTenant = 25;

    private readonly FlowDbContext _db;
    private readonly QuoteApprovalEngine _engine;

    public GetApprovalRulesHandler(FlowDbContext db, QuoteApprovalEngine engine)
    {
        _db = db;
        _engine = engine;
    }

    public static int MaxRules => MaxRulesPerTenant;

    public async Task<ApprovalRulesPageDto> Handle(
        Guid tenantId, string currencySymbol, CancellationToken ct = default)
    {
        var (settings, isDefaultSettings) = await _engine.GetSettingsAsync(tenantId, ct);

        var rules = await _db.ApprovalRules.AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .Include(r => r.Steps)
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Name)
            .ToListAsync(ct);

        // ── the pickers, and the counts that make a step honest ────────
        // Roles: built-in (TenantId null) plus this tenant's own, admitted
        // by FlowDbContext's global Role filter — the same read the record
        // visibility matrix does.
        var roleRows = await _db.Roles.AsNoTracking()
            .Where(r => !r.IsDeleted)
            .OrderBy(r => r.IsSystemRole ? 0 : 1).ThenBy(r => r.DisplayName)
            .Select(r => new
            {
                r.Id,
                DisplayName = r.DisplayName ?? string.Empty,
                r.IsSystemRole
            })
            .ToListAsync(ct);

        // Users is NOT tenant-filtered — the TenantId predicate IS the
        // isolation. Names coalesced in the projection: a NULL column
        // otherwise throws in the reader.
        var userRows = await _db.Users.AsNoTracking()
            .Where(u => u.TenantId == tenantId && !u.IsDeleted && u.IsActive)
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
            .Select(u => new
            {
                u.Id,
                First = u.FirstName ?? string.Empty,
                Last = u.LastName ?? string.Empty,
                Email = u.Email ?? string.Empty,
                u.IsTenantAdmin,
                RoleIds = u.UserRoles.Select(ur => ur.RoleId).ToList()
            })
            .ToListAsync(ct);

        var users = userRows
            .Select(u => new ApproverUserDto(
                u.Id, QuoteApprovalEngine.DisplayName(u.First, u.Last, u.Email), u.Email, u.IsTenantAdmin))
            .ToList();

        var roles = roleRows
            .Select(r => new ApproverRoleDto(
                r.Id, r.DisplayName, r.IsSystemRole,
                userRows.Count(u => u.RoleIds.Contains(r.Id))))
            .ToList();

        var roleById = roles.ToDictionary(r => r.Id);
        var userById = users.ToDictionary(u => u.Id);

        var adminCount = userRows.Count(u => u.IsTenantAdmin);

        // How many teams have a manager who can sign in — the number that
        // decides whether a "team managers" step actually resolves to
        // anybody, since QuoteApprovalEngine only ever picks active users.
        var teamsWithActiveManager = await _db.TeamManagers.AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .Join(_db.Users.AsNoTracking().Where(u => u.TenantId == tenantId && !u.IsDeleted && u.IsActive),
                  m => m.UserId, u => u.Id, (m, u) => m.TeamId)
            .Distinct()
            .CountAsync(ct);

        // How many approval requests are part-way through each rule's chain.
        // Editing or deleting a rule changes what those quotes are asked for
        // from their next step on, so the page has to be able to say so.
        var inFlight = (await _db.QuoteApprovalRequests.AsNoTracking()
                .Where(r => r.TenantId == tenantId &&
                            r.Status == QuoteApprovalRequestStatus.Pending &&
                            r.ApprovalRuleId != null)
                .GroupBy(r => r.ApprovalRuleId!.Value)
                .Select(g => new { RuleId = g.Key, N = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.RuleId, x => x.N);

        var dtos = rules
            .Select(r => ToDto(r, currencySymbol, roleById, userById, adminCount,
                               teamsWithActiveManager, inFlight.GetValueOrDefault(r.Id)))
            .ToList();

        return new ApprovalRulesPageDto(
            settings.IsEnabled,
            dtos,
            roles,
            users,
            isDefaultSettings ? null : settings.UpdatedAtUtc,
            isDefaultSettings ? null : settings.UpdatedBy,
            HasNoRules: dtos.Count == 0);
    }

    private static ApprovalRuleDto ToDto(
        ApprovalRule r,
        string currencySymbol,
        IReadOnlyDictionary<Guid, ApproverRoleDto> roles,
        IReadOnlyDictionary<Guid, ApproverUserDto> users,
        int adminCount,
        int teamsWithActiveManager,
        int inFlight)
    {
        var steps = r.Steps
            .OrderBy(s => s.StepOrder)
            .Select(s => StepDto(s, roles, users, adminCount, teamsWithActiveManager))
            .ToList();

        string? warning = null;

        if (steps.Count == 0)
            warning = "No steps yet — a quote that matched this rule could never be approved. Add at least one step.";
        else if (r.IsCatchAll && r.IsActive)
            warning = "No condition set, so this rule matches EVERY quote. If that isn't what you want, add a discount or total limit.";
        else if (steps.Any(s => s.ApproverCount == 0))
            warning = "One of the steps resolves to nobody right now. A workspace admin can still sign it, but the person you named can't.";

        return new ApprovalRuleDto(
            r.Id, r.Name, r.Description, r.SortOrder, r.IsActive,
            r.DiscountOverPercent, r.TotalOverAmount, r.ConditionMode, r.IsCatchAll,
            steps,
            RuleMatcher.Describe(r, currencySymbol),
            warning,
            inFlight);
    }

    private static ApprovalStepDto StepDto(
        ApprovalStep s,
        IReadOnlyDictionary<Guid, ApproverRoleDto> roles,
        IReadOnlyDictionary<Guid, ApproverUserDto> users,
        int adminCount,
        int teamsWithActiveManager)
    {
        string summary;
        var count = 0;
        string? problem = null;

        switch (s.ApproverKind)
        {
            case ApproverKind.Role:
                if (s.ApproverRoleId.HasValue && roles.TryGetValue(s.ApproverRoleId.Value, out var role))
                {
                    summary = $"Everyone with the role \"{role.DisplayName}\"";
                    count = role.UserCount;
                    if (count == 0)
                        problem = "Nobody holds that role yet, so only a workspace admin could sign this step.";
                }
                else
                {
                    summary = "A role that no longer exists";
                    problem = "That role has been deleted. Pick another, or this step falls to a workspace admin.";
                }
                break;

            case ApproverKind.User:
                if (s.ApproverUserId.HasValue && users.TryGetValue(s.ApproverUserId.Value, out var user))
                {
                    summary = user.FullName;
                    count = 1;
                }
                else
                {
                    summary = "Somebody who is no longer active here";
                    problem = "That person can't sign in any more. Pick somebody else, or this step falls to a workspace admin.";
                }
                break;

            case ApproverKind.WorkspaceAdmin:
                summary = "Any workspace admin";
                count = adminCount;
                if (count == 0)
                    problem = "This workspace has no active admin, so nobody could sign this step.";
                break;

            default:
                summary = "The deal owner's team managers";
                // Not a precise count — it depends on which deal — but zero
                // is the answer that matters, and zero here means no team
                // anywhere has a manager who can sign in.
                count = teamsWithActiveManager > 0 ? teamsWithActiveManager : adminCount;
                if (teamsWithActiveManager == 0)
                    problem = adminCount > 0
                        ? "No team has a manager who can sign in, so every quote on this step goes to a workspace admin. Set managers under Settings → Record visibility."
                        : "No team has a manager and there is no active admin — nobody could sign this step.";
                break;
        }

        return new ApprovalStepDto(
            s.Id, s.StepOrder, s.Name, s.EffectiveName, s.ApproverKind,
            s.ApproverRoleId, s.ApproverUserId, s.AllowSelfApproval,
            summary, count, problem);
    }
}

// ── WRITE: one rule and its chain ─────────────────────────────────────

public class SaveApprovalRuleHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly IAuditService _audit;
    private readonly ILogger<SaveApprovalRuleHandler> _logger;

    public SaveApprovalRuleHandler(FlowDbContext db, IAuditService audit, ILogger<SaveApprovalRuleHandler> logger)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
    }

    public async Task<Guid> Handle(
        Guid tenantId, SaveApprovalRuleDto dto, string updatedBy, CancellationToken ct = default)
    {
        var inv = CultureInfo.InvariantCulture;

        // ── the rule itself ───────────────────────────────────────────
        var name = dto.Name?.Trim() ?? string.Empty;
        if (name.Length == 0) throw new InvalidOperationException("Give the rule a name, so people can tell it apart from the others.");
        if (name.Length > 150) throw new InvalidOperationException("Rule names can be at most 150 characters.");

        if (dto.DiscountOverPercent is < 0 or > 100)
            throw new InvalidOperationException("The discount limit must be between 0 and 100%.");

        if (dto.TotalOverAmount is <= 0)
            throw new InvalidOperationException("The total limit must be more than zero — or leave it empty to ignore quote size.");

        if (!Enum.IsDefined(typeof(ApprovalConditionMode), dto.ConditionMode))
            throw new InvalidOperationException("Choose how the conditions combine.");

        // ── the chain ─────────────────────────────────────────────────
        var incoming = dto.Steps ?? new List<SaveApprovalStepDto>();

        if (incoming.Count == 0)
            throw new InvalidOperationException(
                "A rule needs at least one step — otherwise a quote that matched it could never be approved.");

        if (incoming.Count > ApprovalStep.MaxStepsPerRule)
            throw new InvalidOperationException(
                $"A rule can have at most {ApprovalStep.MaxStepsPerRule} steps. That is already far more than any sales process needs.");

        foreach (var s in incoming)
        {
            if (!Enum.IsDefined(typeof(ApproverKind), s.ApproverKind))
                throw new InvalidOperationException("Choose who approves each step.");

            if (s.ApproverKind == ApproverKind.Role && !s.ApproverRoleId.HasValue)
                throw new InvalidOperationException("One of the steps says a role approves it, but no role is chosen.");

            if (s.ApproverKind == ApproverKind.User && !s.ApproverUserId.HasValue)
                throw new InvalidOperationException("One of the steps says a specific person approves it, but nobody is chosen.");

            if (s.Name?.Length > 150)
                throw new InvalidOperationException("Step names can be at most 150 characters.");
        }

        // Every named role and person must belong to this workspace. The
        // ids arrive from a form, so they are checked rather than trusted.
        var roleIds = incoming.Where(s => s.ApproverKind == ApproverKind.Role && s.ApproverRoleId.HasValue)
            .Select(s => s.ApproverRoleId!.Value).Distinct().ToList();

        if (roleIds.Count > 0)
        {
            var found = await _db.Roles.AsNoTracking()
                .Where(r => roleIds.Contains(r.Id) && !r.IsDeleted)
                .Select(r => r.Id)
                .ToListAsync(ct);

            if (found.Count != roleIds.Count)
                throw new InvalidOperationException("One of the roles you picked no longer exists. Reload the page and try again.");
        }

        var userIds = incoming.Where(s => s.ApproverKind == ApproverKind.User && s.ApproverUserId.HasValue)
            .Select(s => s.ApproverUserId!.Value).Distinct().ToList();

        if (userIds.Count > 0)
        {
            var found = await _db.Users.AsNoTracking()
                .Where(u => u.TenantId == tenantId && !u.IsDeleted && userIds.Contains(u.Id))
                .Select(u => u.Id)
                .ToListAsync(ct);

            if (found.Count != userIds.Count)
                throw new InvalidOperationException("One of the people you picked is no longer in this workspace. Reload the page and try again.");
        }

        // ── name clash, compared the way the unique index does ─────────
        var others = await _db.ApprovalRules.AsNoTracking()
            .Where(r => r.TenantId == tenantId && (dto.Id == null || r.Id != dto.Id.Value))
            .Select(r => new { r.Id, r.Name })
            .ToListAsync(ct);

        if (others.Any(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"You already have a rule called \"{name}\".");

        // ── create or update ──────────────────────────────────────────
        ApprovalRule rule;
        var isNew = dto.Id == null;

        if (isNew)
        {
            if (others.Count >= GetApprovalRulesHandler.MaxRules)
                throw new InvalidOperationException(
                    $"A workspace can have at most {GetApprovalRulesHandler.MaxRules} rules. Retire one you no longer use.");

            rule = new ApprovalRule
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                // New rules go last, so adding one can never change which
                // rule an existing quote would trip.
                SortOrder = others.Count == 0
                    ? 0
                    : await _db.ApprovalRules.Where(r => r.TenantId == tenantId).MaxAsync(r => r.SortOrder, ct) + 1,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = updatedBy
            };

            _db.ApprovalRules.Add(rule);
        }
        else
        {
            rule = await _db.ApprovalRules
                .Include(r => r.Steps)
                .FirstOrDefaultAsync(r => r.Id == dto.Id!.Value && r.TenantId == tenantId, ct)
                ?? throw new KeyNotFoundException("That rule no longer exists. Reload the page and try again.");

            rule.UpdatedAtUtc = DateTime.UtcNow;
            rule.UpdatedBy = updatedBy;

            // Replaced, not diffed: a chain only means anything as a whole.
            // Nothing points at an ApprovalStep — a request in flight
            // snapshots its rule name and step count instead — so there is
            // no identity here worth preserving.
            if (rule.Steps.Count > 0) _db.ApprovalSteps.RemoveRange(rule.Steps);
        }

        var beforeName = isNew ? null : rule.Name;

        rule.Name = name;
        rule.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
        rule.IsActive = dto.IsActive;
        rule.DiscountOverPercent = dto.DiscountOverPercent;
        rule.TotalOverAmount = dto.TotalOverAmount;
        rule.ConditionMode = dto.ConditionMode;

        var order = 1;
        foreach (var s in incoming)
        {
            _db.ApprovalSteps.Add(new ApprovalStep
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ApprovalRuleId = rule.Id,
                StepOrder = order++,
                Name = string.IsNullOrWhiteSpace(s.Name) ? null : s.Name.Trim(),
                ApproverKind = s.ApproverKind,
                // Belt and braces with the CHECK constraint: a role id on a
                // "named person" step would make the row unreadable.
                ApproverRoleId = s.ApproverKind == ApproverKind.Role ? s.ApproverRoleId : null,
                ApproverUserId = s.ApproverKind == ApproverKind.User ? s.ApproverUserId : null,
                AllowSelfApproval = s.AllowSelfApproval,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = updatedBy
            });
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (QuoteApprovalEngine.IsDuplicateKey(ex))
        {
            // The check above compares names with OrdinalIgnoreCase; the
            // unique index compares under the database collation, which is
            // usually accent-insensitive too. "Café" and "Cafe" get past the
            // first and are stopped by the second — as a 500 unless it is
            // turned back into something a person can act on.
            throw new InvalidOperationException(
                $"You already have a rule called \"{name}\" (the database ignores accents and case when comparing names). Pick a different one.");
        }

        await _audit.WriteAsync(
            isNew ? "ApprovalRuleCreated" : "ApprovalRuleUpdated", "ApprovalRule", rule.Id, tenantId,
            new
            {
                name = rule.Name,
                fromName = beforeName,
                active = rule.IsActive,
                discountOver = rule.DiscountOverPercent?.ToString("0.##", inv),
                totalOver = rule.TotalOverAmount?.ToString("0.##", inv),
                conditions = rule.ConditionMode.ToString(),
                steps = incoming.Select((s, i) => new
                {
                    step = i + 1,
                    name = s.Name,
                    approver = s.ApproverKind.ToString(),
                    roleId = s.ApproverRoleId,
                    userId = s.ApproverUserId,
                    selfApproval = s.AllowSelfApproval
                }).ToList(),
                by = updatedBy
            },
            ct);

        _logger.LogInformation(
            "Approval rule {Rule} {Verb} in tenant {TenantId} by {User} with {Steps} step(s)",
            rule.Name, isNew ? "created" : "updated", tenantId, updatedBy, incoming.Count);

        return rule.Id;
    }
}

// ── Reorder — which rule is tried first ───────────────────────────────

public class ReorderApprovalRulesHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly IAuditService _audit;

    public ReorderApprovalRulesHandler(FlowDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    /// <summary>
    /// Order matters: the FIRST active rule that matches is the one that
    /// applies. Ids not mentioned keep their relative order after the ones
    /// that were, so a stale form can't silently shuffle a rule it never
    /// showed.
    /// </summary>
    public async Task Handle(Guid tenantId, List<Guid> idsInOrder, string updatedBy, CancellationToken ct = default)
    {
        var rules = await _db.ApprovalRules
            .Where(r => r.TenantId == tenantId)
            .ToListAsync(ct);

        if (rules.Count == 0) return;

        var wanted = (idsInOrder ?? new List<Guid>()).Distinct().ToList();
        var byId = rules.ToDictionary(r => r.Id);

        var ordered = wanted.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        ordered.AddRange(rules.Except(ordered).OrderBy(r => r.SortOrder).ThenBy(r => r.Name));

        var changed = false;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].SortOrder == i) continue;
            ordered[i].SortOrder = i;
            ordered[i].UpdatedAtUtc = DateTime.UtcNow;
            ordered[i].UpdatedBy = updatedBy;
            changed = true;
        }

        if (!changed) return;

        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(
            "ApprovalRulesReordered", "ApprovalRule", Guid.Empty, tenantId,
            new { order = ordered.Select(r => r.Name).ToList(), by = updatedBy }, ct);
    }
}

// ── Delete ────────────────────────────────────────────────────────────

public class DeleteApprovalRuleHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly IAuditService _audit;

    public DeleteApprovalRuleHandler(FlowDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    /// <summary>
    /// Steps go with it (cascade). Requests already in flight are not
    /// deleted, and they keep the NUMBER of steps they started with — but
    /// not the approvers. Their ApprovalRuleId is left pointing at a rule
    /// that no longer exists, and the engine treats a missing step as "the
    /// deal owner's team managers", so a quote waiting at step 2 of 3 still
    /// finishes, with its remaining steps falling back to that default
    /// rather than to whoever the deleted rule named.
    ///
    /// That is the deliberate trade: nothing strands, but a chain in
    /// flight IS weakened. The page says so before the delete, with the
    /// count of affected quotes.
    /// </summary>
    public async Task Handle(Guid tenantId, Guid ruleId, string updatedBy, CancellationToken ct = default)
    {
        var rule = await _db.ApprovalRules
            .Include(r => r.Steps)
            .FirstOrDefaultAsync(r => r.Id == ruleId && r.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException("That rule no longer exists.");

        var name = rule.Name;
        var stepCount = rule.Steps.Count;

        var inFlight = await _db.QuoteApprovalRequests.AsNoTracking()
            .CountAsync(r => r.TenantId == tenantId && r.ApprovalRuleId == ruleId &&
                             r.Status == QuoteApprovalRequestStatus.Pending, ct);

        _db.ApprovalRules.Remove(rule);
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(
            "ApprovalRuleDeleted", "ApprovalRule", ruleId, tenantId,
            new { name, steps = stepCount, requestsStillInFlight = inFlight, by = updatedBy }, ct);
    }

    /// <summary>
    /// How many approval requests are mid-chain on this rule, so the page
    /// can warn before the delete rather than after.
    /// </summary>
    public async Task<int> InFlightCountAsync(Guid tenantId, Guid ruleId, CancellationToken ct = default)
        => await _db.QuoteApprovalRequests.AsNoTracking()
            .CountAsync(r => r.TenantId == tenantId && r.ApprovalRuleId == ruleId &&
                             r.Status == QuoteApprovalRequestStatus.Pending, ct);
}
