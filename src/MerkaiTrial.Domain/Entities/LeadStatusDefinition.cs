// =====================================================================
// LeadStatusDefinition.cs
// Location: MerkaiTrial.Domain/Entities/LeadStatusDefinition.cs
//
// COMPLETE FILE — replaces the existing one.
//
// CHANGES (025)
//   ✅ Color        — what the status looks like on the leads list.
//   ✅ Description  — what has to be true for a lead to sit here.
//   ✅ StatusColors — the fallback palette, so a NULL colour is never a
//                     problem anywhere.
//
// Nothing was removed, and DefaultLeadStatuses is untouched: the fallback
// below gives a seeded status the right colour without
// TenantProvisioningService moving at all.
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

    // ── Presentation (025) ────────────────────────────────────────────

    /// <summary>
    /// Hex colour including the leading hash, e.g. "#6366F1". NULL means
    /// "nobody has chosen one" — read it through
    /// StatusColors.Resolve(Color, Category, ordinal) rather than
    /// directly, so a status created before this column existed still has
    /// a colour.
    /// </summary>
    public string? Color { get; set; }

    /// <summary>
    /// One or two lines saying what has to be true for a lead to sit here
    /// — "spoken to, budget confirmed". Shown under the status name in
    /// settings and on the lead page, which is where the guidance is
    /// actually needed.
    /// </summary>
    public string? Description { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// The colours a status falls back to when nobody has chosen one.
///
/// WHY A FALLBACK RATHER THAN A NOT NULL COLUMN WITH A DEFAULT
///   A database default only applies to rows inserted after the migration
///   ran. Every other route into this table — provisioning, a support
///   fix, a restored backup — would have to know the same rule and get it
///   right. One function that turns NULL into a colour at the moment of
///   display is a single place to be correct.
/// </summary>
public static class StatusColors
{
    /// <summary>
    /// Open statuses, in pipeline order. Chosen to be distinguishable
    /// from one another rather than to form a gradient. Six is more than
    /// the open statuses any sane lead process has; past that it wraps.
    /// </summary>
    public static readonly IReadOnlyList<string> Open = new[]
    {
        "#6366F1",  // indigo
        "#0EA5E9",  // sky
        "#14B8A6",  // teal
        "#8B5CF6",  // violet
        "#EC4899",  // pink
        "#F97316"   // orange
    };

    public const string Qualified = "#16A34A";   // green — ready to convert

    /// <summary>
    /// Slate, not red. "Not pursuing" is an outcome, not a failure, and a
    /// wall of red down the settings page reads as alarm where none is
    /// meant. A lost DEAL is red because revenue was lost; a
    /// disqualified lead usually just was not a fit.
    /// </summary>
    public const string Disqualified = "#64748B";

    public const string Converted = "#F59E0B";   // amber — matches the existing badge

    /// <summary>What a status should be if nobody has picked anything.</summary>
    /// <param name="ordinal">
    /// Zero-based position among the tenant's OPEN statuses. Ignored for
    /// the other three categories, which have one colour each.
    /// </param>
    public static string DefaultFor(LeadStatusCategory category, int ordinal) => category switch
    {
        LeadStatusCategory.Qualified    => Qualified,
        LeadStatusCategory.Disqualified => Disqualified,
        LeadStatusCategory.Converted    => Converted,
        _ => Open[Math.Abs(ordinal) % Open.Count]
    };

    /// <summary>
    /// The colour to actually paint. Use this everywhere rather than
    /// reading LeadStatusDefinition.Color, so a NULL or a malformed value
    /// can never reach a style attribute.
    /// </summary>
    public static string Resolve(string? stored, LeadStatusCategory category, int ordinal)
        => IsValid(stored)
            ? stored!.Trim().ToUpperInvariant()
            : DefaultFor(category, ordinal);

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
/// What a new tenant starts with. Deliberately close to the old enum so
/// the migration is a straight mapping.
///
/// LeadStatus.Lost (5) is NOT seeded: the database has zero rows in it,
/// and it duplicated Unqualified. Two statuses meaning the same thing
/// only prompts "which one do I use".
///
/// 025: UNCHANGED ON PURPOSE. Adding Color here would mean editing
/// TenantProvisioningService as well, and StatusColors.Resolve already
/// gives a seeded status the right colour without either file moving.
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
