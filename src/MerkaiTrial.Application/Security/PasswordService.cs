using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Application.Security
{
    public interface IPasswordService
    {
        string Hash(string plainPassword);
        PasswordVerificationResult Verify(string hash, string plainPassword);
        (bool Ok, string? Error) ValidateStrength(string plainPassword);
    }

    public class PasswordService : IPasswordService
    {
        // PasswordHasher<T> = PBKDF2-HMAC-SHA256, 100k iterations, versioned
        // format. The generic argument is unused by the algorithm.
        private readonly PasswordHasher<User> _hasher = new();

        public string Hash(string plainPassword) => _hasher.HashPassword(null!, plainPassword);

        public PasswordVerificationResult Verify(string hash, string plainPassword)
            => _hasher.VerifyHashedPassword(null!, hash, plainPassword);

        /// <summary>
        /// Length is the dominant factor; complexity rules mostly push people
        /// toward Password1! and a sticky note. 12 chars minimum, no composition
        /// requirements, plus a small blocklist of the obvious.
        /// </summary>
        public (bool Ok, string? Error) ValidateStrength(string p)
        {
            if (string.IsNullOrWhiteSpace(p) || p.Length < 12)
                return (false, "Password must be at least 12 characters.");
            if (p.Length > 128)
                return (false, "Password must be 128 characters or fewer.");

            var lowered = p.ToLowerInvariant();
            string[] banned = { "password", "merkai", "madeevision", "12345678", "qwerty", "welcome" };
            if (banned.Any(b => lowered.Contains(b)))
                return (false, "Password contains a common or easily guessed word.");

            return (true, null);
        }
    }

}
