// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/XssTests.cs
//
// VUL-02 and VUL-03 (round 104) — script in what people type.
//
//   VUL-02  Script_typed_into_records_is_shown_as_text_and_never_runs
//   VUL-03  Script_in_custom_field_labels_options_and_values_is_encoded
//
// Every payload, if the page ever lets it run, sets window.__x — so after
// each page loads the test simply asks the page whether window.__x exists.
// The payloads cover the four ways out of the place a value is written:
//
//   <img src=x onerror=…>          HTML (as innerHTML or unencoded Razor)
//   "><svg onload=…>               out of an attribute value
//   ');window.__x=…;('            out of a JavaScript string in onclick=
//   </script><script>…</script>    out of a <script> block
//
// And where the payload should be visible (the lead's own page, the
// custom field's label) the test checks it is SHOWN, as text.
//
// Uses workspace E (built by WorldBuilder.EnsureWorkspaceEAsync), so the
// odd-looking names never sit in the lists other tests read.
//
// Round 104 found and fixed real holes here:
//   • the super admin's View As person list and the Tenant page's people
//     table wrote names straight into HTML — a workspace admin could name
//     a person <img src=x onerror=…> and run script AS THE SUPER ADMIN;
//   • Settings → Sales process wrote people's names into its "give it to"
//     list the same way;
//   • the Verticals preview did too.
// =====================================================================

using System.Text.RegularExpressions;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class XssTests : E2ETest
{
    /// <summary>All four escapes in one string. <paramref name="n"/> numbers it, so a hit says which one ran.</summary>
    private static string Payload(string tag, int n) =>
        $"{tag}<img src=x onerror=window.__x={n}>\"><svg onload=window.__x={n + 1}>');window.__x={n + 2};('</script><script>window.__x={n + 3}</script>";

    // ================================================================
    // VUL-02
    // ================================================================

    [Test]
    public async Task Script_typed_into_records_is_shown_as_text_and_never_runs()
    {
        var e = await Step("Workspace E — built the first time it is needed", () => WorldBuilder.EnsureWorkspaceEAsync());
        var tenantId = await Db.TenantIdAsync(e.Name);
        var admin = await Api.AsAsync(e.Admin);
        await Api.EnsureTokensWorkAsync(admin);
        var page = await SignedInAsync(e.Admin);
        var m = Random.Shared.Next(10000, 99999).ToString();
        var tag = "XSS" + m;
        var repId = await Db.UserIdAsync(e["rep"].Email);

        // ── the records ─────────────────────────────────────────────
        var leadId = await Step("A lead whose name, company and address are all script, given to the rep", async () =>
        {
            var r = await admin.PostAsync("/api/leads", new
            {
                tenantId, fullName = Payload(tag, 1), email = World.EmailFor("E", "xss" + m),
                phone = "+9198" + m + "456", companyName = Payload(tag, 11), address = Payload(tag, 21),
                estimatedValue = 1000m, currency = "INR", ownerUserId = repId.ToString(),
            });
            Assert.That(r.IsSuccess, Is.True, $"Adding the lead failed: {r}");
            return r.Json().GetProperty("id").GetGuid();
        });

        await Step("A note and a task on it, both script", async () =>
        {
            var note = await admin.PostAsync($"/api/leads/{leadId}/notes", new { tenantId, leadId, note = Payload(tag, 31) });
            Assert.That(note.IsSuccess, Is.True, $"Adding the note failed: {note}");
            var task = await admin.PostAsync("/api/activities", new
            {
                entityType = "Lead", entityId = leadId, activityType = "Task", subject = Payload(tag, 41),
                description = Payload(tag, 51), isTask = true, dueDate = DateTime.UtcNow.Date.AddDays(3),
            });
            Assert.That(task.IsSuccess, Is.True, $"Adding the task failed: {task}");
        });

        var dealTitle = Payload(tag + "D", 61);
        var (_, dealId) = await Step("A deal (by converting a lead) whose title is script", () =>
            Flows.LeadToDealAsync(page, e, tag + " Convert", World.EmailFor("E", "xssconv" + m), dealTitle, 5000m, Payload(tag + "C", 71)));

        var quoteId = await Step("A quote on it, sent, whose line is script", () =>
            Flows.SendQuoteAsync(page, dealTitle, new QuoteLine(Payload(tag + "Q", 81), 1, 5000m)));

        // ── the pages ───────────────────────────────────────────────
        var problems = new List<string>();

        await Step("The admin's pages: lists, the lead, the deal, the quote, tasks, the board", async () =>
        {
            foreach (var path in new[]
            {
                $"/Leads?SearchTerm={tag}", $"/Leads/Detail/{leadId}", $"/Leads/Edit/{leadId}",
                "/Pipeline", $"/Pipeline/Detail/{dealId}", $"/Pipeline/Edit/{dealId}",
                "/Quotes", $"/Quotes/Detail?id={quoteId}", "/Tasks", "/Dashboard",
                $"/Contacts?SearchTerm={tag}", $"/Companies?SearchTerm={tag}",
            })
                await VisitAsync(page, path, problems);
        });

        await Step("The lead's page SHOWS the script as text", async () =>
        {
            await page.GotoAsync($"/Leads/Detail/{leadId}");
            var text = await page.Locator("body").InnerTextAsync();
            Assert.That(text, Does.Contain($"{tag}<img src=x onerror=window.__x=1>"),
                "The lead's name is not shown as typed — it was dropped or turned into markup.");
        });

        await Step("The rep (the lead's owner): the bell and the notifications page", async () =>
        {
            var rep = await SignedInAsync(e["rep"]);
            foreach (var path in new[] { "/Dashboard", "/Notifications", $"/Leads/Detail/{leadId}", "/Tasks" })
                await VisitAsync(rep, path, problems);
        });

        await Step("The customer's quote page", async () =>
        {
            await page.GotoAsync($"/Quotes/Detail?id={quoteId}");
            var link = await Flows.CustomerLinkAsync(page);
            var customer = await NewPageAsync("customer");
            await VisitAsync(customer, Ui.Rebase(link), problems);
        });

        // ── people's names, where the super admin reads them ─────────
        var person = await Step("A person in workspace E whose last name, job title and department are script", async () =>
        {
            var p = await WorldBuilder.AddPersonForTestAsync(e, "xssname" + m, "Xena", Ui.RolePatterns.Rep);
            var id = await Db.UserIdAsync(p.Email);
            var r = await admin.PutAsync($"/api/users/{id}", new
            {
                tenantId, userId = id, firstName = "Xena", lastName = "</option></select><img src=x onerror=window.__x=91>",
                email = p.Email, jobTitle = "\"><svg onload=window.__x=92>", department = "');window.__x=93;('",
                isActive = true, isTenantAdmin = false,
            });
            Assert.That(r.IsSuccess, Is.True, $"Renaming the person failed: {r}");
            return (p, id);
        });

        try
        {
            await Step("Workspace E's admin: the people list, a lead's owner list, and Sales process's \"give it to\" list", async () =>
            {
                foreach (var path in new[] { "/Settings/Users", "/Leads/Create", $"/Leads/Detail/{leadId}" })
                    await VisitAsync(page, path, problems);

                await page.GotoAsync("/Settings/PipelineRules");
                await Ui.WaitForDocumentAsync(page);
                var cell = page.Locator(".cell-btn").First;
                if (await cell.CountAsync() == 0)
                {
                    problems.Add("Settings → Sales process has no step to open, so its \"give it to\" list wasn't checked.");
                    return;
                }
                await cell.ClickAsync();
                await page.Locator("#pAddAction").ClickAsync();
                await page.Locator("#pActions [data-f=AssignTo]").Last.SelectOptionAsync("2");
                var who = page.Locator("#pActions [data-f=AssignToUserId]").Last;
                await Assertions.Expect(who).ToBeVisibleAsync();
                await page.WaitForTimeoutAsync(400);
                await ExpectNotRunAsync(page, "Settings → Sales process, \"give it to\" a specific person", problems);
                var options = string.Join(" | ", await who.Locator("option").AllInnerTextsAsync());
                if (!options.Contains("<img src=x onerror=window.__x=91>", StringComparison.Ordinal))
                    problems.Add($"Settings → Sales process doesn't show the person's name as typed. Its list: {options}");
            });

            await Step("The super admin: the workspace's people table, and the View As person list", async () =>
            {
                var super = await SignedInAsync(World.SuperAdmin);

                await super.GotoAsync($"/Admin/Tenants/Detail/{tenantId}");
                await Ui.WaitForDocumentAsync(super);
                var tab = super.Locator("#users-tab");
                if (await tab.CountAsync() > 0)
                {
                    await tab.ClickAsync();
                    await Assertions.Expect(super.Locator("#usersTableBody")).ToContainTextAsync("Xena",
                        new() { Timeout = E2ESettings.ActionTimeoutMs });
                }
                await super.WaitForTimeoutAsync(400);
                await ExpectNotRunAsync(super, "Admin → Tenants → the workspace's People", problems);

                await super.GotoAsync("/Admin/ViewAs");
                await Ui.WaitForDocumentAsync(super);
                await super.EvaluateAsync($"() => loadUsers('{tenantId}')");
                await Assertions.Expect(super.Locator("#userMenu")).ToContainTextAsync("Xena",
                    new() { Timeout = E2ESettings.ActionTimeoutMs });
                await super.WaitForTimeoutAsync(400);
                await ExpectNotRunAsync(super, "Admin → View As, the person list", problems);
            });
        }
        finally
        {
            // Gone again, so nobody else's list shows them.
            await admin.DeleteAsync($"/api/users/{person.id}?tenantId={tenantId}");
            e.People.Remove(person.p.Key);
            e.RoleOf.Remove(person.p.Key);
        }

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    // ================================================================
    // VUL-03
    // ================================================================

    [Test]
    public async Task Script_in_custom_field_labels_options_and_values_is_encoded()
    {
        var e = await Step("Workspace E — built the first time it is needed", () => WorldBuilder.EnsureWorkspaceEAsync());
        var tenantId = await Db.TenantIdAsync(e.Name);
        var admin = await Api.AsAsync(e.Admin);
        await Api.EnsureTokensWorkAsync(admin);
        var page = await SignedInAsync(e.Admin);
        var m = Random.Shared.Next(10000, 99999).ToString();

        var textLabel = $"CF{m}<img src=x onerror=window.__x=31>";
        var optionLabel = "<img src=x onerror=window.__x=33>";
        var created = new List<Guid>();

        try
        {
            var textField = await Step("A lead text field whose label and help text are script", () =>
                CreateFieldAsync(admin, new
                {
                    entityType = "Lead", label = textLabel, fieldType = "text",
                    helpText = "\"><svg onload=window.__x=32>", isRequired = false, isActive = true, showInList = true,
                }, created));

            var dropField = await Step("A lead dropdown field whose option is script", () =>
                CreateFieldAsync(admin, new
                {
                    entityType = "Lead", label = "Pick" + m, fieldType = "dropdown", isRequired = false, isActive = true,
                    showInList = true, options = new[] { new { key = "", label = optionLabel }, new { key = "", label = "Safe" } },
                }, created));

            var leadId = await Step("A lead with script as its text value and the script option chosen", async () =>
            {
                var defs = (await admin.GetAsync("/api/custom-fields?entityType=Lead")).Json();
                var key = defs.EnumerateArray()
                    .Where(d => d.GetProperty("id").GetGuid() == dropField)
                    .SelectMany(d => d.GetProperty("options").EnumerateArray())
                    .Where(o => o.GetProperty("label").GetString() == optionLabel)
                    .Select(o => o.GetProperty("key").GetString())
                    .FirstOrDefault() ?? throw new AssertionException("The dropdown's options came back without the script option.");

                var r = await admin.PostAsync("/api/leads", new
                {
                    tenantId, fullName = "CF Lead " + m, email = World.EmailFor("E", "cf" + m), phone = "+9198" + m + "789",
                    companyName = "CF Co " + m, estimatedValue = 1000m, currency = "INR",
                    customFields = new Dictionary<string, string>
                    {
                        [textField.ToString()] = "</script><script>window.__x=34</script><img src=x onerror=window.__x=35>",
                        [dropField.ToString()] = key,
                    },
                });
                Assert.That(r.IsSuccess, Is.True, $"Adding the lead failed: {r}");
                return r.Json().GetProperty("id").GetGuid();
            });

            var problems = new List<string>();

            await Step("Every page that shows the fields: the list (columns and filters), the lead, its edit form, a new lead's form, Settings → Custom fields", async () =>
            {
                foreach (var path in new[]
                {
                    $"/Leads?SearchTerm={Uri.EscapeDataString("CF Lead " + m)}", $"/Leads/Detail/{leadId}", $"/Leads/Edit/{leadId}",
                    "/Leads/Create", "/Settings/CustomFields",
                })
                    await VisitAsync(page, path, problems);
            });

            await Step("The lead's page SHOWS the label, the option and the value as text", async () =>
            {
                await page.GotoAsync($"/Leads/Detail/{leadId}");
                var text = await page.Locator("body").InnerTextAsync();
                foreach (var shown in new[] { textLabel, optionLabel, "</script><script>window.__x=34</script>" })
                    if (!text.Contains(shown, StringComparison.Ordinal))
                        problems.Add($"The lead's page doesn't show \"{shown}\" as typed.");
            });

            await Step("Settings → Automation: the field list in a new rule's editor", async () =>
            {
                await page.GotoAsync("/Settings/Automation/Edit");
                await Ui.WaitForDocumentAsync(page);
                var trigger = page.Locator("#trigger");
                if (await trigger.CountAsync() > 0)
                {
                    await trigger.SelectOptionAsync("1");        // "a field changes" — lists the fields
                    await page.Locator("#addCondition").ClickAsync();
                }
                await page.WaitForTimeoutAsync(400);
                await ExpectNotRunAsync(page, "Settings → Automation, a new rule", problems);
                var listed = string.Join(" | ", await page.Locator("#triggerField option, .cond-field option").AllInnerTextsAsync());
                if (!listed.Contains(textLabel, StringComparison.Ordinal))
                    problems.Add($"The automation editor doesn't list the field by its label as typed. It lists: {ApiResponse.Short(listed, 300)}");
            });

            Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
        }
        finally
        {
            foreach (var id in created)
                await admin.DeleteAsync($"/api/custom-fields/{id}");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────

    /// <summary>Opens a page, waits for it to settle, and records it if any payload ran there.</summary>
    private static async Task VisitAsync(IPage page, string path, List<string> problems)
    {
        try
        {
            var response = await page.GotoAsync(path);
            await Ui.WaitForDocumentAsync(page);
            if (response is not null && response.Status >= 500)
                problems.Add($"{path} → HTTP {response.Status}.");
        }
        catch (PlaywrightException ex)
        {
            problems.Add($"{path} didn't open: {ex.Message.Split('\n')[0]}");
            return;
        }

        // Images fail and fire onerror a moment after the page is ready.
        await page.WaitForTimeoutAsync(500);
        await ExpectNotRunAsync(page, path, problems);
    }

    private static async Task ExpectNotRunAsync(IPage page, string where, List<string> problems)
    {
        var ran = await page.EvaluateAsync<string?>("() => window.__x === undefined ? null : String(window.__x)");
        if (ran is not null)
            problems.Add($"{where}: payload #{ran} RAN as script (window.__x = {ran}).");
    }

    /// <summary>Creates a custom field; if the list already has its maximum columns, without a column.</summary>
    private static async Task<Guid> CreateFieldAsync(ApiCaller admin, object body, List<Guid> created)
    {
        var r = await admin.PostAsync("/api/custom-fields", body);
        if (!r.IsSuccess && r.Status == 400 && Regex.IsMatch(r.Error, "column", RegexOptions.IgnoreCase))
        {
            var json = System.Text.Json.JsonSerializer.SerializeToNode(body, Api.JsonOptions)!.AsObject();
            json["showInList"] = false;
            r = await admin.PostAsync("/api/custom-fields", json);
        }
        Assert.That(r.IsSuccess, Is.True, $"Creating the custom field failed: {r}");
        var id = r.Json().GetProperty("id").GetGuid();
        created.Add(id);
        return id;
    }
}
