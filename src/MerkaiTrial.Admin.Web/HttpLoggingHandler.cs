using System.Net.Http;
using Microsoft.Extensions.Logging;

public class HttpLoggingHandler : DelegatingHandler
{
    private readonly ILogger<HttpLoggingHandler> _logger;
    public HttpLoggingHandler(ILogger<HttpLoggingHandler> logger) => _logger = logger;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var cid = Guid.NewGuid().ToString("N");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", cid);

        _logger.LogInformation("API -> {Method} {Url} cid={Cid}", request.Method, request.RequestUri, cid);
        var res = await base.SendAsync(request, ct);
        _logger.LogInformation("API <- {Status} {Url} cid={Cid}", (int)res.StatusCode, request.RequestUri, cid);

        return res;
    }
}
