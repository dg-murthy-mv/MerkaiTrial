// =====================================================================
// FeatureCatalog.cs
// Location: MerkaiTrial.Application/Configuration/FeatureCatalog.cs
//
// Single source of truth for the feature keys stored in Plan.Features
// (JSON array, e.g. ["leads","deals","reports"]). Previously this list
// only existed inside CreateEditModel, so the Detail page had no way to
// show anything but the raw keys. Anywhere a feature key needs a label
// or icon, it should come from here.
// =====================================================================

namespace MerkaiTrial.Application.Configuration;

public record FeatureDefinition(string Key, string Label, string Icon);

public static class FeatureCatalog
{
    public static readonly List<FeatureDefinition> All = new()
    {
        new("leads",               "Leads",            "person-lines-fill"),
        new("deals",               "Deals",            "briefcase"),
        new("contacts",            "Contacts",         "person-vcard"),
        new("companies",           "Companies",        "buildings"),
        new("tasks",               "Tasks",            "check2-square"),
        new("notes",               "Notes",            "journal-text"),
        new("basic_reports",       "Basic Reports",    "bar-chart"),
        new("advanced_reports",    "Adv. Reports",     "graph-up"),
        new("email_integration",   "Email",            "envelope"),
        new("api_access",          "API Access",       "braces"),
        new("custom_fields",       "Custom Fields",    "sliders"),
        new("lead_scoring",        "Lead Scoring",     "star"),
        new("deal_pipeline_custom","Custom Pipeline",  "diagram-3"),
        new("custom_roles",        "Custom Roles",     "shield-check"),
        new("sso",                 "SSO",              "key"),
        new("audit_logs",          "Audit Logs",       "clock-history"),
        new("webhooks",            "Webhooks",         "plugin"),
        new("white_label",         "White Label",      "brush"),
        new("dedicated_support",   "Priority Support", "headset"),
    };

    private static readonly Dictionary<string, FeatureDefinition> ByKey =
        All.ToDictionary(f => f.Key, f => f);

    /// <summary>
    /// Resolves a feature key to its display definition. Falls back to a
    /// generic definition (raw key as label, neutral icon) for any key
    /// that isn't in the catalog yet, so an unrecognized feature never
    /// breaks the page — it just displays plainly instead of nicely.
    /// </summary>
    public static FeatureDefinition Resolve(string key) =>
        ByKey.TryGetValue(key, out var def)
            ? def
            : new FeatureDefinition(key, key, "question-circle");

    public static List<FeatureDefinition> Resolve(IEnumerable<string> keys) =>
        keys.Select(Resolve).ToList();
}
