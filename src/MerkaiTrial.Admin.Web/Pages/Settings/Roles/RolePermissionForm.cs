// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Settings/Roles/RolePermissionForm.cs
//
// 084. NEW. One place that reads, shows and saves the role-permission
// checkboxes, shared by Create, Edit and Detail.
//
// WHY THIS EXISTS — three bugs in the role editor:
//
//  1. Case. Role JSON in the database uses PascalCase keys ("Leads"),
//     the module list uses lowercase ("leads"). PermissionHandler compares
//     case-insensitively, so sign-in was fine — but the editor compared
//     case-SENSITIVELY, so a role that really had Leads access showed
//     every Leads box unticked.
//
//  2. Silent loss. Saving rebuilt the JSON from the ticked boxes only.
//     Combined with (1), opening a built-in-style role and pressing Save
//     wiped permissions the admin never saw. It also dropped any module
//     or action the editor does not offer (old keys, retired actions).
//
//  3. Lost ticks. A validation error on Create came back with every box
//     unticked.
//
// The rules now:
//   - Reading is case-insensitive for modules and actions.
//   - Saving changes ONLY what the page shows. Anything stored that the
//     page does not offer is kept exactly as it was.
//   - An existing key keeps its stored spelling ("Leads" stays "Leads"),
//     so the saved JSON differs from before only where a box changed.
// =====================================================================

using MerkaiTrial.Application.Configuration;
using System.Text.Json;

namespace MerkaiTrial.Admin.Web.Pages.Settings.Roles;

internal static class RolePermissionForm
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    // ── Field naming (unchanged format: Permissions_{module}_{action}) ──

    public static string FieldName(ModuleDefinition module, string action)
        => $"Permissions_{module.Name}_{action}";

    /// <summary>HTML id for a checkbox, so its label is clickable.</summary>
    public static string FieldId(ModuleDefinition module, string action)
        => "perm_" + Safe(module.Name) + "_" + Safe(action);

    private static string Safe(string value)
        => new string((value ?? string.Empty)
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
            .ToArray());

    // ── Reading ─────────────────────────────────────────────────────────

    /// <summary>
    /// Stored JSON → case-insensitive dictionary. Keys that differ only by
    /// case ("Leads" and "leads") are merged. Corrupt JSON reads as "no
    /// permissions" — SignInService fails closed on the same data, so this
    /// matches what the role actually grants.
    /// </summary>
    public static Dictionary<string, List<string>> Parse(string? json)
    {
        var result = new Dictionary<string, List<string>>(Cmp);
        if (string.IsNullOrWhiteSpace(json)) return result;

        Dictionary<string, List<string>?>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<Dictionary<string, List<string>?>>(json);
        }
        catch (JsonException)
        {
            return result;
        }

        if (raw == null) return result;

        foreach (var (key, actions) in raw)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;

            if (!result.TryGetValue(key, out var list))
            {
                list = new List<string>();
                result[key] = list;
            }

            foreach (var action in actions ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(action)) continue;
                if (!list.Contains(action, Cmp)) list.Add(action);
            }
        }

        return result;
    }

    /// <summary>Is this action granted on this module? Case-insensitive.</summary>
    public static bool Has(IReadOnlyDictionary<string, List<string>> permissions, string module, string action)
    {
        if (permissions is Dictionary<string, List<string>> dict && dict.Comparer.Equals(Cmp))
            return dict.TryGetValue(module, out var direct) && direct.Contains(action, Cmp);

        return permissions
            .Where(kv => Cmp.Equals(kv.Key, module))
            .Any(kv => kv.Value.Contains(action, Cmp));
    }

    /// <summary>
    /// The posted checkboxes, used to redisplay the form after a
    /// validation error without losing what the admin ticked.
    /// </summary>
    public static Dictionary<string, List<string>> ReadForm(
        IFormCollection form, IEnumerable<ModuleDefinition> modules)
    {
        var result = new Dictionary<string, List<string>>(Cmp);

        foreach (var module in modules)
        {
            var ticked = module.Permissions
                .Where(p => IsTicked(form, module, p))
                .ToList();

            if (ticked.Count > 0) result[module.Name] = ticked;
        }

        return result;
    }

    private static bool IsTicked(IFormCollection form, ModuleDefinition module, string action)
        => string.Equals(form[FieldName(module, action)].ToString(), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the role holds that the editor does NOT show — modules not in
    /// the list, or actions a listed module no longer offers. Shown on the
    /// Edit page as "kept as they are" and preserved on save.
    /// </summary>
    public static Dictionary<string, List<string>> NotShown(
        IReadOnlyDictionary<string, List<string>> stored, IEnumerable<ModuleDefinition> modules)
    {
        var list = modules.ToList();
        var result = new Dictionary<string, List<string>>(Cmp);

        foreach (var (key, actions) in stored)
        {
            var module = list.FirstOrDefault(m => Cmp.Equals(m.Name, key));

            var hidden = module == null
                ? actions.ToList()
                : actions.Where(a => !module.Permissions.Contains(a, Cmp)).ToList();

            if (hidden.Count > 0) result[key] = hidden;
        }

        return result;
    }

    // ── Saving ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the JSON to save: the stored permissions with ONLY the shown
    /// checkboxes applied on top. Pass storedJson = null for a new role.
    /// </summary>
    public static string BuildJson(
        string? storedJson, IFormCollection form, IEnumerable<ModuleDefinition> modules)
    {
        var stored = Parse(storedJson);
        var list = modules.ToList();

        // Plain (ordinal) dictionary for output; Parse already merged
        // case-duplicates so no two keys collide.
        var result = new Dictionary<string, List<string>>();

        // 1. Everything the page does not offer, kept verbatim.
        foreach (var (key, actions) in NotShown(stored, list))
            result[key] = actions;

        // 2. Every shown module, from its checkboxes.
        foreach (var module in list)
        {
            var storedKey = stored.Keys.FirstOrDefault(k => Cmp.Equals(k, module.Name));
            var key = storedKey ?? module.Name;
            stored.TryGetValue(key, out var storedActions);

            var actions = result.TryGetValue(key, out var kept) ? kept : new List<string>();

            foreach (var action in module.Permissions)
            {
                if (!IsTicked(form, module, action)) continue;

                // Keep the stored spelling of an action already granted.
                var spelling = storedActions?.FirstOrDefault(a => Cmp.Equals(a, action)) ?? action;
                if (!actions.Contains(spelling, Cmp)) actions.Add(spelling);
            }

            if (actions.Count > 0) result[key] = actions;
            else result.Remove(key);
        }

        return JsonSerializer.Serialize(result);
    }

    // ── Display helpers ─────────────────────────────────────────────────

    /// <summary>
    /// "bi bi-funnel" whether the module icon is stored as "funnel" or
    /// "bi-funnel" — the catalog uses the latter, which used to render as
    /// "bi-bi-funnel" (no icon).
    /// </summary>
    public static string IconClass(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return "bi bi-grid";
        var name = icon.Trim();
        if (name.StartsWith("bi ", StringComparison.OrdinalIgnoreCase)) return name;
        return name.StartsWith("bi-", StringComparison.OrdinalIgnoreCase) ? "bi " + name : "bi bi-" + name;
    }

    /// <summary>"read" → "View", etc. — the words a business owner uses.</summary>
    public static string ActionLabel(string action) => action.ToLowerInvariant() switch
    {
        "read"   => "View",
        "view"   => "View",
        "create" => "Add",
        "update" => "Edit",
        "delete" => "Delete",
        "export" => "Export",
        "import" => "Import",
        _        => string.IsNullOrEmpty(action) ? action : char.ToUpperInvariant(action[0]) + action[1..],
    };

    public static string ActionHint(string action) => action.ToLowerInvariant() switch
    {
        "read" or "view" => "See records",
        "create"         => "Add new records",
        "update"         => "Change existing records",
        "delete"         => "Remove records",
        _                => string.Empty,
    };

    /// <summary>Display name for a stored key, falling back to the key.</summary>
    public static string ModuleLabel(string key, IEnumerable<ModuleDefinition> modules)
        => modules.FirstOrDefault(m => Cmp.Equals(m.Name, key))?.DisplayName ?? key;
}

/// <summary>Model for _RolePermissionsGrid.cshtml (Create and Edit).</summary>
public sealed record RolePermissionsGridVm(
    IReadOnlyList<ModuleDefinition> Modules,
    IReadOnlyDictionary<string, List<string>> Selected);
