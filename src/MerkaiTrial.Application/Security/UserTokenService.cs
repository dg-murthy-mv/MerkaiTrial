using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Application.Security
{

    public interface IUserTokenService
    {
        /// <summary>Creates a token and returns the RAW value — the only time it
        /// exists. Put it in the link; it is not recoverable afterwards.</summary>
        Task<string> IssueAsync(Guid userId, Guid tenantId, string purpose,
                                TimeSpan lifetime, string? createdBy, CancellationToken ct = default);

        Task<UserToken?> FindUsableAsync(string rawToken, string purpose, CancellationToken ct = default);

        Task ConsumeAsync(UserToken token, CancellationToken ct = default);

        /// <summary>Invalidate outstanding tokens of a purpose — call after a
        /// successful password set so an old emailed link stops working.</summary>
        Task RevokeAllAsync(Guid userId, string purpose, CancellationToken ct = default);
    }

    public class UserTokenService : IUserTokenService
    {
        private readonly FlowDbContext _db;

        public UserTokenService(FlowDbContext db) => _db = db;

        private static byte[] HashToken(string raw)
            => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));

        public async Task<string> IssueAsync(Guid userId, Guid tenantId, string purpose,
            TimeSpan lifetime, string? createdBy, CancellationToken ct = default)
        {
            // 256 bits of CSPRNG entropy, URL-safe.
            var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                             .Replace('+', '-').Replace('/', '_').TrimEnd('=');

            _db.Set<UserToken>().Add(new UserToken
            {
                UserId = userId,
                TenantId = tenantId,
                TokenHash = HashToken(raw),
                Purpose = purpose,
                ExpiresAtUtc = DateTime.UtcNow.Add(lifetime),
                CreatedBy = createdBy
            });

            await _db.SaveChangesAsync(ct);
            return raw;
        }

        public async Task<UserToken?> FindUsableAsync(string rawToken, string purpose, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(rawToken)) return null;

            var hash = HashToken(rawToken);

            var token = await _db.Set<UserToken>()
                .Include(t => t.User)
                .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Purpose == purpose, ct);

            return token is not null && token.IsUsable() ? token : null;
        }

        public async Task ConsumeAsync(UserToken token, CancellationToken ct = default)
        {
            token.ConsumedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        public async Task RevokeAllAsync(Guid userId, string purpose, CancellationToken ct = default)
        {
            await _db.Set<UserToken>()
                .Where(t => t.UserId == userId && t.Purpose == purpose && t.ConsumedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAtUtc, DateTime.UtcNow), ct);
        }
    }
}
