// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/WebSession.cs
//
// NEW (105). The website without a browser: plain HTTP with a cookie jar,
// never following redirects, so a test can see exactly where the site
// sends someone (the Location header) and can copy the sign-in cookie into
// another jar — "someone else got hold of it".
//
// Each session has its own network address (X-Forwarded-For, believed only
// from this machine), like BrowserSession, so the sign-in limits of 104
// count it on its own.
// =====================================================================

using System.Net;
using Cookie = System.Net.Cookie;   // not Playwright's
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace MerkaiTrial.E2E.Infrastructure;

public sealed class WebSession : IDisposable
{
    private static readonly Uri Base = new(E2ESettings.BaseUrl);
    private readonly HttpClient _http;

    public CookieContainer Cookies { get; }
    public string NetworkAddress { get; }

    public WebSession(CookieContainer? cookies = null, string? networkAddress = null)
    {
        Cookies = cookies ?? new CookieContainer();
        NetworkAddress = networkAddress ?? BrowserSession.NewNetworkAddress();

        var handler = new HttpClientHandler
        {
            CookieContainer = Cookies,
            UseCookies = true,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        _http = new HttpClient(handler) { BaseAddress = Base, Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-For", NetworkAddress);
    }

    public sealed record Answer(int Status, string? Location, string Body, IReadOnlyDictionary<string, string> Headers)
    {
        public bool IsRedirect => Status is >= 300 and < 400;

        /// <summary>Sent to the sign-in page: the session was not accepted.</summary>
        public bool ToSignIn => IsRedirect && (Location ?? "").Contains("/Account/Login", StringComparison.OrdinalIgnoreCase);

        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

        public override string ToString() =>
            $"HTTP {Status}{(Location is null ? "" : " → " + Location)} {ApiResponse.Short(Body, 120)}";
    }

    public Task<Answer> GetAsync(string path, IDictionary<string, string>? headers = null)
        => SendAsync(HttpMethod.Get, path, null, headers);

    public Task<Answer> PostFormAsync(string path, IEnumerable<KeyValuePair<string, string>> form,
        IDictionary<string, string>? headers = null)
        => SendAsync(HttpMethod.Post, path, new FormUrlEncodedContent(form), headers);

    public async Task<Answer> SendAsync(HttpMethod method, string path, HttpContent? content = null,
        IDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(method, path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new Uri(path) : new Uri(Base, path));
        if (content is not null) request.Content = content;
        if (headers is not null)
            foreach (var (k, v) in headers) request.Headers.TryAddWithoutValidation(k, v);

        using var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return new Answer((int)response.StatusCode, response.Headers.Location?.OriginalString, body, Collect(response));
    }

    /// <summary>
    /// Signs in through the real form: GET /Account/Login (for the form's
    /// token and cookie), then POST it. Returns the session and the answer
    /// to the POST — a 302 whose Location says where the site sent them.
    /// </summary>
    public static async Task<(WebSession Session, Answer Answer)> SignInAsync(
        TestUser user, string? returnUrl = null, bool rememberMe = false, string? password = null)
    {
        var session = new WebSession();
        var path = "/Account/Login" + (returnUrl is null ? "" : "?ReturnUrl=" + Uri.EscapeDataString(returnUrl));

        var form = await session.GetAsync(path);
        var token = FormToken(form.Body)
            ?? throw new AssertionException($"The sign-in page had no form token: {form}");

        var answer = await session.PostFormAsync(path, new[]
        {
            KeyValuePair.Create("Email", user.Email),
            KeyValuePair.Create("Password", password ?? user.Password),
            KeyValuePair.Create("RememberMe", rememberMe ? "true" : "false"),
            KeyValuePair.Create("__RequestVerificationToken", token),
        });
        return (session, answer);
    }

    /// <summary>Signs in and insists it worked (a redirect away from the sign-in page).</summary>
    public static async Task<WebSession> SignedInAsync(TestUser user, bool rememberMe = false, string? password = null)
    {
        var (session, answer) = await SignInAsync(user, rememberMe: rememberMe, password: password);
        if (!answer.IsRedirect || answer.ToSignIn)
            throw new AssertionException($"Signing in {user.Email} over plain HTTP did not work: {answer}");
        return session;
    }

    /// <summary>Presses Sign out: POST /Account/Logout with a form token from the sign-out page.</summary>
    public async Task<Answer> SignOutAsync()
    {
        var page = await GetAsync("/Account/Logout");
        var token = FormToken(page.Body);
        if (token is null)
        {
            page = await GetAsync("/Dashboard");
            token = FormToken(page.Body)
                ?? throw new AssertionException($"No form token to sign out with: {page}");
        }
        return await PostFormAsync("/Account/Logout", new[] { KeyValuePair.Create("__RequestVerificationToken", token) });
    }

    /// <summary>A second jar holding copies of this one's cookies — a copied (stolen) session.</summary>
    public WebSession Copy()
    {
        var jar = new CookieContainer();
        foreach (Cookie c in Cookies.GetCookies(Base))
            jar.Add(Base, new Cookie(c.Name, c.Value) { Path = "/", Secure = true, HttpOnly = c.HttpOnly });
        return new WebSession(jar);
    }

    /// <summary>The sign-in cookie, as the site set it, or null.</summary>
    public Cookie? AuthCookie =>
        Cookies.GetCookies(Base).Cast<Cookie>().FirstOrDefault(c => c.Name == "MerkaiTrial.Auth");

    public static string? FormToken(string html)
    {
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        if (!m.Success) m = Regex.Match(html, "value=\"([^\"]+)\"[^>]*name=\"__RequestVerificationToken\"");
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    private static IReadOnlyDictionary<string, string> Collect(HttpResponseMessage response)
    {
        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in response.Headers) all[h.Key] = string.Join(", ", h.Value);
        foreach (var h in response.Content.Headers) all[h.Key] = string.Join(", ", h.Value);
        return all;
    }

    public void Dispose() => _http.Dispose();
}
