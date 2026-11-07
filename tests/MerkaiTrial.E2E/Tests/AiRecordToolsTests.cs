// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/AiRecordToolsTests.cs
//
// NEW (123). The assistant looking at records — always through the test
// stand-in (Ai__Provider=Fake). The stand-in asks for exactly the tools a
// question names, e.g.
//     [[tool:find_records {"type":"lead","text":"Acme"}]]
// and answers with what each returned, one line per tool:
//     (Test assistant) find_records returned: {…json…}
// so the tests read EXACTLY what the assistant was given.
//
//   Workspace A:
//     • records off (the default): no tools are offered at all
//     • the admin allows records in Settings → AI Assistant; it's in the
//       Activity Log
//     • a lead is found / not found for the rep EXACTLY as the API's own
//       lead page decides (record scope), the admin finds both
//     • another workspace's lead is never found, by name or by id
//     • type by type, what the assistant may read matches what the API
//       lets that person read (rep and viewer)
//     • the Activity Log names the tools a question used
//     • the chat shows answers formatted — bold, lists — and only links to
//       Merkai record pages; it says what it looked up; the greeting has
//       no stray indentation
//     • records switched off again: no tools offered
//   Both switches are left OFF at the end, whatever happens.
// =====================================================================

using System.Text.Json;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("AI")]
public class AiRecordToolsTests : E2ETest
{
    private static readonly string[] Types = { "lead", "deal", "contact", "company", "quote", "invoice" };
    private static readonly Dictionary<string, string> ListPath = new()
    {
        ["lead"] = "/api/leads", ["deal"] = "/api/deals", ["contact"] = "/api/contacts",
        ["company"] = "/api/companies", ["quote"] = "/api/quotes", ["invoice"] = "/api/invoices",
    };

    private static string Tool(string name, object? args = null) =>
        $"[[tool:{name} {JsonSerializer.Serialize(args ?? new { })}]]";

    /// <summary>Asks through the API; returns the answer text (fails the step on anything but 200).</summary>
    private static async Task<string> AskAsync(ApiCaller api, string question)
    {
        var r = await api.PostAsync("/api/ai/ask", new { question });
        Assert.That(r.IsSuccess, Is.True, $"Asking \"{question}\" answered {r}");
        return r.Json().GetProperty("answer").GetString() ?? "";
    }

    /// <summary>"NAME returned: {json}" lines → (name, json) in the order the tools were asked for.</summary>
    private static List<(string Name, JsonElement Result)> ToolResults(string answer)
    {
        var list = new List<(string, JsonElement)>();
        foreach (var raw in answer.Split('\n'))
        {
            var line = raw.Replace(FakeMarker, "").Trim();
            var at = line.IndexOf(" returned: ", StringComparison.Ordinal);
            if (at < 0) continue;
            using var doc = JsonDocument.Parse(line[(at + " returned: ".Length)..]);
            list.Add((line[..at].Trim(), doc.RootElement.Clone()));
        }
        return list;
    }

    private const string FakeMarker = "(Test assistant)";

    private static bool IsNoAccess(JsonElement r) =>
        r.TryGetProperty("error", out var e) && (e.GetString() ?? "").Contains("doesn't allow", StringComparison.Ordinal);

    [Test]
    public async Task The_assistant_looks_only_at_what_the_person_may_see()
    {
        var a = World.A;
        var tenantA = await Db.TenantIdAsync(a.Name);
        var tenantB = await Db.TenantIdAsync(World.B.Name);
        var since = DateTime.UtcNow.AddSeconds(-5);
        var m = Random.Shared.Next(10000, 99999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var adminApi = await Api.AsAsync(a.Admin);
        var repApi = await Api.AsAsync(a["rep"]);
        var viewerApi = await Api.AsAsync(a["viewer"]);
        var bApi = await Api.AsAsync(World.B.Admin);
        adminApi.WaitWhenPaced = repApi.WaitWhenPaced = viewerApi.WaitWhenPaced = bApi.WaitWhenPaced = true;
        var problems = new List<string>();

        var first = await adminApi.GetAsync("/api/ai/status");
        if (first.Status == 403)
            Assert.Fail("Workspace A's plan does not include the AI assistant. Run Sql/122_AiAssistant.sql on MerkaiTrial_E2E (@Commit = 1).");
        if (!first.Body.Contains("recordsOn", StringComparison.OrdinalIgnoreCase))
            Assert.Fail("The API doesn't know the records switch — is the round 123 WebApi running? " + first);

        try
        {
            await Step("Records start off: with the assistant on, no tools are offered", async () =>
            {
                var on = await adminApi.PutAsync("/api/ai/settings", new { on = true });
                Assert.That(on.IsSuccess, Is.True, $"Turning the assistant on answered {on}");
                if (on.Json().GetProperty("recordsOn").GetBoolean()) problems.Add("Looking at records was on before anyone allowed it.");

                var answer = await AskAsync(repApi, Tool("lead_summary"));
                if (!answer.Contains("No tools were offered")) problems.Add($"With records off, the assistant was offered tools: {answer}");
            });

            await Step("The admin allows records in Settings → AI Assistant", async () =>
            {
                var admin = await SignedInAsync(a.Admin);
                await admin.GotoAsync("/Settings/AiAssistant");
                Assert.That((await admin.Locator("#aiRecordsState").InnerTextAsync()).Trim(), Is.EqualTo("Off"));
                await admin.Locator("#aiRecordsOn").ClickAsync();          // its confirm() is accepted by the test browser
                await Assertions.Expect(admin.Locator("#aiRecordsState")).ToHaveTextAsync("On");

                var rows = await Db.AuditAsync("AiSettingChanged", since, tenantA);
                if (!rows.Any(r => (r.Data ?? "").Contains("\"records\":true") && (r.Data ?? "").Contains("\"changed\":\"records\"")))
                    problems.Add("Allowing records is not in the Activity Log.");
            });

            // ── data: a lead of the admin's, one of the rep's, one in workspace B ──
            var (adminLead, repLead, bLead) = await Step("Three leads: the admin's, the rep's, and one in workspace B", async () =>
            {
                async Task<Guid> Lead(ApiCaller api, Guid tenant, string key, string name, string currency)
                {
                    var r = await api.PostAsync("/api/leads", new
                    {
                        tenantId = tenant, fullName = name, email = World.EmailFor(key, "ai" + Random.Shared.Next(100000, 999999)),
                        phone = "+9198" + Random.Shared.Next(10000000, 99999999), companyName = $"AIRec Co {m}",
                        estimatedValue = 12345m, currency,
                    });
                    Assert.That(r.IsSuccess, Is.True, $"Adding the lead \"{name}\" failed: {r}");
                    return ApiIds.IdOf(r);
                }

                var al = await Lead(adminApi, tenantA, "A", $"AIRec Admin {m}", a.Currency);
                var rl = await Lead(adminApi, tenantA, "A", $"AIRec Rep {m}", a.Currency);
                var bl = await Lead(bApi, tenantB, "B", $"AIRec Other {m}", World.B.Currency);
                await Db.SetOwnerAsync("Leads", al, await Db.UserIdAsync(a.Admin.Email));
                await Db.SetOwnerAsync("Leads", rl, await Db.UserIdAsync(a["rep"].Email));
                return (al, rl, bl);
            });

            await Step("The rep sees a lead through the assistant exactly when the API's lead page shows it", async () =>
            {
                var repSeesAdminLead = (await repApi.GetAsync($"/api/leads/{adminLead}")).IsSuccess;
                var repSeesOwnLead = (await repApi.GetAsync($"/api/leads/{repLead}")).IsSuccess;

                var answer = await AskAsync(repApi,
                    Tool("find_records", new { type = "lead", text = $"AIRec Admin {m}" }) +
                    Tool("find_records", new { type = "lead", text = $"AIRec Rep {m}" }) +
                    Tool("get_record", new { type = "lead", id = adminLead.ToString() }));
                var results = ToolResults(answer);
                Assert.That(results, Has.Count.EqualTo(3), $"Expected three tool results, got: {answer}");

                var foundAdmin = results[0].Result.GetProperty("found").GetInt32() > 0;
                var foundOwn = results[1].Result.GetProperty("found").GetInt32() > 0;
                var gotAdmin = !results[2].Result.TryGetProperty("error", out _);

                if (foundAdmin != repSeesAdminLead) problems.Add($"Searching for the admin's lead: the assistant {(foundAdmin ? "found" : "didn't find")} it, the API {(repSeesAdminLead ? "shows" : "hides")} it.");
                if (foundOwn != repSeesOwnLead) problems.Add($"Searching for the rep's own lead: the assistant {(foundOwn ? "found" : "didn't find")} it, the API {(repSeesOwnLead ? "shows" : "hides")} it.");
                if (gotAdmin != repSeesAdminLead) problems.Add($"Opening the admin's lead by id: the assistant {(gotAdmin ? "opened" : "refused")} it, the API {(repSeesAdminLead ? "shows" : "hides")} it.");
                if (!repSeesAdminLead && answer.Contains($"AIRec Admin {m}")) problems.Add("The admin's lead's name reached the rep's answer.");
                if (!foundOwn) problems.Add("The rep's own lead was not found.");

                // Nothing private is sent.
                if (answer.Contains("@") || answer.Contains("+9198")) problems.Add($"An email address or phone number was sent to the assistant: {answer}");
                // Every record carries its page.
                if (foundOwn && !answer.Contains("/Leads/Detail/" + repLead)) problems.Add("The rep's lead came back without its link.");
            });

            await Step("The admin finds both", async () =>
            {
                var answer = await AskAsync(adminApi, Tool("find_records", new { type = "lead", text = $"AIRec Co {m}" }));
                if (!answer.Contains($"AIRec Admin {m}") || !answer.Contains($"AIRec Rep {m}"))
                    problems.Add($"The admin's search didn't find both leads: {answer}");
            });

            await Step("Another workspace's lead is never found, by name or by id", async () =>
            {
                var answer = await AskAsync(adminApi,
                    Tool("find_records", new { type = "lead", text = $"AIRec Other {m}" }) +
                    Tool("get_record", new { type = "lead", id = bLead.ToString() }));
                if (answer.Contains($"AIRec Other {m}")) problems.Add("Workspace B's lead reached workspace A's assistant.");
                var results = ToolResults(answer);
                if (results.Count != 2 || results[0].Result.GetProperty("found").GetInt32() != 0 || !results[1].Result.TryGetProperty("error", out _))
                    problems.Add($"Workspace B's lead wasn't refused cleanly: {answer}");
            });

            await Step("Type by type, the assistant may read exactly what the API lets each person read", async () =>
            {
                foreach (var (who, api) in new[] { ("rep", repApi), ("viewer", viewerApi) })
                {
                    var question = string.Concat(Types.Select(t => Tool("find_records", new { type = t, text = "zzqq" + m })));
                    var results = ToolResults(await AskAsync(api, question));
                    if (results.Count != Types.Length) { problems.Add($"{who}: expected {Types.Length} results, got {results.Count}."); continue; }

                    for (var i = 0; i < Types.Length; i++)
                    {
                        var apiRefuses = (await api.GetAsync(ListPath[Types[i]])).Status == 403;
                        var aiRefuses = IsNoAccess(results[i].Result);
                        if (apiRefuses != aiRefuses)
                            problems.Add($"{who} / {Types[i]}: the API {(apiRefuses ? "refuses" : "allows")} it, the assistant {(aiRefuses ? "refuses" : "allows")} it.");
                    }
                }
            });

            await Step("The summaries and tasks answer", async () =>
            {
                var answer = await AskAsync(adminApi, Tool("pipeline_summary") + Tool("lead_summary") + Tool("unpaid_invoices") + Tool("my_tasks", new { when = "week" }));
                var results = ToolResults(answer);
                if (results.Count != 4) { problems.Add($"Expected four summaries, got: {answer}"); return; }
                foreach (var (name, r) in results)
                    if (r.TryGetProperty("error", out var e)) problems.Add($"{name} failed for the admin: {e.GetString()}");
                if (results[1].Result.TryGetProperty("total", out var total) && total.GetInt32() < 2)
                    problems.Add($"lead_summary counted {total.GetInt32()} leads; the admin has at least 2.");
            });

            await Step("The Activity Log names the tools a question used", async () =>
            {
                var rows = await Db.AuditAsync("AiQuestion", since, tenantA);
                if (!rows.Any(r => (r.Data ?? "").Contains("find_records"))) problems.Add("No AiQuestion entry names find_records.");
            });

            await Step("The chat: formatted answers, only Merkai links, what it looked up, a tidy greeting", async () =>
            {
                var rep = await SignedInAsync(a["rep"], new ViewportSize { Width = 1366, Height = 900 });
                await rep.GotoAsync("/Dashboard");
                await rep.Locator("#aiFab").ClickAsync();
                await Assertions.Expect(rep.Locator("#aiPanelChat[data-ai-ready='1']")).ToHaveCountAsync(1);

                var hello = await rep.Locator("#aiPanelChat .ai-hello .ai-bubble").EvaluateAsync<string>("e => e.textContent");
                if (!hello.StartsWith("Hi!", StringComparison.Ordinal)) problems.Add($"The greeting starts with spaces or line breaks: \"{hello[..Math.Min(20, hello.Length)]}\"");

                var answers = rep.Locator("#aiPanelChat .ai-msg[data-role=assistant]:not(.ai-thinking):not(.ai-hello)");
                async Task Ask(string q)
                {
                    var before = await answers.CountAsync();
                    await rep.Locator("#aiPanelChat textarea[name=question]").FillAsync(q);
                    await rep.Locator("#aiPanelChat textarea[name=question]").PressAsync("Enter");
                    await Assertions.Expect(answers).ToHaveCountAsync(before + 1, new() { Timeout = E2ESettings.ActionTimeoutMs });
                }

                await Ask("[[md]] show me");
                var last = answers.Last.Locator(".ai-bubble");
                if (await last.Locator("strong").CountAsync() < 1) problems.Add("**bold** was not shown as bold.");
                if (await last.Locator("ol > li").CountAsync() != 2) problems.Add("The numbered list was not shown as a list.");
                if (await last.Locator("ul > li").CountAsync() != 2) problems.Add("The bullet list was not shown as a list.");
                if ((await last.InnerTextAsync()).Contains("**")) problems.Add("Stars (**) still show in the answer.");
                var links = last.Locator("a");
                if (await links.CountAsync() != 1) problems.Add($"Expected exactly one link (the Merkai record), found {await links.CountAsync()}.");
                else if (await links.First.GetAttributeAsync("href") != "/Leads/Detail/00000000-0000-0000-0000-000000000001")
                    problems.Add($"The one link points at {await links.First.GetAttributeAsync("href")}.");
                if (!(await last.InnerTextAsync()).Contains("Bad link")) problems.Add("An unsafe link's words were dropped instead of shown as text.");
                if (await rep.EvaluateAsync<bool>("() => window.__aiLink === 1")) problems.Add("A javascript: link RAN.");

                await Ask(Tool("lead_summary"));
                var looked = answers.Last.Locator(".ai-looked");
                if (await looked.CountAsync() != 1 || !(await looked.InnerTextAsync()).Contains("leads summary"))
                    problems.Add("An answer that used records doesn't say what it looked up.");
            });

            await Step("Records switched off again: no tools are offered", async () =>
            {
                var off = await adminApi.PutAsync("/api/ai/settings", new { records = false });
                if (!off.IsSuccess || off.Json().GetProperty("recordsOn").GetBoolean()) problems.Add($"Switching records off answered {off}");
                if (!off.Json().GetProperty("isOn").GetBoolean()) problems.Add("Switching records off also turned the assistant off.");
                var answer = await AskAsync(repApi, Tool("lead_summary"));
                if (!answer.Contains("No tools were offered")) problems.Add($"With records off again, tools were still offered: {answer}");
            });
        }
        finally
        {
            await adminApi.PutAsync("/api/ai/settings", new { on = false, records = false });
        }

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    [Test]
    public async Task A_sales_rep_cannot_change_the_records_switch()
    {
        var a = World.A;
        var repApi = await Api.AsAsync(a["rep"]);
        repApi.WaitWhenPaced = true;
        var r = await repApi.PutAsync("/api/ai/settings", new { records = true });
        Assert.That(r.Status, Is.EqualTo(403), $"A rep allowing records answered {r} — expected 403.");
    }
}
