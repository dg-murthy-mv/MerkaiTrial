// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Services/Core/ApiTokenForwardingHandler.cs
//
// Replaces TenantContextForwardingHandler. Same position in the
// HttpClient pipeline; delete the old file once this is in.
//
// WHAT THE OLD HANDLER DID WRONG
//
//     request.Headers.Add("X-Tenant-Id", tenantId);
//     request.Headers.Add("X-User-Id",   userId);
//
// WebApi read those headers and built an identity from them. No
// signature, no verification. So:
//
//     curl https://api.../api/leads -H "X-Tenant-Id: <any-guid>"
//
// returned that tenant's leads to anyone who could reach the API.
//
// This handler instead mints a short-lived HMAC-signed JWT from the
// CURRENT authenticated principal. WebApi validates the signature, so a
// caller cannot alter the tenant id without the signing key. Tokens are
// minted per request and live 5 minutes — no storage, no refresh flow,
// and a captured token expires before it is much use.
// =====================================================================

using System.Net.Http.Headers;
using MerkaiTrial.Application.Security;

namespace MerkaiTrial.Admin.Web.Services.Core;

public class ApiTokenForwardingHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _http;
    private readonly IApiTokenService _tokens;
    private readonly ILogger<ApiTokenForwardingHandler> _logger;

    public ApiTokenForwardingHandler(
        IHttpContextAccessor http,
        IApiTokenService tokens,
        ILogger<ApiTokenForwardingHandler> logger)
    {
        _http = http;
        _tokens = tokens;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var user = _http.HttpContext?.User;

        if (user?.Identity?.IsAuthenticated == true)
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _tokens.IssueForPrincipal(user));
        }
        else
        {
            // No authenticated principal — a startup or background call.
            // The OLD handler let WebApi fall back to its own appsettings
            // default tenant here, which is exactly the bug being removed.
            // Send nothing; the API answers 401, which is correct.
            _logger.LogDebug(
                "No authenticated principal on the current request — calling the API without a bearer token.");
        }

        return await base.SendAsync(request, cancellationToken);
    }
}

/* =====================================================================
   REQUIRES IApiTokenService / ApiTokenService

   If the build now complains about IApiTokenService, that file is not in
   yet either. It belongs at:

       MerkaiTrial.Application/Security/ApiTokenService.cs

   and needs the NuGet package System.IdentityModel.Tokens.Jwt in the
   Application project. It is the class with IssueForPrincipal() that
   copies UserId, TenantId, IsTenantAdmin, IsSuperAdmin, ViewingAs, Plan,
   Email and the permission claims into a signed JWT.

   =====================================================================
   AFTER THIS COMPILES

   Delete these, in this order:

     1. TenantContextForwardingHandler.cs
        Nothing references it once both AddHttpMessageHandler calls in
        Program.cs point at ApiTokenForwardingHandler.

     2. DemoAuthenticationHandler.cs
        Contains ViewAsCookies, TenantContextHeaders and
        DemoAuthenticationOptions. TenantContextHeaders was only used by
        the handler deleted in step 1, and ViewAsCookies only by the
        old ViewAs page, which now calls ISignInService instead.

   Do them in that order or the build breaks midway.
   ===================================================================== */
