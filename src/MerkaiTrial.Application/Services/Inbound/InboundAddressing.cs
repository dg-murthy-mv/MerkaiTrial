// =====================================================================
// InboundAddressing.cs
// Location: MerkaiTrial.Application/Services/Inbound/InboundAddressing.cs
//
// NEW FILE (083). Everything about ADDRESSES and TEXT that the webhook,
// the worker and the outgoing quote email have to agree on — one place,
// so "what does a reply address look like" has one answer.
//
// THE TWO KINDS OF ADDRESS ON THE RECEIVING DOMAIN
//
//   reply+<token>@in.example.com
//       On the Reply-To of an email WE send (a quote today). The token is
//       a row in EmailReplyTokens pointing at the quote. A reply to it is
//       filed on that quote's deal with no guessing at all.
//
//   <alias>@in.example.com
//       The workspace's own address (Tenant.InboundAlias). Reps BCC it on
//       emails they send from Gmail or Outlook, or forward a customer's
//       email to it. Filed by looking the customer's address up.
//       "<alias>+anything@…" works too — the tag is ignored.
//
// Nothing here touches the database except AddReplyToken, which ADDS a
// row and never saves, like every other outbox-style write in the app.
// =====================================================================

using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;

namespace MerkaiTrial.Application.Services.Inbound;

public static class InboundAddressing
{
    public const string ReplyPrefix = "reply+";

    private const string TokenAlphabet = "abcdefghijkmnpqrstuvwxyz23456789";   // no l, o, 0, 1

    // ── Tokens and aliases ───────────────────────────────────────────

    /// <summary>
    /// 20 random characters from a 32-letter alphabet: 100 bits. Nobody
    /// guesses one, so a token needs no signature and no secret.
    /// </summary>
    public static string NewToken() => RandomString(20);

    /// <summary>
    /// "sathorn-interiors-k7m2qx". The name part makes it recognisable in
    /// somebody's address book; the six random letters make it unguessable
    /// and unique.
    /// </summary>
    public static string NewAlias(string? workspaceName)
    {
        var slug = new StringBuilder();
        var lastDash = true;

        foreach (var ch in (workspaceName ?? string.Empty).ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                slug.Append(ch);
                lastDash = false;
            }
            else if (!lastDash)
            {
                slug.Append('-');
                lastDash = true;
            }

            if (slug.Length >= 20) break;
        }

        var name = slug.ToString().Trim('-');
        if (name.Length == 0) name = "workspace";

        return $"{name}-{RandomString(6)}";
    }

    public static string ReplyAddress(string token, string domain) => $"{ReplyPrefix}{token}@{domain}";

    public static string WorkspaceAddress(string alias, string domain) => $"{alias}@{domain}";

    /// <summary>
    /// ADDS a token row pointing at the record an outgoing email is about,
    /// and returns the token. Does NOT save — the caller's SaveChanges
    /// commits it together with the outbox row.
    /// </summary>
    public static string AddReplyToken(
        FlowDbContext db, Guid tenantId, string entityType, Guid entityId, Guid? outboundMessageId)
    {
        var token = NewToken();

        db.EmailReplyTokens.Add(new EmailReplyToken
        {
            Token             = token,
            TenantId          = tenantId,
            EntityType        = entityType,
            EntityId          = entityId,
            OutboundMessageId = outboundMessageId,
            CreatedAtUtc      = DateTime.UtcNow
        });

        return token;
    }

    /// <summary>
    /// The Reply-To value for an outgoing customer email: the tracked
    /// address first, then the workspace's own mailbox, comma-separated.
    /// Most mail programs reply to every Reply-To address, so the reply is
    /// filed on the record AND still reaches the team's inbox.
    ///
    /// If both together would not fit the 320-character column, the
    /// tracked address wins — the mailbox is still the deal owner's to
    /// check, and a cut-off address would bounce.
    /// </summary>
    public static string? CombineReplyTo(string? trackedAddress, string? mailbox, int maxLength = 320)
    {
        var parts = new[] { trackedAddress, mailbox }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (parts.Count == 0) return null;

        var joined = string.Join(", ", parts);
        if (joined.Length <= maxLength) return joined;

        return parts[0].Length <= maxLength ? parts[0] : null;
    }

    // ── Reading addresses ────────────────────────────────────────────

    /// <summary>The part before the @ when the address is on our receiving domain; otherwise null.</summary>
    public static string? LocalPartOnDomain(string? address, string? domain)
    {
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(domain)) return null;

        var bare = BareAddress(address);
        if (bare is null) return null;

        var at = bare.LastIndexOf('@');
        if (at <= 0) return null;

        return string.Equals(bare[(at + 1)..], domain, StringComparison.OrdinalIgnoreCase)
            ? bare[..at].ToLowerInvariant()
            : null;
    }

    /// <summary>"reply+abc123…" → "abc123…", or null when it is not a well-formed reply address.</summary>
    public static string? TokenFromLocalPart(string? local)
    {
        if (local is null || !local.StartsWith(ReplyPrefix, StringComparison.Ordinal)) return null;

        var token = local[ReplyPrefix.Length..];
        return token.Length is >= 10 and <= 40 && token.All(c => TokenAlphabet.Contains(c))
            ? token
            : null;
    }

    /// <summary>"sathorn-k7m2qx+invoices" → "sathorn-k7m2qx".</summary>
    public static string? AliasFromLocalPart(string? local)
    {
        if (string.IsNullOrWhiteSpace(local) || local.StartsWith(ReplyPrefix, StringComparison.Ordinal))
            return null;

        var plus = local.IndexOf('+');
        var alias = (plus > 0 ? local[..plus] : local).Trim().ToLowerInvariant();

        return alias.Length is > 0 and <= 64 ? alias : null;
    }

    /// <summary>
    /// "Priya Shah &lt;priya@acme.com&gt;" → ("priya@acme.com", "Priya Shah").
    /// Lower-cases the address. Null when it is not an address at all.
    /// </summary>
    public static (string Address, string? DisplayName)? ParseMailbox(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            var m = new MailAddress(raw.Trim());
            var name = string.IsNullOrWhiteSpace(m.DisplayName) ? null : m.DisplayName.Trim();
            return (m.Address.Trim().ToLowerInvariant(), name);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string? BareAddress(string? raw) => ParseMailbox(raw)?.Address;

    /// <summary>Distinct bare addresses from any number of lists of raw addresses.</summary>
    public static List<string> Addresses(params IEnumerable<string?>?[] lists)
        => lists.Where(l => l is not null)
                .SelectMany(l => l!)
                .Select(BareAddress)
                .Where(a => a is not null)
                .Select(a => a!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    /// <summary>mailer-daemon, postmaster, no-reply… — machines, not customers.</summary>
    public static bool IsAutomatedSender(string? address)
    {
        var local = (BareAddress(address) ?? string.Empty).Split('@')[0];
        return local is "mailer-daemon" or "postmaster" or "noreply" or "no-reply"
                     or "donotreply" or "do-not-reply" or "bounce" or "bounces";
    }

    /// <summary>"wo****@example.com" — enough to find the row in a log, not enough to be a contact list.</summary>
    public static string Mask(string? address)
    {
        var bare = BareAddress(address) ?? address;
        if (string.IsNullOrWhiteSpace(bare)) return "(none)";

        var at = bare.IndexOf('@');
        if (at <= 0) return "****";

        var keep = Math.Min(2, at);
        return bare[..keep] + "****" + bare[at..];
    }

    // ── Reading text ─────────────────────────────────────────────────

    private static readonly Regex ForwardPrefix =
        new(@"^\s*(fwd?|fw|tr|wg|rv|enc)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool LooksForwarded(string? subject) =>
        !string.IsNullOrWhiteSpace(subject) && ForwardPrefix.IsMatch(subject);

    // "From: Priya Shah <priya@acme.com>" / "From: priya@acme.com" /
    // Outlook's "From: Priya Shah [mailto:priya@acme.com]"
    private static readonly Regex ForwardedFromLine = new(
        @"^\s*\*?(from|von|de|da|จาก|من)\s*:\*?\s*(?<rest>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex AnyAddress = new(
        @"[A-Z0-9._%+'-]+@[A-Z0-9.-]+\.[A-Z]{2,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The original sender quoted inside a forwarded email, skipping the
    /// addresses in <paramref name="exclude"/> (our users, our domain).
    /// Mail programs all write some variant of "From: Name &lt;address&gt;"
    /// at the top of the forwarded part; the first one that is not one of
    /// us is the customer. Null when there is nothing recognisable — the
    /// email then waits in the Inbox, which is the honest outcome.
    /// </summary>
    public static (string Address, string? DisplayName)? ForwardedSender(string? text, ISet<string> exclude)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (Match line in ForwardedFromLine.Matches(text))
        {
            var rest = line.Groups["rest"].Value.Trim();
            var found = AnyAddress.Match(rest);
            if (!found.Success) continue;

            var address = found.Value.Trim('.', '\'').ToLowerInvariant();
            if (exclude.Contains(address)) continue;

            var nameEnd = rest.IndexOfAny(new[] { '<', '[', '(' });
            var name = nameEnd > 0 ? rest[..nameEnd].Trim().Trim('"', '\'', '*').Trim() : null;
            if (string.Equals(name, address, StringComparison.OrdinalIgnoreCase)) name = null;

            return (address, string.IsNullOrWhiteSpace(name) ? null : name);
        }

        return null;
    }

    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Blocks = new(
        @"<(script|style|head)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Breaks = new(
        @"<\s*(br|/p|/div|/tr|/li|/h[1-6])\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BlankRuns = new(@"\n{3,}", RegexOptions.Compiled);

    /// <summary>
    /// A readable plain-text version of an HTML email, for when the sender
    /// sent no text part. Used for the snippet, for search and for
    /// reading the forwarded sender — never shown as HTML.
    /// </summary>
    public static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var s = Blocks.Replace(html, " ");
        s = Breaks.Replace(s, "\n");
        s = Tags.Replace(s, " ");
        s = WebUtility.HtmlDecode(s);
        s = string.Join("\n", s.Split('\n').Select(l => Regex.Replace(l, @"[ \t ]+", " ").Trim()));
        return BlankRuns.Replace(s, "\n\n").Trim();
    }

    private static readonly Regex QuoteHeader = new(
        @"^\s*(on\s.+wrote:|-{2,}\s*original message\s*-{2,}|-{2,}\s*forwarded message\s*-{2,}|from:\s.+|sent from my\s.+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// The first ~200 characters of what the person actually WROTE — the
    /// quoted history ("On Tue, Priya wrote:", lines starting with &gt;)
    /// is cut off, so a reply's snippet is the reply, not our own quote.
    /// </summary>
    public static string? Snippet(string? text, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var body = text.Replace("\r\n", "\n");
        var cut = QuoteHeader.Match(body);
        if (cut.Success && cut.Index > 0) body = body[..cut.Index];

        var lines = body.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('>'));

        var flat = Regex.Replace(string.Join(" ", lines), @"\s+", " ").Trim();
        if (flat.Length == 0) return null;

        return flat.Length <= max ? flat : flat[..max].TrimEnd() + "…";
    }

    public static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static string RandomString(int length)
    {
        Span<char> chars = stackalloc char[length];
        for (var i = 0; i < length; i++)
            chars[i] = TokenAlphabet[RandomNumberGenerator.GetInt32(TokenAlphabet.Length)];
        return new string(chars);
    }
}
