// =====================================================================
// AutomationHandlers.cs
// Location: MerkaiTrial.Application/Commands/Automation/AutomationHandlers.cs
//
// NEW FILE (098 — workflow automation).
//
//   AutomationPlan               the plan's rule limit, read LIVE from
//                                Plans.MaxAutomationRules through the
//                                tenant's plan name. Upgrading a workspace
//                                raises its limit at once; nothing is copied.
//   AutomationOptionsBuilder     every list the editor needs (fields,
//                                statuses, stages, people, sources, custom
//                                fields) — also what the server validates
//                                a rule against, so the two never disagree
//
//   GetAutomationRulesHandler    the rules page: list + limit + plan
//   GetAutomationRuleHandler     one rule, for the editor
//   GetAutomationOptionsHandler  the editor's lists
//   SaveAutomationRuleHandler    add / edit (validates everything)
//   SetAutomationRuleActiveHandler  switch on / off (the limit lives here)
//   DeleteAutomationRuleHandler  soft delete; its log stays readable
//   GetAutomationRunsHandler     the rule log, newest first
//
//   GetPlanAutomationLimitHandler / SetPlanAutomationLimitHandler
//                                Admin → Plans → a plan: the number itself
//
// 099
//   • Custom fields can be WATCHED (CanTrigger) as well as tested and set.
//   • Time-based rules: TimeKind / TimeDays saved and returned; the
//     options carry the situations each kind of record offers; switching a
//     time-based rule on is no longer refused.
//
// THE LIMIT
//   Counted over SWITCHED-ON rules only. A workspace may keep as many
//   switched-off rules as it likes — drafts, seasonal rules — and a
//   workspace moved to a smaller plan loses nothing: the rules over the
//   limit stay, they just can't be switched on, and the engine runs only
//   the oldest N of the ones that are on.
// =====================================================================

using MerkaiTrial.Application.Commands.Activities;
using MerkaiTrial.Application.Commands.LeadStatuses;
using MerkaiTrial.Application.Commands.PipelineStages;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace MerkaiTrial.Application.Commands.Automation;

// =====================================================================
// THE PLAN'S LIMIT
// =====================================================================

public static class AutomationPlan
{
    /// <summary>
    /// How many rules this workspace may have switched on, and its plan's
    /// display name. A workspace whose plan can't be found gets 0 — the
    /// safe answer: rules stay, nothing new can be switched on.
    /// </summary>
    public static async Task<(int Limit, string? PlanName)> LimitAsync(
        FlowDbContext db, Guid tenantId, CancellationToken ct)
    {
        // Tenants and Plans are not tenant-filtered.
        var planName = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => t.Plan)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(planName)) return (0, null);

        var plan = await db.Plans.AsNoTracking()
            .Where(p => p.Name == planName)
            .Select(p => new { p.MaxAutomationRules, p.DisplayName })
            .FirstOrDefaultAsync(ct);

        return plan is null
            ? (0, planName)
            : (Math.Max(plan.MaxAutomationRules, 0), string.IsNullOrWhiteSpace(plan.DisplayName) ? planName : plan.DisplayName);
    }

    public static Task<int> ActiveCountAsync(FlowDbContext db, Guid tenantId, CancellationToken ct)
        => db.AutomationRules.IgnoreQueryFilters()
             .CountAsync(r => r.TenantId == tenantId && r.IsActive && !r.IsDeleted, ct);

    /// <summary>The sentence for "you can't switch another one on".</summary>
    public static string LimitMessage(int limit, string? planName) => limit <= 0
        ? $"Automation isn't included in the {planName ?? "current"} plan. Ask about an upgrade to switch rules on."
        : $"The {planName ?? "current"} plan allows {limit} switched-on rule{(limit == 1 ? "" : "s")}. Switch one off first, or ask about a bigger plan.";
}

// =====================================================================
// WHAT A RULE MAY SAY, FOR THIS WORKSPACE
// =====================================================================

public static class AutomationOptionsBuilder
{
    public static async Task<AutomationOptionsDto> BuildAsync(
        FlowDbContext db, ILeadStatusResolver statusResolver, IStageResolver stageResolver,
        Guid tenantId, CancellationToken ct)
    {
        var statuses = await statusResolver.GetAsync(tenantId, ct);
        var stages = await stageResolver.GetAsync(tenantId, ct);

        var users = (await db.Users.IgnoreQueryFilters().AsNoTracking()
                .Where(u => u.TenantId == tenantId && !u.IsDeleted && u.IsActive)
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email })
                .ToListAsync(ct))
            .Select(u => new AutomationOptionDto(
                u.Id.ToString(),
                $"{u.FirstName} {u.LastName}".Trim() is { Length: > 0 } n ? n : (u.Email ?? "Unnamed user")))
            .OrderBy(u => u.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sources = await db.LeadSources.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tenantId && !s.IsDeleted && s.IsActive)
            .OrderBy(s => s.Name)
            .Select(s => new AutomationOptionDto(s.Id.ToString(), s.Name))
            .ToListAsync(ct);

        var custom = await db.CustomFieldDefinitions.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted && d.IsActive
                     && (d.EntityType == CustomFieldEntityTypes.Lead || d.EntityType == CustomFieldEntityTypes.Deal))
            .OrderBy(d => d.SortOrder).ThenBy(d => d.Label)
            .Select(d => new { d.Id, d.EntityType, d.Label, d.FieldType, d.OptionsJson })
            .ToListAsync(ct);

        // Statuses: any status can be WATCHED or TESTED, Converted included
        // ("when a lead becomes a deal"); only the ones a person may pick
        // can be SET — Converted is set by converting, nothing else.
        var allStatuses = statuses.All.Where(s => s.IsActive)
            .Select(s => new AutomationOptionDto(s.Key, s.Name)).ToList();
        var settableStatuses = statuses.Selectable.Where(s => !s.IsSystem)
            .Select(s => new AutomationOptionDto(s.Key, s.Name)).ToList();
        var stageOptions = stages.Active
            .Select(s => new AutomationOptionDto(s.Key, s.Name)).ToList();

        var fields = new Dictionary<string, List<AutomationFieldDto>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in AutomationEntity.All)
        {
            var list = new List<AutomationFieldDto>();

            foreach (var b in AutomationFields.BuiltinsFor(entity))
            {
                List<AutomationOptionDto>? opts = b.Key switch
                {
                    AutomationFields.Status => allStatuses,
                    AutomationFields.Stage  => stageOptions,
                    AutomationFields.Source => sources,
                    AutomationFields.Owner  => users,
                    _ => null
                };
                var settable = b.Key == AutomationFields.Status ? settableStatuses : null;
                list.Add(new AutomationFieldDto(b.Key, b.Label, b.Kind, b.CanTrigger, b.CanCondition, b.CanUpdate, opts, settable));
            }

            // Custom fields can be WATCHED (099), TESTED and SET by a rule.
            foreach (var d in custom.Where(c => c.EntityType == entity))
            {
                var kind = AutomationFields.KindOfCustom(d.FieldType);
                var opts = kind == AutomationFieldKind.Choice ? DropdownOptions(d.OptionsJson) : null;
                list.Add(new AutomationFieldDto(
                    AutomationFields.CustomKey(d.Id), d.Label, kind,
                    CanTrigger: true, CanCondition: true, CanUpdate: true, opts));
            }

            fields[entity] = list;
        }

        var (limit, _) = await AutomationPlan.LimitAsync(db, tenantId, ct);
        var active = await AutomationPlan.ActiveCountAsync(db, tenantId, ct);

        var timeKinds = AutomationEntity.All.ToDictionary(
            e => e,
            e => AutomationTimeKinds.For(e)
                    .Select(k => new AutomationTimeKindDto(k.Key, k.Label, k.MinDays, k.MaxDays, k.DefaultDays))
                    .ToList(),
            StringComparer.OrdinalIgnoreCase);

        return new AutomationOptionsDto(
            fields, users, ActivityType.Schedulable.ToList(), limit, active, timeKinds);
    }

    /// <summary>[{ "key": "o_1a2b", "label": "Gold", "isActive": true }] → the active choices.</summary>
    private static List<AutomationOptionDto> DropdownOptions(string? json)
    {
        var result = new List<AutomationOptionDto>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var o in doc.RootElement.EnumerateArray())
            {
                if (o.ValueKind != JsonValueKind.Object) continue;
                var key = Prop(o, "key");
                var label = Prop(o, "label");
                var activeOk = !TryProp(o, "isActive", out var a) || a.ValueKind != JsonValueKind.False;
                if (!string.IsNullOrWhiteSpace(key) && activeOk)
                    result.Add(new AutomationOptionDto(key!, string.IsNullOrWhiteSpace(label) ? key! : label!));
            }
        }
        catch (JsonException) { }
        return result;
    }

    private static string? Prop(JsonElement o, string name)
        => TryProp(o, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool TryProp(JsonElement o, string name, out JsonElement value)
    {
        foreach (var p in o.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }
}

// =====================================================================
// HANDLERS
// =====================================================================

public class GetAutomationOptionsHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ILeadStatusResolver _statuses;
    private readonly IStageResolver _stages;

    public GetAutomationOptionsHandler(FlowDbContext db, ILeadStatusResolver statuses, IStageResolver stages)
    {
        _db = db; _statuses = statuses; _stages = stages;
    }

    public Task<AutomationOptionsDto> Handle(Guid tenantId, CancellationToken ct = default)
        => AutomationOptionsBuilder.BuildAsync(_db, _statuses, _stages, tenantId, ct);
}

public class GetAutomationRulesHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ILeadStatusResolver _statuses;
    private readonly IStageResolver _stages;

    public GetAutomationRulesHandler(FlowDbContext db, ILeadStatusResolver statuses, IStageResolver stages)
    {
        _db = db; _statuses = statuses; _stages = stages;
    }

    public async Task<AutomationRulesPageDto> Handle(Guid tenantId, CancellationToken ct = default)
    {
        var rules = await _db.AutomationRules.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.TenantId == tenantId && !r.IsDeleted)
            .OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Id)
            .ToListAsync(ct);

        var options = await AutomationOptionsBuilder.BuildAsync(_db, _statuses, _stages, tenantId, ct);
        var (_, planName) = await AutomationPlan.LimitAsync(_db, tenantId, ct);

        var items = rules.Select(r => new AutomationRuleListItem(
                r.Id, r.Name, r.EntityType, r.IsActive,
                AutomationDescriber.Describe(AutomationRuleMapper.ToDto(r), options),
                r.RunCount, r.LastRunAtUtc, r.UpdatedAtUtc,
                r.Trigger == AutomationTrigger.TimeBased))
            .ToList();

        return new AutomationRulesPageDto(items, options.Limit, options.ActiveCount, planName);
    }
}

public class GetAutomationRuleHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    public GetAutomationRuleHandler(FlowDbContext db) => _db = db;

    public async Task<AutomationRuleDto?> Handle(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        var rule = await _db.AutomationRules.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId && !r.IsDeleted, ct);
        return rule is null ? null : AutomationRuleMapper.ToDto(rule);
    }
}

public class SaveAutomationRuleHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ILeadStatusResolver _statuses;
    private readonly IStageResolver _stages;
    private readonly ILogger<SaveAutomationRuleHandler> _logger;

    public SaveAutomationRuleHandler(
        FlowDbContext db, ILeadStatusResolver statuses, IStageResolver stages,
        ILogger<SaveAutomationRuleHandler> logger)
    {
        _db = db; _statuses = statuses; _stages = stages; _logger = logger;
    }

    /// <summary>
    /// Adds (id null) or replaces a rule. Throws InvalidOperationException
    /// with every problem at once; KeyNotFoundException for a missing rule.
    /// </summary>
    public async Task<Guid> Handle(Guid tenantId, Guid? id, AutomationRuleDto dto, string? actorName, CancellationToken ct = default)
    {
        dto.Conditions ??= new();
        dto.Actions ??= new();

        var options = await AutomationOptionsBuilder.BuildAsync(_db, _statuses, _stages, tenantId, ct);
        AutomationRuleCheck.ThrowIfInvalid(dto, options);

        var entity = AutomationEntity.Normalise(dto.EntityType)!;
        AutomationRule rule;

        if (id is { } existingId)
        {
            rule = await _db.AutomationRules.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Id == existingId && r.TenantId == tenantId && !r.IsDeleted, ct)
                ?? throw new KeyNotFoundException("That rule no longer exists.");

            // Switching ON in the same save counts against the limit.
            if (dto.IsActive && !rule.IsActive && options.ActiveCount >= options.Limit)
                throw new InvalidOperationException(await LimitTextAsync(tenantId, ct));

            // A change of record type or round-robin list starts the turn again.
            if (!string.Equals(rule.EntityType, entity, StringComparison.Ordinal)) rule.RoundRobinCursor = 0;

            rule.UpdatedAtUtc = DateTime.UtcNow;
            rule.UpdatedBy = actorName;
        }
        else
        {
            if (dto.IsActive && options.ActiveCount >= options.Limit)
                throw new InvalidOperationException(await LimitTextAsync(tenantId, ct));

            rule = new AutomationRule
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = actorName
            };
            _db.AutomationRules.Add(rule);
        }

        rule.Name = dto.Name.Trim();
        rule.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
        rule.EntityType = entity;
        rule.Trigger = dto.Trigger;

        if (dto.Trigger == AutomationTrigger.FieldChanged)
        {
            rule.TriggerField = dto.TriggerField?.Trim();
            rule.TriggerFromValue = Blank(dto.TriggerFromValue);
            rule.TriggerToValue = Blank(dto.TriggerToValue);
        }
        else
        {
            rule.TriggerField = rule.TriggerFromValue = rule.TriggerToValue = null;
        }

        // 099
        if (dto.Trigger == AutomationTrigger.TimeBased)
        {
            // A new situation, or a new number of days, is a different rule
            // as far as "already acted on" goes: start its episodes afresh.
            if (!string.Equals(rule.TimeKind, dto.TimeKind, StringComparison.Ordinal) || rule.TimeDays != dto.TimeDays)
                _db.AutomationTimeFires.RemoveRange(
                    _db.AutomationTimeFires.IgnoreQueryFilters().Where(f => f.RuleId == rule.Id && f.TenantId == tenantId));
            rule.TimeKind = dto.TimeKind;
            rule.TimeDays = dto.TimeDays;
        }
        else
        {
            rule.TimeKind = null;
            rule.TimeDays = null;
        }

        rule.ConditionsJson = AutomationJson.Write(dto.Conditions.Select(Clean));
        rule.ActionsJson = AutomationJson.Write(dto.Actions.Select(Clean));
        rule.IsActive = dto.IsActive;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Automation rule {RuleId} saved for tenant {TenantId} (active: {Active})",
            rule.Id, tenantId, rule.IsActive);

        return rule.Id;
    }

    private async Task<string> LimitTextAsync(Guid tenantId, CancellationToken ct)
    {
        var (limit, plan) = await AutomationPlan.LimitAsync(_db, tenantId, ct);
        return "Saved switched off is fine, but it can't be switched on: " + AutomationPlan.LimitMessage(limit, plan);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static AutomationCondition Clean(AutomationCondition c) => new()
    {
        Field = c.Field?.Trim() ?? string.Empty,
        Op = c.Op?.Trim() ?? string.Empty,
        Value = AutomationOp.NeedsValue(c.Op ?? "") ? Blank(c.Value) : null
    };

    /// <summary>Keeps only the properties the action's kind reads, so the JSON says what the rule does.</summary>
    private static AutomationAction Clean(AutomationAction a) => a.Kind switch
    {
        AutomationActionKind.CreateTask => new AutomationAction
        {
            Kind = a.Kind,
            Subject = Blank(a.Subject),
            Description = Blank(a.Description),
            ActivityType = Blank(a.ActivityType) ?? ActivityType.Task,
            DueInDays = Math.Clamp(a.DueInDays, 0, 365),
            AssignTo = a.AssignTo,
            UserId = a.AssignTo == AutomationPerson.User ? Blank(a.UserId) : null
        },
        AutomationActionKind.Notify => new AutomationAction
        {
            Kind = a.Kind,
            Message = Blank(a.Message),
            AssignTo = a.AssignTo,
            UserId = a.AssignTo == AutomationPerson.User ? Blank(a.UserId) : null
        },
        _ => new AutomationAction
        {
            Kind = AutomationActionKind.UpdateField,
            Field = Blank(a.Field),
            Mode = a.Mode == AutomationUpdateMode.RoundRobin ? AutomationUpdateMode.RoundRobin : null,
            Value = a.Mode == AutomationUpdateMode.RoundRobin ? null : Blank(a.Value),
            UserIds = a.Mode == AutomationUpdateMode.RoundRobin
                ? (a.UserIds ?? new()).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : null,
            DueInDays = 0
        }
    };
}

public class SetAutomationRuleActiveHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    public SetAutomationRuleActiveHandler(FlowDbContext db) => _db = db;

    public async Task Handle(Guid tenantId, Guid id, bool isActive, string? actorName, CancellationToken ct = default)
    {
        var rule = await _db.AutomationRules.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId && !r.IsDeleted, ct)
            ?? throw new KeyNotFoundException("That rule no longer exists.");

        if (rule.IsActive == isActive) return;

        if (isActive)
        {
            var (limit, plan) = await AutomationPlan.LimitAsync(_db, tenantId, ct);
            var active = await AutomationPlan.ActiveCountAsync(_db, tenantId, ct);
            if (active >= limit)
                throw new InvalidOperationException(AutomationPlan.LimitMessage(limit, plan));

            if (AutomationJson.Actions(rule.ActionsJson).Count == 0)
                throw new InvalidOperationException("Open the rule and give it something to do first.");
        }

        rule.IsActive = isActive;
        rule.UpdatedAtUtc = DateTime.UtcNow;
        rule.UpdatedBy = actorName;
        await _db.SaveChangesAsync(ct);
    }
}

public class DeleteAutomationRuleHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    public DeleteAutomationRuleHandler(FlowDbContext db) => _db = db;

    /// <summary>Soft delete: the rule stops, its log lines keep their rule name.</summary>
    public async Task Handle(Guid tenantId, Guid id, string? actorName, CancellationToken ct = default)
    {
        var rule = await _db.AutomationRules.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId && !r.IsDeleted, ct)
            ?? throw new KeyNotFoundException("That rule no longer exists.");

        rule.IsDeleted = true;
        rule.IsActive = false;
        rule.UpdatedAtUtc = DateTime.UtcNow;
        rule.UpdatedBy = actorName;
        await _db.SaveChangesAsync(ct);
    }
}

public class GetAutomationRunsHandler : ICommandHandler
{
    public const int Take = 100;

    private readonly FlowDbContext _db;
    public GetAutomationRunsHandler(FlowDbContext db) => _db = db;

    public Task<List<AutomationRunDto>> Handle(Guid tenantId, Guid? ruleId, CancellationToken ct = default)
    {
        var q = _db.AutomationRuns.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.TenantId == tenantId);
        if (ruleId is { } rid) q = q.Where(r => r.RuleId == rid);

        return q.OrderByDescending(r => r.CreatedAtUtc)
            .Take(Take)
            .Select(r => new AutomationRunDto(
                r.Id, r.RuleId, r.RuleName, r.EntityType, r.EntityId, r.EntityName,
                r.Status, r.Detail, r.CreatedAtUtc))
            .ToListAsync(ct);
    }
}

// ── Admin → Plans ─────────────────────────────────────────────────────

public sealed record PlanAutomationLimitDto(Guid PlanId, int MaxAutomationRules);

public class GetPlanAutomationLimitHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    public GetPlanAutomationLimitHandler(FlowDbContext db) => _db = db;

    public async Task<PlanAutomationLimitDto?> Handle(Guid planId, CancellationToken ct = default)
    {
        var max = await _db.Plans.AsNoTracking()
            .Where(p => p.Id == planId)
            .Select(p => (int?)p.MaxAutomationRules)
            .FirstOrDefaultAsync(ct);
        return max is null ? null : new PlanAutomationLimitDto(planId, max.Value);
    }
}

public class SetPlanAutomationLimitHandler : ICommandHandler
{
    public const int Ceiling = 1000;

    private readonly FlowDbContext _db;
    private readonly ILogger<SetPlanAutomationLimitHandler> _logger;

    public SetPlanAutomationLimitHandler(FlowDbContext db, ILogger<SetPlanAutomationLimitHandler> logger)
    {
        _db = db; _logger = logger;
    }

    public async Task Handle(Guid planId, int max, string? actorName, CancellationToken ct = default)
    {
        if (max < 0 || max > Ceiling)
            throw new InvalidOperationException($"The automation limit must be between 0 and {Ceiling}.");

        var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Id == planId, ct)
            ?? throw new KeyNotFoundException("That plan no longer exists.");

        plan.MaxAutomationRules = max;
        plan.UpdatedAtUtc = DateTime.UtcNow;
        plan.UpdatedBy = actorName;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Plan {Plan} automation limit set to {Max} by {By}", plan.Name, max, actorName);
    }
}

// ── Entity ↔ DTO ──────────────────────────────────────────────────────

public static class AutomationRuleMapper
{
    public static AutomationRuleDto ToDto(AutomationRule r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Description = r.Description,
        EntityType = r.EntityType,
        Trigger = r.Trigger,
        TriggerField = r.TriggerField,
        TriggerFromValue = r.TriggerFromValue,
        TriggerToValue = r.TriggerToValue,
        Conditions = AutomationJson.Conditions(r.ConditionsJson),
        Actions = AutomationJson.Actions(r.ActionsJson),
        IsActive = r.IsActive,
        TimeKind = r.TimeKind,
        TimeDays = r.TimeDays,
        RunCount = r.RunCount,
        LastRunAtUtc = r.LastRunAtUtc
    };
}
