// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/AiRecordHelperTests.cs
//
// NEW (124). The AI helpers on a lead's and a deal's page — Summarise,
// Next step, Draft email, Draft message — through the test stand-in
// (Ai__Provider=Fake), which answers "(Test assistant) You asked: …" with
// exactly what it was sent: the instruction, the note and the record data.
//
//   Workspace A:
//     • not shown while the assistant may not look at records
//     • on a lead: Summarise sends the lead itself, formatted answer, Copy
//     • Draft email carries the person's note
//     • on a deal: Next step sends the deal
//     • a rep can't use them on a lead the API hides from them
//     • every helper is in the Activity Log ("Summary of lead …")
//     • records switched off: the card goes, the API refuses
//   Both switches are left OFF at the end, whatever happens.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("AI")]
public class AiRecordHelperTests : E2ETest
{
    [Test]
    public async Task The_helpers_on_a_lead_and_a_deal_use_that_record_and_follow_the_switches()
    {
        var a = World.A;
        var tenantA = await Db.TenantIdAsync(a.Name);
        var since = DateTime.UtcNow.AddSeconds(-5);
        var m = Random.Shared.Next(10000, 99999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var adminApi = await Api.AsAsync(a.Admin);
        var repApi = await Api.AsAsync(a["rep"]);
        adminApi.WaitWhenPaced = repApi.WaitWhenPaced = true;
        var problems = new List<string>();

        var first = await adminApi.GetAsync("/api/ai/status");
        if (first.Status == 403)
            Assert.Fail("Workspace A's plan does not include the AI assistant. Run Sql/122_AiAssistant.sql on MerkaiTrial_E2E (@Commit = 1).");

        try
        {
            var adminId = (await Db.UserIdAsync(a.Admin.Email)).ToString();
            var (leadId, dealId) = await Step("A lead and a deal of the admin's", async () =>
            {
                var lead = await ApiRecords.LeadAsync(adminApi, tenantA, $"AIHelp Lead {m}", "aihelp" + m, adminId);
                var contact = await ApiRecords.ContactAsync(adminApi, tenantA, $"AIHelp {m}", "aihelpc" + m);
                var deal = await ApiRecords.DealAsync(adminApi, tenantA, contact, $"AIHelp Deal {m}", adminId);
                return (lead, deal);
            });

            var admin = await SignedInAsync(a.Admin, new ViewportSize { Width = 1366, Height = 900 });

            await Step("With the assistant on but records not allowed, the lead page has no AI help", async () =>
            {
                var on = await adminApi.PutAsync("/api/ai/settings", new { on = true, records = false });
                Assert.That(on.IsSuccess, Is.True, $"Turning the assistant on answered {on}");
                await admin.GotoAsync($"/Leads/Detail/{leadId}");
                if (await admin.Locator("#aiHelper").CountAsync() != 0) problems.Add("The AI help card shows while the assistant may not look at records.");

                var r = await adminApi.PostAsync("/api/ai/help", new { kind = "summary", type = "lead", id = leadId });
                if (r.Status != 400 || !r.Error.Contains("look at records")) problems.Add($"A helper with records not allowed answered {r}");
            });

            var on2 = await adminApi.PutAsync("/api/ai/settings", new { records = true });
            Assert.That(on2.IsSuccess, Is.True, $"Allowing records answered {on2}");

            async Task<string> Click(string kind)
            {
                await admin.Locator($"#aiHelper button[data-kind={kind}]").ClickAsync();
                var done = admin.Locator("#aiHelperOut.ai-rich, #aiHelperOut.ai-error");
                await Assertions.Expect(done).ToHaveCountAsync(1, new() { Timeout = E2ESettings.ActionTimeoutMs });
                var text = await admin.Locator("#aiHelperOut").InnerTextAsync();
                if (await admin.Locator("#aiHelperOut.ai-error").CountAsync() > 0) problems.Add($"{kind} answered with an error: {text}");
                return text;
            }

            await Step("On the lead: Summarise sends that lead, and the answer can be copied", async () =>
            {
                await admin.GotoAsync($"/Leads/Detail/{leadId}");
                await Assertions.Expect(admin.Locator("#aiHelper[data-ai-ready='1']")).ToHaveCountAsync(1);
                var text = await Click("summary");
                if (!text.Contains("Summarise this lead")) problems.Add($"Summarise didn't send the summary instruction: {text[..Math.Min(200, text.Length)]}");
                if (!text.Contains($"AIHelp Lead {m}")) problems.Add("Summarise didn't send the lead itself.");
                if (text.Contains("@")) problems.Add("An email address was sent with the lead.");
                if (await admin.Locator("#aiHelperCopy").IsDisabledAsync()) problems.Add("Copy stays disabled after an answer.");
                await Assertions.Expect(admin.Locator("#aiHelper .ai-help-status")).ToContainTextAsync("left this month");
            });

            await Step("Draft email carries the person's note", async () =>
            {
                await admin.Locator("#aiHelperNote").FillAsync($"mention the demo {m}");
                var text = await Click("draft_email");
                if (!text.Contains("follow-up email")) problems.Add("Draft email didn't ask for an email.");
                if (!text.Contains($"mention the demo {m}")) problems.Add("Draft email didn't carry the note.");
            });

            await Step("On the deal: Next step sends that deal", async () =>
            {
                await admin.GotoAsync($"/Pipeline/Detail/{dealId}");
                await Assertions.Expect(admin.Locator("#aiHelper[data-ai-ready='1']")).ToHaveCountAsync(1);
                var text = await Click("next_step");
                if (!text.Contains("next step")) problems.Add("Next step didn't send the next-step instruction.");
                if (!text.Contains($"AIHelp Deal {m}")) problems.Add("Next step didn't send the deal itself.");
            });

            await Step("A rep can't use the helpers on a lead the API hides from them", async () =>
            {
                var repSees = (await repApi.GetAsync($"/api/leads/{leadId}")).IsSuccess;
                var r = await repApi.PostAsync("/api/ai/help", new { kind = "summary", type = "lead", id = leadId });
                if (repSees && !r.IsSuccess) problems.Add($"The rep can open the lead but the helper refused: {r}");
                if (!repSees)
                {
                    if (r.Status != 400 || !r.Error.Contains("can't be found")) problems.Add($"The rep's helper on a hidden lead answered {r}");
                    if (r.Body.Contains($"AIHelp Lead {m}")) problems.Add("The hidden lead's name reached the rep.");
                }
            });

            await Step("Every helper is in the Activity Log", async () =>
            {
                var rows = await Db.AuditAsync("AiQuestion", since, tenantA);
                foreach (var (label, name) in new[] { ("Summary of lead", $"AIHelp Lead {m}"), ("Email draft for lead", $"AIHelp Lead {m}"), ("Next step for deal", $"AIHelp Deal {m}") })
                    if (!rows.Any(r => (r.Data ?? "").Contains(label) && (r.Data ?? "").Contains(name))) problems.Add($"Not in the Activity Log: {label} {name}");
            });

            await Step("Records switched off: the card goes and the API refuses", async () =>
            {
                await adminApi.PutAsync("/api/ai/settings", new { records = false });
                await admin.GotoAsync($"/Leads/Detail/{leadId}");
                if (await admin.Locator("#aiHelper").CountAsync() != 0) problems.Add("The AI help card still shows after records were switched off.");
                var r = await adminApi.PostAsync("/api/ai/help", new { kind = "draft_message", type = "lead", id = leadId });
                if (r.Status != 400) problems.Add($"A helper after records were switched off answered {r}");
            });
        }
        finally
        {
            await adminApi.PutAsync("/api/ai/settings", new { on = false, records = false });
        }

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }
}
