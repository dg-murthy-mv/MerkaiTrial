// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/RecordVisibility.cs
//
// NEW (092). Settings → Record Visibility, for tests that need a role
// set to Own and put back afterwards. The same reading of the grid
// WorkspaceSettingsTests has used since 088 (which keeps its own copy and
// is left alone), with two additions:
//
//   • SetAsync returns what the cell said before, so a finally block can
//     put it back;
//   • TrySetAsync for a module the grid may not have a column for —
//     it answers null instead of failing.
// =====================================================================

namespace MerkaiTrial.E2E.Infrastructure;

public static class RecordVisibility
{
    public sealed record ScopeCell(string Current, Dictionary<string, string> RadioIds);

    /// <summary>
    /// Sets <paramref name="roleName"/>'s scope for <paramref name="module"/>
    /// ("Leads", "Deals", …) and saves. Returns the scope it had before.
    /// </summary>
    public static async Task<string> SetAsync(IPage page, string roleName, string module, string scope)
    {
        await page.GotoAsync("/Settings/Visibility");
        var cell = await ReadAsync(page, roleName, module)
                   ?? throw new AssertionException($"Record Visibility has no \"{module}\" column.");
        await ApplyAsync(page, cell, scope);
        return cell.Current;
    }

    /// <summary>As SetAsync, but null (and nothing changed) when the grid has no such column.</summary>
    public static async Task<string?> TrySetAsync(IPage page, string roleName, string module, string scope)
    {
        await page.GotoAsync("/Settings/Visibility");
        var cell = await ReadAsync(page, roleName, module);
        if (cell is null) return null;
        await ApplyAsync(page, cell, scope);
        return cell.Current;
    }

    /// <summary>
    /// The cell for a role and a module: which of Own / Team / All is
    /// ticked, and each radio's id. Null when there is no such column.
    /// </summary>
    public static async Task<ScopeCell?> ReadAsync(IPage page, string roleName, string module)
    {
        var raw = await page.EvaluateAsync<string[]>(
            @"args => {
                const [role, module] = args;
                const heads = [...document.querySelectorAll('table.rv-grid thead th')].map(t => t.textContent.trim());
                let col = heads.findIndex(t => t.toLowerCase() === module.toLowerCase());
                if (col < 1) col = heads.findIndex((t, i) => i > 0 && t.toLowerCase().includes(module.toLowerCase().replace(/s$/, '')));
                if (col < 1) return ['nocolumn', heads.join(' | ')];
                const rows = [...document.querySelectorAll('tr.rv-rolerow')];
                const nameOf = r => (r.querySelector('.rv-rolename')?.textContent || '').trim();
                let row = rows.find(r => nameOf(r) === role);
                if (!row) row = rows.filter(r => nameOf(r) && role.includes(nameOf(r)))
                                    .sort((a, b) => nameOf(b).length - nameOf(a).length)[0];
                if (!row) return ['error', 'no row for the role ""' + role + '"" — roles are: ' + rows.map(nameOf).join(' | ')];
                const cell = row.children[col];
                if (!cell || !cell.classList.contains('rv-cell')) return ['error', '""' + role + '"" is locked (it always sees everything)'];
                const out = ['ok', ''];
                for (const r of cell.querySelectorAll('input.btn-check')) {
                    if (r.checked) out[1] = r.value;
                    out.push(r.value + '=' + r.id);
                }
                return out;
            }", new[] { roleName, module });

        if (raw.Length >= 1 && raw[0] == "nocolumn") return null;
        if (raw.Length < 2 || raw[0] != "ok")
            throw new AssertionException("Record Visibility: " + (raw.Length > 1 ? raw[1] : "the grid could not be read."));

        var ids = raw.Skip(2)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
        return new ScopeCell(raw[1], ids);
    }

    private static async Task ApplyAsync(IPage page, ScopeCell cell, string scope)
    {
        if (string.Equals(cell.Current, scope, StringComparison.OrdinalIgnoreCase)) return;
        if (!cell.RadioIds.TryGetValue(scope, out var id))
            throw new AssertionException($"Record Visibility offers no \"{scope}\" here (offers: {string.Join(", ", cell.RadioIds.Keys)}).");

        await page.Locator($"label[for='{id}']").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save visibility" }).ClickAsync();
        await Ui.ExpectSuccessAsync(page, "Saved");
    }
}
