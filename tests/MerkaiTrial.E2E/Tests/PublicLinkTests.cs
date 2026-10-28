// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/PublicLinkTests.cs
//
// VUL-21 (round 106) — the customer links can't be guessed or walked.
//
//   Customer_links_cannot_be_guessed_walked_or_used_to_learn_more
//
// /q/{token} (a quote) and /i/{token} (an invoice) are the only pages
// anyone can open without signing in.
//
//   • Control: a sent quote's link and an issued invoice's link open, and
//     their tokens are 40 random letters and digits.
//   • What the link hands out (the API answer behind the page) carries the
//     customer's document and nothing of the workspace's insides: no
//     staff email address, no password or security fields, no other
//     record's details.
//   • Wrong links — random, short, over-long, the right token in the wrong
//     case or with a character changed, the other kind's token, ../, SQL —
//     are all "not found", never a server error and never another record.
//   • 20 wrong links from one network address in 10 minutes (agreed in
//     106), and that address gets "too many tries" (429) on every customer
//     link — a genuine one included, and the Accept button too — while
//     the genuine link still opens from any other address.
// =====================================================================

using System.Text.RegularExpressions;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class PublicLinkTests : E2ETest
{
    private const int Allowed = 20;

    [Test]
    public async Task Customer_links_cannot_be_guessed_walked_or_used_to_learn_more()
    {
        var a = World.A;
        var tenantA = await Db.TenantIdAsync(a.Name);
        var apiA = await Api.AsAsync(a.Admin);
        var m = Random.Shared.Next(10000, 99999).ToString();
        var problems = new List<string>();

        var (quoteId, quoteToken) = await Step<(Guid, string)>("A quote of workspace A's, sent to the customer", async () =>
        {
            var contact = await ApiRecords.ContactAsync(apiA, tenantA, "Link Contact " + m, "linkc" + m);
            var deal = await ApiRecords.DealAsync(apiA, tenantA, contact, "Link Deal " + m);
            var quote = await ApiRecords.QuoteAsync(apiA, deal, "Link line " + m);
            await ApiRecords.SendQuoteAsync(apiA, quote);
            var token = await Db.ValueAsync("Quotes", "PublicLinkToken", quote);
            Assert.That(token, Is.Not.Null.And.Not.Empty, "The sent quote has no customer link.");
            return (quote, token!);
        });

        var invoiceTokenMadeByTest = false;
        var invoiceToken = await Step("An invoice of workspace A's, issued", async () =>
        {
            var contact = await ApiRecords.ContactAsync(apiA, tenantA, "Link Inv Contact " + m, "linkic" + m);
            var deal = await ApiRecords.DealAsync(apiA, tenantA, contact, "Link Inv Deal " + m);
            var invoice = await ApiRecords.InvoiceAsync(apiA, deal, "Link inv line " + m);
            var issued = await apiA.PostAsync($"/api/invoices/{invoice}/issue");
            Assert.That(issued.IsSuccess, Is.True, $"Issuing the invoice failed: {issued}");
            var token = await Db.ValueAsync("Invoices", "PublicLinkToken", invoice);
            if (string.IsNullOrEmpty(token))
            {
                // 106-update3: an invoice gets its link when it is EMAILED,
                // and test servers have no email key (by design), so the
                // email is refused. Give it a link the way the app makes one
                // (40 letters and digits, the same generator as quotes).
                // The quote's token, made by the app itself, is the one
                // whose shape is checked below.
                token = System.Security.Cryptography.RandomNumberGenerator.GetString(
                    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 40);
                await Db.SetTextAsync("Invoices", "PublicLinkToken", invoice, token);
                invoiceTokenMadeByTest = true;
            }
            return token!;
        });

        await Step("Control: both links open, and their tokens are 40 random letters and digits", async () =>
        {
            using var web = new WebSession();
            var q = await web.GetAsync("/q/" + quoteToken);
            Assert.That(q.Status, Is.EqualTo(200), $"The genuine quote link didn't open, so the refusals below would prove nothing: {q}");
            var i = await web.GetAsync("/i/" + invoiceToken);
            Assert.That(i.Status, Is.EqualTo(200), $"The genuine invoice link didn't open: {i}");

            foreach (var (what, token) in new[] { ("quote", quoteToken), ("invoice", invoiceToken) })
                if ((what == "quote" || !invoiceTokenMadeByTest) && !Regex.IsMatch(token, "^[A-Za-z0-9]{40}$"))
                    problems.Add($"The {what} link's token is not 40 letters and digits: {token.Length} characters.");
        });

        await Step("What the link hands out is the customer's document, and nothing of the workspace's insides", async () =>
        {
            var q = await Api.Anonymous.GetAsync("/api/quotes/public/" + quoteToken);
            var i = await Api.Anonymous.GetAsync("/api/invoices/public/" + invoiceToken);
            foreach (var (what, r) in new[] { ("quote", q), ("invoice", i) })
            {
                if (!r.IsSuccess) { problems.Add($"The {what}'s public answer failed: {r}"); continue; }
                foreach (var secret in new[] { a.Admin.Email, "passwordHash", "securityStamp", "ownerUserId", "internalNote" })
                    if (r.Body.Contains(secret, StringComparison.OrdinalIgnoreCase))
                        problems.Add($"The {what}'s public answer carries '{secret}'.");
            }
        });

        var wrong = new List<(string What, string Path)>
        {
            ("a random 40-letter token", "/q/" + RandomToken()),
            ("a random invoice token", "/i/" + RandomToken()),
            ("the quote token in upper case", "/q/" + quoteToken.ToUpperInvariant()),
            ("the quote token in lower case", "/q/" + quoteToken.ToLowerInvariant()),
            ("the quote token with its last character changed", "/q/" + quoteToken[..^1] + (quoteToken[^1] == 'a' ? 'b' : 'a')),
            ("the quote token one character short", "/q/" + quoteToken[..^1]),
            ("the invoice token as a quote link", "/q/" + invoiceToken),
            ("the quote token as an invoice link", "/i/" + quoteToken),
            ("a short token", "/q/abc"),
            ("a very long token", "/q/" + new string('A', 400)),
            ("../ in the token", "/q/..%2F..%2Fappsettings.json"),
            ("SQL in the token", "/q/" + Uri.EscapeDataString("' OR '1'='1")),
            ("a percent-encoded token", "/i/%41%42%43"),
            ("another random quote token", "/q/" + RandomToken()),
            ("another random invoice token", "/i/" + RandomToken()),
            ("an invoice PDF with a random token", "/i/" + RandomToken() + "?handler=Pdf"),
            ("random token 17", "/q/" + RandomToken()),
            ("random token 18", "/i/" + RandomToken()),
            ("random token 19", "/q/" + RandomToken()),
            ("random token 20", "/i/" + RandomToken()),
        };

        using var prober = new WebSession();
        await Step($"{wrong.Count} wrong links from one address: each \"not found\", never a server error or another record", async () =>
        {
            var misses = 0;
            foreach (var (what, path) in wrong)
            {
                var r = await prober.GetAsync(path);
                if (r.Status == 404) misses++;
                if (r.Status >= 500) problems.Add($"A link with {what} gave a server error (HTTP {r.Status}).");
                else if (r.Status == 200) problems.Add($"A link with {what} OPENED a page (HTTP 200).");
                else if (r.Status is not (404 or 400)) problems.Add($"A link with {what} answered HTTP {r.Status} (expected not found).");
            }

            // Only "not found" counts. Top up with plain random tokens if a
            // few of the odd ones above were refused differently (400).
            for (var extra = 0; misses < Allowed && extra < 10; extra++)
                if ((await prober.GetAsync("/q/" + RandomToken())).Status == 404) misses++;
            Assert.That(misses, Is.EqualTo(Allowed), $"Only {misses} wrong links were answered \"not found\".");
        });

        await Step("…then that address gets \"too many tries\" on every customer link, a genuine one and its Accept button included", async () =>
        {
            var q = await prober.GetAsync("/q/" + quoteToken);
            if (q.Status != 429) problems.Add($"After {Allowed} wrong links, the genuine quote link still opened from the same address (HTTP {q.Status}).");
            else if (!q.Body.Contains("Too many tries", StringComparison.OrdinalIgnoreCase))
                problems.Add("The paused answer doesn't say what happened.");

            var i = await prober.GetAsync("/i/" + invoiceToken);
            if (i.Status != 429) problems.Add($"…and the genuine invoice link answered HTTP {i.Status}.");

            var statusBefore = await Db.ValueAsync("Quotes", "Status", quoteId);
            var accept = await prober.PostFormAsync($"/q/{quoteToken}?handler=Accept", Array.Empty<KeyValuePair<string, string>>());
            if (accept.Status != 429) problems.Add($"…and the Accept button answered HTTP {accept.Status}.");
            if (await Db.ValueAsync("Quotes", "Status", quoteId) != statusBefore)
                problems.Add("The paused address CHANGED the quote's status.");
        });

        await Step("…while the genuine links still open from any other address", async () =>
        {
            using var customer = new WebSession();
            var q = await customer.GetAsync("/q/" + quoteToken);
            if (q.Status != 200) problems.Add($"From another address the genuine quote link answered HTTP {q.Status} — one address's tries must not lock out the customer.");
            var i = await customer.GetAsync("/i/" + invoiceToken);
            if (i.Status != 200) problems.Add($"From another address the genuine invoice link answered HTTP {i.Status}.");
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    private static string RandomToken()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return new string(Enumerable.Range(0, 40).Select(_ => alphabet[Random.Shared.Next(alphabet.Length)]).ToArray());
    }
}
