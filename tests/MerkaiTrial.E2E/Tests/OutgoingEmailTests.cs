// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/OutgoingEmailTests.cs
//
// VUL-24 (round 106) — people's text can't take over an email.
//
//   Text_from_records_cannot_add_email_headers_or_script
//
// The test servers never send email; every email waits in the outbox
// exactly as it would be sent. So the test makes the app write emails
// whose ingredients are hostile, and reads what was queued:
//
//   • a contact whose name carries a line break and "Bcc: …";
//   • a workspace whose NAME carries one (it is in every quote email's
//     subject) — workspace E, renamed for the test and put back after;
//   • a quote line and an invoice's personal message that are script,
//     an onerror image and a javascript: link;
//   • an invoice sent with copies (Cc) that carry a line break.
//
// In every email queued for the workspace during the test:
//   • the subject, the recipient's name and address, the reply-to and the
//     copies are each ONE line — nothing can start a header of its own;
//   • the HTML body carries the hostile text as text (&lt;script&gt;…),
//     never as a working tag, an event handler or a javascript: link.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class OutgoingEmailTests : E2ETest
{
    private const string Injection = "\r\nBcc: attacker@evil.example\r\nX-Injected: yes";

    [Test]
    public async Task Text_from_records_cannot_add_email_headers_or_script()
    {
        var e = await WorldBuilder.EnsureWorkspaceEAsync();
        var tenantE = await Db.TenantIdAsync(e.Name);
        var apiE = await Api.AsAsync(e.Admin);
        var m = Random.Shared.Next(10000, 99999).ToString();
        var problems = new List<string>();
        var start = DateTime.UtcNow.AddSeconds(-2);

        var originalName = e.Name;
        try
        {
            await Step("Workspace E's name, its contact's name and a quote line are hostile", async () =>
            {
                await Db.SetTextAsync("Tenants", "Name", tenantE, originalName + Injection);
            });

            await Step<(Guid, string)>("A quote to a contact whose name carries a line break, with a script line, sent", async () =>
            {
                var key = "mailc" + m;
                var contact = await ApiRecords.ContactAsync(apiE, tenantE, "Mail Contact " + m, key);
                await Db.SetTextAsync("Contacts", "LastName", contact, "Mail" + m + Injection);
                var deal = await ApiRecords.DealAsync(apiE, tenantE, contact, "Mail Deal " + m);
                var quote = await ApiRecords.QuoteAsync(apiE, deal, "<script>window.__x=1</script><img src=x onerror=alert(1)> Line " + m);
                await ApiRecords.SendQuoteAsync(apiE, quote);
                return (quote, World.EmailFor("A", key));
            });

            await Step("An invoice emailed with a hostile personal message, a hostile name and copies with line breaks", async () =>
            {
                var contact = await ApiRecords.ContactAsync(apiE, tenantE, "Mail Inv Contact " + m, "mailic" + m);
                var deal = await ApiRecords.DealAsync(apiE, tenantE, contact, "Mail Inv Deal " + m);
                var invoice = await ApiRecords.InvoiceAsync(apiE, deal, "Mail inv line " + m);
                var issued = await apiE.PostAsync($"/api/invoices/{invoice}/issue");
                Assert.That(issued.IsSuccess, Is.True, $"Issuing the invoice failed: {issued}");

                var withCopies = await apiE.PostAsync($"/api/invoices/{invoice}/email", new
                {
                    toAddress = World.EmailFor("A", "mailic" + m),
                    toName = "Accounts" + Injection,
                    ccAddresses = World.EmailFor("A", "mailcc" + m) + Injection,
                    message = "<script>window.__x=1</script> <a href=\"javascript:alert(1)\">Pay here</a> <img src=x onerror=alert(1)>" + Injection,
                    baseUrl = E2ESettings.BaseUrl,
                });
                if (withCopies.Status >= 500) problems.Add($"Emailing the invoice with hostile copies gave a server error: {withCopies}");

                // 106-update3: test servers never get an email key, and the
                // invoice email (unlike the quote email) is refused up front
                // without one — so nothing is queued here to check. The
                // header cleaning lives in OutboundMessage itself, which
                // every email passes through, and the quote email above
                // checks it.
                if (withCopies.Body.Contains("isn't set up on this server", StringComparison.OrdinalIgnoreCase))
                {
                    TestContext.Progress.WriteLine("    (invoice email is refused on a test server without an email key, as it should be; checked through the quote email instead)");
                    return;
                }

                if (!withCopies.IsSuccess)
                {
                    // Copies with a line break may be refused outright — that is fine. Send without them.
                    var plain = await apiE.PostAsync($"/api/invoices/{invoice}/email", new
                    {
                        toAddress = World.EmailFor("A", "mailic" + m),
                        toName = "Accounts" + Injection,
                        ccAddresses = (string?)null,
                        message = "<script>window.__x=1</script> <a href=\"javascript:alert(1)\">Pay here</a> <img src=x onerror=alert(1)>" + Injection,
                        baseUrl = E2ESettings.BaseUrl,
                    });
                    Assert.That(plain.IsSuccess, Is.True, $"Emailing the invoice failed: {plain}");
                }
            });

            await Step("Every email queued: headers are one line each, and the body carries the hostile text only as text", async () =>
            {
                var ids = await Db.OutboundSinceAsync(tenantE, start);
                Assert.That(ids, Is.Not.Empty, "No email was queued for workspace E during the test, so nothing could be checked.");

                var checkedSubjectWithName = false;
                foreach (var id in ids)
                {
                    var o = await Db.OutboundAsync(id);
                    if (o is null) continue;
                    var label = $"The email \"{Short(o.Subject)}\" to {o.ToAddress}";

                    foreach (var (field, value) in new[]
                             {
                                 ("subject", o.Subject), ("recipient name", o.ToName), ("recipient address", o.ToAddress),
                                 ("reply-to", o.ReplyToAddress), ("copies", o.CcAddresses),
                             })
                    {
                        if (value is null) continue;
                        if (value.IndexOfAny(new[] { '\r', '\n', '\u2028', '\u2029', '\0' }) >= 0)
                            problems.Add($"{label}: its {field} has a LINE BREAK — it could start a header of its own: \"{Visible(value)}\"");
                    }

                    if (o.Subject.Contains(originalName, StringComparison.Ordinal) && o.Subject.Contains("X-Injected", StringComparison.Ordinal))
                        checkedSubjectWithName = true;

                    var body = o.BodyHtml;
                    if (body.Contains("<script", StringComparison.OrdinalIgnoreCase)) problems.Add($"{label}: the body has a working <script> tag.");
                    if (System.Text.RegularExpressions.Regex.IsMatch(body, @"<[a-z][^<>]*\son[a-z]+\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                        && System.Text.RegularExpressions.Regex.IsMatch(body, @"<img[^<>]*\sonerror\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        problems.Add($"{label}: the body has a working onerror= handler.");
                    if (body.Contains("href=\"javascript:", StringComparison.OrdinalIgnoreCase) || body.Contains("href='javascript:", StringComparison.OrdinalIgnoreCase))
                        problems.Add($"{label}: the body has a javascript: link.");
                }

                if (!checkedSubjectWithName)
                    TestContext.Progress.WriteLine("    (no queued subject carried the workspace's name, so the subject check covered the other fields only)");
            });
        }
        finally
        {
            await Db.SetTextAsync("Tenants", "Name", tenantE, originalName);
        }

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    private static string Visible(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");

    private static string Short(string s) => s.Length <= 50 ? s : s[..50] + "…";
}
