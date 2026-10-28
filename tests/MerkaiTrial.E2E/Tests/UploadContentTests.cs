// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/UploadContentTests.cs
//
// VUL-13 (round 105) — an upload is judged by what is inside it.
//
//   Uploads_are_checked_by_their_contents_not_their_label
//
// The type a browser says a file is (Content-Type) is chosen by whoever
// sends it. So, attaching files to a lead through the API:
//
//   REFUSED (4xx, nothing stored)
//     • a web page (.html) — not an allowed type at all;
//     • a web page renamed photo.png and labelled image/png;
//     • a web page renamed report.pdf and labelled application/pdf;
//     • a Windows program (MZ…) renamed invoice.pdf;
//     • a real PNG renamed proposal.pdf — right label, wrong inside;
//     • a Word file with macros (vbaProject.bin);
//     • a zip that is not a real Excel file, named .xlsx;
//     • binary bytes named notes.txt;
//     • a name ending .pdf.exe.
//
//   KEPT, as what they really are
//     • a real PDF (control);
//     • a real PNG labelled text/html — stored as image/png, the label ignored;
//     • a real PDF with no ending on its name — stored as "<name>.pdf";
//     • a Word file without macros;
//     • a text file whose words are HTML markup — it IS text, and it comes
//       back as text/plain, "nosniff", as a download (never shown as a page).
// =====================================================================

using System.IO.Compression;
using System.Text;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class UploadContentTests : E2ETest
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static readonly byte[] Page = Encoding.UTF8.GetBytes(
        "<!doctype html><html><body><script>window.__x = 1; alert(document.cookie)</script></body></html>");

    [Test]
    public async Task Uploads_are_checked_by_their_contents_not_their_label()
    {
        var a = World.A;
        var tenantA = await Db.TenantIdAsync(a.Name);
        var apiA = await Api.AsAsync(a.Admin);
        var m = Random.Shared.Next(10000, 99999).ToString();
        var problems = new List<string>();

        var leadId = await Step("A lead of workspace A's to attach files to", () =>
            ApiRecords.LeadAsync(apiA, tenantA, "Upload Lead " + m, "upload" + m));
        var path = $"/api/leads/{leadId}/attachments";

        await Step("Control: a real PDF is kept, as a PDF", async () =>
        {
            var name = $"real-{m}.pdf";
            var r = await apiA.UploadAsync(path, name, Files.SmallPdf("Real " + m), "application/pdf");
            Assert.That(r.IsSuccess, Is.True, $"A real PDF was refused, so the refusals below would prove nothing: {r}");
            var row = await Db.AttachmentAsync(leadId, name);
            Assert.That(row?.MimeType, Is.EqualTo("application/pdf"), "The real PDF was not stored as a PDF.");
        });

        await Step("Files whose insides don't match their names, and types that aren't allowed, are refused", async () =>
        {
            var before = await Db.CountAttachmentsAsync(leadId);

            var cases = new List<(string What, string Name, string Label, byte[] Bytes)>
            {
                ("a web page", $"page-{m}.html", "text/html", Page),
                ("a web page renamed .png and labelled image/png", $"photo-{m}.png", "image/png", Page),
                ("a web page renamed .pdf and labelled application/pdf", $"report-{m}.pdf", "application/pdf", Page),
                ("a Windows program renamed .pdf", $"invoice-{m}.pdf", "application/pdf", Program()),
                ("a real PNG renamed .pdf", $"proposal-{m}.pdf", "application/pdf", Png),
                ("a Word file with macros", $"plan-{m}.docx",
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document", Docx(withMacros: true)),
                ("a zip that is not a real Excel file, named .xlsx", $"sheet-{m}.xlsx",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", NotOffice()),
                ("binary bytes named .txt", $"notes-bin-{m}.txt", "text/plain", new byte[] { 0x4D, 0x5A, 0x00, 0x00, 0x01, 0x02, 0x00 }),
                ("a name ending .pdf.exe", $"setup-{m}.pdf.exe", "application/pdf", Files.SmallPdf("Exe " + m)),
            };

            foreach (var (what, name, label, bytes) in cases)
            {
                var r = await apiA.UploadAsync(path, name, bytes, label);
                if (r.IsSuccess) problems.Add($"{Cap(what)} ({name}, sent as {label}) was KEPT.");
                else if (r.Status >= 500) problems.Add($"{Cap(what)} was refused with a server error: {r}");
                else if (r.Body.Contains("Exception", StringComparison.Ordinal)) problems.Add($"Refusing {what} showed error details: {r}");
            }

            var after = await Db.CountAttachmentsAsync(leadId);
            if (after != before) problems.Add($"The lead has {after - before} more attachment(s) after the refused uploads.");
        });

        await Step("A real PNG labelled text/html is kept as image/png — the label is not believed", async () =>
        {
            var name = $"photo-real-{m}.png";
            var r = await apiA.UploadAsync(path, name, Png, "text/html");
            if (!r.IsSuccess) { problems.Add($"A real PNG labelled text/html was refused: {r}"); return; }
            var row = await Db.AttachmentAsync(leadId, name);
            if (row?.MimeType != "image/png") problems.Add($"A real PNG labelled text/html was stored as '{row?.MimeType}', not image/png.");
        });

        await Step("A real PDF with no ending on its name is kept as \"<name>.pdf\"", async () =>
        {
            var name = $"contract-{m}";
            var r = await apiA.UploadAsync(path, name, Files.SmallPdf("Bare " + m), "application/pdf");
            if (!r.IsSuccess) { problems.Add($"A real PDF with no ending was refused: {r}"); return; }
            var row = await Db.AttachmentAsync(leadId, name + ".pdf");
            if (row is null) problems.Add($"A PDF sent as '{name}' was not stored as '{name}.pdf'.");
            else if (row.MimeType != "application/pdf") problems.Add($"'{name}.pdf' was stored as '{row.MimeType}'.");
        });

        await Step("A Word file without macros is kept", async () =>
        {
            var r = await apiA.UploadAsync(path, $"brief-{m}.docx", Docx(withMacros: false),
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
            if (!r.IsSuccess) problems.Add($"A Word file without macros was refused: {r}");
        });

        await Step("Text that happens to be HTML markup is kept as text, and comes back as a text download, never a page", async () =>
        {
            var name = $"snippet-{m}.txt";
            var r = await apiA.UploadAsync(path, name, Page, "text/html");
            if (!r.IsSuccess) { problems.Add($"A text file of HTML markup was refused: {r}"); return; }

            var row = await Db.AttachmentAsync(leadId, name);
            if (row is null) { problems.Add("The text file was not stored."); return; }
            if (row.MimeType != "text/plain") problems.Add($"A .txt sent as text/html was stored as '{row.MimeType}', not text/plain.");

            var d = await apiA.GetAsync($"/api/attachments/{row.Id}/download");
            if (!d.IsSuccess) { problems.Add($"Downloading it failed: {d}"); return; }
            var type = d.Header("Content-Type") ?? "";
            if (!type.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase))
                problems.Add($"It came back as '{type}', not text/plain.");
            if (!string.Equals(d.Header("X-Content-Type-Options"), "nosniff", StringComparison.OrdinalIgnoreCase))
                problems.Add("Its download has no 'X-Content-Type-Options: nosniff', so a browser may guess it is a page.");
            if (!(d.Header("Content-Disposition") ?? "").Contains("attachment", StringComparison.OrdinalIgnoreCase))
                problems.Add($"Its download is not marked as an attachment (Content-Disposition: '{d.Header("Content-Disposition")}').");
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    private static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>The start of a Windows program: "MZ", then padding.</summary>
    private static byte[] Program()
    {
        var bytes = new byte[512];
        bytes[0] = 0x4D; bytes[1] = 0x5A; bytes[2] = 0x90;
        return bytes;
    }

    /// <summary>A minimal Word file; with a macro project inside when asked.</summary>
    private static byte[] Docx(bool withMacros)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>");
            Add(zip, "word/document.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                "<w:body><w:p><w:r><w:t>E2E</w:t></w:r></w:p></w:body></w:document>");
            if (withMacros) Add(zip, "word/vbaProject.bin", "Attribute VB_Name = \"AutoOpen\"");
        }
        return ms.ToArray();
    }

    /// <summary>A zip with nothing of Excel's in it.</summary>
    private static byte[] NotOffice()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            Add(zip, "payload/run.html", "<script>window.__x = 1</script>");
        return ms.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        w.Write(text);
    }
}
