// =====================================================================
// TransitionAction.cs
// Location: MerkaiTrial.Domain/Entities/TransitionAction.cs
//
// NEW FILE (022).
//
// WHAT THIS IS
//   Something the system does FOR you after a deal moves. 020 gave you
//   which step may follow which and what a deal needs first; this is the
//   half where a process stops being a set of rules and starts saving
//   someone work.
//
// TWO KINDS TO BEGIN WITH
//   CreateTask   — a follow-up on somebody's list
//   LogActivity  — a line on the deal's timeline
//
//   Both are built on Activities, which already exists, so each is one
//   call rather than a subsystem. A third kind — send a notification —
//   waits for there to be a notifications system; the bell in the topbar
//   is still a hard-coded red dot. Until then a task assigned to the deal
//   owner IS the notification: it lands on their Tasks page, which is the
//   page a rep opens first each morning.
//
// WHAT AN ACTION MUST NEVER DO
//   Undo the move. If a deal was legitimately marked Won, a broken task
//   template must not reverse that — the move commits first, actions run
//   after, and a failure is logged and reported rather than thrown. See
//   TransitionActionRunner.
// =====================================================================

namespace MerkaiTrial.Domain.Entities;

public enum TransitionActionKind
{
    /// <summary>A follow-up on somebody's list.</summary>
    CreateTask = 0,

    /// <summary>A line on the deal's timeline, recording what happened.</summary>
    LogActivity = 1
}

/// <summary>
/// Who the task goes to. Resolved at the moment of the move, not stored:
/// a deal that changes hands should send its follow-ups to whoever owns
/// it now.
/// </summary>
public enum TransitionAssignee
{
    /// <summary>Whoever owns the deal when the move happens.</summary>
    DealOwner = 0,

    /// <summary>The person who pressed the button.</summary>
    WhoeverMovedIt = 1,

    /// <summary>A named person — a sales admin, an installer, a finance lead.</summary>
    SpecificUser = 2
}

public class TransitionAction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>
    /// The step that causes this. Cascade-deleted with it: an action
    /// describes what happens when a particular move is made, so it has no
    /// meaning once that move is gone.
    /// </summary>
    public Guid TransitionId { get; set; }

    public TransitionActionKind Kind { get; set; } = TransitionActionKind.CreateTask;

    /// <summary>Order actions run in. Two tasks should arrive predictably.</summary>
    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// The task's title, or the activity's subject. May contain the tokens
    /// in <see cref="ActionTokens"/> — "Call {deal} about the site survey"
    /// becomes "Call Sathorn Lobby Refit about the site survey".
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>
    /// ActivityType.Schedulable for a task, ActivityType.Loggable for a
    /// log. Validated by CreateActivityHandler, which is the one place
    /// that knows which types can be scheduled and which can only be
    /// recorded.
    /// </summary>
    public string ActivityType { get; set; } = "Task";

    public TransitionAssignee AssignTo { get; set; } = TransitionAssignee.DealOwner;

    /// <summary>Only when AssignTo is SpecificUser.</summary>
    public Guid? AssignToUserId { get; set; }

    /// <summary>
    /// Days from the move. 0 means today — "call them back today" is a
    /// real instruction, so zero is allowed rather than treated as unset.
    /// Ignored for a logged activity, which records something that has
    /// already happened.
    /// </summary>
    public int DueInDays { get; set; } = 1;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }

    public ProcessTransition? Transition { get; set; }
}

/// <summary>
/// What a tenant may put in a subject or description. Deliberately few:
/// every token is something that has to be resolvable at the moment of
/// the move, and a token that sometimes resolves to nothing reads worse
/// than no token at all.
/// </summary>
public static class ActionTokens
{
    public const string Deal = "{deal}";
    public const string Stage = "{stage}";
    public const string Owner = "{owner}";

    public static readonly IReadOnlyList<string> All = new[] { Deal, Stage, Owner };

    /// <summary>
    /// Fills the tokens. A missing value becomes an empty string rather
    /// than the literal "{owner}" — a task titled "Call {owner}" on
    /// somebody's list is worse than one titled "Call".
    /// </summary>
    public static string Fill(string? text, string? dealTitle, string? stageName, string? ownerName)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        return text
            .Replace(Deal, dealTitle ?? string.Empty)
            .Replace(Stage, stageName ?? string.Empty)
            .Replace(Owner, ownerName ?? string.Empty)
            .Trim();
    }
}

/// <summary>
/// The marker stored in Activity.CreatedBy for anything this feature
/// makes.
///
/// A real user id would read better on the timeline, but it would also
/// claim a person wrote a task they never saw. "process" is honest, it
/// makes these rows findable in one query, and ActivityReadModel already
/// falls back to showing the raw value when it is not a user id — the
/// same way older "seed" rows behave.
/// </summary>
public static class ProcessAuthor
{
    public const string Value = "process";
}
