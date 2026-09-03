using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Domain.Entities
{
    public static class TokenPurpose
    {
        public const string Invite = "Invite";
        public const string PasswordReset = "PasswordReset";
    }

    /// <summary>
    /// One-time link for invites and password resets.
    ///
    /// TokenHash holds SHA-256 of the raw token. The raw value exists only
    /// in the link handed to the user and is never persisted, so a database
    /// leak yields nothing usable. Lookup hashes the presented token and
    /// matches on the hash.
    /// </summary>
    public class UserToken
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid UserId { get; set; }
        public Guid TenantId { get; set; }
        public byte[] TokenHash { get; set; } = Array.Empty<byte>();
        public string Purpose { get; set; } = TokenPurpose.Invite;
        public DateTime ExpiresAtUtc { get; set; }
        public DateTime? ConsumedAtUtc { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }

        public User? User { get; set; }

        public bool IsUsable() => ConsumedAtUtc == null && ExpiresAtUtc > DateTime.UtcNow;
    }
}

