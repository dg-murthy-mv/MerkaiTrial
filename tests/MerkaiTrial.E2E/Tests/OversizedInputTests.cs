// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/OversizedInputTests.cs
//
// VUL-23 (round 106) — one request can't swamp the server.
//
//   Oversized_and_booby_trapped_input_is_refused_cleanly
//
//   • A 3 MB request to an ordinary endpoint is refused (413) before it is
//     read — the agreed limit is 2 MB — and nothing is saved. An upload of
//     the same size still works: uploads keep their own 10 MB.
//   • Text longer than its column (a 5,000-character name, company, deal
//     title, contact name…) is refused with a sentence that names the
//     field — not a server error — and nothing is saved.
//   • JSON nested 300 deep, and a 20,000-character search, are refused or
//     answered — never a server error.
//   • Lead import: an .xlsx that unpacks to 150 MB from a few hundred KB
//     (a "zip bomb"), and one with 2,500 parts, are refused quickly with a
//     sentence, and the server is still well afterwards.
// =====================================================================

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class OversizedInputTests : E2ETest
{
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    [Test]
    public async Task Oversized_and_booby_trapped_input_is_refused_cleanly()
    {
        var a = World.A;
        var tenantA = await Db.TenantIdAsync(a.Name);
        var apiA = await Api.AsAsync(a.Admin);
        var m = Random.Shared.Next(10000, 99999).ToString();
        var problems = new List<string>();

        await Step("A 3 MB request to an ordinary endpoint: refused (413) before it is read, nothing saved", async () =>
        {
            var marker = "Huge" + m;
            // "Expect: 100-continue" makes the client wait for the server's
            // go-ahead before sending the 3 MB. The server refuses on the
            // size it was told, so the 413 comes back before any body is
            // sent. Without it the server answers mid-upload and closes the
            // connection, and the client only sees "Error while copying
            // content to a stream" instead of the 413.
            ApiResponse r;
            try
            {
                r = await apiA.PostAsync("/api/leads", new
                {
                    tenantId = tenantA, fullName = marker, email = World.EmailFor("A", "huge" + m), phone = "+919812" + m,
                    companyName = marker + " Co", estimatedValue = 1000m, currency = "INR",
                    notes = new string('x', 3 * 1024 * 1024),
                }, new Dictionary<string, string> { ["Expect"] = "100-continue" });
            }
            catch (HttpRequestException ex)
            {
                problems.Add($"A 3 MB request to POST /api/leads cut the connection instead of answering 413: {ex.Message}");
                r = new ApiResponse(0, "", null, Array.Empty<byte>());
            }
            if (r.Status is not (413 or 0)) problems.Add($"A 3 MB request to POST /api/leads answered {r} (expected 413).");
            if (await Db.CountContainingAsync("Leads", tenantA, marker) > 0) problems.Add("The 3 MB request SAVED a lead.");
        });

        await Step("…while an upload of the same size still works (uploads keep 10 MB)", async () =>
        {
            var lead = await ApiRecords.LeadAsync(apiA, tenantA, "Big File Lead " + m, "bigfile" + m);
            var pdf = Files.SmallPdf("Big " + m);
            var big = new byte[3 * 1024 * 1024];
            Array.Copy(pdf, big, pdf.Length);
            for (var i = pdf.Length; i < big.Length; i++) big[i] = (byte)'\n';
            var r = await apiA.UploadAsync($"/api/leads/{lead}/attachments", $"big-{m}.pdf", big, "application/pdf");
            if (!r.IsSuccess) problems.Add($"A 3 MB PDF upload was refused — the 2 MB limit must not apply to uploads: {r}");
        });

        await Step("Text longer than its column: refused with a sentence naming the field, nothing saved", async () =>
        {
            var longText = new string('L', 5000);
            var contact = await ApiRecords.ContactAsync(apiA, tenantA, "Long Contact " + m, "longc" + m);

            // Each marker sits in the column CountContainingAsync looks at
            // (Leads: name, Contacts: first name, Companies: name, Deals: title),
            // so "nothing saved" is checked whichever field was too long.
            var cases = new List<(string What, Func<Task<ApiResponse>> Call, string Table, string Marker)>
            {
                ("a lead's name", () => apiA.PostAsync("/api/leads", new
                    { tenantId = tenantA, fullName = "LongLead" + m + longText, email = World.EmailFor("A", "longl" + m),
                      phone = "+919813" + m, companyName = "Co", estimatedValue = 1000m, currency = "INR" }), "Leads", "LongLead" + m),
                ("a lead's company", () => apiA.PostAsync("/api/leads", new
                    { tenantId = tenantA, fullName = "LongCo " + m, email = World.EmailFor("A", "longco" + m),
                      phone = "+919814" + m, companyName = "LongCompany" + longText, estimatedValue = 1000m, currency = "INR" }), "Leads", "LongCo " + m),
                ("a contact's last name", () => apiA.PostAsync("/api/contacts", new
                    { tenantId = tenantA, firstName = "LongLast" + m, lastName = "L" + longText, email = World.EmailFor("A", "longlast" + m) }), "Contacts", "LongLast" + m),
                ("a company's name", () => apiA.PostAsync("/api/companies", new
                    { tenantId = tenantA, name = "LongName" + m + longText }), "Companies", "LongName" + m),
                ("a deal's title", () => apiA.PostAsync("/api/deals", new
                    { tenantId = tenantA, contactId = contact, title = "LongTitle" + m + longText, expectedValue = 1000m, currency = "INR",
                      expectedCloseDateUtc = DateTime.UtcNow.Date.AddDays(30) }), "Deals", "LongTitle" + m),
            };

            foreach (var (what, call, table, marker) in cases)
            {
                var r = await call();
                if (r.IsSuccess) problems.Add($"{Cap(what)} of 5,000 characters was SAVED.");
                else if (r.Status >= 500) problems.Add($"{Cap(what)} of 5,000 characters gave a server error: {r}");
                else if (r.Body.Contains("truncated", StringComparison.OrdinalIgnoreCase) || r.Body.Contains("Exception", StringComparison.Ordinal))
                    problems.Add($"{Cap(what)}: the refusal shows the database's own text: {r}");
                if (await Db.CountContainingAsync(table, tenantA, marker) > 0) problems.Add($"{Cap(what)} of 5,000 characters is in the database.");
            }
        });

        await Step("JSON nested 300 deep, and a 20,000-character search: no server error", async () =>
        {
            var deep = new StringBuilder("{\"fullName\":\"Deep " + m + "\",\"tags\":");
            for (var i = 0; i < 300; i++) deep.Append('[');
            for (var i = 0; i < 300; i++) deep.Append(']');
            deep.Append('}');
            var r = await apiA.SendAsync(HttpMethod.Post, "/api/leads",
                content: new StringContent(deep.ToString(), Encoding.UTF8, "application/json"));
            if (r.Status >= 500) problems.Add($"JSON nested 300 deep gave a server error: {r}");
            if (r.IsSuccess) problems.Add($"JSON nested 300 deep was accepted: {r}");

            var search = await apiA.GetAsync("/api/leads?pageNumber=1&pageSize=5&searchTerm=" + new string('s', 20000));
            if (search.Status >= 500) problems.Add($"A 20,000-character search gave a server error: {search}");
        });

        await Step("Lead import: a workbook that unpacks to 150 MB, and one with 2,500 parts — refused quickly, server still well", async () =>
        {
            var bombs = new[]
            {
                ("a workbook that unpacks to 150 MB", $"bomb-{m}.xlsx", Bomb(150L * 1024 * 1024)),
                ("a workbook with 2,500 parts", $"parts-{m}.xlsx", ManyParts(2500)),
            };

            foreach (var (what, name, bytes) in bombs)
            {
                var watch = Stopwatch.StartNew();
                var r = await apiA.UploadAsync("/api/leads/import/upload", name, bytes, Xlsx);
                watch.Stop();
                TestContext.Progress.WriteLine($"    {what}: {bytes.Length / 1024} KB sent, answered {r.Status} in {watch.Elapsed.TotalSeconds:0.0}s");

                if (r.IsSuccess) problems.Add($"{Cap(what)} ({bytes.Length / 1024} KB) was ACCEPTED for import.");
                else if (r.Status >= 500) problems.Add($"{Cap(what)} gave a server error: {r}");
                if (watch.Elapsed > TimeSpan.FromSeconds(30)) problems.Add($"{Cap(what)} took {watch.Elapsed.TotalSeconds:0}s to refuse.");
            }

            var health = await Api.Anonymous.GetAsync("/health");
            if (health.Status != 200) problems.Add($"After the import attempts the server's health check answered {health}.");
            var list = await apiA.GetAsync("/api/leads?pageNumber=1&pageSize=1");
            if (list.Status != 200) problems.Add($"After the import attempts a plain list answered {list}.");
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    /// <summary>An .xlsx-shaped zip whose one big part is <paramref name="unpacked"/> bytes of zeros — tiny when packed.</summary>
    private static byte[] Bomb(long unpacked)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Add(zip, "xl/workbook.xml", "<?xml version=\"1.0\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"/>");
            var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.SmallestSize);
            using var s = entry.Open();
            var zeros = new byte[1024 * 1024];
            for (long written = 0; written < unpacked; written += zeros.Length) s.Write(zeros, 0, zeros.Length);
        }
        return ms.ToArray();
    }

    private static byte[] ManyParts(int count)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Add(zip, "xl/workbook.xml", "<?xml version=\"1.0\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"/>");
            for (var i = 0; i < count; i++) Add(zip, $"xl/media/p{i}.xml", "<p/>");
        }
        return ms.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    private static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
