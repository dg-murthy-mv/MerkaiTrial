// =====================================================================
// ProductCategoriesConfiguration.cs
// Location: MerkaiTrial.Application/Configuration/ProductCategoriesConfiguration.cs
//
// COMPLETE FILE — replaces the existing one (068).
//
// WHAT CHANGED, AND WHY THE FILE SURVIVED AT ALL
//
//   This file used to BE the category list: a static List<ProductCategory>
//   of eight entries, read directly by four Products pages. Round 068
//   moves the list into dbo.ProductCategories so tenants can edit it.
//
//   The file is not deleted, because three jobs are left over and all
//   three want to live in one place:
//
//     1. DEFAULTS — the eight names, icons and colours are still the
//        starting list. Sql/068_ProductCategories.sql seeds them, and
//        this file is where they are written down in C# so the seed and
//        the application cannot disagree about what "Software" looks
//        like.
//
//     2. FALLBACK — a product can carry a category that no longer has a
//        row: deleted on the Settings page, or typed before this round
//        and never adopted. Something still has to render that product's
//        icon. GetCategoryIcon / GetCategoryColor answer for any name at
//        all, which is why both still exist with the same signatures
//        they always had.
//
//     3. THE ICON SET — ProductCategoryIcons, the list the picker on the
//        Settings page offers. A free-text icon field would let somebody
//        save "bi-nonsense" and get an invisible category.
//
//   The old `ProductCategory` POCO declared at the bottom of this file is
//   GONE. The real entity is MerkaiTrial.Domain.Entities.ProductCategory.
//   Nothing referenced the old type outside this file, so nothing breaks;
//   keeping both would mean two types with one name, and whichever
//   namespace a file imported second would decide which it got.
//
// ALSO IN HERE: ProductCategoryResolution, the one place that decides
// which rows a workspace actually sees. Read it before touching any
// category query — it is three lines and it is the whole design.
// =====================================================================

using MerkaiTrial.Domain.Entities;

namespace MerkaiTrial.Application.Configuration;

/// <summary>
/// The Merkai default categories, the fallback icon and colour for a name
/// with no row behind it, and nothing else. The live list comes from
/// dbo.ProductCategories.
/// </summary>
public static class ProductCategoriesConfiguration
{
    /// <summary>Fallback icon for a category with no row — and for no category at all.</summary>
    public const string FallbackIcon = "bi-box";

    /// <summary>Fallback colour. gray-500, the same shade the pages used before 068.</summary>
    public const string FallbackColor = "#6b7280";

    /// <summary>Longest category name the column holds — NVARCHAR(80).</summary>
    public const int NameMaxLength = 80;

    /// <summary>
    /// The eight defaults, in display order, with the SortOrder values
    /// Sql/068_ProductCategories.sql seeds. Names, icons and colours are
    /// byte-for-byte what this file held before 068, so no product that
    /// renders today changes shade the day the migration runs.
    ///
    /// Changing an entry here does NOT change the database. The seed runs
    /// once; after that the rows are data. Edit them on the Settings page,
    /// or add a migration — that is the price of the list being editable
    /// at all, and it is the right price.
    /// </summary>
    public static readonly IReadOnlyList<ProductCategorySeed> Defaults = new List<ProductCategorySeed>
    {
        new("Electronics",   "bi-lightning",    "#3b82f6", 10),
        new("Software",      "bi-code-square",  "#8b5cf6", 20),
        new("Services",      "bi-tools",        "#10b981", 30),
        new("Hardware",      "bi-cpu",          "#6366f1", 40),
        new("Subscriptions", "bi-arrow-repeat", "#f59e0b", 50),
        new("Consulting",    "bi-people",       "#06b6d4", 60),
        new("Training",      "bi-book",         "#ec4899", 70),
        new("Support",       "bi-headset",      "#84cc16", 80),
    };

    /// <summary>
    /// The default names. Kept with its original signature because the
    /// Products pages called it — they call the service now, but an API
    /// client or a seeding path may still want the starting list.
    /// </summary>
    public static List<string> GetCategoryNames()
        => Defaults.Select(c => c.Name).ToList();

    /// <summary>
    /// FALLBACK ONLY. The icon for a category name when there is no row to
    /// read it from — a category deleted on the Settings page while
    /// products still carry its name, or a value imported before 068.
    ///
    /// Always answers. A products list that threw because one row's
    /// category had been removed would be a worse outcome than a grey box.
    /// </summary>
    public static string GetCategoryIcon(string? categoryName)
        => Defaults.FirstOrDefault(c =>
               string.Equals(c.Name, categoryName, StringComparison.OrdinalIgnoreCase))?.Icon
           ?? FallbackIcon;

    /// <summary>FALLBACK ONLY — see GetCategoryIcon.</summary>
    public static string GetCategoryColor(string? categoryName)
        => Defaults.FirstOrDefault(c =>
               string.Equals(c.Name, categoryName, StringComparison.OrdinalIgnoreCase))?.Color
           ?? FallbackColor;

    /// <summary>
    /// What to STORE for a name somebody typed. Trimmed, inner whitespace
    /// collapsed, and cut to the column width.
    ///
    /// Collapsing the inner spaces matters more than it looks: "Course
    /// material" and "Course  material" are the same category to a human
    /// and two different strings in Product.Category, and the unique index
    /// would happily accept both.
    /// </summary>
    public static string? NormaliseName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var collapsed = string.Join(' ',
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Length <= NameMaxLength ? collapsed : collapsed[..NameMaxLength];
    }

    /// <summary>
    /// True when a colour is a usable "#rrggbb". Deliberately strict: the
    /// value goes straight into a style attribute, and the three-digit and
    /// named forms would make "is this the same colour as that one"
    /// impossible to answer by comparing strings.
    /// </summary>
    public static bool IsValidColor(string? value)
    {
        if (value is null || value.Length != 7 || value[0] != '#') return false;

        for (var i = 1; i < 7; i++)
            if (!Uri.IsHexDigit(value[i])) return false;

        return true;
    }
}

/// <summary>
/// One default category. A record rather than a mutable class: these are
/// constants, and the old mutable POCO was what made it possible to
/// believe this list could be edited at runtime.
/// </summary>
public sealed record ProductCategorySeed(string Name, string Icon, string Color, int SortOrder);

// =====================================================================
// THE RESOLUTION RULE
// =====================================================================

/// <summary>
/// Which category rows a workspace actually sees, given everything the
/// query filter allowed through.
///
/// THE RULE, in full:
///
///   A workspace that owns NO rows sees the Merkai defaults.
///   A workspace that owns ANY rows sees ONLY its own.
///
/// WHY NOT "defaults, plus tenant rows that shadow them by name"
///   Because the name is the only identity a category has
///   (Product.Category is a string), so a shadowing row could never
///   express "rename Electronics to Devices" — the moment the name
///   changed it would stop shadowing and the workspace would see both.
///   Supporting that properly needs a second stable key on every row,
///   and it produces a list whose contents nobody can predict without
///   reading code.
///
/// WHY THIS IS NOT A ONE-WAY DOOR FOR THE TENANT
///   The first write of any kind — add, rename, recolour, reorder,
///   remove — copies the whole default list in as tenant-owned rows
///   first. That is ADOPTION (EnsureAdoptedAsync in
///   ProductCategoryHandlers). So "owns any rows" is never a half state
///   where one edit has hidden the other seven defaults.
///
/// WHAT THE TENANT GIVES UP BY ADOPTING
///   A default Merkai adds later will not appear in their list. That is
///   the correct trade and it is why adoption is audited
///   (AuditAction.ProductCategoriesAdopted) rather than silent.
/// </summary>
public static class ProductCategoryResolution
{
    /// <summary>
    /// Applies the rule to a set of rows that has already been through
    /// the query filter — i.e. this workspace's rows plus the defaults.
    ///
    /// <paramref name="includeInactive"/> is for the Settings page, which
    /// has to show a switched-off category in order to switch it back on.
    /// Every other caller wants the active ones.
    /// </summary>
    public static List<ProductCategory> Effective(
        IEnumerable<ProductCategory> visibleRows,
        bool includeInactive = false)
    {
        var rows = visibleRows?.Where(r => !r.IsDeleted).ToList()
                   ?? new List<ProductCategory>();

        var owned = rows.Where(r => r.TenantId is not null).ToList();
        var chosen = owned.Count > 0
            ? owned
            : rows.Where(r => r.TenantId is null).ToList();

        if (!includeInactive)
            chosen = chosen.Where(r => r.IsActive).ToList();

        return chosen
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// True when this workspace is still following the Merkai defaults —
    /// what the Settings page uses to show its "these are the standard
    /// ones" banner, and nothing else.
    /// </summary>
    public static bool IsFollowingDefaults(IEnumerable<ProductCategory> visibleRows)
        => visibleRows?.Any(r => !r.IsDeleted && r.TenantId is not null) != true;
}

// =====================================================================
// THE ICON SET
// =====================================================================

/// <summary>
/// The icons the Settings page offers. A curated list rather than a text
/// box: Bootstrap Icons has well over two thousand classes, a typo in one
/// renders as nothing at all, and "my category is invisible" is a support
/// conversation nobody should have to have.
///
/// Chosen to cover what SMEs across India, Thailand, the Philippines and
/// the UAE actually sell — goods, services, food, construction, logistics,
/// teaching, property, healthcare — rather than to be a tour of the icon
/// font. Add to it freely; it is a list of strings.
/// </summary>
public static class ProductCategoryIcons
{
    public static readonly IReadOnlyList<string> All = new List<string>
    {
        // Generic
        "bi-box", "bi-box-seam", "bi-tags", "bi-grid", "bi-star",
        // Technology
        "bi-cpu", "bi-code-square", "bi-lightning", "bi-phone", "bi-laptop",
        "bi-printer", "bi-hdd-network", "bi-cloud",
        // Services and people
        "bi-tools", "bi-people", "bi-person-badge", "bi-headset", "bi-briefcase",
        "bi-clipboard-check", "bi-shield-check",
        // Recurring and money
        "bi-arrow-repeat", "bi-receipt", "bi-credit-card",
        // Teaching
        "bi-book", "bi-mortarboard", "bi-easel",
        // Trades, building, property
        "bi-hammer", "bi-wrench", "bi-house", "bi-building", "bi-paint-bucket",
        "bi-lightbulb", "bi-thermometer-half",
        // Food and hospitality
        "bi-cup-hot", "bi-egg-fried", "bi-basket", "bi-shop",
        // Logistics and travel
        "bi-truck", "bi-airplane", "bi-geo-alt", "bi-boxes",
        // Health and care
        "bi-heart-pulse", "bi-capsule", "bi-bandaid",
        // Media and marketing
        "bi-camera", "bi-megaphone", "bi-palette", "bi-music-note-beamed",
    };

    /// <summary>
    /// True when an icon is one this app will actually render. The
    /// handlers refuse anything else rather than storing it — the column
    /// would take it, and the category would come out blank.
    /// </summary>
    public static bool IsKnown(string? value)
        => value is not null && All.Contains(value, StringComparer.Ordinal);

    /// <summary>Falls back rather than throwing, like ProductKinds.Normalise.</summary>
    public static string Normalise(string? value)
        => IsKnown(value) ? value! : ProductCategoriesConfiguration.FallbackIcon;
}

// =====================================================================
// THE COLOUR SET
// =====================================================================

/// <summary>
/// The swatches the Settings page offers. Free hex is still accepted —
/// somebody matching a brand palette should not be stopped — but these
/// are what the picker shows, and they are the eight default colours plus
/// enough of the same family to keep a list of twenty categories
/// distinguishable at badge size.
/// </summary>
public static class ProductCategoryColors
{
    public static readonly IReadOnlyList<string> Swatches = new List<string>
    {
        "#3b82f6", // blue    — Electronics
        "#8b5cf6", // violet  — Software
        "#10b981", // emerald — Services
        "#6366f1", // indigo  — Hardware
        "#f59e0b", // amber   — Subscriptions
        "#06b6d4", // cyan    — Consulting
        "#ec4899", // pink    — Training
        "#84cc16", // lime    — Support
        "#ef4444", // red
        "#f97316", // orange
        "#14b8a6", // teal
        "#a855f7", // purple
        "#0ea5e9", // sky
        "#22c55e", // green
        "#eab308", // yellow
        "#64748b", // slate
    };
}
