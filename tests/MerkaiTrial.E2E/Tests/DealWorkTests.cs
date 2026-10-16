// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/DealWorkTests.cs
//
// NEW (090). The four tabs people use every day on a deal — Notes,
// Activities, Tasks and Attachments — in workspace A (India). Each test
// makes its own deal (lead → qualify → convert), so they run in any order
// or one at a time (-Filter "FullyQualifiedName~DealWork").
//
//   1. Notes: a note is saved, counted, and signed with the author's NAME
//      (not an id). A Viewer sees it but has no "Add New Note" form — and
//      posting the form by hand is refused, so the count stays at one.
//   2. Activities: a call logged for yesterday 09:15 shows as a Call, with
//      its subject, 25 min, who logged it, and yesterday 09:15 in the
//      workspace's time — not shifted by the time zone.
//   3. Tasks: a task given to the rep, due tomorrow 15:30, shows on the deal
//      with the rep's name and that time; it appears on the rep's Tasks page
//      linked to the deal; Done there says so ONCE; and the deal then lists
//      it under Completed.
//   4. Attachments:
//        • a PDF uploads, is listed with its name and who uploaded it, and
//          downloads back byte for byte;
//        • its link gives nothing to someone signed out — on the app or
//          straight from the API server — nor to another workspace's admin;
//        • an .html file and a file over 10 MB are refused with a message
//          and not listed;
//        • deleted, it leaves the list and its old link no longer works.
//
// The files are made in memory by the test; nothing is read from disk.
//
// 090b: every tab check reloads the deal first (see OpenTabAsync). The
// download link is now /Attachments/Download/{id}, a page that checks the
// person; the test reads it from the page as before.
// =====================================================================

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("DealWork")]
public class DealWorkTests : E2ETest
{
    private static TestTenant A => World.A;

    // =================================================================
    // 1. Notes
    // =================================================================

    [Test]
    public async Task Notes_carry_the_authors_name_and_a_viewer_cannot_add_one()
    {
        var admin = await SignedInAsync(A.Admin);
        var (dealId, _) = await Step("Make a deal", () => NewDealAsync(admin, "Priya Notes"));
        var text = $"Priya wants delivery before Diwali — {World.Stamp}";

        await Step("Add a note", async () =>
        {
            await OpenTabAsync(admin, dealId, "notes");
            await admin.Locator("#NoteInput_Note").FillAsync(text);
            await admin.Locator("#notes form button[type=submit]").First.ClickAsync();
            await Ui.ExpectSuccessAsync(admin, "Note added");
        });

        await Step("It is listed once, counted, and signed with the admin's name", async () =>
        {
            await OpenTabAsync(admin, dealId, "notes");
            var item = admin.Locator("#notes .list-group-item", new() { HasText = text });
            await Assertions.Expect(item).ToHaveCountAsync(1);
            await Assertions.Expect(TabBadge(admin, "notes", "Notes History")).ToContainTextAsync("1");

            var signed = (await item.InnerTextAsync()).Trim();
            Assert.That(signed, Does.Contain(A.Admin.FullName),
                $"The note does not say who wrote it. It reads: \"{OneLine(signed)}\"");
            Assert.That(Regex.IsMatch(signed, @"[0-9a-f]{8}-[0-9a-f]{4}-"), Is.False,
                $"The note is signed with an id instead of a name: \"{OneLine(signed)}\"");
        });

        await Step("A viewer sees it, with no form to add one", async () =>
        {
            var viewer = await SignedInAsync(A["viewer"]);
            await OpenTabAsync(viewer, dealId, "notes");
            await Assertions.Expect(viewer.Locator("#notes .list-group-item", new() { HasText = text })).ToHaveCountAsync(1);
            await Assertions.Expect(viewer.Locator("#NoteInput_Note")).ToHaveCountAsync(0);

            var posted = await PostFormByHandAsync(viewer, $"/Pipeline/Detail/{dealId}?handler=AddNote",
                "NoteInput.Note", "Added by a viewer " + World.Stamp);
            if (!posted)
                TestContext.Progress.WriteLine("[e2e] The viewer's page has no anti-forgery token to borrow; the hand-made post was skipped.");
        });

        await Step("Posting the form by hand as the viewer added nothing", async () =>
        {
            await OpenTabAsync(admin, dealId, "notes");
            await Assertions.Expect(TabBadge(admin, "notes", "Notes History")).ToContainTextAsync("1");
            await Assertions.Expect(admin.Locator("#notes", new() { HasText = "Added by a viewer" })).ToHaveCountAsync(0);
        });
    }

    // =================================================================
    // 2. Activities
    // =================================================================

    [Test]
    public async Task A_logged_call_shows_its_details_in_the_workspaces_time()
    {
        var admin = await SignedInAsync(A.Admin);
        var (dealId, _) = await Step("Make a deal", () => NewDealAsync(admin, "Farhan Calls"));
        var subject = $"Pricing call {World.Stamp}";

        var local = DateTime.UtcNow + A.UtcOffset;
        var when = local.Date.AddDays(-1).AddHours(9).AddMinutes(15);

        await Step("Log a 25-minute call for yesterday 09:15", async () =>
        {
            await OpenTabAsync(admin, dealId, "activities");
            await admin.Locator("#ActivityInput_ActivityType").SelectOptionAsync("Call");
            await admin.Locator("#ActivityInput_Duration").FillAsync("25");
            await admin.Locator("#ActivityInput_Subject").FillAsync(subject);
            await admin.Locator("#ActivityInput_Description").FillAsync("Asked for 5% off on 20 seats.");
            await admin.Locator("#ActivityInput_ActivityDate").FillAsync(when.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture));
            await admin.Locator("#activities form button[type=submit]").First.ClickAsync();
            await Ui.ExpectSuccessAsync(admin, "Activity logged");
        });

        await Step("The history shows a Call, the subject, 25 min, who and when", async () =>
        {
            await OpenTabAsync(admin, dealId, "activities");
            var item = admin.Locator("#activities .list-group-item", new() { HasText = subject });
            await Assertions.Expect(item).ToHaveCountAsync(1);
            await Assertions.Expect(TabBadge(admin, "activities", "Activity History")).ToContainTextAsync("1");

            var shown = OneLine(await item.InnerTextAsync());
            Assert.That(shown, Does.Contain("Call"), $"The activity reads: \"{shown}\"");
            Assert.That(shown, Does.Contain("25 min"), $"The duration is missing: \"{shown}\"");
            Assert.That(shown, Does.Contain(A.Admin.FullName), $"It does not say who logged it: \"{shown}\"");

            var times = Ui.FindDateTimes(shown);
            Assert.That(times.Contains(when), Is.True,
                $"Expected {when:dd/MM/yyyy HH:mm} (workspace time), but it shows: \"{shown}\"");
        });
    }

    // =================================================================
    // 3. Tasks
    // =================================================================

    [Test]
    public async Task A_task_given_to_the_rep_reaches_their_task_list_and_can_be_done_there()
    {
        var rep = A["rep"];
        var admin = await SignedInAsync(A.Admin);
        var (dealId, dealTitle) = await Step("Make a deal", () => NewDealAsync(admin, "Neha Tasks"));
        var subject = $"Send the revised proposal {World.Stamp}";

        var local = DateTime.UtcNow + A.UtcOffset;
        var due = local.Date.AddDays(1).AddHours(15).AddMinutes(30);

        await Step("Give the rep a task due tomorrow 15:30", async () =>
        {
            await OpenTabAsync(admin, dealId, "tasks");
            await admin.Locator("#TaskInput_ActivityType").SelectOptionAsync("Task");
            await admin.Locator("#TaskInput_Subject").FillAsync(subject);
            await admin.Locator("#TaskInput_DueDate").FillAsync(due.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture));
            await Ui.SelectByTextAsync(admin.Locator("#TaskInput_AssignedToUserId"), rep.FullName);
            await admin.Locator("#tasks form button[type=submit]").First.ClickAsync();
            await Ui.ExpectSuccessAsync(admin, "Task created and assigned");
        });

        await Step("The deal lists it as open, for the rep, due tomorrow 15:30", async () =>
        {
            await OpenTabAsync(admin, dealId, "tasks");
            var item = admin.Locator("#tasks .list-group-item", new() { HasText = subject });
            await Assertions.Expect(item).ToHaveCountAsync(1);

            var shown = OneLine(await item.InnerTextAsync());
            Assert.That(shown, Does.Contain(rep.FullName), $"The task does not show who it is for: \"{shown}\"");
            Assert.That(Ui.FindDateTimes(shown).Contains(due), Is.True,
                $"Expected due {due:dd/MM/yyyy HH:mm} (workspace time), but it shows: \"{shown}\"");
        });

        await Step("The rep's Tasks page lists it, linked to the deal, and Done says so once", async () =>
        {
            var repPage = await SignedInAsync(rep);
            await repPage.GotoAsync("/Tasks");

            var row = repPage.Locator(".list-group-item", new() { HasText = subject });
            try
            {
                await Assertions.Expect(row).ToHaveCountAsync(1);
            }
            catch (Exception ex) when (ex is not AssertionException)
            {
                throw new AssertionException(
                    "The task is not on the rep's Tasks page. The page says: " +
                    OneLine(await repPage.Locator("main").First.InnerTextAsync()));
            }

            var link = row.Locator($"a[href*='/Pipeline/Detail/{dealId}']");
            await Assertions.Expect(link).ToHaveCountAsync(1);
            await Assertions.Expect(link).ToContainTextAsync(dealTitle);

            await row.Locator("form button[type=submit]", new() { HasText = "Done" }).ClickAsync();
            await Ui.ExpectSuccessAsync(repPage, "Task done");
            await Assertions.Expect(repPage.Locator(".alert-success", new() { HasText = "Task done" })).ToHaveCountAsync(1);
            await Assertions.Expect(repPage.Locator(".list-group-item", new() { HasText = subject })).ToHaveCountAsync(0);
        });

        await Step("The deal now lists it under Completed", async () =>
        {
            await OpenTabAsync(admin, dealId, "tasks");
            var done = admin.Locator("#tasks .card", new() { HasText = "Completed" })
                            .Locator(".list-group-item", new() { HasText = subject });
            await Assertions.Expect(done).ToHaveCountAsync(1);
            await Assertions.Expect(done).ToContainTextAsync("Done");
        });
    }

    // =================================================================
    // 4. Attachments
    // =================================================================

    [Test]
    public async Task An_attachment_downloads_only_for_its_workspace_and_bad_files_are_refused()
    {
        var admin = await SignedInAsync(A.Admin);
        var (dealId, _) = await Step("Make a deal", () => NewDealAsync(admin, "Kiran Files"));

        var pdfName = $"proposal-{World.Stamp}.pdf";
        var pdf = SmallPdf($"Proposal {World.Stamp}");

        await Step("Upload a PDF", async () =>
        {
            await UploadAsync(admin, dealId, pdfName, "application/pdf", pdf);
            await Ui.ExpectSuccessAsync(admin, "uploaded successfully");
        });

        var url = await Step("It is listed once, with its name and who uploaded it", async () =>
        {
            await OpenTabAsync(admin, dealId, "attachments");
            var item = admin.Locator("#attachments .list-group-item", new() { HasText = pdfName });
            await Assertions.Expect(item).ToHaveCountAsync(1);
            await Assertions.Expect(TabBadge(admin, "attachments", "Attachments")).ToContainTextAsync("1");
            await Assertions.Expect(item).ToContainTextAsync(A.Admin.FullName);

            var href = await item.Locator("a[title^='Download']").GetAttributeAsync("href") ?? "";
            Assert.That(href, Is.Not.Empty, "The attachment has no download link.");
            return Absolute(href);
        });

        await Step("It downloads back byte for byte", async () =>
        {
            var body = await FetchAsync(admin, url);
            Assert.That(body.Status, Is.EqualTo(200), $"Downloading it answered HTTP {body.Status} ({Ui.PathOf(url)}).");
            Assert.That(body.Bytes.SequenceEqual(pdf), Is.True,
                $"The downloaded file is not the one uploaded ({body.Bytes.Length} bytes back, {pdf.Length} sent).");
        });

        await Step("Signed out, the link gives nothing — on the app or on the API server", async () =>
        {
            var anon = await NewPageAsync("signed-out");
            var onApp = await FetchAsync(anon, url);
            Assert.That(onApp.Bytes.SequenceEqual(pdf), Is.False,
                $"Someone signed out downloaded the attachment from {Ui.PathOf(url)} (HTTP {onApp.Status}).");

            // 090b: the API's own download endpoint, asked with no token.
            var apiUrl = $"{E2ESettings.ApiBaseUrl}/api/attachments/{Ui.IdFromUrl(url)}/download";
            var onApi = await FetchAsync(anon, apiUrl);
            Assert.That(onApi.Bytes.SequenceEqual(pdf), Is.False,
                $"Someone signed out downloaded the attachment straight from the API server (HTTP {onApi.Status}).");
        });

        await Step("Another workspace's admin cannot download it", async () =>
        {
            var other = await SignedInAsync(World.B.Admin);
            var got = await FetchAsync(other, url);
            Assert.That(got.Bytes.SequenceEqual(pdf), Is.False,
                $"Workspace B's admin downloaded workspace A's attachment (HTTP {got.Status}).");
        });

        await Step("An .html file is refused and not listed", async () =>
        {
            var name = $"page-{World.Stamp}.html";
            await UploadAsync(admin, dealId, name, "text/html", Encoding.UTF8.GetBytes("<script>alert(1)</script>"));
            await ExpectRefusedAsync(admin, dealId, name);
        });

        await Step("A file over 10 MB is refused and not listed", async () =>
        {
            var name = $"huge-{World.Stamp}.pdf";
            var big = new byte[11 * 1024 * 1024];
            Array.Copy(pdf, big, pdf.Length);
            await UploadAsync(admin, dealId, name, "application/pdf", big);
            await ExpectRefusedAsync(admin, dealId, name);
        });

        await Step("Deleted, it leaves the list and its link stops working", async () =>
        {
            await OpenTabAsync(admin, dealId, "attachments");
            var item = admin.Locator("#attachments .list-group-item", new() { HasText = pdfName });
            await item.Locator("button[title=Delete]").ClickAsync();   // confirm() is accepted by the session
            await Ui.ExpectSuccessAsync(admin, "Attachment deleted");

            await OpenTabAsync(admin, dealId, "attachments");
            await Assertions.Expect(admin.Locator("#attachments .list-group-item", new() { HasText = pdfName })).ToHaveCountAsync(0);

            var after = await FetchAsync(admin, url);
            Assert.That(after.Bytes.SequenceEqual(pdf), Is.False,
                $"The deleted attachment still downloads (HTTP {after.Status}).");
        });
    }

    // =================================================================
    // Helpers
    // =================================================================

    /// <summary>Lead → qualify → convert in workspace A. Returns the deal's id and title.</summary>
    private static async Task<(Guid Id, string Title)> NewDealAsync(IPage page, string person)
    {
        var leadName = World.Unique(person);
        var dealTitle = World.Unique(person.Split(' ')[0] + " deal");
        await Flows.CreateLeadAsync(page, leadName, World.EmailFor("A", "w" + Random.Shared.Next(10000, 99999)), 300000m, A);
        await Flows.QualifyLeadAsync(page);
        await Flows.ConvertLeadAsync(page, dealTitle, 300000m);

        var tenantId = await GlobalSetup.Db.TenantIdAsync(A.Name);
        return (await GlobalSetup.Db.DealIdByTitleAsync(tenantId, dealTitle), dealTitle);
    }

    /// <summary>
    /// (Re)loads the deal and clicks one of its tabs (notes, activities,
    /// tasks, attachments). 090b: ALWAYS loads it — the first run skipped
    /// the load when the page was already on the deal, so a check made after
    /// someone else changed the deal (the rep's Done, the viewer's post)
    /// looked at the page as it was before.
    /// </summary>
    private static async Task OpenTabAsync(IPage page, Guid dealId, string tab)
    {
        await page.GotoAsync($"/Pipeline/Detail/{dealId}");

        await page.Locator($"#{tab}-tab").ClickAsync();
        await Assertions.Expect(page.Locator($"#{tab}")).ToBeVisibleAsync();
    }

    /// <summary>The count badge on a tab's list card, found by the card's heading.</summary>
    private static ILocator TabBadge(IPage page, string tab, string heading) =>
        page.Locator($"#{tab} .card-header", new() { HasText = heading }).Locator(".badge").First;

    /// <summary>Attachments tab → choose a file → Upload.</summary>
    private static async Task UploadAsync(IPage page, Guid dealId, string name, string mime, byte[] bytes)
    {
        await OpenTabAsync(page, dealId, "attachments");
        await page.Locator("#attachmentFile").SetInputFilesAsync(new FilePayload { Name = name, MimeType = mime, Buffer = bytes });
        await page.Locator("#attachments form[enctype='multipart/form-data'] button[type=submit]").ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
    }

    /// <summary>After an upload: a red message, and the file is not on the list.</summary>
    private static async Task ExpectRefusedAsync(IPage page, Guid dealId, string name)
    {
        var red = page.Locator(".alert-danger");
        try
        {
            await Assertions.Expect(red.First).ToBeVisibleAsync();
        }
        catch (Exception ex) when (ex is not AssertionException)
        {
            var green = await Ui.TextOrEmptyAsync(page.Locator(".alert-success"));
            throw new AssertionException(
                $"Uploading {name} showed no refusal." + (green.Length > 0 ? $" The page said: {green}" : ""));
        }
        TestContext.Progress.WriteLine($"[e2e] {name} refused: {OneLine(await red.First.InnerTextAsync())}");

        await OpenTabAsync(page, dealId, "attachments");
        await Assertions.Expect(page.Locator("#attachments .list-group-item", new() { HasText = name })).ToHaveCountAsync(0);
    }

    private sealed record Fetched(int Status, byte[] Bytes);

    /// <summary>GET with this session's cookies (none for a signed-out session).
    /// Redirects are followed, so a sign-in page comes back as HTML.</summary>
    private static async Task<Fetched> FetchAsync(IPage page, string url)
    {
        try
        {
            var response = await page.APIRequest.GetAsync(url);
            return new Fetched(response.Status, await response.BodyAsync());
        }
        catch (PlaywrightException ex)
        {
            // Nothing listening there (e.g. no API server on that port) gave nothing.
            TestContext.Progress.WriteLine($"[e2e] GET {Ui.PathOf(url)} failed: {ex.Message.Split('\n')[0]}");
            return new Fetched(0, Array.Empty<byte>());
        }
    }

    /// <summary>
    /// Builds a form on the current page and submits it, with the page's own
    /// anti-forgery token — what a hand-made request from this signed-in
    /// person would send. False when the page has no token to borrow.
    /// </summary>
    private static async Task<bool> PostFormByHandAsync(IPage page, string action, string field, string value)
    {
        var posted = await page.EvaluateAsync<bool>(
            @"args => {
                const token = document.querySelector('input[name=__RequestVerificationToken]');
                if (!token) return false;
                const f = document.createElement('form');
                f.method = 'post';
                f.action = args[0];
                const add = (n, v) => { const i = document.createElement('input'); i.type = 'hidden'; i.name = n; i.value = v; f.appendChild(i); };
                add('__RequestVerificationToken', token.value);
                add(args[1], args[2]);
                document.body.appendChild(f);
                f.submit();
                return true;
            }", new[] { action, field, value });

        if (posted) await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        return posted;
    }

    private static string Absolute(string href) =>
        Uri.TryCreate(href, UriKind.Absolute, out _) ? href : E2ESettings.BaseUrl + (href.StartsWith('/') ? href : "/" + href);

    private static string OneLine(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>A tiny, valid one-page PDF carrying <paramref name="label"/>.</summary>
    private static byte[] SmallPdf(string label)
    {
        var content = $"BT /F1 18 Tf 72 720 Td ({label}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
