// =====================================================================
// OverridableTenantProvider.cs
// Location: MerkaiTrial.WebApi/Workers/OverridableTenantProvider.cs
//
// NEW FILE (099). The WebApi's ITenantProvider from now on.
//
// 099 update 1: was called WorkerTenantProvider, which clashed with the
// class of that name InboundEmailWorker.cs already has (same namespace;
// that one takes a Guid and is built by hand, never through DI). Renamed
// so the two never meet. InboundEmailWorker is not touched.
//
// In a request it behaves exactly as HttpTenantProvider did — it IS one,
// underneath: the tenant comes from the signed-in user's "TenantId" claim.
//
// In a background scope there is no request and no claim. A worker that
// needs the normal tenant filter to see one workspace's rows (the
// automation worker does — see AutomationWorker) calls UseTenant FIRST in
// its own scope. Only that scope is affected; requests never call it, so
// nothing a person does can switch workspace through it.
//
// Registered once, scoped, and handed out under both names, so the
// FlowDbContext of a scope and the worker that set the tenant are talking
// about the same object (ApiServiceRegistration.AddApiPersistence).
// =====================================================================

using MerkaiTrial.Infrastructure.Tenancy;

namespace MerkaiTrial.WebApi.Workers;

public sealed class OverridableTenantProvider : ITenantProvider
{
    private readonly HttpTenantProvider _http;
    private Guid? _override;

    public OverridableTenantProvider(IHttpContextAccessor http) => _http = new HttpTenantProvider(http);

    /// <summary>For a background scope only — see the header.</summary>
    public void UseTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("A background scope needs a real workspace.", nameof(tenantId));
        _override = tenantId;
    }

    public Guid TenantId => _override ?? _http.TenantId;

    public bool HasTenant => TenantId != Guid.Empty;
}
