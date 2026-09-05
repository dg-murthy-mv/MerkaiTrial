using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace MerkaiTrial.Infrastructure.Tenancy;

public interface ITenantProvider
{
    /// <summary>Current tenant, or Guid.Empty when there is no request
    /// context (startup, background work, pre-authentication).</summary>
    Guid TenantId { get; }

    bool HasTenant { get; }
}

public class HttpTenantProvider : ITenantProvider
{
    private readonly IHttpContextAccessor _http;

    public HttpTenantProvider(IHttpContextAccessor http) => _http = http;

    public Guid TenantId
    {
        get
        {
            // Same claim SignInService writes and CurrentUserService reads.
            var raw = _http.HttpContext?.User?.FindFirst("TenantId")?.Value;
            return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
        }
    }

    public bool HasTenant => TenantId != Guid.Empty;
}

/// <summary>
/// For startup and background contexts (DatabaseWarmup, migrations,
/// future hosted services). Resolves to no tenant, so filtered queries
/// return nothing rather than throwing.
/// </summary>
public class NoTenantProvider : ITenantProvider
{
    public Guid TenantId => Guid.Empty;
    public bool HasTenant => false;
}