// =====================================================================
// ProcessTransition.cs
// Location: MerkaiTrial.Domain/Entities/ProcessTransition.cs
//
// NEW FILE (020 — Blueprint transitions).
//
// WHAT THIS IS
//   One allowed move in a tenant's sales process: from this stage, to
//   that stage, called this, doable by these people, and only once the
//   deal has these things.
//
//   The set of rows IS the process. A tenant with no row from Proposal
//   to Discovery cannot make that move, and the deal page never offers
//   it. A tenant who wants a free-for-all keeps every row, which is what
//   the migration seeds.
//
// WHY THE REQUIREMENTS MOVED OFF PipelineStage
//   019 put them on the stage: "a deal entering Closed Won needs an
//   accepted quote." That reads well until you reopen a deal. Going back
//   into Closed Won after a correction is the SAME stage and a completely
//   different question — the quote was accepted months ago and the move
//   is an admin fixing a mistake. On the stage there is one answer; on
//   the transition there are two, which is the honest number.
//
//   It also generalises the lost reason. 019 had two hard-coded cases —
//   "why was it lost", "why are you reopening" — written into the guard.
//   Here any transition can ask for a note with its own prompt, so a
//   tenant can put "Which site did you survey?" on Qualification →
//   Proposal without a line of code.
//
// WHAT STAYED BEHIND
//   The invoice block — a won, invoiced deal cannot be reopened — is NOT
//   a transition property. It is a money rule that holds whatever route
//   someone takes, including the admin override. It stays on
//   PipelineRuleSettings.
// =====================================================================

namespace MerkaiTrial.Domain.Entities;

/// <summary>
/// Who may make a move. A ladder: each level includes the ones above it,
/// so a workspace admin can do everything and an owner-only transition is
/// still open to that owner's manager.
///
/// Deliberately a fixed set rather than a picker over the tenant's own
/// roles. These four map onto TeamManagers and Deal.OwnerUserId, which
/// already exist and are already tested, and they cover what people
/// actually configure. Swapping in a role picker later is a wider column,
/// not a redesign.
/// </summary>
public enum TransitionActor
{
    /// <summary>Anyone who can update deals at all.</summary>
    Anyone = 0,

    /// <summary>The deal's owner — plus managers of their team, plus admins.</summary>
    DealOwner = 1,

    /// <summary>Managers of the deal owner's team, plus admins.</summary>
    TeamManagers = 2,

    /// <summary>Workspace admins only.</summary>
    Admins = 3
}

public class ProcessTransition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>
    /// The stage a deal must be in for this move to be offered. Stores the
    /// stage KEY, not its id — the key is what Deal.Stage holds and what
    /// survives a rename.
    /// </summary>
    public string FromStageKey { get; set; } = string.Empty;

    public string ToStageKey { get; set; } = string.Empty;

    /// <summary>
    /// The button. "Send Quote", "Mark as Lost", "Reopen". This is the
    /// tenant's own language for the move, which is the whole reason the
    /// deal page shows buttons rather than a stage dropdown: a dropdown
    /// says where the deal ends up, a button says what you are doing.
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Left-to-right order of the buttons on the deal page.</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Retired transitions stay so history still reads correctly, but are
    /// not offered and are refused if posted.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public TransitionActor Actor { get; set; } = TransitionActor.Anyone;

    // ── What the deal must already have ───────────────────────────────

    /// <summary>A quote exists for the deal, in any state.</summary>
    public bool RequiresQuote { get; set; }

    /// <summary>A quote on the deal has been accepted. Implies RequiresQuote.</summary>
    public bool RequiresAcceptedQuote { get; set; }

    /// <summary>The expected value is above zero.</summary>
    public bool RequiresValue { get; set; }

    /// <summary>The expected close date is filled in.</summary>
    public bool RequiresCloseDate { get; set; }

    /// <summary>
    /// The person must type something, which is kept on the stage history
    /// row. This is 019's lost reason and reopen reason generalised —
    /// either one is now just a transition with RequiresNote and its own
    /// prompt.
    /// </summary>
    public bool RequiresNote { get; set; }

    /// <summary>
    /// What to ask. "Why was this deal lost?" Falls back to a generic
    /// prompt when empty, but a specific question gets a far better answer
    /// than a blank box does.
    /// </summary>
    public string? NotePrompt { get; set; }

    /// <summary>
    /// At least one attachment on the deal. For the signed order form, the
    /// site survey, the PO — the documents a process actually turns on.
    /// </summary>
    public bool RequiresAttachment { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }

    // Navigation deliberately omitted. A transition points at two stages
    // by (TenantId, Key), and two navigations to the same entity on one
    // row is more trouble in EF than it is worth for a lookup the
    // TransitionCatalog does once per request anyway.

    /// <summary>True when this move asks anything of the deal at all.</summary>
    public bool HasRequirements =>
        RequiresQuote || RequiresAcceptedQuote || RequiresValue ||
        RequiresCloseDate || RequiresNote || RequiresAttachment;

    /// <summary>The prompt to show, never empty.</summary>
    public string EffectiveNotePrompt =>
        string.IsNullOrWhiteSpace(NotePrompt)
            ? "Add a note for this change"
            : NotePrompt!;
}

/// <summary>
/// Labels the migration and the "Suggest a process" button use when they
/// invent a transition. Kept here so the SQL seed and the C# generator
/// cannot drift into wording each one differently.
/// </summary>
public static class TransitionLabels
{
    public const string ReopenPrefix = "Reopen";
    public const string ClosePrefix = "Mark as";
    public const string MovePrefix = "Move to";

    public const string ReopenPrompt = "Why are you reopening this deal?";
    public const string LostPrompt = "Why was this deal lost?";

    /// <summary>
    /// "Mark as Closed Won" for a terminal stage, "Reopen — Discovery"
    /// coming back out of one, "Move to Proposal" otherwise.
    /// </summary>
    public static string For(
        StageCategory fromCategory, StageCategory toCategory, string toStageName)
    {
        var leavingClosed = fromCategory is StageCategory.Won or StageCategory.Lost;

        if (leavingClosed && toCategory == StageCategory.Open)
            return $"{ReopenPrefix} — {toStageName}";

        if (toCategory is StageCategory.Won or StageCategory.Lost)
            return $"{ClosePrefix} {toStageName}";

        return $"{MovePrefix} {toStageName}";
    }
}
