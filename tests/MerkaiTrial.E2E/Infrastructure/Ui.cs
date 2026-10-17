// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/Ui.cs
//
// Small helpers for the things every test does: sign in, pick an option
// from a drop-down by what it says, set a password from an invite or
// reset link, and check that a page was refused.
//
// 088: ExpectSuccessAsync (the green banner, or the page's own reason
// when there is none) and FindDateTimes (dates and times as the app
// prints them, for the time-zone checks).
//
// 088d: DescribePageProblemsAsync ignores an empty file picker.
//
// 088c: SetPasswordFromLinkAsync no longer waits for the sign-in page to
// finish loading every resource — one run stopped there for 20 seconds
// with "Your password is set" already on screen. It waits for the
// address to change, then for that message.
//
// 092: A SLOW PAGE NOW SAYS IT WAS SLOW. When a wait ran out because the
// page had not finished arriving, the failure read like a page problem —
// "Expected a success message … The page shows no error message" — and
// sent people looking for a validation bug that was not there. Now:
//   • ExpectSuccessAsync and WaitUntilAsync check whether the page was
//     still loading when time ran out, and say so first ("…was still
//     loading after 20s — the server was slow to answer, not a form
//     problem"), with the time to look for in webapi.out.log.
//   • WaitForPageAsync: wait for an address, then for the page to be
//     usable (DOMContentLoaded, not every font and image), with the same
//     explanation when it does not arrive. Flows.OpenLeadAsync uses it —
//     that is the wait one 089 run lost 20 seconds on.
//
// 093d: SetPasswordFromLinkAsync waits up to 40 seconds for the redirect
// after a new password, and says why when it does not come — see there.
// =====================================================================

using System.Text.RegularExpressions;

namespace MerkaiTrial.E2E.Infrastructure;

public static class Ui
{
    // ── Signing in ────────────────────────────────────────────────────

    /// <summary>Signs in through /Account/Login and waits for the landing
    /// page: /Admin for the super admin, /Dashboard for everyone else.</summary>
    public static async Task SignInAsync(IPage page, TestUser user, bool superAdmin = false)
    {
        await page.GotoAsync("/Account/Login");
        await page.Locator("#Email").FillAsync(user.Email);
        await page.Locator("#Password").FillAsync(user.Password);
        await page.Locator("form button[type=submit]").ClickAsync();

        var landing = superAdmin ? "/Admin" : "/Dashboard";
        try
        {
            await page.WaitForURLAsync(url => PathOf(url).StartsWith(landing, StringComparison.OrdinalIgnoreCase),
                new PageWaitForURLOptions { Timeout = E2ESettings.ActionTimeoutMs });
        }
        catch (Exception ex) when (Ui.IsTimeout(ex))
        {
            var error = await TextOrEmptyAsync(page.Locator(".alert-error"));
            throw new AssertionException(
                $"Signing in as {user.Key} ({user.Email}) did not reach {landing}. " +
                $"Still on {page.Url}. {(error.Length > 0 ? "The page said: " + error : "")}");
        }
    }

    /// <summary>
    /// Opens an invite or reset link and chooses a password. Ends on the
    /// sign-in page with "Your password is set".
    /// </summary>
    public static async Task SetPasswordFromLinkAsync(IPage page, string link, string password)
    {
        await page.GotoAsync(Rebase(link));

        var newPassword = page.Locator("#NewPassword");
        if (await newPassword.CountAsync() == 0)
            throw new AssertionException(
                $"The link {MaskToken(link)} did not open the set-password form " +
                $"(expired or already used?). Page: {page.Url}");

        await newPassword.FillAsync(password);
        await page.Locator("#ConfirmPassword").FillAsync(password);
        await page.Locator("form:has(#NewPassword) button[type=submit]").ClickAsync();

        // 088c: Commit, not the default Load — the redirect is what proves
        // the password was accepted, and a slow web font on the sign-in
        // page must not fail the run. The message is checked instead.
        //
        // 093d: twice the usual wait, and a reason when it still runs out.
        // Setting a password hashes it on the server, and the first one in a
        // freshly provisioned workspace also warms that workspace up — one
        // run took longer than 20 seconds here and failed a whole fixture's
        // setup with a bare "Timeout 20000ms exceeded". This runs once per
        // person per run, so the longer wait costs nothing when all is well.
        var wait = E2ESettings.ActionTimeoutMs * 2;
        try
        {
            await page.WaitForURLAsync(url => url.Contains("passwordSet=true", StringComparison.OrdinalIgnoreCase),
                new PageWaitForURLOptions { Timeout = wait, WaitUntil = WaitUntilState.Commit });
        }
        catch (Exception ex) when (IsTimeout(ex))
        {
            throw new AssertionException(
                $"Setting the password from {MaskToken(link)} did not reach the sign-in page within {wait / 1000:0}s " +
                $"(still on {PathOf(page.Url)}). " +
                (await StillLoadingAsync(page) ?? await DescribePageProblemsAsync(page)));
        }
        await page.GetByText("Your password is set").First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = E2ESettings.ActionTimeoutMs });
    }

    // ── Drop-downs ────────────────────────────────────────────────────

    /// <summary>
    /// Selects the first option whose visible text contains <paramref name="text"/>
    /// (case-insensitive). Returns the option's text, or null when nothing
    /// matches and <paramref name="required"/> is false.
    /// </summary>
    public static async Task<string?> SelectByTextAsync(ILocator select, string text, bool required = true)
    {
        var found = await select.EvaluateAsync<string[]?>(
            @"(el, needle) => {
                const o = [...el.options].find(o => o.value && o.text.toLowerCase().includes(needle.toLowerCase()));
                return o ? [o.value, o.text.trim()] : null;
            }", text);

        if (found is null)
        {
            if (!required) return null;
            var all = await OptionTextsAsync(select);
            throw new AssertionException($"No option containing '{text}'. Options were: {string.Join(" | ", all)}");
        }

        await select.SelectOptionAsync(found[0]);
        return found[1];
    }

    /// <summary>Selects the first option matching a regular expression.</summary>
    public static async Task<string> SelectByPatternAsync(ILocator select, string pattern, string? excludePattern = null)
    {
        var found = await select.EvaluateAsync<string[]?>(
            @"(el, args) => {
                const inc = new RegExp(args[0], 'i');
                const exc = args[1] ? new RegExp(args[1], 'i') : null;
                const o = [...el.options].find(o => o.value && inc.test(o.text) && !(exc && exc.test(o.text)));
                return o ? [o.value, o.text.trim()] : null;
            }", new[] { pattern, excludePattern ?? "" });

        if (found is null)
        {
            var all = await OptionTextsAsync(select);
            throw new AssertionException($"No option matching /{pattern}/. Options were: {string.Join(" | ", all)}");
        }

        await select.SelectOptionAsync(found[0]);
        return found[1];
    }

    /// <summary>Selects the first option with a value, unless one is
    /// already selected.</summary>
    public static async Task SelectFirstIfEmptyAsync(ILocator select)
    {
        var current = await select.InputValueAsync();
        if (!string.IsNullOrEmpty(current)) return;

        var first = await select.EvaluateAsync<string?>(
            "el => { const o = [...el.options].find(o => o.value); return o ? o.value : null; }");
        if (first is null)
            throw new AssertionException("The drop-down has no options to choose from.");
        await select.SelectOptionAsync(first);
    }

    public static Task<string[]> OptionTextsAsync(ILocator select) =>
        select.EvaluateAsync<string[]>("el => [...el.options].map(o => o.text.trim())");

    // ── Roles (Settings → Users → Add person) ─────────────────────────

    /// <summary>Loose patterns for the three seeded roles, whatever this
    /// database's RoleTemplates call them.</summary>
    public static class RolePatterns
    {
        public const string Manager = @"sales\s*manager|manager";
        public const string Rep     = @"sales\s*rep|representative|sales\s*exec|^\s*rep\b";
        public const string Viewer  = @"view|read[\s-]*only";
    }

    /// <summary>
    /// Ticks the first role checkbox whose label matches <paramref name="pattern"/>
    /// on Settings → Users → Add person, and returns the label. Fails with
    /// the list of roles the page actually offers.
    /// </summary>
    public static async Task<string> TickRoleAsync(IPage page, string pattern)
    {
        var labels = await page.Locator(".form-check:has(input[name='Input.RoleIds']) label")
            .EvaluateAllAsync<string[]>("els => els.map(e => e.textContent.trim())");

        var regex = new Regex(pattern, RegexOptions.IgnoreCase);
        var index = Array.FindIndex(labels, l => regex.IsMatch(l));
        if (index < 0)
            throw new AssertionException(
                $"Settings → Users → Add person has no role matching /{pattern}/. " +
                $"Roles offered: {(labels.Length == 0 ? "(none)" : string.Join(" | ", labels))}");

        await page.Locator(".form-check:has(input[name='Input.RoleIds'])").Nth(index)
            .Locator("input[name='Input.RoleIds']").CheckAsync();
        return labels[index];
    }

    // ── Waiting ───────────────────────────────────────────────────────

    /// <summary>Checks <paramref name="condition"/> every 250ms until it is
    /// true, or fails with <paramref name="failure"/> after the timeout.
    /// For "one of several things should happen" waits.</summary>
    public static async Task WaitUntilAsync(IPage page, Func<Task<bool>> condition, string failure,
        float timeoutMs = E2ESettings.ActionTimeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            try
            {
                if (await condition()) return;
            }
            catch (PlaywrightException)
            {
                // Mid-navigation; the page will be there next time round.
            }

            if (DateTime.UtcNow > deadline)
            {
                var loading = await StillLoadingAsync(page);
                throw new AssertionException(
                    $"{failure} (waited {timeoutMs / 1000:0}s; now at {page.Url})" +
                    (loading is null ? "" : $". {loading}"));
            }
            await page.WaitForTimeoutAsync(250);
        }
    }

    /// <summary>
    /// 092. Waits for the address to match <paramref name="url"/> and the
    /// page to be usable (DOMContentLoaded — not every image and font,
    /// which is what the default "load" waits for). <paramref name="what"/>
    /// names the page for the failure message, which says whether the
    /// address never changed or the page was still arriving.
    /// </summary>
    public static async Task WaitForPageAsync(IPage page, Func<string, bool> url, string what,
        float timeoutMs = E2ESettings.ActionTimeoutMs)
    {
        var started = DateTime.UtcNow;
        try
        {
            await page.WaitForURLAsync(url, new PageWaitForURLOptions { Timeout = timeoutMs, WaitUntil = WaitUntilState.Commit });
        }
        catch (Exception ex) when (IsTimeout(ex))
        {
            throw new AssertionException(
                $"{what} did not open: after {timeoutMs / 1000:0}s the browser was still on {PathOf(page.Url)}. " +
                (await StillLoadingAsync(page) ?? await DescribePageProblemsAsync(page)));
        }

        var left = Math.Max(1000f, timeoutMs - (float)(DateTime.UtcNow - started).TotalMilliseconds);
        try
        {
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = left });
        }
        catch (Exception ex) when (IsTimeout(ex))
        {
            throw new AssertionException(
                $"{what} ({PathOf(page.Url)}) was still loading after {timeoutMs / 1000:0}s. The server was slow to " +
                $"answer — not a problem with the page itself. Look in webapi.out.log around {DateTime.Now:HH:mm:ss} " +
                "for a slow request.");
        }
    }

    /// <summary>
    /// 092. A sentence when the page had NOT finished arriving — the
    /// document still loading, or no document yet because a navigation was
    /// in flight — and null when it had. Never throws and never waits more
    /// than three seconds itself.
    /// </summary>
    public static async Task<string?> StillLoadingAsync(IPage page)
    {
        string? state;
        try
        {
            var probe = page.EvaluateAsync<string>("() => document.readyState");
            var done = await Task.WhenAny(probe, Task.Delay(3000));
            state = done == probe ? await probe : null;
        }
        catch (Exception)
        {
            state = null;   // mid-navigation: there is no document to ask
        }

        if (state == "complete" || state == "interactive") return null;

        return $"The page at {PathOf(page.Url)} was still loading when the wait ran out " +
               $"({(state is null ? "no page had arrived yet" : "document " + state)}) — the server was slow to answer, " +
               $"not a form problem. Look in webapi.out.log around {DateTime.Now:HH:mm:ss} for a slow request.";
    }

    /// <summary>
    /// True for a Playwright wait that ran out of time. Matched by name so
    /// it holds whichever TimeoutException type the Playwright version
    /// throws (System's, or its own).
    /// </summary>
    public static bool IsTimeout(Exception ex) =>
        ex is System.TimeoutException || ex.GetType().Name == "TimeoutException";

    /// <summary>
    /// Everything on the page that explains why a form did not go through:
    /// error alerts, validation messages, fields the browser refuses
    /// (required, out of range), fields the page marked invalid, and the
    /// quote editor's own problem list. For failure messages.
    /// </summary>
    public static async Task<string> DescribePageProblemsAsync(IPage page)
    {
        try
        {
            var found = await page.EvaluateAsync<string[]>(
                @"() => {
                    const out = [];
                    const vis = el => !!(el.offsetWidth || el.offsetHeight || el.getClientRects().length);
                    const add = (label, text) => { text = (text || '').replace(/\s+/g, ' ').trim(); if (text) out.push(label + ': ' + text); };
                    document.querySelectorAll('.alert-danger, .alert-warning, .validation-summary-errors, #qeProblems, #qcDateErr, .qe-hint')
                        .forEach(el => { if (vis(el)) add('Page says', el.innerText); });
                    document.querySelectorAll('span.text-danger, .field-validation-error')
                        .forEach(el => { if (vis(el)) add('Field message', el.innerText); });
                    // 088d: an empty file picker (attachments) counts as invalid on
                    // every page that has one — noise, not a reason.
                    document.querySelectorAll('form :invalid')
                        .forEach(el => { if (el.tagName !== 'FORM' && el.tagName !== 'FIELDSET' && el.type !== 'file')
                            add('Browser refuses ' + (el.getAttribute('aria-label') || el.name || el.id || el.tagName),
                                el.validationMessage || 'invalid'); });
                    document.querySelectorAll('.is-invalid')
                        .forEach(el => add('Marked invalid', el.getAttribute('aria-label') || el.name || el.id));
                    return out.slice(0, 15);
                }");
            return found.Length == 0
                ? "The page shows no error message — open the trace to see what happened."
                : string.Join(" | ", found);
        }
        catch (Exception ex)
        {
            return "(could not read the page: " + ex.Message + ")";
        }
    }

    // ── Results ───────────────────────────────────────────────────────

    /// <summary>
    /// Waits for the green banner _Layout draws after a save, containing
    /// <paramref name="text"/>. Fails with whatever the page says instead
    /// (the red banner, validation messages) rather than "not visible".
    /// </summary>
    public static async Task ExpectSuccessAsync(IPage page, string text)
    {
        var ok = page.Locator(".alert-success", new PageLocatorOptions { HasText = text });
        try
        {
            await ok.First.WaitForAsync(new LocatorWaitForOptions { Timeout = E2ESettings.ActionTimeoutMs });
        }
        catch (Exception ex) when (IsTimeout(ex))
        {
            // 092 — a page that never finished arriving has no message to
            // show; say that, rather than "the page shows no error".
            var loading = await StillLoadingAsync(page);
            if (loading is not null)
                throw new AssertionException(
                    $"Expected a success message containing \"{text}\", but no page came back in " +
                    $"{E2ESettings.ActionTimeoutMs / 1000:0}s. {loading}");

            var green = await TextOrEmptyAsync(page.Locator(".alert-success"));
            throw new AssertionException(
                $"Expected a success message containing \"{text}\" on {PathOf(page.Url)}. " +
                (green.Length > 0 ? $"The green message said: {green}. " : "") +
                await DescribePageProblemsAsync(page));
        }
    }

    // ── Dates and times as the app prints them ────────────────────────

    /// <summary>
    /// Every "date time" in <paramref name="text"/>, read the way the app
    /// writes them: day first (dd/MM/yyyy, dd-MM-yyyy, dd.MM.yyyy), then
    /// HH:mm or h:mm AM/PM. A Buddhist-era year (2569 for 2026) is turned
    /// back into the Christian one, in case a server culture is Thai.
    /// </summary>
    public static List<DateTime> FindDateTimes(string text)
    {
        var found = new List<DateTime>();
        var re = new Regex(@"(\d{1,2})[/.\-](\d{1,2})[/.\-](\d{4})[ ,T]+(\d{1,2}):(\d{2})(?::\d{2})?\s*([AaPp][Mm])?");
        foreach (Match m in re.Matches(text))
        {
            var day = int.Parse(m.Groups[1].Value);
            var month = int.Parse(m.Groups[2].Value);
            var year = int.Parse(m.Groups[3].Value);
            var hour = int.Parse(m.Groups[4].Value);
            var minute = int.Parse(m.Groups[5].Value);
            if (year > 2400) year -= 543;

            var ampm = m.Groups[6].Value.ToUpperInvariant();
            if (ampm == "PM" && hour < 12) hour += 12;
            if (ampm == "AM" && hour == 12) hour = 0;

            // Day-first is what these markets use; month-first is tried too
            // so a US-formatted server is reported as a wrong TIME only if
            // the time itself is wrong.
            foreach (var (d, mo) in new[] { (day, month), (month, day) })
            {
                if (mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(year, mo) || hour > 23 || minute > 59) continue;
                found.Add(new DateTime(year, mo, d, hour, minute, 0, DateTimeKind.Unspecified));
            }
        }
        return found;
    }

    // ── Refusals ──────────────────────────────────────────────────────

    /// <summary>
    /// Opens <paramref name="path"/> and asserts the app refused it: either
    /// it answered 403/404, or it redirected somewhere else (AccessDenied,
    /// sign-in). Passing means the page's content was never shown.
    /// </summary>
    public static async Task ExpectRefusedAsync(IPage page, string path)
    {
        var response = await page.GotoAsync(path);
        var status = response?.Status ?? 0;
        var landed = PathOf(page.Url);
        var wanted = PathOf(E2ESettings.BaseUrl + path);

        var refused = status is 403 or 404 || !landed.Equals(wanted, StringComparison.OrdinalIgnoreCase);
        Assert.That(refused, Is.True,
            $"Expected {path} to be refused, but it opened (HTTP {status}, now at {page.Url}).");
    }

    // ── Links ─────────────────────────────────────────────────────────

    /// <summary>
    /// Points a link at the test server. Public quote links, invite and
    /// reset links are built from configuration or the request, so they may
    /// name another host; only the path and query matter here.
    /// </summary>
    public static string Rebase(string link)
    {
        if (Uri.TryCreate(link, UriKind.Absolute, out var abs))
            return E2ESettings.BaseUrl + abs.PathAndQuery;
        return E2ESettings.BaseUrl + (link.StartsWith('/') ? link : "/" + link);
    }

    public static string PathOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath.TrimEnd('/') : url;

    /// <summary>For messages: never print a whole token.</summary>
    public static string MaskToken(string link) =>
        Regex.Replace(link, @"(token=|/q/|/i/)([^&\s]{4})[^&\s]*", "$1$2****", RegexOptions.IgnoreCase);

    public static async Task<string> TextOrEmptyAsync(ILocator locator) =>
        await locator.CountAsync() > 0 ? (await locator.First.InnerTextAsync()).Trim() : "";

    /// <summary>The Guid at the end of a URL like /Leads/Detail/{id}, or in ?id=.</summary>
    public static Guid IdFromUrl(string url)
    {
        var m = Regex.Match(url, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        if (!m.Success) throw new AssertionException($"No record id in {url}.");
        return Guid.Parse(m.Value);
    }
}
