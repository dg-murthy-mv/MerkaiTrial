using System;
using System.Security.Cryptography;
using System.Text;
using MerkaiTrial.Infrastructure.Persistence;

namespace MerkaiTrial.WebApi.Services;
public class PublicLinkService
{
    private readonly FlowDbContext _db;
    public PublicLinkService(FlowDbContext db) => _db = db;

    public async Task<string> CreateTokenAsync(Guid tenantId, Guid quoteId, DateTime expiresUtc, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FindAsync(new object?[] { tenantId }, ct) ?? throw new("Tenant not found");
        var payload = $"{quoteId:N}|{expiresUtc:O}";
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(tenant.PublicLinkSecret));
        var sig = Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(payload)));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{payload}|{sig}"));
    }

    public async Task<(bool ok, Guid quoteId)> ValidateAsync(Guid tenantId, string token, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FindAsync(new object?[] { tenantId }, ct) ?? throw new("Tenant not found");
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token));
        var parts = decoded.Split('|');
        if (parts.Length != 3) return (false, Guid.Empty);

        var quoteId = Guid.Parse(parts[0]);
        var exp = DateTime.Parse(parts[1], null, System.Globalization.DateTimeStyles.RoundtripKind);
        if (DateTime.UtcNow > exp) return (false, Guid.Empty);

        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(tenant.PublicLinkSecret));
        var payload = $"{parts[0]}|{parts[1]}";
        var expected = Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(payload)));
        return (expected == parts[2], quoteId);
    }
}
