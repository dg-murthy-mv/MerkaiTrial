// =====================================================================
// AutomationRule.cs
// Location: MerkaiTrial.Domain/Entities/AutomationRule.cs
//
// NEW FILE (098 — workflow automation).
// 099: time-based rules — TimeKind / TimeDays on the rule, and
//      AutomationTimeFire, the record of which "episode" a time rule has
//      already acted on for a record, so it never fires twice for it.
//
// A RULE IS A SENTENCE
//   "When a deal moves to Negotiation, if its value is over 5,00,000,
//    give the owner a task and notify the sales manager."
//
//     When   Trigger + TriggerField / TriggerToValue / TriggerFromValue
//     If     ConditionsJson   — every condition must hold (AND)
//     Then   ActionsJson      — run in order; one failing does not stop
//                               the others, and never undoes the change
//                               that fired the rule
//
// WHY THE CONDITIONS AND ACTIONS ARE JSON
//   Both are short, ordered lists that are always read and written WITH
//   their rule, never queried on their own. Two child tables would mean
//   two more DbSets, two more tenant filters and a delete cascade to get
//   right, for lists of one to five entries. The shapes are fixed in
//   Application/Commands/Automation/AutomationModel.cs, which is the only
//   code that reads or writes them.
//
// TENANT-OWNED, BOTH TABLES. FlowDbContext filters them, and the coverage
// assertion there would refuse to start without that.
// =====================================================================

namespace MerkaiTrial.Domain.Entities
{
    /// <summary>What sets a rule off. Stored as an int: add at the END.</summary>
    public enum AutomationTrigger
    {
        /// <summary>A lead or deal is added — the form, an import, a conversion, the website form.</summary>
        RecordCreated = 0,

        /// <summary>One field changes — optionally FROM and/or TO a particular value.</summary>
        FieldChanged = 1,

        /// <summary>
        /// Checked by the background worker every few minutes (099): no
        /// activity for N days, a deal's close date coming up or passed,
        /// stuck in the same status / stage for N days. See TimeKind.
        /// </summary>
        TimeBased = 2
    }

    public class AutomationRule
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }

        /// <summary>What the admin calls it — "Big deals go to Arjun".</summary>
        public string Name { get; set; } = string.Empty;

        public string? Description { get; set; }

        /// <summary>"Lead" or "Deal".</summary>
        public string EntityType { get; set; } = string.Empty;

        public AutomationTrigger Trigger { get; set; }

        /// <summary>For FieldChanged: the field key ("status", "stage", "owner", "value"…).</summary>
        public string? TriggerField { get; set; }

        /// <summary>For FieldChanged: only when it changes FROM this value. Null = from anything.</summary>
        public string? TriggerFromValue { get; set; }

        /// <summary>For FieldChanged: only when it changes TO this value. Null = to anything.</summary>
        public string? TriggerToValue { get; set; }

        /// <summary>The "If" part. A JSON array of conditions; "[]" = always.</summary>
        public string ConditionsJson { get; set; } = "[]";

        /// <summary>The "Then" part. A JSON array of actions, at least one.</summary>
        public string ActionsJson { get; set; } = "[]";

        /// <summary>
        /// 099. For TimeBased: what to watch — "noActivity", "closeSoon",
        /// "closePassed", "stuck" (AutomationTimeKinds in the Application
        /// layer). Null for the other triggers.
        /// </summary>
        public string? TimeKind { get; set; }

        /// <summary>099. For TimeBased: the N in "for N days" / "N days before".</summary>
        public int? TimeDays { get; set; }

        public bool IsActive { get; set; }

        /// <summary>
        /// Where round-robin owner assignment got to. Advanced once per
        /// assignment so leads go to each person in turn, and kept on the
        /// rule so the turn survives a restart.
        /// </summary>
        public int RoundRobinCursor { get; set; }

        /// <summary>How many times the rule has actually done something.</summary>
        public int RunCount { get; set; }

        public DateTime? LastRunAtUtc { get; set; }

        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }
    }

    /// <summary>How one run of a rule ended.</summary>
    public enum AutomationRunStatus
    {
        /// <summary>The conditions held and every action ran.</summary>
        Done = 0,

        /// <summary>The trigger fired but a condition did not hold. Detail says which.</summary>
        Skipped = 1,

        /// <summary>At least one action could not be carried out. Detail says which and why.</summary>
        Failed = 2,

        /// <summary>Some actions ran and some did not.</summary>
        Partial = 3
    }

    /// <summary>
    /// One line of the rule log: which rule, on which record, what it did
    /// or why it did not. Written for the admin who asks "why didn't my
    /// rule fire?" — so a skip is logged as well as a success.
    /// </summary>
    public class AutomationRun
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid RuleId { get; set; }

        /// <summary>The rule's name at the time — the log still reads after a rename or delete.</summary>
        public string RuleName { get; set; } = string.Empty;

        public string EntityType { get; set; } = string.Empty;
        public Guid EntityId { get; set; }

        /// <summary>The lead's name or the deal's title at the time.</summary>
        public string? EntityName { get; set; }

        public AutomationRunStatus Status { get; set; }

        /// <summary>What happened, in a sentence or two.</summary>
        public string? Detail { get; set; }

        public DateTime CreatedAtUtc { get; set; }
    }

    /// <summary>
    /// 099. "This time-based rule has already acted on this record, for this
    /// episode." An EPISODE is identified by the moment the situation began:
    ///
    ///   noActivity   the last activity / note (or when the record was added)
    ///   closeSoon,
    ///   closePassed  the deal's expected close date
    ///   stuck        when the record entered its current status / stage
    ///
    /// So a deal that goes cold fires once; if someone logs a call, the
    /// anchor moves, and if it goes cold again that is a new episode and the
    /// rule fires again. Moving a close date starts a new episode too.
    /// </summary>
    public class AutomationTimeFire
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid RuleId { get; set; }
        public Guid EntityId { get; set; }
        public DateTime EpisodeAtUtc { get; set; }
        public DateTime FiredAtUtc { get; set; }
    }
}
