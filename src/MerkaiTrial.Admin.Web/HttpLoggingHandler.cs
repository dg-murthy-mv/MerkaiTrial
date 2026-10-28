// =====================================================================
// FILE: MerkaiTrial.Admin.Web/HttpLoggingHandler.cs
//
// One log line out and one back for every call Admin.Web makes to the API,
// tied together by a correlation id.
//
// 105 — VUL-20: THE LINE NO LONGER CARRIES THE WHOLE ADDRESS.
//   It used to log request.RequestUri in full, so a customer opening their
//   quote put the quote's secret link token into adminweb.out.log
//   ("API -> GET https://…/api/quotes/public/9JGgqZ…"), and searches put
//   whatever was typed — names, emails, phone numbers — there too. The
//   request logging on both hosts already masked these (063/085); this was
//   the line that did not.
//
//   Now: the path only, through the same PublicLinkPaths.Mask as the
//   request logs (…/public/****bSxl), and the query string reduced to its
//   parameter NAMES (?searchTerm=…&pageSize=…) — enough to see which call
//   it was, nothing anyone typed.
// =====================================================================

using System.Net.Http;
using MerkaiTrial.Application.Common;
using Microsoft.Extensions.Logging;

public class HttpLoggingHandler : DelegatingHandler
{
    private readonly ILogger<HttpLoggingHandler> _logger;
    public HttpLoggingHandler(ILogger<HttpLoggingHandler> logger) => _logger = logger;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var cid = Guid.NewGuid().ToString("N");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", cid);

        var what = SafeTarget(request.RequestUri);
        _logger.LogInformation("API -> {Method} {Path} cid={Cid}", request.Method, what, cid);
        var res = await base.SendAsync(request, ct);
        _logger.LogInformation("API <- {Status} {Path} cid={Cid}", (int)res.StatusCode, what, cid);

        return res;
    }

    /// <summary>105. "/api/quotes/public/****bSxl?status=…" — never a token, never a value.</summary>
    public static string SafeTarget(Uri? uri)
    {
        if (uri is null) return "(none)";

        var path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString.Split('?', 2)[0];
        if (!path.StartsWith('/')) path = "/" + path;          // "api/…" relative → "/api/…"
        path = PublicLinkPaths.Mask(path);

        var query = uri.IsAbsoluteUri ? uri.Query : (uri.OriginalString.Contains('?') ? "?" + uri.OriginalString.Split('?', 2)[1] : "");
        if (string.IsNullOrEmpty(query) || query == "?") return path;

        var names = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2)[0])
            .Where(n => n.Length > 0)
            .Select(n => (n.Length <= 40 ? n : n[..40]) + "=…");

        return path + "?" + string.Join("&", names);
    }
}
