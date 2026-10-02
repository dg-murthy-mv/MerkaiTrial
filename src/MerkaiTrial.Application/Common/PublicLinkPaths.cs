// =====================================================================
// PublicLinkPaths.cs
// Location: MerkaiTrial.Application/Common/PublicLinkPaths.cs
//
// NEW FILE (063). ONE rule for "which part of this URL is a secret".
//
// WHY THIS IS NOT TWO COPIES IN TWO Startup FOLDERS
//   Both hosts need the identical rule, and the day a third public link
//   is added — an invoice link, a payment link, a signature link — it
//   must be masked in both or the leak comes back in half the logs. A
//   rule duplicated across files is the bug class that produced the
//   /q/{token} leak in four places to begin with.
//
//   Pure string handling, no Serilog and no ASP.NET dependency, so it
//   sits in Application where both hosts already reference it. The
//   Serilog wiring is thin and lives in each host.
//
// WHAT IS BEING PROTECTED
//   The public quote token is the ENTIRE security of the customer's
//   link. Anyone who can read it can open the quote and accept it on the
//   customer's behalf. 061 masked the four places our own code logged
//   it; 062 silenced the framework loggers that printed whole URLs. This
//   is the last one — Serilog's own request-completion line, which has
//   to keep logging the path because that is what the line is for.
//
// WHY MASK RATHER THAN DROP THE LINE
//   Dropping requests to /q/ would also drop the only record that a
//   customer opened their quote at all. "HTTP GET /q/****bSxl responded
//   200 in 200ms" still tells you the endpoint was hit, by how many
//   people, how fast, and whether it failed — which is the whole job of
//   a request log.
// =====================================================================

using System.Text.RegularExpressions;

namespace MerkaiTrial.Application.Common;

public static class PublicLinkPaths
{
    /// <summary>
    /// The token segment of every path that carries one.
    ///
    ///     /q/{token}                              the customer's page
    ///     /api/quotes/public/{token}              the API read
    ///     /api/quotes/public/{token}/status       accept / decline
    ///
    /// A lookbehind so only the token itself is replaced and the rest of
    /// the path survives — "/api/quotes/public/****bSxl/status" still
    /// says which endpoint was called, which is the point of keeping the
    /// line at all.
    ///
    /// ADD NEW PUBLIC LINKS HERE, not in a second copy somewhere else.
    /// </summary>
    private static readonly Regex TokenSegment = new(
        @"(?<=^/q/)[^/?#]+" +
        @"|(?<=^/api/quotes/public/)[^/?#]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// The path with any public token reduced to its last four characters.
    /// Returns the path unchanged when it carries no token, which is
    /// almost every request — so this is a cheap no-match on the hot path.
    /// </summary>
    public static string Mask(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path ?? string.Empty;

        // Cheap guard before the regex. Most requests are neither of
        // these, and a request log runs on every single one.
        if (path.Length < 3) return path;
        if (path[1] != 'q' && path[1] != 'Q' && !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            return path;

        try
        {
            return TokenSegment.Replace(path, m => MaskToken(m.Value));
        }
        catch (RegexMatchTimeoutException)
        {
            // Never let a logging concern throw into a request. A path
            // we could not parse is a path we must not print.
            return "/(path masked)";
        }
    }

    /// <summary>
    /// "****bSxl". Enough to match a row against a support question;
    /// useless for opening the quote. A short token degrades to "****"
    /// rather than leaking a prefix of a short one — the same rule as the
    /// Mask helpers in QuotesController and QuoteService, and the phone
    /// numbers in WhatsApp sending.
    /// </summary>
    public static string MaskToken(string? token)
        => string.IsNullOrWhiteSpace(token) || token.Length < 8
            ? "****"
            : "****" + token[^4..];
}
