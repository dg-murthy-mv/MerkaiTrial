// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Services/Core/TenantContextForwardingHandler.cs
//
// WHY THIS EXISTS:
// Admin.Web's tenant CRM pages (Leads, Deals, Dashboard, etc.) don't hit
// FlowDbContext directly — they call out to MerkaiTrial.WebApi over HTTP via
// IApiService/ApiClient. WebApi authenticates every incoming request with
// its OWN separate DemoAuthenticationHandler, reading its OWN appsettings.json.
// Without this handler, WebApi has no idea Admin.Web is "viewing as" a
// different tenant/user — it just always answers as whatever's hardcoded in
// WebApi's own config, regardless of what Admin.Web's ViewAs picker selected.
//
// This handler reads the CURRENT effective identity from Admin.Web's own
// authenticated principal (which already correctly reflects either the
// ViewAs cookie or the appsettings fallback, via DemoAuthenticationHandler)
// and attaches it as headers on every outgoing request. WebApi's copy of
// DemoAuthenticationHandler (same shared class, see TenantContextHeaders)
// reads those headers when no browser cookie is present — which is always
// the case for server-to-server calls like this one.
// =====================================================================

using System.Net.Http.Headers;

namespace MerkaiTrial.Admin.Web.Services.Core;

public class TenantContextForwardingHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<TenantContextForwardingHandler> _logger;

    public TenantContextForwardingHandler(
        IHttpContextAccessor httpContextAccessor,
        ILogger<TenantContextForwardingHandler> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var user = _httpContextAccessor.HttpContext?.User;

        var tenantId = user?.FindFirst("TenantId")?.Value;
        var userId   = user?.FindFirst("UserId")?.Value;

        if (!string.IsNullOrWhiteSpace(tenantId) && !string.IsNullOrWhiteSpace(userId))
        {
            request.Headers.Remove("X-Tenant-Id");
            request.Headers.Remove("X-User-Id");
            request.Headers.Add("X-Tenant-Id", tenantId);
            request.Headers.Add("X-User-Id", userId);
        }
        else
        {
            // No authenticated principal on the current request (e.g. a
            // background/startup call with no HttpContext) — let WebApi
            // fall back to its own appsettings default rather than sending
            // nothing and failing GUID parsing on an empty header.
            _logger.LogDebug("TenantContextForwardingHandler: no TenantId/UserId claims on current HttpContext — WebApi will use its own appsettings default.");
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
