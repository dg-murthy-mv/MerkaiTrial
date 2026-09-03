using Microsoft.AspNetCore.Http;
namespace MerkaiTrial.Infrastructure.Tenancy;
// Minimal tenant context with Starter limits
public interface ITenantContext {
    Guid TenantId { get; }
    int PipelinesMax { get; }
    int JourneysMax { get; }
}

public class TenantContext : ITenantContext {
    private readonly IHttpContextAccessor _http;
    public TenantContext(IHttpContextAccessor http) => _http = http;
    public Guid TenantId {
        get {
            var h = _http.HttpContext?.Request?.Headers["X-Tenant-Id"].ToString();
            if (Guid.TryParse(h, out var id)) return id;
            return Guid.Parse("00000000-0000-0000-0000-000000000001"); // default Starter demo
        }
    }
    public int PipelinesMax => 1; // Starter
    public int JourneysMax => 1;  // Starter
}
