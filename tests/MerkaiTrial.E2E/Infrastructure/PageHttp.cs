// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/PageHttp.cs
//
// NEW (104). A request made from INSIDE a signed-in page with fetch(), so
// it carries that person's cookies exactly as a click or a form would —
// for the security tests, which need to send what a browser would send
// but with something changed (no anti-forgery token, another person's
// token, a payload in the address).
//
//   • form   — sent as application/x-www-form-urlencoded, like an HTML form.
//   • follow — false keeps a redirect as the answer (a successful form post
//              is a 302), true follows it like the browser.
// =====================================================================

using System.Text.Json;

namespace MerkaiTrial.E2E.Infrastructure;

public static class PageHttp
{
    public sealed record Result(int Status, string Text, string Url, string Type, long Millis)
    {
        /// <summary>A redirect kept as the answer (fetch reports it as an "opaqueredirect" with status 0).</summary>
        public bool Redirected => Type == "opaqueredirect";

        public override string ToString() => $"HTTP {Status}{(Redirected ? " (redirect)" : "")} {ApiResponse.Short(Text, 160)}";
    }

    public static async Task<Result> FetchAsync(IPage page, string method, string path,
        IEnumerable<KeyValuePair<string, string>>? form = null, bool follow = true)
    {
        var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : E2ESettings.BaseUrl + path;
        var pairs = form?.Select(kv => new[] { kv.Key, kv.Value }).ToArray();

        var json = await page.EvaluateAsync<string>(
            @"async (a) => {
                const init = { method: a.method, credentials: 'same-origin', redirect: a.follow ? 'follow' : 'manual' };
                if (a.form) init.body = new URLSearchParams(a.form);
                const started = performance.now();
                let r;
                try { r = await fetch(a.url, init); }
                catch (e) { return JSON.stringify({ s: -1, t: String(e), u: a.url, ty: 'error', ms: performance.now() - started }); }
                let text = '';
                try { text = await r.text(); } catch (e) { }
                return JSON.stringify({ s: r.status, t: text, u: r.url, ty: r.type, ms: performance.now() - started });
              }",
            new { url, method, form = pairs, follow });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new Result(
            root.GetProperty("s").GetInt32(),
            root.GetProperty("t").GetString() ?? "",
            root.GetProperty("u").GetString() ?? "",
            root.GetProperty("ty").GetString() ?? "",
            (long)root.GetProperty("ms").GetDouble());
    }

    /// <summary>The anti-forgery token on the page the browser has open now.</summary>
    public static async Task<string> FormTokenAsync(IPage page)
    {
        var token = page.Locator("input[name=__RequestVerificationToken]");
        if (await token.CountAsync() == 0)
            throw new AssertionException($"{Ui.PathOf(page.Url)} has no anti-forgery token.");
        return await token.First.InputValueAsync();
    }
}
