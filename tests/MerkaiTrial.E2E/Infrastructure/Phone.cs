// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/Phone.cs
//
// NEW (094). What the phone-sized tests share.
//
//   • Size — iPhone SE, 375 × 667: the narrowest phone still in common
//     use. If a screen works here it works on anything wider.
//   • ExpectFitsAsync — the page does not scroll sideways. When it does,
//     the failure NAMES what sticks out ("div.lx-head > div.d-flex is
//     452px wide, right edge at 464px"), so the fix starts from the
//     element rather than from a screenshot. Things inside a box that
//     scrolls on purpose (the pipeline board, a table-responsive) are
//     left out: their content is meant to be wider than the screen.
// =====================================================================

namespace MerkaiTrial.E2E.Infrastructure;

public static class Phone
{
    public static readonly ViewportSize Size = new() { Width = 375, Height = 667 };

    /// <summary>Fails, naming the widest offenders, when the page is wider than the screen.</summary>
    public static async Task ExpectFitsAsync(IPage page, string what)
    {
        // Late styles and fonts can widen a page after DOMContentLoaded.
        // 101d: readyState "complete", looked at — see Ui.WaitForDocumentAsync.
        try { await Ui.WaitForDocumentAsync(page, 10_000, complete: true); }
        catch (Exception ex) when (Ui.IsTimeout(ex)) { /* measure what is there */ }

        var report = await page.EvaluateAsync<string[]>(@"() => {
            const vw = window.innerWidth;
            const pageWidth = document.documentElement.scrollWidth;
            if (pageWidth <= vw + 1) return [];

            // Is this element inside something that scrolls (or clips) sideways on purpose?
            const inScroller = el => {
                for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
                    const ox = getComputedStyle(p).overflowX;
                    if (ox === 'auto' || ox === 'scroll' || ox === 'hidden' || ox === 'clip') return true;
                }
                return false;
            };
            const name = el => {
                let s = el.tagName.toLowerCase();
                if (el.id) s += '#' + el.id;
                const cls = (el.getAttribute('class') || '').trim().split(/\s+/).filter(Boolean).slice(0, 3);
                if (cls.length) s += '.' + cls.join('.');
                return s;
            };

            const wide = [];
            for (const el of document.querySelectorAll('body *')) {
                const r = el.getBoundingClientRect();
                if (r.width === 0 || r.height === 0) continue;
                if (r.right <= vw + 1) continue;
                if (getComputedStyle(el).position === 'fixed') continue;
                if (inScroller(el)) continue;
                // Report the outermost offender only: its children are the same problem.
                const parent = el.parentElement;
                if (parent && parent !== document.body) {
                    const pr = parent.getBoundingClientRect();
                    if (pr.right > vw + 1 && !inScroller(parent)) continue;
                }
                const p = el.parentElement;
                wide.push((p && p !== document.body ? name(p) + ' > ' : '') + name(el) +
                          ' is ' + Math.round(r.width) + 'px wide, right edge at ' + Math.round(r.right) + 'px');
                if (wide.length >= 5) break;
            }
            return ['The page is ' + pageWidth + 'px wide on a ' + vw + 'px screen.'].concat(wide);
        }");

        if (report.Length > 0)
            throw new AssertionException($"{what} scrolls sideways on a phone. " + string.Join(" | ", report));
    }
}
