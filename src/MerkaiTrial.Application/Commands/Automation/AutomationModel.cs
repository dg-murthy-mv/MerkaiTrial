// =====================================================================
// AutomationModel.cs
// Location: MerkaiTrial.Application/Commands/Automation/AutomationModel.cs
//
// NEW FILE (098 — workflow automation).
//
// Everything that defines what a rule CAN say, in one place:
//
//   AutomationFields     the fields a rule can watch, test and set, per
//                        kind of record, and what each one is
//   AutomationCondition  one line of the "If" block
//   AutomationAction     one line of the "Then" block
//   AutomationRuleDto    a whole rule, as the editor and the API see it
//   AutomationRuleCheck  the server's validation — the editor's limits are
//                        a convenience, these are the rules
//   AutomationDescriber  the plain-English sentence on the rules list
//
// THE JSON COLUMNS
//   AutomationRule.ConditionsJson / ActionsJson hold List<AutomationCondition>
//   and List<AutomationAction>, serialised with AutomationJson.Options and
//   nothing else. A field that is not here cannot be stored.
//
// WHAT A FIELD KEY IS
//   Built-in fields have a fixed key: "status", "stage", "owner", "value",
//   "probability", "source", "company". A custom field's key is
//   "cf:" + its definition id, so a rule keeps pointing at the right field
//   when the field is renamed.
//
// 099
//   • TIME-BASED rules: AutomationTimeKinds, TimeKind / TimeDays on the
//     rule DTO, their checks and their sentence.
//   • Custom fields can now be WATCHED ("when Tier changes to Gold") —
//     AutomationOptionsBuilder marks them CanTrigger; nothing here changed
//     for that beyond the editor hint.
// =====================================================================

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MerkaiTrial.Domain.Entities;

namespace MerkaiTrial.Application.Commands.Automation;

// ── Records a rule can act on ─────────────────────────────────────────

public static class AutomationEntity
{
    public const string Lead = "Lead";
    public const string Deal = "Deal";

    public static readonly IReadOnlyList<string> All = new[] { Lead, Deal };

    public static string? Normalise(string? value)
        => All.FirstOrDefault(e => string.Equals(e, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

// ── Fields ────────────────────────────────────────────────────────────

/// <summary>What kind of value a field holds. Decides the operators and the input on the editor.</summary>
public static class AutomationFieldKind
{
    public const string Choice   = "choice";     // a list: statuses, stages, sources, a dropdown field
    public const string User     = "user";       // a person in this workspace
    public const string Number   = "number";
    public const string Text     = "text";
    public const string Date     = "date";       // yyyy-MM-dd
    public const string Checkbox = "checkbox";   // "true" or empty
}

public static class AutomationFields
{
    public const string Status      = "status";        // lead
    public const string Stage       = "stage";         // deal
    public const string Owner       = "owner";         // both
    public const string Value       = "value";         // both: estimated / expected value
    public const string Probability = "probability";   // deal
    public const string Source      = "source";        // lead
    public const string Company     = "company";       // lead: the company name typed on it

    public const string CustomPrefix = "cf:";

    public static string CustomKey(Guid definitionId) => CustomPrefix + definitionId.ToString("D");

    public static Guid? CustomId(string? key)
        => key is not null && key.StartsWith(CustomPrefix, StringComparison.Ordinal)
           && Guid.TryParse(key[CustomPrefix.Length..], out var id) ? id : null;

    /// <summary>One built-in field. Custom fields are added from the workspace's definitions.</summary>
    public sealed record Builtin(string Key, string Label, string Kind, bool CanTrigger, bool CanCondition, bool CanUpdate);

    /// <summary>
    /// The built-in fields, per record. CanTrigger = a rule can fire when it
    /// changes; CanCondition = an "If" line can test it; CanUpdate = a
    /// "Set a field" action can change it.
    ///
    /// A deal's STAGE is deliberately not updatable: a stage move goes
    /// through the sales process — who may make it, what it needs, its own
    /// follow-ups — and a rule quietly bypassing all of that would be the
    /// very thing the process exists to stop.
    /// </summary>
    public static IReadOnlyList<Builtin> BuiltinsFor(string entityType) => entityType switch
    {
        AutomationEntity.Lead => new[]
        {
            new Builtin(Status,  "Status",          AutomationFieldKind.Choice, true,  true, true),
            new Builtin(Owner,   "Owner",           AutomationFieldKind.User,   true,  true, true),
            new Builtin(Value,   "Estimated value", AutomationFieldKind.Number, true,  true, false),
            new Builtin(Source,  "Source",          AutomationFieldKind.Choice, false, true, false),
            new Builtin(Company, "Company name",    AutomationFieldKind.Text,   false, true, false),
        },
        AutomationEntity.Deal => new[]
        {
            new Builtin(Stage,       "Stage",          AutomationFieldKind.Choice, true, true, false),
            new Builtin(Owner,       "Owner",          AutomationFieldKind.User,   true, true, true),
            new Builtin(Value,       "Expected value", AutomationFieldKind.Number, true, true, false),
            new Builtin(Probability, "Probability %",  AutomationFieldKind.Number, true, true, true),
        },
        _ => Array.Empty<Builtin>()
    };

    /// <summary>A custom field type → the kind a rule treats it as.</summary>
    public static string KindOfCustom(string fieldType) => fieldType switch
    {
        "number"   => AutomationFieldKind.Number,
        "date"     => AutomationFieldKind.Date,
        "dropdown" => AutomationFieldKind.Choice,
        "checkbox" => AutomationFieldKind.Checkbox,
        _          => AutomationFieldKind.Text
    };
}

// ── 099: what a time-based rule can watch ─────────────────────────────

/// <summary>
/// The time-based situations. Checked by the automation worker every few
/// minutes; each fires once per record per EPISODE (see AutomationTimeFire).
/// </summary>
public static class AutomationTimeKinds
{
    /// <summary>Nothing logged, completed or noted for N days (lead or deal).</summary>
    public const string NoActivity  = "noActivity";
    /// <summary>N days before an open deal's expected close date (0 = on the day).</summary>
    public const string CloseSoon   = "closeSoon";
    /// <summary>N days after an open deal's expected close date (1 = the day after it passes).</summary>
    public const string ClosePassed = "closePassed";
    /// <summary>Still in the same status (lead) or stage (deal) N days after getting there.</summary>
    public const string Stuck       = "stuck";

    public sealed record Kind(string Key, string Label, int MinDays, int MaxDays, int DefaultDays);

    public static IReadOnlyList<Kind> For(string entityType) => entityType switch
    {
        AutomationEntity.Lead => new[]
        {
            new Kind(NoActivity, "has had no activity for", 1, 365, 7),
            new Kind(Stuck,      "has stayed in the same status for", 1, 365, 14),
        },
        AutomationEntity.Deal => new[]
        {
            new Kind(NoActivity,  "has had no activity for", 1, 365, 7),
            new Kind(Stuck,       "has stayed in the same stage for", 1, 365, 14),
            new Kind(CloseSoon,   "is this many days from its expected close date:", 0, 90, 3),
            new Kind(ClosePassed, "is still open this many days after its expected close date:", 1, 365, 1),
        },
        _ => Array.Empty<Kind>()
    };

    public static Kind? Find(string entityType, string? key)
        => For(entityType).FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.Ordinal));

    /// <summary>"has had no activity for 7 days" — the WHEN of the rule's sentence.</summary>
    public static string Describe(string entityType, string? key, int? days)
    {
        var noun = entityType == AutomationEntity.Deal ? "deal" : "lead";
        var n = days ?? 0;
        string Days(int d) => d == 1 ? "1 day" : $"{d} days";
        return key switch
        {
            NoActivity  => $"When a {noun} has had no activity for {Days(n)}",
            Stuck       => $"When a {noun} has stayed in the same {(noun == "deal" ? "stage" : "status")} for {Days(n)}",
            CloseSoon   => n == 0 ? "On a deal's expected close date" : $"{Days(n)} before a deal's expected close date",
            ClosePassed => $"When a deal is still open {Days(n)} after its expected close date",
            _           => $"On a schedule ({noun}s)"
        };
    }
}

// ── Operators ─────────────────────────────────────────────────────────

public static class AutomationOp
{
    public const string Is        = "eq";
    public const string IsNot     = "neq";
    public const string Greater   = "gt";
    public const string AtLeast   = "gte";
    public const string Less      = "lt";
    public const string AtMost    = "lte";
    public const string Contains  = "contains";
    public const string IsEmpty   = "empty";
    public const string NotEmpty  = "notEmpty";

    /// <summary>Which operators make sense for a kind of field — the editor offers these, the server accepts these.</summary>
    public static IReadOnlyList<string> For(string kind) => kind switch
    {
        AutomationFieldKind.Number   => new[] { Is, IsNot, Greater, AtLeast, Less, AtMost, IsEmpty, NotEmpty },
        AutomationFieldKind.Date     => new[] { Is, Greater, Less, IsEmpty, NotEmpty },
        AutomationFieldKind.Text     => new[] { Is, IsNot, Contains, IsEmpty, NotEmpty },
        AutomationFieldKind.Checkbox => new[] { NotEmpty, IsEmpty },
        _                            => new[] { Is, IsNot, IsEmpty, NotEmpty }   // choice, user
    };

    public static bool NeedsValue(string op) => op is not (IsEmpty or NotEmpty);

    /// <summary>The words on the rules list: "Value is more than 5,00,000".</summary>
    public static string Words(string op, string kind) => op switch
    {
        Is        => kind == AutomationFieldKind.Date ? "is on" : "is",
        IsNot     => "is not",
        Greater   => kind == AutomationFieldKind.Date ? "is after" : "is more than",
        AtLeast   => "is at least",
        Less      => kind == AutomationFieldKind.Date ? "is before" : "is less than",
        AtMost    => "is at most",
        Contains  => "contains",
        IsEmpty   => kind switch
        {
            AutomationFieldKind.User     => "is unassigned",
            AutomationFieldKind.Checkbox => "is not ticked",
            _                            => "is empty"
        },
        NotEmpty  => kind switch
        {
            AutomationFieldKind.User     => "is assigned",
            AutomationFieldKind.Checkbox => "is ticked",
            _                            => "is filled in"
        },
        _ => op
    };
}

// ── The "If" and "Then" lines ────────────────────────────────────────

public sealed class AutomationCondition
{
    public string Field { get; set; } = string.Empty;
    public string Op { get; set; } = AutomationOp.Is;
    public string? Value { get; set; }
}

public static class AutomationActionKind
{
    public const string CreateTask  = "task";
    public const string Notify      = "notify";
    public const string UpdateField = "update";

    public static readonly IReadOnlyList<string> All = new[] { CreateTask, Notify, UpdateField };
}

/// <summary>Who a task or a message goes to. Resolved when the rule runs, never stored as a person.</summary>
public static class AutomationPerson
{
    public const string Owner = "owner";     // the record's owner at the time
    public const string Actor = "actor";     // whoever made the change that fired the rule
    public const string User  = "user";      // a named person (UserId)
}

/// <summary>How "Set a field" sets an owner.</summary>
public static class AutomationUpdateMode
{
    public const string Set        = "set";
    public const string RoundRobin = "roundRobin";
}

/// <summary>
/// One action. A single shape for all three kinds — only the properties
/// for its Kind are read. Kept flat because it lives in a JSON column and
/// in a hidden field on the editor, where a flat object is easiest to keep
/// right on both ends.
/// </summary>
public sealed class AutomationAction
{
    public string Kind { get; set; } = AutomationActionKind.CreateTask;

    // ── task ──────────────────────────────────────────────────────────
    public string? Subject { get; set; }
    public string? Description { get; set; }
    public string? ActivityType { get; set; }
    public int DueInDays { get; set; } = 1;

    // ── task and notify: who ──────────────────────────────────────────
    public string? AssignTo { get; set; }      // AutomationPerson
    public string? UserId { get; set; }

    // ── notify ────────────────────────────────────────────────────────
    public string? Message { get; set; }

    // ── update ────────────────────────────────────────────────────────
    public string? Field { get; set; }
    public string? Value { get; set; }
    public string? Mode { get; set; }          // AutomationUpdateMode, owner only
    public List<string>? UserIds { get; set; } // round robin
}

public static class AutomationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static List<AutomationCondition> Conditions(string? json) => Read<AutomationCondition>(json);
    public static List<AutomationAction> Actions(string? json) => Read<AutomationAction>(json);

    public static string Write<T>(IEnumerable<T> items) => JsonSerializer.Serialize(items.ToList(), Options);

    /// <summary>Unreadable JSON reads as an empty list — a rule with no actions does nothing, and says so in the log.</summary>
    private static List<T> Read<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<T>();
        try { return JsonSerializer.Deserialize<List<T>>(json, Options) ?? new List<T>(); }
        catch (JsonException) { return new List<T>(); }
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────

/// <summary>A whole rule. The editor sends one of these to save; the API returns one to edit.</summary>
public sealed class AutomationRuleDto
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string EntityType { get; set; } = AutomationEntity.Lead;
    public AutomationTrigger Trigger { get; set; } = AutomationTrigger.RecordCreated;
    public string? TriggerField { get; set; }
    public string? TriggerFromValue { get; set; }
    public string? TriggerToValue { get; set; }
    public List<AutomationCondition> Conditions { get; set; } = new();
    public List<AutomationAction> Actions { get; set; } = new();
    public bool IsActive { get; set; }

    /// <summary>099. For a time-based rule: AutomationTimeKinds key.</summary>
    public string? TimeKind { get; set; }

    /// <summary>099. For a time-based rule: the N days.</summary>
    public int? TimeDays { get; set; }

    // Read-only, for the editor's header.
    public int RunCount { get; set; }
    public DateTime? LastRunAtUtc { get; set; }
}

public sealed record AutomationRuleListItem(
    Guid Id,
    string Name,
    string EntityType,
    bool IsActive,
    string Summary,
    int RunCount,
    DateTime? LastRunAtUtc,
    DateTime? UpdatedAtUtc,
    /// <summary>099. Checked by the clock rather than set off by a change.</summary>
    bool IsTimeBased = false);

/// <param name="Limit">How many rules the plan allows switched on. 0 = automation not in the plan.</param>
public sealed record AutomationRulesPageDto(
    List<AutomationRuleListItem> Rules,
    int Limit,
    int ActiveCount,
    string? PlanName);

public sealed record AutomationRunDto(
    Guid Id,
    Guid RuleId,
    string RuleName,
    string EntityType,
    Guid EntityId,
    string? EntityName,
    AutomationRunStatus Status,
    string? Detail,
    DateTime CreatedAtUtc);

public sealed record AutomationOptionDto(string Value, string Label);

public sealed record AutomationFieldDto(
    string Key,
    string Label,
    string Kind,
    bool CanTrigger,
    bool CanCondition,
    bool CanUpdate,
    List<AutomationOptionDto>? Options,
    /// <summary>For "Set a field": choices a rule may SET (a status list without Converted).</summary>
    List<AutomationOptionDto>? SettableOptions = null);

/// <summary>Everything the editor's dropdowns need, in one call.</summary>
public sealed record AutomationOptionsDto(
    Dictionary<string, List<AutomationFieldDto>> Fields,
    List<AutomationOptionDto> Users,
    List<string> TaskTypes,
    int Limit,
    int ActiveCount,
    /// <summary>099. Per kind of record, the time-based situations a rule can watch.</summary>
    Dictionary<string, List<AutomationTimeKindDto>>? TimeKinds = null);

/// <summary>099. One time-based situation, as the editor offers it.</summary>
public sealed record AutomationTimeKindDto(string Key, string Label, int MinDays, int MaxDays, int DefaultDays);

public sealed record SetAutomationRuleActiveDto(bool IsActive);

// ── Validation ────────────────────────────────────────────────────────

/// <summary>
/// The server's checks on a rule before it is saved. Every problem at
/// once, as one InvalidOperationException whose message is a list a person
/// can work through — the editor shows it as it is.
/// </summary>
public static class AutomationRuleCheck
{
    public const int MaxNameLength       = 150;
    public const int MaxDescriptionLength = 500;
    public const int MaxConditions       = 10;
    public const int MaxActions          = 5;
    public const int MaxSubjectLength    = 200;
    public const int MaxMessageLength    = 500;
    public const int MaxRoundRobinPeople = 50;

    public static void ThrowIfInvalid(AutomationRuleDto rule, AutomationOptionsDto options)
    {
        var errors = new List<string>();

        var name = rule.Name?.Trim() ?? string.Empty;
        if (name.Length == 0) errors.Add("Give the rule a name.");
        if (name.Length > MaxNameLength) errors.Add($"Keep the name to {MaxNameLength} characters.");
        if ((rule.Description?.Trim().Length ?? 0) > MaxDescriptionLength)
            errors.Add($"Keep the description to {MaxDescriptionLength} characters.");

        var entity = AutomationEntity.Normalise(rule.EntityType);
        if (entity is null)
        {
            errors.Add("Choose whether the rule is about leads or deals.");
            Throw(errors);
            return;
        }

        var fields = options.Fields.TryGetValue(entity, out var list)
            ? list.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, AutomationFieldDto>(StringComparer.OrdinalIgnoreCase);

        var users = options.Users.Select(u => u.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var noun = entity == AutomationEntity.Lead ? "lead" : "deal";

        // ── When ──────────────────────────────────────────────────────
        switch (rule.Trigger)
        {
            case AutomationTrigger.RecordCreated:
                break;

            case AutomationTrigger.FieldChanged:
                if (string.IsNullOrWhiteSpace(rule.TriggerField)
                    || !fields.TryGetValue(rule.TriggerField, out var tf) || !tf.CanTrigger)
                {
                    errors.Add($"Choose which {noun} field the rule watches.");
                    break;
                }

                if (!string.IsNullOrWhiteSpace(rule.TriggerToValue)
                    && !ValueFits(tf, rule.TriggerToValue, users, settable: false))
                    errors.Add($"\"{tf.Label}\" can't change to that value — choose one from the list.");

                if (!string.IsNullOrWhiteSpace(rule.TriggerFromValue)
                    && !ValueFits(tf, rule.TriggerFromValue, users, settable: false))
                    errors.Add($"\"{tf.Label}\" can't change from that value — choose one from the list.");

                if (!string.IsNullOrWhiteSpace(rule.TriggerToValue)
                    && string.Equals(rule.TriggerToValue, rule.TriggerFromValue, StringComparison.OrdinalIgnoreCase))
                    errors.Add("\"From\" and \"to\" are the same, so the rule could never fire.");
                break;

            case AutomationTrigger.TimeBased:
            {
                // 099
                var kind = AutomationTimeKinds.Find(entity, rule.TimeKind);
                if (kind is null)
                {
                    errors.Add($"Choose what the rule waits for on a {noun}.");
                    break;
                }
                if (rule.TimeDays is not { } d || d < kind.MinDays || d > kind.MaxDays)
                    errors.Add($"The number of days must be between {kind.MinDays} and {kind.MaxDays}.");
                break;
            }

            default:
                errors.Add("Choose what sets the rule off.");
                break;
        }

        // ── If ────────────────────────────────────────────────────────
        if (rule.Conditions.Count > MaxConditions)
            errors.Add($"A rule can test at most {MaxConditions} things.");

        for (var i = 0; i < rule.Conditions.Count; i++)
        {
            var c = rule.Conditions[i];
            var n = i + 1;

            if (string.IsNullOrWhiteSpace(c.Field) || !fields.TryGetValue(c.Field, out var f) || !f.CanCondition)
            {
                errors.Add($"Condition {n}: choose a field.");
                continue;
            }

            if (!AutomationOp.For(f.Kind).Contains(c.Op))
            {
                errors.Add($"Condition {n}: \"{f.Label}\" can't be compared that way.");
                continue;
            }

            if (AutomationOp.NeedsValue(c.Op))
            {
                if (string.IsNullOrWhiteSpace(c.Value))
                    errors.Add($"Condition {n}: fill in what \"{f.Label}\" should be compared with.");
                else if (!ValueFits(f, c.Value, users, settable: false))
                    errors.Add($"Condition {n}: \"{c.Value}\" isn't a valid value for \"{f.Label}\".");
            }
        }

        // ── Then ──────────────────────────────────────────────────────
        if (rule.Actions.Count == 0)
            errors.Add("Add at least one thing the rule does.");
        if (rule.Actions.Count > MaxActions)
            errors.Add($"A rule can do at most {MaxActions} things.");

        for (var i = 0; i < rule.Actions.Count; i++)
        {
            var a = rule.Actions[i];
            var n = i + 1;

            switch (a.Kind)
            {
                case AutomationActionKind.CreateTask:
                    if (string.IsNullOrWhiteSpace(a.Subject))
                        errors.Add($"Action {n}: give the task a title.");
                    else if (a.Subject.Trim().Length > MaxSubjectLength)
                        errors.Add($"Action {n}: keep the task title to {MaxSubjectLength} characters.");
                    if (a.DueInDays is < 0 or > 365)
                        errors.Add($"Action {n}: the task can be due between 0 and 365 days later.");
                    if (!string.IsNullOrWhiteSpace(a.ActivityType) && !options.TaskTypes.Contains(a.ActivityType))
                        errors.Add($"Action {n}: \"{a.ActivityType}\" isn't a kind of task.");
                    CheckPerson(a, n, "task", users, errors, rule.Trigger);
                    break;

                case AutomationActionKind.Notify:
                    if (string.IsNullOrWhiteSpace(a.Message))
                        errors.Add($"Action {n}: write the message.");
                    else if (a.Message.Trim().Length > MaxMessageLength)
                        errors.Add($"Action {n}: keep the message to {MaxMessageLength} characters.");
                    CheckPerson(a, n, "message", users, errors, rule.Trigger);
                    break;

                case AutomationActionKind.UpdateField:
                    if (string.IsNullOrWhiteSpace(a.Field) || !fields.TryGetValue(a.Field, out var uf) || !uf.CanUpdate)
                    {
                        errors.Add($"Action {n}: choose the field to set.");
                        break;
                    }

                    if (uf.Kind == AutomationFieldKind.User && a.Mode == AutomationUpdateMode.RoundRobin)
                    {
                        var people = (a.UserIds ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        if (people.Count < 2)
                            errors.Add($"Action {n}: take turns needs at least two people.");
                        else if (people.Count > MaxRoundRobinPeople)
                            errors.Add($"Action {n}: take turns can share between at most {MaxRoundRobinPeople} people.");
                        else if (people.Any(p => !users.Contains(p)))
                            errors.Add($"Action {n}: one of the people to take turns is no longer active in this workspace.");
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(a.Value))
                    {
                        // Clearing a CUSTOM field is a real thing to want; an owner,
                        // a status or a probability always needs a value.
                        if (AutomationFields.CustomId(uf.Key) is null)
                            errors.Add($"Action {n}: choose what \"{uf.Label}\" becomes.");
                        break;
                    }

                    if (!ValueFits(uf, a.Value, users, settable: true))
                        errors.Add($"Action {n}: \"{a.Value}\" isn't a value \"{uf.Label}\" can be set to.");
                    break;

                default:
                    errors.Add($"Action {n}: choose what it does.");
                    break;
            }
        }

        Throw(errors);
    }

    private static void CheckPerson(AutomationAction a, int n, string what, HashSet<string> users, List<string> errors,
        AutomationTrigger trigger)
    {
        switch (a.AssignTo)
        {
            case AutomationPerson.Owner:
                return;
            case AutomationPerson.Actor:
                // 099: a time-based rule is set off by the clock, not a person.
                if (trigger == AutomationTrigger.TimeBased)
                    errors.Add($"Action {n}: a time-based rule has no \"person who made the change\" — choose the owner or a particular person.");
                return;
            case AutomationPerson.User:
                if (string.IsNullOrWhiteSpace(a.UserId) || !users.Contains(a.UserId))
                    errors.Add($"Action {n}: choose who gets the {what}.");
                return;
            default:
                errors.Add($"Action {n}: choose who gets the {what}.");
                return;
        }
    }

    /// <summary>Does a typed or chosen value make sense for this field?</summary>
    public static bool ValueFits(AutomationFieldDto field, string value, HashSet<string> users, bool settable)
    {
        var v = value.Trim();

        switch (field.Kind)
        {
            case AutomationFieldKind.User:
                return users.Contains(v);

            case AutomationFieldKind.Choice:
                var choices = settable ? (field.SettableOptions ?? field.Options) : field.Options;
                return choices is null || choices.Any(o => string.Equals(o.Value, v, StringComparison.OrdinalIgnoreCase));

            case AutomationFieldKind.Number:
                if (!decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) return false;
                return field.Key != AutomationFields.Probability || d is >= 0 and <= 100;

            case AutomationFieldKind.Date:
                return DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

            case AutomationFieldKind.Checkbox:
                return v is "true" or "false";

            default:
                return v.Length <= 500;
        }
    }

    private static void Throw(List<string> errors)
    {
        if (errors.Count == 0) return;
        throw new InvalidOperationException(errors.Count == 1
            ? errors[0]
            : "Please fix these: " + string.Join(" · ", errors));
    }
}

// ── The sentence on the rules list ────────────────────────────────────

public static class AutomationDescriber
{
    /// <summary>
    /// "When a deal moves to Negotiation · if Expected value is more than
    /// 500000 · then create a task and notify the owner". Names, not keys:
    /// a stage renamed tomorrow reads correctly tomorrow.
    /// </summary>
    public static string Describe(AutomationRuleDto rule, AutomationOptionsDto options)
    {
        var entity = AutomationEntity.Normalise(rule.EntityType) ?? AutomationEntity.Lead;
        var noun = entity == AutomationEntity.Lead ? "lead" : "deal";

        var fields = options.Fields.TryGetValue(entity, out var list)
            ? list.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, AutomationFieldDto>(StringComparer.OrdinalIgnoreCase);

        string Label(string? key) => key is not null && fields.TryGetValue(key, out var f) ? f.Label : "a field";

        string ValueName(string? key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "—";
            if (key is not null && fields.TryGetValue(key, out var f))
            {
                if (f.Kind == AutomationFieldKind.User)
                    return options.Users.FirstOrDefault(u => string.Equals(u.Value, value, StringComparison.OrdinalIgnoreCase))?.Label
                           ?? "someone no longer here";
                var opt = (f.Options ?? new()).Concat(f.SettableOptions ?? new())
                    .FirstOrDefault(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase));
                if (opt is not null) return opt.Label;
            }
            return value;
        }

        var when = rule.Trigger switch
        {
            AutomationTrigger.RecordCreated => $"When a {noun} is added",
            AutomationTrigger.FieldChanged => DescribeChange(),
            AutomationTrigger.TimeBased => AutomationTimeKinds.Describe(entity, rule.TimeKind, rule.TimeDays),
            _ => "When …"
        };

        string DescribeChange()
        {
            var field = rule.TriggerField;
            var to = rule.TriggerToValue;
            var from = rule.TriggerFromValue;

            if (string.Equals(field, AutomationFields.Stage, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(to))
                return $"When a deal moves to {ValueName(field, to)}" + (string.IsNullOrWhiteSpace(from) ? "" : $" from {ValueName(field, from)}");

            var text = $"When a {noun}'s {Label(field).ToLowerInvariant()} changes";
            if (!string.IsNullOrWhiteSpace(from)) text += $" from {ValueName(field, from)}";
            if (!string.IsNullOrWhiteSpace(to)) text += $" to {ValueName(field, to)}";
            return text;
        }

        var parts = new List<string> { when };

        if (rule.Conditions.Count > 0)
        {
            var ifs = rule.Conditions.Select(c =>
            {
                var kind = fields.TryGetValue(c.Field, out var f) ? f.Kind : AutomationFieldKind.Text;
                var words = AutomationOp.Words(c.Op, kind);
                return AutomationOp.NeedsValue(c.Op)
                    ? $"{Label(c.Field)} {words} {ValueName(c.Field, c.Value)}"
                    : $"{Label(c.Field)} {words}";
            });
            parts.Add("if " + string.Join(" and ", ifs));
        }

        var thens = rule.Actions.Select(a => a.Kind switch
        {
            AutomationActionKind.CreateTask => $"create a task for {Person(a)}",
            AutomationActionKind.Notify => $"notify {Person(a)}",
            AutomationActionKind.UpdateField when a.Mode == AutomationUpdateMode.RoundRobin
                => $"share out the {Label(a.Field).ToLowerInvariant()} between {(a.UserIds?.Count ?? 0)} people",
            AutomationActionKind.UpdateField
                => $"set {Label(a.Field).ToLowerInvariant()} to {ValueName(a.Field, a.Value)}",
            _ => "…"
        }).ToList();

        string Person(AutomationAction a) => a.AssignTo switch
        {
            AutomationPerson.Owner => "the owner",
            AutomationPerson.Actor => "whoever made the change",
            AutomationPerson.User => options.Users.FirstOrDefault(u => string.Equals(u.Value, a.UserId, StringComparison.OrdinalIgnoreCase))?.Label
                                     ?? "someone no longer here",
            _ => "someone"
        };

        if (thens.Count > 0)
            parts.Add("then " + (thens.Count == 1 ? thens[0] : string.Join(", ", thens.Take(thens.Count - 1)) + " and " + thens[^1]));

        return string.Join(" · ", parts);
    }
}
