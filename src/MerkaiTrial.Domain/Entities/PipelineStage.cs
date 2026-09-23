// =====================================================================
// PipelineStage.cs
// Location: MerkaiTrial.Domain/Entities/PipelineStage.cs
//
// COMPLETE FILE — replaces the existing one.
//
// CHANGES (023 — pipeline stage care)
//   ✅ Color        — what the stage looks like on the board.
//   ✅ Description  — what has to be true for a deal to sit here.
//   ✅ StageColors  — the fallback palette, so a NULL colour is never a
//                     problem anywhere.
//
//   Nothing was removed. The five entry-requirement flags from 019 are
//   untouched: nothing has read them since 020 moved that logic onto
//   ProcessTransitions, but DefaultPipelineStages.Seed still carries two
//   of them and TenantProvisioningService builds from that record.
//   Removing them belongs in its own round.
//
// WHY COLOUR IS NULLABLE AND NOT SEEDED
//   Because seeding it would mean editing TenantProvisioningService, and
//   a new tenant getting the wrong colour is a worse failure than a new
//   tenant getting no colour. StageColors.DefaultFor turns a NULL into
//   a sensible value at the point of display, so a workspace created
//   before the migration, after the migration, or by a support script
//   all render identically. The migration backfills existing tenants so
//   their colours become real, editable data rather than a code default.
//
// THE ORIGINAL DESIGN, UNCHANGED
//
// A stage carries two different things, and they belong in different
// places:
//
//   The NAME  — "Proposal", "Site Visit", "Contract Signed"
//               This is the client's business process. An interiors firm
//               does not sell the way a software firm does, and telling
//               Sathorn their pipeline is "Discovery → Qualification" is
//               telling them their own process is wrong.
//               → tenant data, editable
//
//   The CATEGORY — Open / Won / Lost
//               Forecasting, revenue reporting and "is this deal closed"
//               all depend on this. It cannot be a free-text string a
//               client can rename, or the reports quietly stop working.
//               → code, fixed
//
// KEY vs NAME
//   Key is stable and stored on Deal.Stage. Name is what people see.
//   Renaming "Proposal" to "Quotation" changes Name only, so no deal
//   rows move and no history is rewritten.
// =====================================================================

namespace MerkaiTrial.Domain.Entities;

/// <summary>
/// What a stage MEANS to the engine. Fixed in code: reports, forecasting
/// and the "closed deals can't be deleted" rule all read this rather
/// than matching stage names.
/// </summary>
public enum StageCategory
{
    /// <summary>Still in play. Counts toward open pipeline value.</summary>
    Open = 0,

    /// <summary>Sold. Counts as revenue. Terminal.</summary>
    Won = 1,

    /// <summary>Lost or abandoned. Terminal, and needs a reason.</summary>
    Lost = 2
}

public class PipelineStage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>
    /// Stable identifier stored on Deal.Stage. Generated from the name on
    /// creation and NEVER changed afterwards — renaming a stage must not
    /// move the deals sitting in it.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>What the client sees. Freely editable.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Left-to-right order on the pipeline board.</summary>
    public int SortOrder { get; set; }

    /// <summary>Default win likelihood, 0-100. Used for weighted forecasting.</summary>
    public int Probability { get; set; }

    public StageCategory Category { get; set; } = StageCategory.Open;

    /// <summary>
    /// Retired stages stay in the table so existing deals and history
    /// still resolve, but are not offered for new ones. Deleting a stage
    /// that deals sit in would orphan them.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// The stage a new deal starts in. Exactly one per tenant.
    /// </summary>
    public bool IsDefault { get; set; }

    // ── Presentation (023) ────────────────────────────────────────────

    /// <summary>
    /// Hex colour including the leading hash, e.g. "#6366F1". NULL means
    /// "nobody has chosen one" — read it through
    /// StageColors.Resolve(Color, Category, ordinal) rather than directly,
    /// so a stage created before this column existed still has a colour.
    /// </summary>
    public string? Color { get; set; }

    /// <summary>
    /// One or two lines saying what has to be true for a deal to sit
    /// here — "quote sent and the client has seen it". Shown under the
    /// stage name in settings and, more importantly, on the transition
    /// button a rep presses, which is where the guidance is actually
    /// needed.
    /// </summary>
    public string? Description { get; set; }

    // ── Entry requirements (019) ──────────────────────────────────────
    // SUPERSEDED BY 020. These are no longer read by StageTransitionGuard
    // — requirements now live on ProcessTransitions, because "what does a
    // deal need" is a question about a MOVE, not about a stage. They stay
    // on the entity only because DefaultPipelineStages.Seed still carries
    // two of them. A later round removes both together.

    /// <summary>A quote must exist for the deal, in any state.</summary>
    public bool RequiresQuote { get; set; }

    /// <summary>A quote on the deal must be Accepted. Implies RequiresQuote.</summary>
    public bool RequiresAcceptedQuote { get; set; }

    /// <summary>The expected close date must be filled in.</summary>
    public bool RequiresCloseDate { get; set; }

    /// <summary>The expected value must be above zero.</summary>
    public bool RequiresValue { get; set; }

    /// <summary>
    /// A reason must be given for losing the deal. Only meaningful on a
    /// Lost stage — the settings page hides it elsewhere, and the guard
    /// ignores it elsewhere.
    /// </summary>
    public bool RequiresLostReason { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }

    // No IsDeleted: IsActive covers retirement, and a hard delete is
    // blocked while deals reference the stage.

    /// <summary>Won or Lost — the deal is over, whatever it is called.</summary>
    public bool IsTerminal => Category is StageCategory.Won or StageCategory.Lost;

    /// <summary>True when this stage asks anything of a deal at all.</summary>
    public bool HasEntryRequirements =>
        RequiresQuote || RequiresAcceptedQuote || RequiresCloseDate ||
        RequiresValue || RequiresLostReason;
}

/// <summary>
/// The colours a stage falls back to when nobody has chosen one.
///
/// WHY A FALLBACK RATHER THAN A NOT NULL COLUMN WITH A DEFAULT
///   A database default only applies to rows inserted after the migration
///   ran. Every other route into this table — the provisioning service, a
///   support fix, a restored backup — would have to know the same rule
///   and get it right. One function that turns NULL into a colour at the
///   moment of display is a single place to be correct.
/// </summary>
public static class StageColors
{
    /// <summary>
    /// Open stages, in pipeline order. Chosen to be distinguishable from
    /// one another on a kanban board rather than to form a gradient — the
    /// board's job is to let someone find a column, not to be pretty.
    /// Eight is more than the stages any sane pipeline has; past that it
    /// wraps.
    /// </summary>
    public static readonly IReadOnlyList<string> Open = new[]
    {
        "#6366F1",  // indigo
        "#0EA5E9",  // sky
        "#14B8A6",  // teal
        "#F59E0B",  // amber
        "#8B5CF6",  // violet
        "#EC4899",  // pink
        "#10B981",  // emerald
        "#F97316"   // orange
    };

    /// <summary>Green for Won and red for Lost are conventions, not taste.
    /// A tenant can still change them; almost none will.</summary>
    public const string Won  = "#16A34A";
    public const string Lost = "#DC2626";

    /// <summary>What a stage should be if nobody has picked anything.</summary>
    /// <param name="ordinal">
    /// Zero-based position among the tenant's OPEN stages. Ignored for
    /// Won and Lost, which have one colour each.
    /// </param>
    public static string DefaultFor(StageCategory category, int ordinal) => category switch
    {
        StageCategory.Won  => Won,
        StageCategory.Lost => Lost,
        _ => Open[Math.Abs(ordinal) % Open.Count]
    };

    /// <summary>
    /// The colour to actually paint. Use this everywhere rather than
    /// reading PipelineStage.Color, so a NULL or a malformed value can
    /// never reach a style attribute.
    /// </summary>
    public static string Resolve(string? stored, StageCategory category, int ordinal)
        // Trimmed as well as upper-cased: IsValid trims before checking,
        // so " #6366F1" passes — and this value goes straight into a
        // style attribute. Anything written through the handlers is
        // already clean; this defends the script-and-restore case the
        // class exists for.
        => IsValid(stored) ? stored!.Trim().ToUpperInvariant() : DefaultFor(category, ordinal);

    /// <summary>
    /// "#RRGGBB" only. Deliberately strict: this value goes straight into
    /// a style attribute, so anything that is not exactly a hex colour is
    /// treated as absent rather than escaped and hoped for.
    /// </summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var v = value.Trim();
        if (v.Length != 7 || v[0] != '#') return false;

        for (var i = 1; i < 7; i++)
            if (!Uri.IsHexDigit(v[i])) return false;

        return true;
    }

    /// <summary>Normalised for storage, or null if it is not a colour.</summary>
    public static string? Clean(string? value)
        => IsValid(value) ? value!.Trim().ToUpperInvariant() : null;
}

/// <summary>
/// The stages a new tenant starts with. Deliberately generic — a client
/// renames them to their own process on day one, which is the point.
/// This replaces the old DealStages constants as the SEED, not as the
/// runtime source of truth.
///
/// 019: the seed carries the same two requirements the migration switches
/// on for existing tenants, so a workspace created tomorrow behaves like
/// one created last month.
///
/// 023: UNCHANGED ON PURPOSE. Adding Color here would mean editing
/// TenantProvisioningService as well, and StageColors.Resolve already
/// gives a seeded stage the right colour without either file moving.
/// </summary>
public static class DefaultPipelineStages
{
    public record Seed(
        string Key,
        string Name,
        int SortOrder,
        int Probability,
        StageCategory Category,
        bool IsDefault,
        bool RequiresAcceptedQuote = false,
        bool RequiresLostReason = false);

    public static readonly IReadOnlyList<Seed> All = new List<Seed>
    {
        new("Discovery",     "Discovery",     1,  20, StageCategory.Open, true),
        new("Qualification", "Qualification", 2,  30, StageCategory.Open, false),
        new("Proposal",      "Proposal",      3,  40, StageCategory.Open, false),
        new("Negotiation",   "Negotiation",   4,  60, StageCategory.Open, false),
        new("ClosedWon",     "Closed Won",    5, 100, StageCategory.Won,  false, RequiresAcceptedQuote: true),
        new("ClosedLost",    "Closed Lost",   6,   0, StageCategory.Lost, false, RequiresLostReason: true),
    };
}
