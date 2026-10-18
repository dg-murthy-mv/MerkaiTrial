// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/LeadImportExportTests.cs
//
// NEW (092). Custom fields on Add lead, and a lead list that goes out to
// Excel and comes back in. Workspace D (India / INR), like the other
// custom field tests, so a required field cannot get in the way of the
// leads the rest of the suite adds — and every field made here is
// switched off again at the end, pass or fail.
//
//   1. Add lead asks for the workspace's custom fields:
//        • a required text field and a choice field are on the form, the
//          required one marked required;
//        • with the browser's check taken off, saving is refused by the
//          server, naming the field — and no lead is created;
//        • filled in, the lead is created and its page shows both values.
//   2. Export, then import back:
//        • a lead with a date, a choice and a tick box is exported from a
//          searched list — the file holds just that lead, its first
//          fourteen headings are the import's own names, and its custom
//          columns hold 2027-03-01, "Gold" and "Yes";
//        • the export's own headings, with two new people under them, are
//          imported: every column, custom ones included, is matched
//          without touching a drop-down;
//        • one row has readable values written as people write them
//          (13/04/2027, "silver", "yes"), the other unreadable ones
//          (31/31/2027, "Platinum", "maybe"); the Review step says what is
//          wrong with each unreadable value, and both leads import;
//        • the readable row's values are stored exactly (13 April 2027,
//          Silver, ticked) and the other lead has none.
//
// 095: every custom field box is found by the label that STARTS WITH its
// name (CustomFieldsUi.LabelStarting). "Tier …" also matched "Licence
// tier …", a field another test adds to the same workspace earlier in the
// run. (Exact matching was tried first and failed: a required field's
// label ends in " *".)
//
// 092b: a custom field's row on the mapping screen is found by the
// field's NAME CELL. The first run looked for a row containing the name
// anywhere — and every row does, because each row's drop-down lists
// every column of the file, including "Renewal …".
// =====================================================================

using System.Text;
using System.Text.RegularExpressions;
using MerkaiTrial.E2E.Infrastructure;
using static MerkaiTrial.E2E.Infrastructure.CustomFieldsUi;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("ImportExport")]
public class LeadImportExportTests : E2ETest
{
    private static TestTenant D => World.D ?? throw new InvalidOperationException("Workspace D was not built.");

    [OneTimeSetUp]
    public async Task BuildWorkspaceDAsync() => await WorldBuilder.EnsureWorkspaceDAsync();

    /// <summary>The headings Export writes first, in order — the import's own field names.</summary>
    private static readonly string[] StandardHeadings =
    {
        "Full name", "Email", "Phone", "Company", "Address", "Country", "Lead source",
        "Channel", "Status", "Score", "Estimated value", "Currency", "Owner", "Industry"
    };

    // =================================================================
    // 1. Custom fields on Add lead
    // =================================================================

    [Test]
    public async Task Add_lead_asks_for_custom_fields_and_the_server_checks_the_required_one()
    {
        var code = $"Site code {World.Stamp}";
        var segment = $"Segment {World.Stamp}";
        var admin = await SignedInAsync(D.Admin);
        var made = new List<string>();

        try
        {
            await Step("Add a required text field and a choice field to Leads", async () =>
            {
                await AddFieldAsync(admin, "Lead", new FieldSpec(code, "text") { Required = true });
                made.Add(code);
                await AddFieldAsync(admin, "Lead", new FieldSpec(segment, "dropdown") { Choices = new[] { "Retail", "Wholesale" } });
                made.Add(segment);
            });

            await Step("Add lead shows both, the required one marked required", async () =>
            {
                await admin.GotoAsync("/Leads/Create");
                await Assertions.Expect(admin.GetByLabel(LabelStarting(code))).ToBeVisibleAsync();
                await Assertions.Expect(admin.GetByLabel(LabelStarting(segment))).ToBeVisibleAsync();
                Assert.That(await admin.GetByLabel(LabelStarting(code)).EvaluateAsync<bool>("el => el.required"), Is.True,
                    $"\"{code}\" is required but its input on Add lead is not marked required.");
            });

            var refusedName = World.Unique("Nisha Nocode");
            await Step("Without it — the browser's check taken off — the server refuses and names the field", async () =>
            {
                string? refusal = null;
                try
                {
                    await Flows.CreateLeadAsync(admin, refusedName, World.EmailFor("D", "nocode" + Random.Shared.Next(1000, 9999)),
                        150000m, D, beforeSave: p =>
                            p.GetByLabel(LabelStarting(code)).EvaluateAsync<bool>("el => { el.removeAttribute('required'); return true; }"));
                }
                catch (AssertionException ex)
                {
                    refusal = ex.Message;
                }

                Assert.That(refusal, Is.Not.Null, $"Add lead saved \"{refusedName}\" with the required \"{code}\" empty.");
                Assert.That(refusal, Does.Contain($"{code} is required"),
                    $"Add lead was refused, but not because of \"{code}\": {refusal}");

                var tenantId = await Db.TenantIdAsync(D.Name);
                Assert.That(await Db.CountLeadsAsync(tenantId, refusedName), Is.EqualTo(0),
                    "The refused lead was saved anyway.");
            });

            var leadName = World.Unique("Nisha Fields");
            var leadId = await Step("Filled in, the lead is created", () =>
                Flows.CreateLeadAsync(admin, leadName, World.EmailFor("D", "fields" + Random.Shared.Next(1000, 9999)),
                    150000m, D, beforeSave: async p =>
                    {
                        await p.GetByLabel(LabelStarting(code)).FillAsync("SC-0921");
                        await Ui.SelectByTextAsync(p.GetByLabel(LabelStarting(segment)), "Wholesale");
                    }));

            await Step("Its page shows both values", async () =>
            {
                await admin.GotoAsync($"/Leads/Detail/{leadId}");
                await ExpectDetailValueAsync(admin, code, "SC-0921");
                await ExpectDetailValueAsync(admin, segment, "Wholesale");
            });
        }
        finally
        {
            // The required one first: left on, it would stop every later
            // Add lead in workspace D.
            await SwitchOffQuietlyAsync(admin, "Lead", made);
        }
    }

    // =================================================================
    // 2. Export, and import back
    // =================================================================

    [Test]
    public async Task Leads_export_to_Excel_and_the_file_imports_back_with_custom_fields()
    {
        var renewal = $"Renewal {World.Stamp}";
        var tier = $"Tier {World.Stamp}";
        var gst = $"GST registered {World.Stamp}";
        var admin = await SignedInAsync(D.Admin);
        var made = new List<string>();

        // One word every lead in this test carries, so a search finds them all and nothing else.
        var tag = $"RT{Random.Shared.Next(100000, 999999)}";
        var original = $"Meera {tag}";
        var okName = $"Imported Ok {tag}";
        var oddName = $"Imported Odd {tag}";

        try
        {
            await Step("Add a date, a choice and a tick box field to Leads", async () =>
            {
                await AddFieldAsync(admin, "Lead", new FieldSpec(renewal, "date"));
                made.Add(renewal);
                await AddFieldAsync(admin, "Lead", new FieldSpec(tier, "dropdown") { Choices = new[] { "Gold", "Silver" } });
                made.Add(tier);
                await AddFieldAsync(admin, "Lead", new FieldSpec(gst, "checkbox"));
                made.Add(gst);
            });

            await Step("Add a lead with all three filled in", () =>
                Flows.CreateLeadAsync(admin, original, World.EmailFor("D", "meera" + Random.Shared.Next(1000, 9999)),
                    800000m, D, beforeSave: async p =>
                    {
                        await p.GetByLabel(LabelStarting(renewal)).FillAsync("2027-03-01");
                        await Ui.SelectByTextAsync(p.GetByLabel(LabelStarting(tier)), "Gold");
                        await p.GetByLabel(LabelStarting(gst)).CheckAsync();
                    }));

            var rows = await Step("Export the searched list", async () =>
            {
                await admin.GotoAsync($"/Leads?StatusFilter=all&SearchTerm={Uri.EscapeDataString(tag)}");
                var button = admin.Locator("#leadsExport");
                await Assertions.Expect(button).ToBeVisibleAsync();

                var download = await admin.RunAndWaitForDownloadAsync(() => button.ClickAsync(),
                    new PageRunAndWaitForDownloadOptions { Timeout = E2ESettings.ActionTimeoutMs });
                Assert.That(download.SuggestedFilename, Does.EndWith(".xlsx"),
                    $"The export downloaded as \"{download.SuggestedFilename}\", not an Excel file.");

                var path = await download.PathAsync();
                return Xlsx.ReadFirstSheet(await File.ReadAllBytesAsync(path));
            });

            var header = rows[0];
            int Col(string heading)
            {
                var i = header.IndexOf(heading);
                if (i < 0) throw new AssertionException($"The export has no \"{heading}\" column. Headings: {string.Join(" | ", header)}");
                return i;
            }

            await Step("The file holds just that lead, under the import's own headings", () =>
            {
                Assert.That(header.Take(StandardHeadings.Length), Is.EqualTo(StandardHeadings),
                    $"The export's first headings are not the import's field names: {string.Join(" | ", header)}");

                var data = rows.Skip(1).Where(r => r.Any(c => c.Length > 0)).ToList();
                Assert.That(data.Select(r => r[Col("Full name")]), Is.EqualTo(new[] { original }),
                    "The export of a list searched for one lead should hold exactly that lead.");
                return Task.CompletedTask;
            });

            await Step("Its custom columns hold 2027-03-01, Gold and Yes", () =>
            {
                var row = rows[1];
                Assert.That(Xlsx.DateFromSerial(row[Col(renewal)]) ?? TryIso(row[Col(renewal)]),
                    Is.EqualTo(new DateOnly(2027, 3, 1)), $"\"{renewal}\" was exported as \"{row[Col(renewal)]}\".");
                Assert.That(row[Col(tier)], Is.EqualTo("Gold"), $"\"{tier}\" should be exported by the choice's name.");
                Assert.That(row[Col(gst)], Is.EqualTo("Yes"), $"A ticked \"{gst}\" should be exported as Yes.");
                return Task.CompletedTask;
            });

            // Two new people under the export's own headings. Phone left
            // blank: every workspace D lead shares the sample number, and
            // the importer would rightly call them duplicates.
            string[] NewRow(string name, string email, string date, string choice, string tick)
            {
                var r = Enumerable.Repeat("", header.Count).ToArray();
                for (var i = 0; i < header.Count; i++) r[i] = rows[1][i];
                r[Col("Full name")] = name;
                r[Col("Email")] = email;
                r[Col("Phone")] = "";
                r[Col(renewal)] = date;
                r[Col(tier)] = choice;
                r[Col(gst)] = tick;
                return r;
            }

            var csv = Xlsx.ToCsv(new IReadOnlyList<string>[]
            {
                header,
                NewRow(okName, World.EmailFor("D", "rtok" + Random.Shared.Next(1000, 9999)), "13/04/2027", "silver", "yes"),
                NewRow(oddName, World.EmailFor("D", "rtodd" + Random.Shared.Next(1000, 9999)), "31/31/2027", "Platinum", "maybe"),
            });

            await Step("Upload it on Import leads", async () =>
            {
                await admin.GotoAsync("/Leads/Import");
                await admin.Locator("#File").SetInputFilesAsync(new FilePayload
                {
                    Name = $"leads-roundtrip-{tag}.csv",
                    MimeType = "text/csv",
                    Buffer = Encoding.UTF8.GetBytes(csv)
                });
                await admin.GetByRole(AriaRole.Button, new() { Name = "Upload and continue" }).ClickAsync();
                await Assertions.Expect(admin.Locator("#importMapping")).ToBeVisibleAsync();
            });

            await Step("Every column is matched already, the custom ones too", async () =>
            {
                var hint = await MappingRow(admin, renewal).Locator("td").First.InnerTextAsync();
                Assert.That(hint, Does.Contain("16/10/2026"),
                    "This test writes dates day-first (13/04/2027), which needs workspace D's date format to be " +
                    $"dd/MM/yyyy. The mapping screen's hint for the date field says: \"{hint.Trim()}\"");

                var wrong = new List<string>();
                async Task Check(ILocator select, string expected)
                {
                    var chosen = await select.EvaluateAsync<string>("el => el.options[el.selectedIndex]?.text?.trim() || ''");
                    if (chosen != expected) wrong.Add($"{expected} → \"{chosen}\"");
                }

                foreach (var key in new[] { "fullName", "email", "phone", "companyName", "address", "country", "source",
                                            "channel", "status", "score", "estimatedValue", "currency", "owner", "vertical" })
                {
                    var expected = StandardHeadings[Array.IndexOf(new[] { "fullName", "email", "phone", "companyName", "address",
                        "country", "source", "channel", "status", "score", "estimatedValue", "currency", "owner", "vertical" }, key)];
                    await Check(admin.Locator($"select[name='Mapping[{key}]']"), expected);
                }

                foreach (var label in new[] { renewal, tier, gst })
                    await Check(MappingRow(admin, label).Locator("select"), label);

                Assert.That(wrong, Is.Empty, "Columns the mapper did not match to themselves: " + string.Join("; ", wrong));
            });

            await Step("Review: both will be imported, and the odd row says what is wrong with each value", async () =>
            {
                await admin.GetByRole(AriaRole.Button, new() { Name = "Check the file" }).ClickAsync();
                var odd = admin.Locator("tbody tr", new() { HasText = oddName });
                await Assertions.Expect(odd).ToHaveCountAsync(1);

                var said = await odd.InnerTextAsync();
                foreach (var expected in new[] { "isn't a date", "isn't one of the choices", "isn't Yes or No" })
                    Assert.That(said, Does.Contain(expected), $"The odd row's notes should say \"{expected}\". They say: {said}");

                await Assertions.Expect(admin.GetByRole(AriaRole.Button, new() { NameRegex = new("Import 2 lead") })).ToBeEnabledAsync();
            });

            await Step("Import them", async () =>
            {
                await admin.GetByRole(AriaRole.Button, new() { NameRegex = new("Import 2 lead") }).ClickAsync();
                await Ui.ExpectSuccessAsync(admin, "Imported 2 lead");
            });

            var tenantId = await Db.TenantIdAsync(D.Name);

            await Step("The readable row's values are stored exactly; the odd lead has none", async () =>
            {
                var okId = await Db.LeadIdByNameAsync(tenantId, okName)
                           ?? throw new AssertionException($"\"{okName}\" was not imported.");
                var oddId = await Db.LeadIdByNameAsync(tenantId, oddName)
                            ?? throw new AssertionException($"\"{oddName}\" was not imported.");

                var ok = await Db.CustomValuesAsync(okId);
                Assert.That(ok.TryGetValue(renewal, out var r) ? r.Date : null, Is.EqualTo(new DateOnly(2027, 4, 13)),
                    "13/04/2027 should be stored as 13 April 2027 (the workspace writes the day first).");
                Assert.That(ok.TryGetValue(tier, out var t) ? t.Text : null, Is.Not.Null.And.Not.Empty,
                    $"\"silver\" should have matched the choice Silver on \"{tier}\".");
                Assert.That(ok.TryGetValue(gst, out var g) ? g.Flag : null, Is.True,
                    $"\"yes\" should have ticked \"{gst}\".");

                var odd = await Db.CustomValuesAsync(oddId);
                Assert.That(odd.Keys.Where(k => k == renewal || k == tier || k == gst), Is.Empty,
                    $"Unreadable values were stored on \"{oddName}\": {string.Join(", ", odd.Keys)}");

                await admin.GotoAsync($"/Leads/Detail/{okId}");
                await ExpectDetailValueAsync(admin, tier, "Silver");
            });
        }
        finally
        {
            await SwitchOffQuietlyAsync(admin, "Lead", made);
        }
    }

    /// <summary>
    /// 092b. A custom field's row on "Match your columns", by the field's
    /// name in the first cell — exactly, not "contains": every row's
    /// drop-down lists every column heading, so a contains-match on the
    /// row finds them all.
    /// </summary>
    private static ILocator MappingRow(IPage page, string label) =>
        page.Locator("tr[data-field^='cf_']", new()
        {
            Has = page.Locator("td:first-child span.fw-semibold",
                new() { HasTextRegex = new Regex("^" + Regex.Escape(label) + "$") })
        });

    private static DateOnly? TryIso(string text)
        => DateOnly.TryParseExact(text, "yyyy-MM-dd", out var d) ? d : null;
}
