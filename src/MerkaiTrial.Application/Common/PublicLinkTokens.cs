// =====================================================================
// PublicLinkTokens.cs
// Location: MerkaiTrial.Application/Common/PublicLinkTokens.cs
//
// NEW FILE (087e). Makes the secret part of a customer link — the token
// in /q/{token} (quotes) and /i/{token} (invoices).
//
// THE BUG THIS REPLACES (found by the end-to-end tests)
//   Both links made their token the same way:
//
//       Convert.ToBase64String(32 random bytes)        44 characters
//           .Replace("+", "").Replace("/", "").Replace("=", "")
//           [..40]                                     first 40
//
//   Base-64 of 32 bytes is 43 characters plus one "=". Every "+" or "/"
//   removed makes it shorter, and each character has a 1-in-32 chance of
//   being one of the two. With four or more of them the string is under
//   40 characters, and [..40] throws:
//
//       "Index and length must refer to a location within the string."
//
//   That happens on roughly 1 send in 20. The person pressing "Send to
//   customer" got an error, at random, and pressing it again usually
//   worked — the worst kind of bug to report, and the test run caught it
//   on its first quote.
//
// NOW
//   40 characters drawn straight from A–Z, a–z, 0–9 by the operating
//   system's secure random generator. Always exactly 40, the same
//   alphabet and length as every token already issued, so existing links
//   and anything that looks at them are unaffected. 62^40 ≈ 2^238
//   possibilities — far beyond guessing.
// =====================================================================

using System.Security.Cryptography;

namespace MerkaiTrial.Application.Common;

public static class PublicLinkTokens
{
    /// <summary>Length of every public link token, quote or invoice.</summary>
    public const int Length = 40;

    private const string Alphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>A new token: exactly <see cref="Length"/> letters and digits.</summary>
    public static string New() => RandomNumberGenerator.GetString(Alphabet, Length);

    /// <summary>For log lines: the last four characters only.</summary>
    public static string Mask(string? token) =>
        string.IsNullOrEmpty(token) || token.Length < 8 ? "****" : "****" + token[^4..];
}
