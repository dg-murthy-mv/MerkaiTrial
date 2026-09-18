// =====================================================================
// PipelineStage.cs
// Location: MerkaiTrial.Domain/Entities/PipelineStage.cs
//
// NEW FILE. Makes the sales pipeline the tenant's, not ours.
//
// THE DESIGN, AND WHY
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
// This is the same rule already in the handoff: config describing CODE
// stays in code; config describing COMMERCIAL POLICY goes in the DB.
//
// Zoho, HubSpot and Salesforce all work this way — custom stage names,
// each tagged with a fixed category the engine understands.
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

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }

    // No IsDeleted: IsActive covers retirement, and a hard delete is
    // blocked while deals reference the stage.
}

/// <summary>
/// The stages a new tenant starts with. Deliberately generic — a client
/// renames them to their own process on day one, which is the point.
/// This replaces the old DealStages constants as the SEED, not as the
/// runtime source of truth.
/// </summary>
public static class DefaultPipelineStages
{
    public record Seed(string Key, string Name, int SortOrder, int Probability, StageCategory Category, bool IsDefault);

    public static readonly IReadOnlyList<Seed> All = new List<Seed>
    {
        new("Discovery",     "Discovery",     1,  20, StageCategory.Open, true),
        new("Qualification", "Qualification", 2,  30, StageCategory.Open, false),
        new("Proposal",      "Proposal",      3,  40, StageCategory.Open, false),
        new("Negotiation",   "Negotiation",   4,  60, StageCategory.Open, false),
        new("ClosedWon",     "Closed Won",    5, 100, StageCategory.Won,  false),
        new("ClosedLost",    "Closed Lost",   6,   0, StageCategory.Lost, false),
    };
}
