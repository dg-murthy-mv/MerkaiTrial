// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/BackupRestoreTests.cs
//
// NEW (110). If you ever have to restore production from a backup, will
// Merkai work afterwards?
//
// In workspace A, through the API:
//   1. A sent quote (so it has a customer link) with a PDF attached.
//   2. The test database is backed up (COPY_ONLY).
//   3. AFTER the backup: a new lead, and a second PDF on the quote.
//   4. The backup is restored OVER the test database, as you would in
//      production: everyone disconnected, the database replaced, back
//      online. The two test servers keep running, as a live site would.
//   5. The servers reconnect (their old connections fail once each —
//      counted and reported; in production, restart the app after a
//      restore and nobody sees that).
//   Then:
//   • the restore really happened: the lead and the PDF added after the
//     backup are gone
//   • signing in works, in the browser
//   • the customer's quote link still opens the quote
//   • the PDF from before the backup still downloads, byte for byte,
//     through the API and the website
//   • the PDF added after the backup answers "not found" cleanly — not an
//     error page. Its FILE is still in the uploads folder: the database
//     backup holds the attachment records, the uploads folder holds the
//     files, and they must be backed up together.
//
// The backup file is MerkaiTrial_E2E_test_copy.bak in SQL Server's backup
// folder, overwritten each run. Only a database ending in _E2E is ever
// restored over.
// =====================================================================

using System.Diagnostics;
using System.Globalization;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Database"), NonParallelizable]
public class BackupRestoreTests : E2ETest
{
    [Test]
    public async Task After_a_restore_sign_in_customer_links_and_attachments_still_work()
    {
        var a = World.A;
        var tenantId = await Db.TenantIdAsync(a.Name);
        var api = await Api.AsAsync(a.Admin);
        api.WaitWhenPaced = true;
        var mark = Random.Shared.Next(10000, 99999).ToString(CultureInfo.InvariantCulture);
        var lineName = $"Restore line {mark}";
        var database = DatabaseCopies.TestDatabase;

        await Step("This SQL login may back up and restore databases", DatabaseCopies.CheckPermissionsAsync);

        // Signed in BEFORE the backup, so the session is in the backup too
        // and keeps working after the restore.
        using var web = await Step("Sign in to the website over HTTP (a session that is in the backup)",
            () => WebSession.SignedInAsync(a.Admin));

        var (quoteId, token) = await Step("A sent quote with a customer link (through the API)", async () =>
        {
            var contact = await ApiRecords.ContactAsync(api, tenantId, $"Restore {mark}", "restore" + mark);
            var deal = await ApiRecords.DealAsync(api, tenantId, contact, $"Restore deal {mark}");
            var quote = await ApiRecords.QuoteAsync(api, deal, lineName);
            await ApiRecords.SendQuoteAsync(api, quote);
            var t = await Db.ValueAsync("Quotes", "PublicLinkToken", quote);
            Assert.That(t, Is.Not.Null.And.Not.Empty, "The quote has no customer link after being sent.");
            return (quote, t!);
        });

        var beforeName = $"restore-before-{mark}.pdf";
        var beforeBytes = Files.SmallPdf("Before the backup " + mark);
        var before = await Step("A PDF attached to the quote, and it downloads", async () =>
        {
            var up = await api.UploadAsync($"/api/quotes/{quoteId}/attachments", beforeName, beforeBytes, "application/pdf");
            Assert.That(up.IsSuccess, Is.True, $"Attaching the PDF failed: {up}");
            var row = await Db.AttachmentAsync(quoteId, beforeName)
                      ?? throw new AssertionException("The attachment's record wasn't found.");
            var down = await api.GetAsync($"/api/attachments/{row.Id}/download");
            Assert.That(down.IsSuccess && down.Bytes.SequenceEqual(beforeBytes), Is.True,
                $"Before the backup, the PDF didn't download intact: {down}");
            return row;
        });

        var backup = await Step($"Back up {database} (COPY_ONLY)", () => DatabaseCopies.BackupAsync(database));

        var afterLeadName = $"After backup {mark}";
        var afterName = $"restore-after-{mark}.pdf";
        var (afterLead, afterAttachment) = await Step("After the backup: a new lead, and a second PDF on the quote", async () =>
        {
            var lead = await ApiRecords.LeadAsync(api, tenantId, afterLeadName, "afterbackup" + mark);
            var up = await api.UploadAsync($"/api/quotes/{quoteId}/attachments", afterName,
                Files.SmallPdf("After the backup " + mark), "application/pdf");
            Assert.That(up.IsSuccess, Is.True, $"Attaching the second PDF failed: {up}");
            var row = await Db.AttachmentAsync(quoteId, afterName)
                      ?? throw new AssertionException("The second attachment's record wasn't found.");
            return (lead, row);
        });

        await Step($"Restore the backup over {database} (everyone disconnected, replaced, back online)",
            () => DatabaseCopies.RestoreInPlaceAsync(backup, database));

        var hiccups = await Step("Both servers reconnect to the restored database", () => WaitForServersAsync(web, api));
        TestContext.Progress.WriteLine(hiccups == 0
            ? "      The servers carried on without a single failed request."
            : $"      {hiccups} request(s) failed while the servers' old database connections were replaced. " +
              "In production, restart the app after a restore.");

        await Step("The restore really happened: the lead and PDF added after the backup are gone", async () =>
        {
            Assert.That(await Db.IsLeadDeletedAsync(afterLead), Is.Null,
                $"The lead '{afterLeadName}', added after the backup, is still there — the database wasn't restored.");
            Assert.That(await Db.AttachmentAsync(quoteId, afterName), Is.Null,
                "The PDF attached after the backup still has its record — the database wasn't restored.");
        });

        await Step("Signing in works, in the browser", async () =>
        {
            var page = await SignedInAsync(a.Admin);
            var response = await page.GotoAsync($"/Quotes/Detail?id={quoteId}");
            Assert.That(response?.Status ?? 0, Is.LessThan(400), $"The quote's page answered HTTP {response?.Status}.");
            await Assertions.Expect(page.Locator("main")).ToContainTextAsync(lineName);
        });

        await Step("The customer's quote link still opens the quote", async () =>
        {
            var customer = await NewPageAsync("customer");
            var response = await customer.GotoAsync("/q/" + token);
            Assert.That(response?.Status ?? 0, Is.EqualTo(200), $"/q/{Ui.MaskToken(token)} answered HTTP {response?.Status}.");
            await Assertions.Expect(customer.Locator("body")).ToContainTextAsync(lineName);
        });

        await Step("The PDF from before the backup still downloads, byte for byte (API and website)", async () =>
        {
            var down = await api.GetAsync($"/api/attachments/{before.Id}/download");
            Assert.That(down.IsSuccess, Is.True, $"The API wouldn't download it: {down}");
            Assert.That(down.Bytes.SequenceEqual(beforeBytes), Is.True, "The API's download isn't the file that was attached.");

            var w = await web.GetAsync($"/Attachments/Download/{before.Id}");
            Assert.That(w.Status, Is.EqualTo(200), $"The website wouldn't download it: {w}");
            Assert.That(w.Body, Does.Contain("Before the backup " + mark), "The website's download isn't the file that was attached.");
        });

        await Step("The PDF added after the backup answers \"not found\" — not an error", async () =>
        {
            var down = await api.GetAsync($"/api/attachments/{afterAttachment.Id}/download");
            Assert.That(down.IsSuccess, Is.False, "The PDF added after the backup still downloads, though its record is gone.");
            Assert.That(down.Status, Is.LessThan(500), $"Asking for it crashed instead of saying not found: {down}");
            TestContext.Progress.WriteLine(
                $"      Note: its file ({afterAttachment.FileUrl}) is still in the uploads folder with no record. " +
                "Back up the uploads folder together with the database.");
        });
    }

    /// <summary>
    /// Opens the dashboard (with the session from before the backup) and
    /// lists leads through the API until both work four times in a row (up
    /// to 3 minutes). Returns how many attempts failed on the way — the
    /// servers' old pooled connections. No repeated sign-ins, so the
    /// sign-in attempt limit is never involved.
    /// </summary>
    private static async Task<int> WaitForServersAsync(WebSession web, ApiCaller api)
    {
        var clock = Stopwatch.StartNew();
        var failed = 0;
        var inARow = 0;
        string last = "";

        while (inARow < 4)
        {
            if (clock.Elapsed > TimeSpan.FromMinutes(3))
                throw new AssertionException(
                    $"The servers still weren't working 3 minutes after the restore ({failed} failed attempts). Last answer: {last}");
            try
            {
                var dash = await web.GetAsync("/Dashboard");
                if (dash.Status != 200) throw new InvalidOperationException($"dashboard: {dash}");
                var leads = await api.GetAsync("/api/leads?pageNumber=1&pageSize=1");
                if (!leads.IsSuccess) throw new InvalidOperationException($"API: {leads}");
                inARow++;
            }
            catch (Exception ex) when (ex is not AssertionException)
            {
                failed++;
                inARow = 0;
                last = ex.Message;
                await Task.Delay(1000);
            }
        }
        return failed;
    }
}
