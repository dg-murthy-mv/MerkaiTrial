// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/AiAssistantTests.cs
//
// NEW (122). The AI assistant's foundation, with the test stand-in
// (Ai__Provider=Fake, set by run-e2e.ps1 — never a real provider, never a
// key). The stand-in answers "(Test assistant) You asked: …", and fails on
// purpose when a question contains [[fail]].
//
//   Workspace A (its plan includes ai_assistant — Sql/122_AiAssistant.sql):
//     • it starts OFF: no "Ask Merkai" button, the API refuses questions
//     • a sales rep can't turn it on (403); the admin turns it on in
//       Settings → AI Assistant, and that is in the Activity Log
//     • the rep asks in the panel and gets the answer; a follow-up carries
//       the conversation
//     • markup in a question is shown as text, never run
//     • the provider failing: a clear message, recorded, NOT counted
//     • too long a question is refused; the monthly limit is enforced
//     • every question is in the Activity Log with its text
//     • turned off again: the button goes and the API refuses
//   Workspace F (Starter plan): not in the plan — the API says so, the
//     pages go to /Upgrade, the menu shows a lock.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("AI")]
public class AiAssistantTests : E2ETest
{
    private const string Marker = "(Test assistant)";

    [Test]
    public async Task The_assistant_is_off_until_the_admin_turns_it_on_then_answers_and_is_recorded()
    {
        var a = World.A;
        var tenantA = await Db.TenantIdAsync(a.Name);
        var since = DateTime.UtcNow.AddSeconds(-5);
        var adminApi = await Api.AsAsync(a.Admin);
        var repApi = await Api.AsAsync(a["rep"]);
        adminApi.WaitWhenPaced = repApi.WaitWhenPaced = true;
        var problems = new List<string>();

        var first = await adminApi.GetAsync("/api/ai/status");
        if (first.Status == 403)
            Assert.Fail("Workspace A's plan does not include the AI assistant. Run Sql/122_AiAssistant.sql on MerkaiTrial_E2E " +
                        $"(@Commit = 1), then run the tests again. The API said: {first}");

        try
        {
            await Step("It starts off: no button, and the API refuses a question", async () =>
            {
                var status = first.Json();
                Assert.That(status.GetProperty("isOn").GetBoolean(), Is.False, "The assistant was on before anyone turned it on.");

                var rep = await SignedInAsync(a["rep"]);
                await rep.GotoAsync("/Dashboard");
                Assert.That(await rep.Locator("#aiFab").CountAsync(), Is.EqualTo(0), "\"Ask Merkai\" shows while the assistant is off.");

                var asked = await repApi.PostAsync("/api/ai/ask", new { question = "Hello?" });
                if (asked.Status != 400 || !asked.Error.Contains("turned off")) problems.Add($"Asking while off answered {asked}");
            });

            await Step("A sales rep can't turn it on", async () =>
            {
                var r = await repApi.PutAsync("/api/ai/settings", new { on = true });
                if (r.Status != 403) problems.Add($"A rep turning the assistant on answered {r} — expected 403.");
            });

            await Step("The admin turns it on in Settings → AI Assistant", async () =>
            {
                var admin = await SignedInAsync(a.Admin);
                await admin.GotoAsync("/Settings/AiAssistant");
                Assert.That((await admin.Locator("#aiState").InnerTextAsync()).Trim(), Is.EqualTo("Off"));
                await admin.Locator("#aiTurnOn").ClickAsync();
                await Assertions.Expect(admin.Locator("#aiState")).ToHaveTextAsync("On");

                var rows = await Db.AuditAsync("AiSettingChanged", since, tenantA);
                if (!rows.Any(r => (r.Data ?? "").Contains("\"on\":true"))) problems.Add("Turning the assistant on is not in the Activity Log.");
            });

            var rep = await SignedInAsync(a["rep"], new ViewportSize { Width = 1366, Height = 900 });
            ILocator Answers() => rep.Locator("#aiPanelChat .ai-msg[data-role=assistant]:not(.ai-thinking)");

            async Task AskAsync(string question)
            {
                var before = await Answers().CountAsync();
                await rep.Locator("#aiPanelChat textarea[name=question]").FillAsync(question);
                await rep.Locator("#aiPanelChat textarea[name=question]").PressAsync("Enter");
                try
                {
                    await Assertions.Expect(Answers()).ToHaveCountAsync(before + 1, new() { Timeout = E2ESettings.ActionTimeoutMs });
                }
                catch (PlaywrightException)
                {
                    // Say what the chat DID show, so a failure explains itself.
                    var shown = await rep.Locator("#aiPanelChat .ai-msg").EvaluateAllAsync<string[]>(
                        "rows => rows.map(r => (r.getAttribute('data-role') || '?') + (r.classList.contains('ai-thinking') ? ' (thinking)' : '') + ': ' + r.innerText.trim().slice(0, 160))");
                    var status = await rep.Locator("#aiPanelChat .ai-status").InnerTextAsync();
                    var box = await rep.Locator("#aiPanelChat textarea[name=question]").InputValueAsync();
                    Assert.Fail($"No answer to \"{question}\" within {E2ESettings.ActionTimeoutMs / 1000} s.\n" +
                                $"  Chat: {string.Join(" | ", shown)}\n  Status line: \"{status}\"\n  Still in the box: \"{box}\"");
                }
            }

            await Step("The rep asks from the panel and gets the answer", async () =>
            {
                await rep.GotoAsync("/Dashboard");
                await rep.Locator("#aiFab").ClickAsync();
                await Assertions.Expect(rep.Locator("#aiPanel")).ToBeVisibleAsync();
                // 122-update1: the chat is wired (Enter sends) before anything is typed.
                await Assertions.Expect(rep.Locator("#aiPanelChat[data-ai-ready='1']")).ToHaveCountAsync(1);
                await AskAsync("How do I add a lead?");
                var text = await Answers().Last.InnerTextAsync();
                Assert.That(text, Does.Contain($"{Marker} You asked: How do I add a lead?"));
                await Assertions.Expect(rep.Locator("#aiPanelChat .ai-status")).ToContainTextAsync("left this month");
            });

            await Step("A follow-up carries the conversation", async () =>
            {
                await AskAsync("And after that?");
                var text = await Answers().Last.InnerTextAsync();
                if (!text.Contains("earlier message")) problems.Add($"The follow-up was sent without the conversation: {text}");
            });

            await Step("Markup in a question is shown as text, never run", async () =>
            {
                await AskAsync("<img src=x onerror=window.__ai=1>");
                if (await rep.EvaluateAsync<bool>("() => window.__ai === 1")) problems.Add("Markup in a question RAN in the chat.");
                if (await rep.Locator("#aiPanelChat .ai-log img").CountAsync() > 0) problems.Add("Markup in a question became an element.");
                if (!(await Answers().Last.InnerTextAsync()).Contains("<img src=x")) problems.Add("Markup in a question was not shown as text.");
            });

            await Step("The provider failing: a clear message, recorded, not counted", async () =>
            {
                var answeredBefore = await Db.CountAiUsageAsync(tenantA, "answered", since);
                await rep.Locator("#aiPanelChat textarea[name=question]").FillAsync("[[fail]] please");
                await rep.Locator("#aiPanelChat textarea[name=question]").PressAsync("Enter");
                await Assertions.Expect(rep.Locator("#aiPanelChat .ai-error").Last).ToContainTextAsync("wasn't counted",
                    new() { Timeout = E2ESettings.ActionTimeoutMs });
                if (await Db.CountAiUsageAsync(tenantA, "failed", since) < 1) problems.Add("The failed question was not recorded.");
                if (await Db.CountAiUsageAsync(tenantA, "answered", since) != answeredBefore) problems.Add("The failed question was counted.");
            });

            await Step("Too long a question is refused", async () =>
            {
                var r = await repApi.PostAsync("/api/ai/ask", new { question = new string('x', 1001) });
                if (r.Status != 400) problems.Add($"A 1,001-character question answered {r} — expected 400.");
            });

            await Step("The monthly limit is enforced", async () =>
            {
                var used = await Db.CountAiUsageAsync(tenantA, "answered", DateTime.UtcNow.AddDays(-40));
                await Db.SetAiMonthlyLimitAsync(tenantA, used);
                var r = await repApi.PostAsync("/api/ai/ask", new { question = "One more?" });
                if (r.Status != 400 || !r.Error.Contains("used all")) problems.Add($"Asking past the month's limit answered {r}");
                if (await Db.CountAiUsageAsync(tenantA, "limit", since) < 1) problems.Add("The refused question (limit) was not recorded.");
            });

            await Step("Every question is in the Activity Log with its text", async () =>
            {
                var rows = await Db.AuditAsync("AiQuestion", since, tenantA);
                if (!rows.Any(r => (r.Data ?? "").Contains("How do I add a lead?"))) problems.Add("The question is not in the Activity Log.");
                if (rows.Count < 5) problems.Add($"Expected at least 5 AiQuestion rows (answered, failed and limit), found {rows.Count}.");
            });

            await Step("Turned off again: the button goes and the API refuses", async () =>
            {
                var off = await adminApi.PutAsync("/api/ai/settings", new { on = false });
                if (!off.IsSuccess) problems.Add($"Turning the assistant off answered {off}");
                await rep.GotoAsync("/Dashboard");
                if (await rep.Locator("#aiFab").CountAsync() != 0) problems.Add("\"Ask Merkai\" still shows after it was turned off.");
                await Db.SetAiMonthlyLimitAsync(tenantA, null);
                var r = await repApi.PostAsync("/api/ai/ask", new { question = "Still there?" });
                if (r.Status != 400 || !r.Error.Contains("turned off")) problems.Add($"Asking after it was turned off answered {r}");
            });
        }
        finally
        {
            // Left off, with the plan's limit, for every other test.
            await adminApi.PutAsync("/api/ai/settings", new { on = false });
            await Db.SetAiMonthlyLimitAsync(tenantA, null);
        }

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    [Test]
    public async Task A_plan_without_the_assistant_is_refused_everywhere()
    {
        var f = await Step("Workspace F (Starter plan)", WorldBuilder.EnsureWorkspaceFAsync);
        var problems = new List<string>();

        var api = await Api.AsAsync(f.Admin);
        api.WaitWhenPaced = true;
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, "/api/ai/status", null),
                     (HttpMethod.Post, "/api/ai/ask", new { question = "Hello?" }),
                     (HttpMethod.Put, "/api/ai/settings", new { on = true }),
                 })
        {
            var r = await api.SendAsync(method, path, body);
            if (r.Status != 403 || !r.Body.Contains("Not in your plan")) problems.Add($"{method} {path} on Starter answered {r} — expected 403 \"Not in your plan\".");
        }

        var page = await SignedInAsync(f.Admin);
        foreach (var path in new[] { "/Assistant", "/Settings/AiAssistant" })
        {
            await page.GotoAsync(path);
            if (!Ui.PathOf(page.Url).StartsWith("/Upgrade", StringComparison.OrdinalIgnoreCase))
                problems.Add($"{path} on Starter opened {page.Url} — expected /Upgrade.");
        }

        await page.GotoAsync("/Dashboard");
        if (await page.Locator("#aiFab").CountAsync() != 0) problems.Add("\"Ask Merkai\" shows on a Starter workspace.");
        var link = page.Locator("a.lf-nav-item[href='/Settings/AiAssistant']");
        if (await link.CountAsync() == 1 && await link.Locator(".lf-lock-icon").CountAsync() == 0)
            problems.Add("The AI Assistant menu item on Starter has no lock.");

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }
}
