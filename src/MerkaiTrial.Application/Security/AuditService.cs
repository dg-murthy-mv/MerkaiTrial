using System.Security.Claims;
using System.Text.Json;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Security;

public interface IAuditService
{
    Task WriteAsync(string action, string entityType, Guid? entityId = null,
                    Guid? tenantId = null, object? data = null, CancellationToken ct = default);

    Task WriteCriticalAsync(string action, string entityType, Guid? entityId = null,
                            Guid? tenantId = null, object? data = null, CancellationToken ct = default);
}

public class AuditService : IAuditService
{
    private readonly FlowDbContext _db;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<AuditService> _logger;

    public AuditService(FlowDbContext db, IHttpContextAccessor http, ILogger<AuditService> logger)
    {
        _db = db; _http = http; _logger = logger;
    }

    public async Task WriteAsync(string action, string entityType, Guid? entityId = null,
        Guid? tenantId = null, object? data = null, CancellationToken ct = default)
    {
        try
        {
            await PersistAsync(action, entityType, entityId, tenantId, data, ct);
        }
        catch (Exception ex)
        {
            // Never let an audit failure break the user's operation.
            _logger.LogError(ex, "Audit write failed for {Action} on {EntityType}", action, entityType);
        }
    }

    public Task WriteCriticalAsync(string action, string entityType, Guid? entityId = null,
        Guid? tenantId = null, object? data = null, CancellationToken ct = default)
        => PersistAsync(action, entityType, entityId, tenantId, data, ct);

    private async Task PersistAsync(string action, string entityType, Guid? entityId,
        Guid? tenantId, object? data, CancellationToken ct)
    {
        var user = _http.HttpContext?.User;

        // During ViewAs, RealUserId is the super admin and UserId is the
        // impersonated user. Recording both is the point.
        var actorRaw = user?.FindFirst(SignInService.ClaimRealUserId)?.Value
                    ?? user?.FindFirst(SignInService.ClaimUserId)?.Value;

        Guid? actorId = Guid.TryParse(actorRaw, out var a) ? a : null;

        var by = user?.FindFirst(ClaimTypes.Email)?.Value
              ?? user?.Identity?.Name
              ?? "system";

        if (tenantId is null)
        {
            var raw = user?.FindFirst(SignInService.ClaimTenantId)?.Value;
            if (Guid.TryParse(raw, out var t)) tenantId = t;
        }

        _db.Set<AuditLog>().Add(new AuditLog
        {
            TenantId = tenantId,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            By = by.Length > 64 ? by[..64] : by,
            ActorUserId = actorId,
            IpAddress = _http.HttpContext?.Connection?.RemoteIpAddress?.ToString(),
            Data = data is null ? null : JsonSerializer.Serialize(data),
            CreatedAtUtc = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(ct);
    }
}