// =====================================================================
// LeadStatusDefinition.cs
// Location: MerkaiTrial.Domain/Entities/LeadStatusDefinition.cs
//
// NEW FILE. Makes lead statuses the tenant's, not ours.
//
// SAME SPLIT AS PIPELINE STAGES
//   The NAME is the tenant's process — "Enquiry", "Site Visit Booked",
//   "ตรวจหน้างานแล้ว". Editable.
//   The CATEGORY is what the engine depends on. Fixed in code.
//
// WHY THE CATEGORY CANNOT BE THE TENANT'S
//   ConvertLeadToDealHandler requires a lead to be qualified before it
//   becomes a deal. If a tenant could set that themselves, they could
//   make conversion impossible, or let unvetted leads through. The
//   category is the contract; the label is theirs.
//
// WHY NOW RATHER THAN LATER
//   Lead.Status is an enum stored as an int. Every month of data makes
//   that more expensive to change. Deal stages were already strings,
//   which is why that migration was cheap — this one is not, and it only
//   gets worse.
// =====================================================================

namespace MerkaiTrial.Domain.Entities;

/// <summary>
/// What a status MEANS to the engine. Fixed in code.
/// </summary>
public enum LeadStatusCategory
{
    /// <summary>Still being worked. The default for anything new.</summary>
    Open = 0,

    /// <summary>
    /// Vetted and ready to become a deal. Conversion checks for THIS
    /// category, not for a status called "Qualified".
    /// </summary>
    Qualified = 1,

    /// <summary>
    /// Not being pursued. A tenant may have several — "No budget",
    /// "Wrong area", "Went elsewhere" — and they all behave the same.
    /// </summary>
    Disqualified = 2,

    /// <summary>
    /// Became a deal. Set by the conversion handler and by nothing else,
    /// which is why its status is marked IsSystem.
    /// </summary>
    Converted = 3
}

public class LeadStatusDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>
    /// Stable identifier stored on Lead.Status. Generated from the name
    /// on creation and NEVER changed — renaming a status must not move
    /// the leads sitting in it.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>What the client sees. Freely editable.</summary>
    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    /// <summary>
    /// Points this status contributes to the lead score, 0-15.
    /// LeadScoringService hardcoded 0/5/15/15; a tenant whose process has
    /// "Site Visit Booked" should be able to say what that is worth.
    /// </summary>
    public int Score { get; set; }

    public LeadStatusCategory Category { get; set; } = LeadStatusCategory.Open;

    /// <summary>
    /// Retired statuses stay so existing leads and audit history still
    /// resolve, but are not offered for new work.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Where a newly created lead starts. Exactly one per tenant.</summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Set by the system, never chosen by a person. Converted is the only
    /// one: it can be renamed, but not deleted, not deactivated, and it
    /// does not appear in the status dropdown. Without this a tenant could
    /// mark a lead Converted with no deal behind it.
    /// </summary>
    public bool IsSystem { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// What a new tenant starts with. Deliberately close to the old enum so
/// the migration is a straight mapping.
///
/// LeadStatus.Lost (5) is NOT seeded: the database has zero rows in it,
/// and it duplicated Unqualified. Two statuses meaning the same thing
/// only prompts "which one do I use".
/// </summary>
public static class DefaultLeadStatuses
{
    public record Seed(
        string Key, string Name, int SortOrder, int Score,
        LeadStatusCategory Category, bool IsDefault, bool IsSystem);

    public static readonly IReadOnlyList<Seed> All = new List<Seed>
    {
        new("New",         "New",          1,  0, LeadStatusCategory.Open,         true,  false),
        new("Working",     "Working",      2,  5, LeadStatusCategory.Open,         false, false),
        new("Qualified",   "Qualified",    3, 15, LeadStatusCategory.Qualified,    false, false),
        new("Unqualified", "Unqualified",  4,  0, LeadStatusCategory.Disqualified, false, false),
        // System: set by conversion only, never offered in a dropdown.
        new("Converted",   "Converted",    5, 15, LeadStatusCategory.Converted,    false, true),
    };
}
