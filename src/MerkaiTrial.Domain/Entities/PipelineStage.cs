// =====================================================================
// PipelineStage.cs
// Location: MerkaiTrial.Domain/Entities/PipelineStage.cs
//
// COMPLETE FILE — replaces the existing one.
//
// CHANGES (019 — transition rules)
//   ✅ Five ENTRY REQUIREMENT flags. A stage now carries not only what
//      it is called and what it means, but what a deal must already have
//      before it is allowed in.
//
// WHY THEY LIVE ON THE STAGE AND NOT IN A RULES TABLE
//   Every one of these is a question about ONE stage — "what does a deal
//   need before it counts as Won here". A separate rules table would let
//   a rule exist for a stage that has been deleted, and would need its
//   own tenant filter, its own settings page and its own migration. Five
//   BIT columns say the same thing and cannot get out of step with the
//   stage they describe.
//
//   The rules that are about the pipeline AS A WHOLE — forward-only, who
//   may reopen a closed deal — belong to no single stage, so those go in
//   PipelineRuleSettings instead.
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

    // ── Entry requirements (019) ──────────────────────────────────────
    // Checked by StageTransitionGuard before a deal is allowed in. All
    // default to false: a tenant's pipeline must behave exactly as it did
    // before this round until they choose otherwise. The migration turns
    // on the two that protect money — an accepted quote before Won, a
    // reason before Lost.
    //
    // These are never checked on a SYSTEM move (a quote being accepted,
    // an invoice being paid). The event that triggers such a move is the
    // very thing the requirement asks about, and a manual invoice has no
    // quote to point at.

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
/// The stages a new tenant starts with. Deliberately generic — a client
/// renames them to their own process on day one, which is the point.
/// This replaces the old DealStages constants as the SEED, not as the
/// runtime source of truth.
///
/// 019: the seed now carries the same two requirements the migration
/// switches on for existing tenants, so a workspace created tomorrow
/// behaves like one created last month.
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
