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

        /// <summary>
        /// 106. The same, and also refuses a password built from the
        /// person's own details (<paramref name="who"/>), when known.
        /// </summary>
        (bool Ok, string? Error) ValidateStrength(string plainPassword, PasswordOwner? who);
    }

    /// <summary>106. What a new password must not contain: the person's own details.</summary>
    public sealed record PasswordOwner(string? Email, string? FirstName, string? LastName, string? WorkspaceName);

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
        ///
        /// 106 (VUL-26) adds, still with no composition rules:
        ///   • the 1,000 most common passwords, and any of them padded with
        ///     digits or symbols ("Sunshine2026!!") — CommonPasswords.cs;
        ///   • simple patterns: one character over and over, three or fewer
        ///     different characters, digits only, a keyboard or alphabet run
        ///     ("qwertyuiop[]", "abcdefghijkl"), a short piece repeated
        ///     ("abcabcabcabc");
        ///   • the person's own details, when the caller knows them: the name
        ///     part of their email, their first or last name, the workspace's
        ///     name.
        /// A passphrase of a few ordinary words ("Blue-Harbour-Tide-2026!")
        /// passes every one of these.
        /// </summary>
        public (bool Ok, string? Error) ValidateStrength(string p) => ValidateStrength(p, null);

        public (bool Ok, string? Error) ValidateStrength(string p, PasswordOwner? who)
        {
            if (string.IsNullOrWhiteSpace(p) || p.Length < 12)
                return (false, "Password must be at least 12 characters.");
            if (p.Length > 128)
                return (false, "Password must be 128 characters or fewer.");

            var lowered = p.ToLowerInvariant();
            string[] banned = { "password", "merkai", "madeevision", "12345678", "qwerty", "welcome" };
            if (banned.Any(b => lowered.Contains(b)))
                return (false, "Password contains a common or easily guessed word.");

            // 106 — common passwords, as they are or padded at the ends.
            var core = TrimEnds(lowered);
            if (CommonPasswords.Words.Contains(lowered) || (core.Length >= 4 && CommonPasswords.Words.Contains(core)))
                return (false, "That password is one of the most commonly used. Try a few unrelated words together.");

            // 106 — simple patterns.
            if (lowered.Distinct().Count() <= 3)
                return (false, "That password repeats the same few characters. Try a few unrelated words together.");
            if (lowered.All(char.IsDigit))
                return (false, "A password can't be digits only. Try a few unrelated words together.");
            var plain = new string(lowered.Where(char.IsLetterOrDigit).ToArray());
            if (plain.Length >= 8 && Runs.Any(run => run.Contains(plain, StringComparison.Ordinal)))
                return (false, "That password is a keyboard or alphabet run. Try a few unrelated words together.");
            if (IsRepeatedPiece(lowered))
                return (false, "That password repeats a short piece over and over. Try a few unrelated words together.");

            // 106 — the person's own details.
            if (who is not null)
            {
                foreach (var part in OwnParts(who))
                    if (lowered.Contains(part, StringComparison.Ordinal))
                        return (false, "A password can't contain your name, your email address or your workspace's name.");
            }

            return (true, null);
        }

        // Keyboard rows and alphabet / number runs, forwards and backwards.
        private static readonly string[] Runs = BuildRuns();

        private static string[] BuildRuns()
        {
            var forward = new[]
            {
                "abcdefghijklmnopqrstuvwxyz",
                "01234567890123456789",
                "qwertyuiopasdfghjklzxcvbnm",
                "qwertyuiop", "asdfghjkl", "zxcvbnm",
                "1qaz2wsx3edc4rfv5tgb6yhn7ujm8ik9ol0p",
                "qazwsxedcrfvtgbyhnujmikolp",
            };
            return forward.Concat(forward.Select(f => new string(f.Reverse().ToArray()))).ToArray();
        }

        /// <summary>"Sunshine2026!!" → "sunshine": the digits and symbols at both ends taken off.</summary>
        private static string TrimEnds(string s)
        {
            int start = 0, end = s.Length;
            while (start < end && !char.IsLetter(s[start])) start++;
            while (end > start && !char.IsLetter(s[end - 1])) end--;
            return s[start..end];
        }

        /// <summary>"abcabcabcabc", "ab12ab12ab12": the whole password is one piece of up to 4 characters, repeated.</summary>
        private static bool IsRepeatedPiece(string s)
        {
            for (var size = 1; size <= 4 && size * 3 <= s.Length; size++)
            {
                var piece = s[..size];
                var repeated = true;
                for (var i = 0; i < s.Length && repeated; i++)
                    repeated = s[i] == piece[i % size];
                if (repeated) return true;
            }
            return false;
        }

        /// <summary>The bits of a person's details a password must not contain: 4 letters or longer, lower-cased.</summary>
        private static IEnumerable<string> OwnParts(PasswordOwner who)
        {
            var parts = new List<string?>();

            var local = who.Email?.Split('@')[0].Split('+')[0];
            parts.Add(local);
            parts.Add(who.FirstName);
            parts.Add(who.LastName);

            if (!string.IsNullOrWhiteSpace(who.WorkspaceName))
            {
                parts.Add(new string(who.WorkspaceName.Where(char.IsLetterOrDigit).ToArray()));    // "Acme Traders" → "acmetraders"
                parts.AddRange(who.WorkspaceName.Split(new[] { ' ', '-', '_', '.', ',', '&' }, StringSplitOptions.RemoveEmptyEntries));
            }

            return parts
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim().ToLowerInvariant())
                .Where(x => x.Length >= 4 && x.Any(char.IsLetter))
                .Distinct();
        }
    }

}
