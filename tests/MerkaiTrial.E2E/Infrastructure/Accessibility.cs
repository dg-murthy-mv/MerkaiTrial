// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/Accessibility.cs
//
// NEW (110). Accessibility checks for AccessibilityTests.
//
//   ScanAsync  runs axe-core (Deque's open-source checker, the one browser
//              accessibility tools use) on the page as it is, against the
//              WCAG 2.x A and AA rules. axe-core is vendored in
//              tests/MerkaiTrial.E2E/Assets/axe.min.js (4.13.0, MPL-2.0 —
//              its licence header stays in the file). Nothing is
//              downloaded during a run.
//
//              Which findings FAIL a test (what was asked for):
//                • labels   — a form field, button, link or image with no
//                             name a screen reader can read out
//                • contrast — text too faint against its background
//                             (4.5:1, or 3:1 for large text)
//                • keyboard — something that can't be reached or escaped
//                             with the keyboard
//              Everything else axe reports is printed as advice and saved
//              in the page's report, but does not fail the test.
//
//   WalkAsync  presses Tab through the whole page, the way a keyboard user
//              moves around, and reports a TRAP: focus that goes round a
//              few elements forever, or stops moving.
//
//   TabToAsync presses Tab until a given field has focus — for the
//              keyboard-only deal test. Fails, saying where focus went, if
//              the field can't be reached.
//
// The page's own scripts are never changed. axe is evaluated through the
// browser's debugging channel, which the site's Content-Security-Policy
// does not block (and does not need loosening for).
// =====================================================================

using System.Text;
using System.Text.Json;

namespace MerkaiTrial.E2E.Infrastructure;

public static class Accessibility
{
    // ── what fails a test ─────────────────────────────────────────────

    /// <summary>Things with no name a screen reader can read out.</summary>
    public static readonly HashSet<string> LabelRules = new(StringComparer.Ordinal)
    {
        "label", "select-name", "button-name", "link-name", "input-button-name",
        "aria-input-field-name", "aria-toggle-field-name", "aria-command-name",
        "aria-meter-name", "aria-progressbar-name", "aria-tooltip-name",
        "image-alt", "input-image-alt", "role-img-alt", "svg-img-alt", "object-alt",
        "frame-title", "document-title", "html-has-lang", "html-lang-valid",
        "form-field-multiple-labels",
    };

    /// <summary>Text too faint against its background.</summary>
    public static readonly HashSet<string> ContrastRules = new(StringComparer.Ordinal)
    {
        "color-contrast",
    };

    /// <summary>Things a keyboard can't reach or get out of.</summary>
    public static readonly HashSet<string> KeyboardRules = new(StringComparer.Ordinal)
    {
        "scrollable-region-focusable", "nested-interactive", "frame-focusable-content",
        "aria-hidden-focus", "focus-order-semantics", "tabindex", "accesskeys",
    };

    public static string Group(string ruleId) =>
        LabelRules.Contains(ruleId) ? "label"
        : ContrastRules.Contains(ruleId) ? "contrast"
        : KeyboardRules.Contains(ruleId) ? "keyboard"
        : "advice";

    public static bool Blocks(string ruleId) => Group(ruleId) != "advice";

    // ── axe ───────────────────────────────────────────────────────────

    public sealed record Finding(string Target, string Html, string Summary);
    public sealed record Violation(string Id, string Impact, string Help, string HelpUrl, List<Finding> Nodes)
    {
        public string Group => Accessibility.Group(Id);
        public bool Blocks => Accessibility.Blocks(Id);
    }

    private static readonly Lazy<string> AxeSource = new(LoadAxe);

    private static string LoadAxe()
    {
        // bin\Debug\net8.0 sits under the test project, so walk up to Assets\.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "Assets", "axe.min.js");
            if (File.Exists(path)) return File.ReadAllText(path) + "\n;true";
        }
        throw new AssertionException(
            "Couldn't find Assets\\axe.min.js above " + AppContext.BaseDirectory +
            ". It comes with round 110 — copy tests\\MerkaiTrial.E2E\\Assets\\axe.min.js from the zip.");
    }

    private const string RunAxe = @"async () => {
        const r = await axe.run(document, {
            runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'] },
            resultTypes: ['violations']
        });
        const t = x => Array.isArray(x) ? x.join(' >>> ') : String(x);
        return JSON.stringify(r.violations.map(v => ({
            id: v.id, impact: v.impact || '', help: v.help || '', helpUrl: v.helpUrl || '',
            nodes: v.nodes.map(n => ({
                target: (n.target || []).map(t).join(' '),
                html: (n.html || '').replace(/\s+/g, ' ').slice(0, 180),
                summary: (n.failureSummary || '').split('\n').map(s => s.trim()).filter(s => s && !/^Fix (any|all) of the following/.test(s)).slice(0, 3).join(' ')
            }))
        })));
    }";

    /// <summary>Runs axe on the page as it is now.</summary>
    public static async Task<List<Violation>> ScanAsync(IPage page)
    {
        await Ui.WaitForDocumentAsync(page, complete: true);
        await page.WaitForTimeoutAsync(400);     // let fade-ins finish, or contrast is measured mid-animation

        if (!await page.EvaluateAsync<bool>("() => typeof window.axe === 'object'"))
            await page.EvaluateAsync(AxeSource.Value);

        var json = await page.EvaluateAsync<string>(RunAxe);
        using var doc = JsonDocument.Parse(json);
        var list = new List<Violation>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            var nodes = v.GetProperty("nodes").EnumerateArray()
                .Select(n => new Finding(n.GetProperty("target").GetString() ?? "",
                                         n.GetProperty("html").GetString() ?? "",
                                         n.GetProperty("summary").GetString() ?? ""))
                .ToList();
            list.Add(new Violation(v.GetProperty("id").GetString() ?? "", v.GetProperty("impact").GetString() ?? "",
                v.GetProperty("help").GetString() ?? "", v.GetProperty("helpUrl").GetString() ?? "", nodes));
        }
        return list;
    }

    /// <summary>Violations as readable text, up to <paramref name="perRule"/> places each.</summary>
    public static string Describe(IEnumerable<Violation> violations, int perRule = 6)
    {
        var sb = new StringBuilder();
        foreach (var v in violations.OrderBy(v => v.Group).ThenBy(v => v.Id))
        {
            sb.AppendLine($"  [{v.Group}] {v.Id} ({v.Impact}) — {v.Help} — {v.Nodes.Count} place(s)");
            foreach (var n in v.Nodes.Take(perRule))
            {
                sb.AppendLine($"      at {n.Target}");
                sb.AppendLine($"         {n.Html}");
                if (n.Summary.Length > 0) sb.AppendLine($"         {n.Summary}");
            }
            if (v.Nodes.Count > perRule) sb.AppendLine($"      …and {v.Nodes.Count - perRule} more (see the report file)");
        }
        return sb.ToString();
    }

    // ── the keyboard ──────────────────────────────────────────────────

    /// <summary>The focused element as "n|tag#id "name"", or "" when nothing is focused.</summary>
    private const string Focused = @"() => {
        const el = document.activeElement;
        if (!el || el === document.body || el === document.documentElement) return '';
        if (!el.dataset.e2eTab) el.dataset.e2eTab = String((window.__e2eTab = (window.__e2eTab || 0) + 1));
        let d = el.tagName.toLowerCase();
        if (el.tagName === 'INPUT') d += '[' + (el.type || 'text') + ']';
        if (el.id) d += '#' + el.id;
        else if (el.classList.length) d += '.' + Array.from(el.classList).slice(0, 2).join('.');
        const name = (el.getAttribute('aria-label') || el.innerText || el.value || el.getAttribute('placeholder') || el.getAttribute('name') || '')
            .toString().trim().replace(/\s+/g, ' ').slice(0, 40);
        return el.dataset.e2eTab + '|' + d + (name ? ' ""' + name + '""' : '');
    }";

    /// <summary>Roughly how many things Tab should stop at.</summary>
    private const string CountTabbable = @"() => {
        const sel = 'a[href], button, input, select, textarea, summary, iframe, [tabindex], [contenteditable=""true""]';
        return Array.from(document.querySelectorAll(sel)).filter(el => {
            if (el.disabled) return false;
            if (el.getAttribute('tabindex') === '-1') return false;
            if (el.tagName === 'INPUT' && el.type === 'hidden') return false;
            if (el.closest('[inert]')) return false;
            const s = getComputedStyle(el);
            if (s.visibility === 'hidden' || s.display === 'none') return false;
            return el.getClientRects().length > 0;
        }).length;
    }";

    private static readonly System.Text.RegularExpressions.Regex DateBox =
        new(@"\|input\[(date|time|datetime-local|month|week)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string Label(string focused) => focused.Contains('|') ? focused[(focused.IndexOf('|') + 1)..] : focused;

    public sealed record TabWalk(bool Trapped, int Stops, int Tabbable, string Detail);

    /// <summary>
    /// Presses Tab through the page from wherever focus starts. Fine when
    /// focus leaves the page or comes back round to where it started. A
    /// trap is focus that stops moving, or goes round a few elements
    /// without ever getting back to the start.
    /// </summary>
    public static async Task<TabWalk> WalkAsync(IPage page)
    {
        await page.EvaluateAsync("() => { if (document.activeElement && document.activeElement.blur) document.activeElement.blur(); }");
        var tabbable = await page.EvaluateAsync<int>(CountTabbable);
        var max = Math.Clamp(tabbable + 30, 60, 500);

        string? first = null, previous = null;
        var stuck = 0;
        var trail = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < max; i++)
        {
            await page.Keyboard.PressAsync("Tab");
            var now = await page.EvaluateAsync<string>(Focused);

            if (now.Length == 0)
            {
                if (first is not null)
                    return new TabWalk(false, seen.Count, tabbable, $"Tab visited {seen.Count} place(s) and left the page.");
                continue;
            }

            if (first is null) first = now;
            else if (now == first)
                return new TabWalk(false, seen.Count, tabbable, $"Tab went round all {seen.Count} place(s) and back to the start.");

            if (now == previous)
            {
                // Inside an iframe (a PDF preview, say) focus moves through the
                // frame's own elements while the page still reports the frame.
                // A date or time box: Tab moves day → month → year (→ hour →
                // minute → AM/PM) inside the one box before leaving it.
                var limit = now.Contains("|iframe", StringComparison.Ordinal) ? 60
                          : DateBox.IsMatch(now) ? 8
                          : 2;
                if (++stuck >= limit)
                    return new TabWalk(true, seen.Count, tabbable,
                        $"Focus stopped moving at {Label(now)}: Tab no longer takes you anywhere.");
            }
            else stuck = 0;

            seen.Add(now);
            trail.Add(Label(now));
            previous = now;
        }

        var loop = string.Join(" → ", trail.TakeLast(8));
        return new TabWalk(true, seen.Count, tabbable,
            $"After {max} presses of Tab, focus was still going round {seen.Count} place(s) " +
            $"(the page has about {tabbable}) and never got back to where it started or off the page. Last stops: {loop}");
    }

    /// <summary>Presses Tab until the element matching <paramref name="selector"/> has focus.</summary>
    public static async Task TabToAsync(IPage page, string selector, string what, int maxTabs = 150)
    {
        var trail = new List<string>();
        for (var i = 0; i < maxTabs; i++)
        {
            if (await page.EvaluateAsync<bool>("s => !!document.activeElement && document.activeElement.matches(s)", selector))
                return;
            await page.Keyboard.PressAsync("Tab");
            var now = await page.EvaluateAsync<string>(Focused);
            trail.Add(now.Length == 0 ? "(off the page)" : Label(now));
        }
        throw new AssertionException(
            $"Couldn't reach {what} with the Tab key in {maxTabs} presses — it may not be focusable, or something " +
            $"before it holds focus. The last stops were: {string.Join(" → ", trail.TakeLast(10))}");
    }

    /// <summary>A file-name-safe version of a page path.</summary>
    public static string SafeName(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.Trim('/'))
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "home" : s;
    }
}
