using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Application.Authorization;

public sealed record ModuleInfo(
    string Key,
    string DisplayName,
    string Icon,
    string Group,
    int SortOrder,
    IReadOnlyList<string> Actions)
{
    /// <summary>Policy name for one action, e.g. "leads.read".</summary>
    public string Policy(string action) => $"{Key}.{action}";
}

public static class ModuleCatalog
{
    /// <summary>
    /// Standard CRUD. Deviating from this needs a reason — the Reports
    /// bug came from one module quietly using view/create/delete while
    /// everything else used read.
    /// </summary>
    private static readonly string[] Crud = { "read", "create", "update", "delete" };

    /// <summary>Read-only modules: nothing to create or delete.</summary>
    private static readonly string[] ReadOnly = { "read" };

    /// <summary>
    /// Every module the application actually enforces. Keys are lowercase
    /// and match Modules.* below.
    ///
    /// The role JSON stored in the database uses PascalCase keys
    /// ("Leads"), which is fine — PermissionHandler and CanNav compare
    /// case-insensitively. Keep new entries lowercase for consistency
    /// with the Policies strings.
    /// </summary>
    public static readonly IReadOnlyList<ModuleInfo> All = new List<ModuleInfo>
    {
        // ── CUSTOMERS & SALES ──────────────────────────────────────
        new("companies", "Companies", "bi-building",           "CUSTOMERS & SALES", 10, Crud),
        new("contacts",  "Contacts",  "bi-people",             "CUSTOMERS & SALES", 11, Crud),
        new("products",  "Products",  "bi-box-seam",           "CUSTOMERS & SALES", 12, Crud),
        new("leads",     "Leads",     "bi-funnel",             "CUSTOMERS & SALES", 13, Crud),

        // ── Sales Pipeline ───────────────────────────────────────────
        new("deals",     "Pipeline",  "bi-kanban",             "Sales Pipeline", 20, Crud),
        new("quotes",    "Quotes",    "bi-file-earmark-text",  "Sales Pipeline", 21, Crud),
        new("invoices",  "Invoices",  "bi-receipt",            "Sales Pipeline", 22, Crud),

        // ── Reporting ────────────────────────────────────────────────
        // READ ONLY, and "read" not "view". Reports are generated, not
        // created as records. The old config offered view/create/delete,
        // none of which any call site checked.
        new("reports",   "Reports",   "bi-bar-chart",          "Reports", 30, ReadOnly),

        // ── Workspace administration ─────────────────────────────────
        // Grantable by a tenant admin to their own people.

        // 024. How the pipeline is SHAPED — the stages a deal moves
        // through, the sales process that says which move is allowed and
        // what it requires, and the lead statuses and their scoring.
        //
        // NOT the deals and leads that move through it. Those are "deals"
        // and "leads", and every rep needs update on them. Until this
        // entry existed the two were the same permission, so a Sales Rep
        // with deals.update could retire Negotiation and one with
        // leads.update could rewrite the lead scoring every rep's queue
        // is ordered by.
        //
        // "Sales Configuration", not "Pipeline" — the deals module already
        // displays as "Pipeline", and two entries with that name in the
        // role editor would be worse than the bug this fixes.
        //
        // Sorted 39 so it heads the Workspace Settings group: it is the
        // one a sales manager is most likely to be granted.
        new("settings",  "Sales Configuration", "bi-sliders",  "Workspace Settings", 39, Crud),

        new("users",     "Users",     "bi-person-badge",       "Workspace Settings", 40, Crud),
        new("roles",     "Roles",     "bi-shield-lock",        "Workspace Settings", 41, Crud),
        new("audit",     "Activity Log", "bi-clock-history",     "Workspace Settings", 42, ReadOnly),

    };

    /// <summary>
    /// SuperAdmin-only areas. Deliberately NOT in All: they are gated by
    /// AuthorizeFolder("/Admin", "SuperAdmin"), not by tenant
    /// permissions, so offering them as checkboxes in a tenant's role
    /// editor would imply a grant that does nothing.
    ///
    /// company-verticals, tax-rates, countries and payments lived in the
    /// old config for this reason and were never enforceable by a tenant.
    /// If a tenant should manage its own tax rates later, move that entry
    /// into All and add the controller policies at the same time.
    /// </summary>
    public static readonly IReadOnlyList<string> SuperAdminOnlyAreas = new[]
    {
        "countries", "company-verticals", "tax-rates", "tenants", "plans",
    };

    public static ModuleInfo? Find(string key) =>
        All.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every valid policy name. Useful as a startup assertion: a
    /// controller referencing a policy not in this set is a typo that
    /// would otherwise deny silently.
    /// </summary>
    public static IEnumerable<string> AllPolicies() =>
        All.SelectMany(m => m.Actions.Select(m.Policy));
}
