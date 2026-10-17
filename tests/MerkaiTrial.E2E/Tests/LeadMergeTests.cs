// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/LeadMergeTests.cs
//
// NEW (093). Duplicate leads, in workspace D (India).
//
//   1. Add lead warns about a duplicate, and merging folds one lead into
//      the other:
//        • a second lead with the same email is not saved straight away —
//          Add lead lists the first one, saying "Same email";
//        • "Save as a new lead anyway" saves it;
//        • a task is put on the second lead;
//        • from the first lead's page: Merge → find the second → the two
//          side by side. The email is under "the same on both"; the value
//          differs, and the other lead's value is chosen;
//        • after Merge the first lead has the second one's value and its
//          task, a "Merged with …" note on its activities, and the second
//          lead is gone (deleted, and its page no longer opens).
//   2. Edit lead warns when a change makes the lead match another one:
//        • changing a lead's email to another lead's shows that lead, with
//          "Merge these two leads";
//        • an edit that leaves name, email, phone and company alone is not
//          warned about at all;
//        • "Save changes anyway" saves it.
//
// 093b: test 2 looks for "Merge these two leads" on the first lead's own
// entry. The first run counted every merge link in the warning and found
// two — the other one was a lead matched by the shared sample phone.
// =====================================================================

using System.Globalization;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("LeadMerge")]
public class LeadMergeTests : E2ETest
{
    private static TestTenant D => World.D ?? throw new InvalidOperationException("Workspace D was not built.");

    [OneTimeSetUp]
    public async Task BuildWorkspaceDAsync() => await WorldBuilder.EnsureWorkspaceDAsync();

    // =================================================================
    // 1. Warn, save anyway, merge
    // =================================================================

    [Test]
    public async Task A_duplicate_is_warned_about_and_merging_moves_everything_onto_the_kept_lead()
    {
        var admin = await SignedInAsync(D.Admin);
        var tag = $"MG{Random.Shared.Next(100000, 999999)}";
        var keepName = $"Kiran Keep {tag}";
        var otherName = $"Kiran Other {tag}";
        var email = World.EmailFor("D", "kiran" + Random.Shared.Next(1000, 9999));
        var company = $"Kiran Traders {tag}";

        var keepId = await Step("Add the first lead", () =>
            Flows.CreateLeadAsync(admin, keepName, email, 500000m, D, company));

        await Step("A second lead with the same email is not saved — Add lead lists the first, \"Same email\"", async () =>
        {
            string? warning = null;
            try
            {
                await Flows.CreateLeadAsync(admin, otherName, email, 900000m, D, company, acceptDuplicate: false);
            }
            catch (AssertionException ex) when (ex.Message.Contains("possible duplicate"))
            {
                warning = ex.Message;
            }

            Assert.That(warning, Is.Not.Null, "The second lead with the same email was saved with no warning.");
            Assert.That(warning, Does.Contain(keepName), $"The warning does not name the first lead: {warning}");
            Assert.That(warning, Does.Contain("Same email"), $"The warning does not say why: {warning}");

            var tenantId = await Db.TenantIdAsync(D.Name);
            Assert.That(await Db.CountLeadsAsync(tenantId, otherName), Is.EqualTo(0),
                "The second lead was saved before anyone chose to.");
        });

        var otherId = await Step("\"Save as a new lead anyway\" saves it", async () =>
        {
            await admin.Locator("#saveAnyway").ClickAsync();
            await Ui.WaitUntilAsync(admin,
                () => Task.FromResult(!Ui.PathOf(admin.Url).EndsWith("/Leads/Create", StringComparison.OrdinalIgnoreCase)),
                "\"Save as a new lead anyway\" did not save.");
            return await Flows.OpenLeadAsync(admin, otherName);
        });

        var task = $"Call back {tag}";
        await Step("Put a task on the second lead", async () =>
        {
            await admin.GotoAsync($"/Leads/Detail/{otherId}");
            await admin.Locator("#tasks-tab").ClickAsync();
            await Assertions.Expect(admin.Locator("#tasks")).ToBeVisibleAsync();
            await admin.Locator("#TaskInput_ActivityType").SelectOptionAsync("Task");
            await admin.Locator("#TaskInput_Subject").FillAsync(task);
            await admin.Locator("#TaskInput_DueDate").FillAsync(
                DateTime.Today.AddDays(2).AddHours(10).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture));
            var assign = admin.Locator("#TaskInput_AssignedToUserId");
            if (await assign.CountAsync() > 0) await assign.SelectOptionAsync("");
            await admin.Locator("#tasks form button[type=submit]").First.ClickAsync();
            await Ui.ExpectSuccessAsync(admin, "Task created");
        });

        await Step("From the first lead: Merge → find the second", async () =>
        {
            await admin.GotoAsync($"/Leads/Detail/{keepId}");
            await admin.Locator("#leadMerge").ClickAsync();
            await Ui.WaitForPageAsync(admin, u => u.Contains("/Leads/Merge", StringComparison.OrdinalIgnoreCase), "Merge leads");

            await admin.Locator("#mergeSearch").FillAsync(otherName);
            await admin.GetByRole(AriaRole.Button, new() { Name = "Find" }).ClickAsync();
            var result = admin.Locator("#mergeResults a", new() { HasText = otherName });
            await Assertions.Expect(result).ToHaveCountAsync(1);
            await result.ClickAsync();
            await Assertions.Expect(admin.Locator("#mergeForm")).ToBeVisibleAsync();
        });

        await Step("Side by side: the email is the same on both; take the other lead's value", async () =>
        {
            await Assertions.Expect(admin.Locator("tr[data-merge-row='email']")).ToHaveCountAsync(0);
            var value = admin.Locator("tr[data-merge-row='estimatedValue']");
            await Assertions.Expect(value).ToHaveCountAsync(1);
            Assert.That(await admin.Locator("#pick_estimatedValue_keep").IsCheckedAsync(), Is.True,
                "The kept lead's value should be chosen by default.");

            await admin.Locator("label[for='pick_estimatedValue_other']").ClickAsync();
            Assert.That(await admin.Locator("#pick_estimatedValue_other").IsCheckedAsync(), Is.True);
        });

        await Step("Merge", async () =>
        {
            await admin.Locator("#mergeSubmit").ClickAsync();     // confirm() is accepted by the session
            await Ui.ExpectSuccessAsync(admin, "was merged into");
            Assert.That(Ui.PathOf(admin.Url), Does.Contain(keepId.ToString()),
                $"After merging, the page should be the kept lead; it is {Ui.PathOf(admin.Url)}.");
        });

        await Step("The kept lead has the other's value and its task, and a note saying what happened", async () =>
        {
            Assert.That(await Db.LeadValueAsync(keepId), Is.EqualTo(900000m),
                "The kept lead should have taken the other lead's estimated value (900000).");
            Assert.That(await Db.CountActivitiesAsync(keepId, task), Is.EqualTo(1),
                "The other lead's task did not move to the kept lead.");
            Assert.That(await Db.CountActivitiesAsync(otherId, task), Is.EqualTo(0),
                "The task is still on the other lead.");
            Assert.That(await Db.CountActivitiesAsync(keepId, "Merged with"), Is.EqualTo(1),
                "The kept lead has no \"Merged with …\" activity.");
        });

        await Step("The other lead is gone", async () =>
        {
            Assert.That(await Db.IsLeadDeletedAsync(otherId), Is.True, "The other lead was not deleted.");

            await admin.GotoAsync($"/Leads/Detail/{otherId}");
            Assert.That(Ui.PathOf(admin.Url), Does.Not.Contain(otherId.ToString()),
                "The merged-away lead's page still opens.");
        });
    }

    // =================================================================
    // 2. Edit warns when it makes a twin
    // =================================================================

    [Test]
    public async Task Edit_lead_warns_when_a_change_makes_it_match_another_lead()
    {
        var admin = await SignedInAsync(D.Admin);
        var tag = $"ED{Random.Shared.Next(100000, 999999)}";
        var firstName = $"Neel First {tag}";
        var secondName = $"Neel Second {tag}";
        var firstEmail = World.EmailFor("D", "neel1" + Random.Shared.Next(1000, 9999));
        var secondEmail = World.EmailFor("D", "neel2" + Random.Shared.Next(1000, 9999));

        await Step("Add two leads with different emails and companies", async () =>
        {
            await Flows.CreateLeadAsync(admin, firstName, firstEmail, 100000m, D, $"First Co {tag}");
            await Flows.CreateLeadAsync(admin, secondName, secondEmail, 100000m, D, $"Second Co {tag}");
        });
        var secondId = await Flows.OpenLeadAsync(admin, secondName);

        await Step("Changing only the value is not warned about", async () =>
        {
            await admin.GotoAsync($"/Leads/Edit/{secondId}");
            await admin.Locator("#Input_EstimatedValue").FillAsync("150000");
            await admin.GetByRole(AriaRole.Button, new() { Name = "Save Changes" }).ClickAsync();
            await Ui.ExpectSuccessAsync(admin, "Lead updated");
        });

        await Step("Changing its email to the first lead's shows the first lead, with \"Merge these two leads\"", async () =>
        {
            await admin.GotoAsync($"/Leads/Edit/{secondId}");
            await admin.Locator("#Input_Email").FillAsync(firstEmail);
            await admin.GetByRole(AriaRole.Button, new() { Name = "Save Changes" }).ClickAsync();

            var warning = admin.Locator("#dupWarning");
            try
            {
                await Assertions.Expect(warning).ToBeVisibleAsync();
            }
            catch (Exception ex) when (ex is not AssertionException)
            {
                throw new AssertionException(
                    $"No duplicate warning after giving the lead another lead's email (now at {Ui.PathOf(admin.Url)}). " +
                    await Ui.DescribePageProblemsAsync(admin));
            }
            await Assertions.Expect(warning).ToContainTextAsync(firstName);
            await Assertions.Expect(warning).ToContainTextAsync("Same email");
            // 093b: the first lead's OWN entry offers the merge. Other leads
            // may be listed too — every lead in workspace D shares the sample
            // phone number — so the link is looked for on that entry only.
            var entry = warning.Locator("li.dup-item", new() { HasText = firstName });
            await Assertions.Expect(entry).ToHaveCountAsync(1);
            await Assertions.Expect(entry.Locator("a.dup-merge")).ToHaveCountAsync(1);
        });

        await Step("\"Save changes anyway\" saves it", async () =>
        {
            await admin.Locator("#saveAnyway").ClickAsync();
            await Ui.ExpectSuccessAsync(admin, "Lead updated");
        });
    }
}
