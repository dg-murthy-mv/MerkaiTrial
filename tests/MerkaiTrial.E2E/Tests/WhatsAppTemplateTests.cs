// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/WhatsAppTemplateTests.cs
//
// NEW (097). Admin → WhatsApp Templates, as the super admin.
//
//   • Every notification event that needs a template has its wording for
//     Meta — task assigned (round 091) included, which had none.
//   • The message previews read once: the stage-changed one names the
//     deal and then where it went, as Meta's samples do.
//   • A name typed for the task event is kept after Save. The table had no
//     row for that event, and Save used to skip it without a word.
//
// The test servers have no WhatsApp token, so Meta is never asked and the
// "Still to get through Meta" panel does not appear here; nothing is sent.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("WhatsApp")]
public class WhatsAppTemplateTests : E2ETest
{
    [Test]
    public async Task Every_required_event_has_wording_for_Meta_including_tasks()
    {
        var admin = await SignedInAsync(World.SuperAdmin, new ViewportSize { Width = 1366, Height = 900 });
        await admin.GotoAsync("/Admin/WhatsAppTemplates");

        var expected = new Dictionary<string, string>
        {
            ["lead_assigned_v1"]            = "A lead has been assigned to you",
            ["deal_assigned_v1"]            = "A deal has been assigned to you",
            ["deal_stage_changed_v1"]       = "A deal has moved to a new stage",
            ["quote_sent_v1"]               = "A quote has been sent to a customer",
            ["quote_accepted_v1"]           = "A customer has accepted a quote",
            ["quote_rejected_v1"]           = "A customer has declined a quote",
            ["quote_approval_requested_v1"] = "A quote is waiting for your approval",
            ["quote_approval_decided_v1"]   = "Your approval request has been decided",
            ["task_assigned_v1"]            = "A task has been assigned to you",
        };

        var wording = admin.Locator("details.tpl-wording");

        await Step("Each of the nine has its body for Meta", async () =>
        {
            foreach (var (name, sentence) in expected)
            {
                var block = wording.Filter(new() { HasText = name });
                Assert.That(await block.CountAsync(), Is.EqualTo(1), $"No 'Wording for Meta' for {name}.");

                var body = (await block.Locator("code.tpl-code").TextContentAsync() ?? "").Trim();
                Assert.That(body, Does.Contain(sentence + ": {{headline}} ({{detail}})"), $"{name}'s body.");
                Assert.That(body, Does.StartWith("This is a notification from your Merkai CRM account."), $"{name}'s opening.");
            }
        });

        await Step("The stage-changed preview names the deal, then where it went", async () =>
        {
            var bubble = (await wording.Filter(new() { HasText = "deal_stage_changed_v1" })
                .Locator(".tpl-bubble").TextContentAsync() ?? "").Trim();
            Assert.That(bubble, Does.Contain("A deal has moved to a new stage: Acme office fit-out (now Negotiation, from Proposal)."));
        });

        await Step("The task row offers task_assigned_v1", async () =>
        {
            var row = admin.Locator(".tpl-row").Filter(new() { HasText = "A task is assigned to me" });
            Assert.That(await row.CountAsync(), Is.EqualTo(1), "No 'A task is assigned to me' row on the page.");
            var text = await row.TextContentAsync() ?? "";
            Assert.That(text, Does.Contain("task_assigned_v1"));
        });

        await Step("Its name saves, even though the table had no row for it", async () =>
        {
            // 097: Save used to skip an event with no table row, silently —
            // "Templates saved", and the name was gone on reload. Left OFF,
            // so nothing is ever sent from the test database.
            var row = admin.Locator(".tpl-row").Filter(new() { HasText = "A task is assigned to me" });
            await row.Locator("input[name=Names]").FillAsync("task_assigned_v1");
            await admin.GetByRole(AriaRole.Button, new() { Name = "Save templates" }).ClickAsync();
            await Assertions.Expect(admin.GetByText("Templates saved.").First).ToBeVisibleAsync();

            await admin.GotoAsync("/Admin/WhatsAppTemplates");
            row = admin.Locator(".tpl-row").Filter(new() { HasText = "A task is assigned to me" });
            Assert.That(await row.Locator("input[name=Names]").InputValueAsync(), Is.EqualTo("task_assigned_v1"),
                "The task template's name was not kept after Save.");
            Assert.That(await row.Locator("input[name=ActiveIds]").IsCheckedAsync(), Is.False,
                "Saving a name must not switch the template on.");
        });
    }
}
