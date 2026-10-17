// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/OwnVisibilityTests.cs
//
// NEW (092). Record visibility "Own", in workspace C (Thailand), for the
// two places it is easiest to forget: the Export button and attachments.
// Each test sets the rep role to Own for what it needs and puts every
// setting back afterwards, pass or fail.
//
//   1. Export: Ratt (rep) and Ploy (rep 2) each add a lead. Ratt's export
//      of a list searched for both holds only Ratt's lead; the admin's
//      export of the same list holds both — so the file is narrowed by
//      who asks, not merely empty.
//   2. Attachments: the admin puts a PDF on a deal and on a draft quote for
//      it. Ratt, with Own visibility for leads and deals (and quotes, when
//      the grid has a column for them):
//        • does not see either file on the deal's or the quote's page, and
//          their download links give him nothing;
//        • cannot add a file to either — a hand-made upload, with his own
//          anti-forgery token, does not put anything on them;
//        • cannot delete either file — a hand-made delete leaves both
//          listed for the admin.
//      The hand-made requests are checked to have reached the page (not
//      stopped at the anti-forgery check), so a pass means the server
//      refused them, not that the request was malformed.
// =====================================================================

using System.Text;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Visibility")]
public class OwnVisibilityTests : E2ETest
{
    private static TestTenant C => World.C ?? throw new InvalidOperationException("Workspace C was not built.");

    [OneTimeSetUp]
    public async Task BuildWorkspaceCAsync() => await WorldBuilder.EnsureWorkspaceCAsync();

    // =================================================================
    // 1. Export
    // =================================================================

    [Test]
    public async Task A_rep_with_Own_visibility_exports_only_their_own_leads()
    {
        var repRole = C.RoleOf["rep"];
        var admin = await SignedInAsync(C.Admin);

        var original = await Step($"Set \"{repRole}\" to Own for leads", () =>
            RecordVisibility.SetAsync(admin, repRole, "Leads", "Own"));

        try
        {
            var tag = $"EX{Random.Shared.Next(100000, 999999)}";
            var ploysLead = $"Ploy Export {tag}";
            var rattsLead = $"Ratt Export {tag}";

            await Step("Ploy adds a lead", async () =>
            {
                var ploy = await SignedInAsync(C["rep2"]);
                await Flows.CreateLeadAsync(ploy, ploysLead, World.EmailFor("C", "pexp" + Random.Shared.Next(1000, 9999)), 90000m, C);
            });

            var ratt = await SignedInAsync(C["rep"]);
            await Step("Ratt adds a lead", () =>
                Flows.CreateLeadAsync(ratt, rattsLead, World.EmailFor("C", "rexp" + Random.Shared.Next(1000, 9999)), 70000m, C));

            await Step("Ratt's export holds his lead and not Ploy's", async () =>
            {
                var names = await ExportNamesAsync(ratt, tag);
                Assert.That(names, Does.Contain(rattsLead), "Ratt's own lead is missing from his export.");
                Assert.That(names, Does.Not.Contain(ploysLead),
                    "Ratt's export contains Ploy's lead, although Ratt can only see his own leads.");
            });

            await Step("The admin's export of the same list holds both", async () =>
            {
                var names = await ExportNamesAsync(admin, tag);
                Assert.That(names, Does.Contain(rattsLead).And.Contain(ploysLead),
                    $"The admin's export should hold both leads. It holds: {string.Join(", ", names)}");
            });
        }
        finally
        {
            await PutBackAsync(admin, repRole, "Leads", original);
        }
    }

    // =================================================================
    // 2. Attachments
    // =================================================================

    [Test]
    public async Task A_rep_with_Own_visibility_cannot_see_add_or_delete_a_colleagues_attachments()
    {
        var repRole = C.RoleOf["rep"];
        var admin = await SignedInAsync(C.Admin);
        var restore = new List<(string Module, string Scope)>();

        try
        {
            await Step($"Set \"{repRole}\" to Own for leads, deals and (if listed) quotes", async () =>
            {
                restore.Add(("Leads", await RecordVisibility.SetAsync(admin, repRole, "Leads", "Own")));
                restore.Add(("Deals", await RecordVisibility.SetAsync(admin, repRole, "Deals", "Own")));
                var quotes = await RecordVisibility.TrySetAsync(admin, repRole, "Quotes", "Own");
                if (quotes is not null) restore.Add(("Quotes", quotes));
            });

            var tag = $"AT{Random.Shared.Next(100000, 999999)}";
            var dealTitle = $"Somsak project {tag}";

            var dealId = await Step("The admin makes a deal", async () =>
            {
                await Flows.CreateLeadAsync(admin, $"Somsak {tag}", World.EmailFor("C", "att" + Random.Shared.Next(1000, 9999)), 150000m, C);
                await Flows.QualifyLeadAsync(admin);
                await Flows.ConvertLeadAsync(admin, dealTitle, 150000m);
                return await Db.DealIdByTitleAsync(await Db.TenantIdAsync(C.Name), dealTitle);
            });

            var dealPdf = $"deal-plan-{tag}.pdf";
            var pdfBytes = SmallPdf($"Plan {tag}");
            var dealFileUrl = await Step("…and puts a PDF on it", async () =>
            {
                await admin.GotoAsync($"/Pipeline/Detail/{dealId}");
                await admin.Locator("#attachments-tab").ClickAsync();
                await admin.Locator("#attachmentFile").SetInputFilesAsync(new FilePayload { Name = dealPdf, MimeType = "application/pdf", Buffer = pdfBytes });
                await admin.Locator("#attachments form[enctype='multipart/form-data'] button[type=submit]").ClickAsync();
                await Ui.ExpectSuccessAsync(admin, "uploaded successfully");
                return await DownloadLinkAsync(admin, $"/Pipeline/Detail/{dealId}", DealFile(admin, dealPdf));
            });

            var quotePdf = $"quote-spec-{tag}.pdf";
            var quoteUrl = await Step("The admin saves a draft quote for the deal", async () =>
            {
                await Flows.FillQuoteAsync(admin, dealTitle, new QuoteLine("Showroom survey", 1, 30000m, TaxRate: 7));
                await Flows.SubmitQuoteAsync(admin, send: false);
                return admin.Url;
            });

            var quoteFileUrl = await Step("…and puts a PDF on it", async () =>
            {
                await admin.GotoAsync(quoteUrl);
                var form = admin.Locator("form[enctype='multipart/form-data']:has(input[type=file][name=file])");
                await form.Locator("input[type=file]").SetInputFilesAsync(new FilePayload { Name = quotePdf, MimeType = "application/pdf", Buffer = pdfBytes });
                await form.Locator("button[type=submit]").ClickAsync();
                await Ui.ExpectSuccessAsync(admin, "uploaded successfully");
                return await DownloadLinkAsync(admin, quoteUrl, QuoteFile(admin, quotePdf));
            });

            var ratt = await SignedInAsync(C["rep"]);

            await Step("Ratt does not see either file, on the deal's page or the quote's", async () =>
            {
                await ratt.GotoAsync($"/Pipeline/Detail/{dealId}");
                await Assertions.Expect(ratt.GetByText(dealPdf)).ToHaveCountAsync(0);
                await ratt.GotoAsync(quoteUrl);
                await Assertions.Expect(ratt.GetByText(quotePdf)).ToHaveCountAsync(0);
            });

            await Step("…and their download links give him nothing", async () =>
            {
                foreach (var url in new[] { dealFileUrl, quoteFileUrl })
                {
                    var got = await ratt.APIRequest.GetAsync(url);
                    var body = await got.BodyAsync();
                    Assert.That(body.SequenceEqual(pdfBytes), Is.False,
                        $"Ratt downloaded a file from a deal he cannot see ({Ui.PathOf(url)}, HTTP {got.Status}).");
                }
            });

            var token = await Step("Ratt's own anti-forgery token, from a page he can use", () => TokenAsync(ratt));

            await Step("A hand-made upload by Ratt puts nothing on the deal or the quote", async () =>
            {
                var sneaky = $"sneaky-{tag}.pdf";
                await PostByHandAsync(ratt, WithHandler($"/Pipeline/Detail/{dealId}", "UploadAttachment"), token,
                    form => form.Set("file", new FilePayload { Name = sneaky, MimeType = "application/pdf", Buffer = pdfBytes }));
                await PostByHandAsync(ratt, WithHandler(quoteUrl, "UploadAttachment"), token,
                    form => form.Set("file", new FilePayload { Name = sneaky, MimeType = "application/pdf", Buffer = pdfBytes }));

                await admin.GotoAsync($"/Pipeline/Detail/{dealId}");
                await Assertions.Expect(admin.GetByText(sneaky)).ToHaveCountAsync(0);
                await admin.GotoAsync(quoteUrl);
                await Assertions.Expect(admin.GetByText(sneaky)).ToHaveCountAsync(0);
            });

            await Step("A hand-made delete by Ratt leaves both files listed for the admin", async () =>
            {
                await PostByHandAsync(ratt, WithHandler($"/Pipeline/Detail/{dealId}", "DeleteAttachment"), token,
                    form => form.Set("attachmentId", Ui.IdFromUrl(dealFileUrl).ToString()));
                await PostByHandAsync(ratt, WithHandler(quoteUrl, "DeleteAttachment"), token,
                    form => form.Set("attachmentId", Ui.IdFromUrl(quoteFileUrl).ToString()));

                await admin.GotoAsync($"/Pipeline/Detail/{dealId}");
                await admin.Locator("#attachments-tab").ClickAsync();
                await Assertions.Expect(DealFile(admin, dealPdf)).ToHaveCountAsync(1);

                await admin.GotoAsync(quoteUrl);
                await Assertions.Expect(QuoteFile(admin, quotePdf)).ToHaveCountAsync(1);
            });
        }
        finally
        {
            foreach (var (module, scope) in restore)
                await PutBackAsync(admin, repRole, module, scope);
        }
    }

    // =================================================================
    // Helpers
    // =================================================================

    /// <summary>Searches the leads list (All tab) and exports it; returns the Full name column.</summary>
    private static async Task<List<string>> ExportNamesAsync(IPage page, string search)
    {
        await page.GotoAsync($"/Leads?StatusFilter=all&SearchTerm={Uri.EscapeDataString(search)}");
        var button = page.Locator("#leadsExport");
        await Assertions.Expect(button).ToBeVisibleAsync();

        var download = await page.RunAndWaitForDownloadAsync(() => button.ClickAsync(),
            new PageRunAndWaitForDownloadOptions { Timeout = E2ESettings.ActionTimeoutMs });
        var rows = Xlsx.ReadFirstSheet(await File.ReadAllBytesAsync(await download.PathAsync()));

        var col = rows[0].IndexOf("Full name");
        if (col < 0) throw new AssertionException($"The export has no \"Full name\" column: {string.Join(" | ", rows[0])}");
        return rows.Skip(1).Select(r => r[col]).Where(n => n.Length > 0).ToList();
    }

    private static ILocator DealFile(IPage page, string name) =>
        page.Locator("#attachments .list-group-item", new() { HasText = name });

    private static ILocator QuoteFile(IPage page, string name) =>
        page.Locator(".qd-att", new() { HasText = name });

    /// <summary>The download link of a listed file, made absolute.</summary>
    private static async Task<string> DownloadLinkAsync(IPage page, string pageUrl, ILocator item)
    {
        await page.GotoAsync(pageUrl);
        if (pageUrl.Contains("/Pipeline/", StringComparison.OrdinalIgnoreCase))
            await page.Locator("#attachments-tab").ClickAsync();

        await Assertions.Expect(item).ToHaveCountAsync(1);
        var hrefs = await item.Locator("a[href]").EvaluateAllAsync<string[]>("els => els.map(a => a.getAttribute('href'))");
        var href = hrefs.FirstOrDefault(h => h is not null && h.Contains("Download", StringComparison.OrdinalIgnoreCase))
                   ?? hrefs.FirstOrDefault(h => h is not null && System.Text.RegularExpressions.Regex.IsMatch(h, "[0-9a-fA-F-]{36}"));
        if (string.IsNullOrEmpty(href))
            throw new AssertionException($"The file on {Ui.PathOf(pageUrl)} has no download link.");

        return Uri.TryCreate(href, UriKind.Absolute, out _) ? href : E2ESettings.BaseUrl + (href.StartsWith('/') ? href : "/" + href);
    }

    /// <summary>A page's address with ?handler= added, keeping any query it already has.</summary>
    private static string WithHandler(string url, string handler) =>
        url + (url.Contains('?') ? "&" : "?") + "handler=" + handler;

    /// <summary>The anti-forgery token on Add lead — a form every rep can open.</summary>
    private static async Task<string> TokenAsync(IPage page)
    {
        await page.GotoAsync("/Leads/Create");
        var token = await page.Locator("input[name=__RequestVerificationToken]").First.GetAttributeAsync("value");
        if (string.IsNullOrEmpty(token))
            throw new AssertionException("Add lead has no anti-forgery token to borrow.");
        return token;
    }

    /// <summary>
    /// POSTs a multipart form with this person's cookies and token, without
    /// following the redirect. Fails if the request was stopped before the
    /// page saw it (400 = anti-forgery), because then a "nothing changed"
    /// afterwards would prove nothing.
    /// </summary>
    private static async Task PostByHandAsync(IPage page, string url, string token, Action<IFormData> fill)
    {
        var form = page.APIRequest.CreateFormData();
        form.Set("__RequestVerificationToken", token);
        fill(form);

        var response = await page.APIRequest.PostAsync(url, new APIRequestContextOptions { Multipart = form, MaxRedirects = 0 });
        TestContext.Progress.WriteLine($"[e2e] Hand-made POST {Ui.PathOf(url)} → HTTP {response.Status}");

        Assert.That(response.Status, Is.Not.EqualTo(400),
            $"The hand-made request to {Ui.PathOf(url)} was turned away before reaching the page (HTTP 400 — " +
            "anti-forgery?), so it cannot show whether the server protects the file.");
    }

    private static async Task PutBackAsync(IPage admin, string role, string module, string scope)
    {
        try
        {
            await RecordVisibility.SetAsync(admin, role, module, scope);
        }
        catch (Exception ex)
        {
            TestContext.Progress.WriteLine($"[e2e] Could not put {role} back to {scope} for {module}: {ex.Message.Split('\n')[0]}");
        }
    }

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
