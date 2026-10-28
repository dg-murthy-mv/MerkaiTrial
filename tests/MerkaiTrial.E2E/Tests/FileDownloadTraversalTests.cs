// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/FileDownloadTraversalTests.cs
//
// VUL-14 (round 105) — a download can't reach the server's own files or
// another workspace's.
//
//   Downloads_reach_only_the_workspaces_own_files
//
//   • Control: workspace A downloads its own attachment.
//   • Workspace B asks for A's attachment by id — through the API and
//     through the website's download address: "not found".
//   • An attachment row of A's that names a file in B's folder (what a
//     badly written row would look like) gives "not found", not B's file.
//   • The same row naming ../../appsettings.json (both kinds of slash,
//     and %2e%2e), an absolute Windows or Linux path, or a network share:
//     "not found" every time, and no settings, no system file.
//     (The row is put back afterwards.)
//   • Asking either server for a file straight by address — the uploads
//     folder, appsettings.json, web.config, with ../ and %2e%2e in the
//     address — serves none of them.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class FileDownloadTraversalTests : E2ETest
{
    // What a leaked settings file or system file would contain.
    private static readonly string[] Leaks = { "ConnectionStrings", "SigningKey", "\"Jwt\"", "[fonts]", "root:x:", "<configuration" };

    [Test]
    public async Task Downloads_reach_only_the_workspaces_own_files()
    {
        var a = World.A;
        var b = World.B;
        var tenantA = await Db.TenantIdAsync(a.Name);
        var tenantB = await Db.TenantIdAsync(b.Name);
        var apiA = await Api.AsAsync(a.Admin);
        var apiB = await Api.AsAsync(b.Admin);
        var m = Random.Shared.Next(10000, 99999).ToString();
        var problems = new List<string>();

        var markA = "A-file " + m;
        var markB = "B-secret " + m;

        var (aId, aUrl) = await Step<(Guid, string)>("Workspace A has a lead with a PDF attached", async () =>
        {
            var lead = await ApiRecords.LeadAsync(apiA, tenantA, "Download A " + m, "dla" + m);
            var name = $"a-{m}.pdf";
            var r = await apiA.UploadAsync($"/api/leads/{lead}/attachments", name, Files.SmallPdf(markA), "application/pdf");
            Assert.That(r.IsSuccess, Is.True, $"Attaching A's PDF failed: {r}");
            var row = await Db.AttachmentAsync(lead, name) ?? throw new AssertionException("A's attachment row wasn't found.");
            return (row.Id, row.FileUrl);
        });

        var (bId, bUrl) = await Step<(Guid, string)>("…and so does workspace B", async () =>
        {
            var lead = await ApiRecords.LeadAsync(apiB, tenantB, "Download B " + m, "dlb" + m);
            var name = $"b-{m}.pdf";
            var r = await apiB.UploadAsync($"/api/leads/{lead}/attachments", name, Files.SmallPdf(markB), "application/pdf");
            Assert.That(r.IsSuccess, Is.True, $"Attaching B's PDF failed: {r}");
            var row = await Db.AttachmentAsync(lead, name) ?? throw new AssertionException("B's attachment row wasn't found.");
            return (row.Id, row.FileUrl);
        });

        await Step("Control: A downloads its own file", async () =>
        {
            var r = await apiA.GetAsync($"/api/attachments/{aId}/download");
            Assert.That(r.IsSuccess && Has(r.Bytes, markA), Is.True,
                $"A couldn't download its own file ({r}), so the refusals below would prove nothing.");
        });

        await Step("B asks for A's file by id — through the API and the website: not found", async () =>
        {
            var r = await apiB.GetAsync($"/api/attachments/{aId}/download");
            if (r.IsSuccess || Has(r.Bytes, markA)) problems.Add($"Workspace B downloaded A's attachment through the API: {r}");

            using var web = await WebSession.SignedInAsync(b.Admin);
            var w = await web.GetAsync($"/Attachments/Download/{aId}");
            if (w.Status == 200 || w.Body.Contains(markA, StringComparison.Ordinal))
                problems.Add($"Workspace B downloaded A's attachment through the website: {w}");
        });

        try
        {
            await Step("A row of A's that names a file in B's folder gives \"not found\", not B's file", async () =>
            {
                await Db.SetAttachmentFileUrlAsync(aId, bUrl);
                var r = await apiA.GetAsync($"/api/attachments/{aId}/download");
                if (Has(r.Bytes, markB)) problems.Add("A row of workspace A's pointing into B's folder handed over B's file.");
                else if (r.IsSuccess) problems.Add($"A row pointing into B's folder was answered {r} (expected not found).");
            });

            await Step("…and naming ../, absolute paths or a network share gives \"not found\" too", async () =>
            {
                var aFolder = $"/uploads/{tenantA:N}";
                var paths = new[]
                {
                    $"{aFolder}/../../appsettings.json",
                    $"{aFolder}/..\\..\\appsettings.json",
                    $"{aFolder}/%2e%2e/%2e%2e/appsettings.json",
                    $"{aFolder}/lead/../../../appsettings.Development.json",
                    "/uploads/../appsettings.json",
                    "../appsettings.json",
                    "appsettings.json",
                    @"C:\Windows\win.ini",
                    "/etc/passwd",
                    @"\\localhost\c$\Windows\win.ini",
                    $"/uploads/{tenantB:N}/../{tenantA:N}/x.pdf",
                };

                foreach (var p in paths)
                {
                    await Db.SetAttachmentFileUrlAsync(aId, p);
                    var r = await apiA.GetAsync($"/api/attachments/{aId}/download");
                    var leak = Leaks.FirstOrDefault(l => r.Body.Contains(l, StringComparison.Ordinal));
                    if (leak is not null) problems.Add($"A row naming '{p}' handed over a server file (it contains '{leak}').");
                    else if (r.IsSuccess) problems.Add($"A row naming '{p}' was answered {r} (expected not found).");
                    else if (r.Status >= 500) problems.Add($"A row naming '{p}' gave a server error: {r}");
                }
            });
        }
        finally
        {
            await Db.SetAttachmentFileUrlAsync(aId, aUrl);
        }

        await Step("Neither server hands out files by address: the uploads folder, settings, ../ and %2e%2e", async () =>
        {
            var addresses = new[]
            {
                aUrl,
                bUrl,
                "/appsettings.json",
                "/appsettings.Development.json",
                "/web.config",
                "/%2e%2e/appsettings.json",
                "/uploads/..%2Fappsettings.json",
                "/css/..%2F..%2Fappsettings.json",
                $"/api/attachments/..%2F..%2Fappsettings.json/download",
                $"/Attachments/Download/..%2F..%2Fappsettings.json",
            };

            using var web = await WebSession.SignedInAsync(a.Admin);
            foreach (var address in addresses)
            {
                var api = await apiA.GetAsync(address);
                Judge("The API", address, api.Status, api.Body, api.Bytes);

                var site = await web.GetAsync(address);
                Judge("The website", address, site.Status, site.Body, System.Text.Encoding.UTF8.GetBytes(site.Body));
            }

            void Judge(string who, string address, int status, string body, byte[] bytes)
            {
                var leak = Leaks.FirstOrDefault(l => body.Contains(l, StringComparison.Ordinal));
                if (leak is not null) problems.Add($"{who} served '{address}' — it contains '{leak}'.");
                else if (status == 200 && (Has(bytes, markA) || Has(bytes, markB)))
                    problems.Add($"{who} served an uploaded file straight from '{address}', past every check.");
                else if (status >= 500) problems.Add($"{who} answered '{address}' with a server error (HTTP {status}).");
            }
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    private static bool Has(byte[] bytes, string text)
        => bytes.Length > 0 && System.Text.Encoding.Latin1.GetString(bytes).Contains(text, StringComparison.Ordinal);
}
