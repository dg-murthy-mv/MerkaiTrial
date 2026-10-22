// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/AutomationTests.cs
//
// NEW (098). Settings → Automation, workspace A.
//
//   • A rule built in the editor ("when a lead is added, if its company
//     contains X, give the owner a call task") fires on a lead that
//     matches: the task is on the lead and the rule log says Done. A lead
//     that doesn't match gets no task, and the log says Skipped — the
//     answer to "why didn't my rule fire?".
//   • A "field changes" rule: a lead's status becoming Qualified notifies
//     the sales manager in the bell.
//   • The plan's limit, read live: the super admin lowers it on Admin →
//     Plans, the workspace admin can't switch another rule on, and the
//     message says why. The limit is put back afterwards.
//   • A rep can't open the page.
//   • (099) A TIME-BASED rule: "a lead with no activity for 3 days gets a
//     follow-up task". The lead is made to look 5 days old in the test
//     database, "Check time-based rules now" runs the same code the worker
//     runs every 15 minutes, the task appears — and pressing it again does
//     not add a second one (same spell, acted on once).
//
// ISOLATION. Every rule here only fires for records whose company name
// carries this run's stamp, and every rule is switched off and removed at
// the end — rules are workspace-wide, and later tests' leads must not set
// them off.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Automation")]
public class AutomationTests : E2ETest
{
    private static readonly ViewportSize Wide = new() { Width = 1366, Height = 900 };

    /// <summary>
    /// Fills the editor for "Leads · is added" with one condition, "Company
    /// name contains <paramref name="companyMark"/>". The page must be on
    /// /Settings/Automation/Edit.
    /// </summary>
    private static async Task StartLeadRuleAsync(IPage page, string name, string companyMark)
    {
        await page.Locator("#ruleName").FillAsync(name);
        await page.Locator("#entityType").SelectOptionAsync("Lead");

        await page.Locator("#addCondition").ClickAsync();
        var line = page.Locator(".cond-line").Last;
        await line.Locator(".cond-field").SelectOptionAsync("company");
        await line.Locator(".cond-op").SelectOptionAsync("contains");
        await line.Locator(".cond-value").FillAsync(companyMark);
    }

    private static async Task SaveRuleAsync(IPage page, bool switchOn, string expectSuccess)
    {
        if (switchOn) await page.Locator("#isActive").CheckAsync();
        await page.Locator("#saveRule").ClickAsync();
        await Ui.ExpectSuccessAsync(page, expectSuccess);
    }

    [Test]
    public async Task A_lead_rule_creates_the_task_and_explains_a_skip()
    {
        var tenantId = await Db.TenantIdAsync(World.A.Name);
        var mark = World.Unique("AutoCo");
        var ruleName = World.Unique("Call new leads");
        var admin = await SignedInAsync(World.A.Admin, Wide);

        try
        {
            await Step("Build the rule in the editor and switch it on", async () =>
            {
                await admin.GotoAsync("/Settings/Automation/Edit");
                await StartLeadRuleAsync(admin, ruleName, mark);

                // The new rule starts with one "Create a task" action.
                var task = admin.Locator(".act-line[data-kind='task']").First;
                await task.Locator(".act-subject").FillAsync("Call {name}");
                await task.Locator(".act-due").SelectOptionAsync("0");
                await task.Locator(".act-assign").SelectOptionAsync("owner");

                await SaveRuleAsync(admin, switchOn: true, "saved and switched on");

                var row = admin.Locator("tr.rule-row").Filter(new() { HasText = ruleName });
                Assert.That(await row.CountAsync(), Is.EqualTo(1), "The rule is not on the list.");
                var summary = await row.Locator(".rule-summary").InnerTextAsync();
                Assert.That(summary, Does.Contain("When a lead is added").And.Contain(mark),
                    "The list should describe the rule as a sentence.");
            });

            var matching = World.Unique("Auto Lead");
            var matchingId = await Step("A lead whose company matches gets the task", async () =>
            {
                var id = await Flows.CreateLeadAsync(admin, matching, $"auto.{World.Stamp}@example.com", 40000,
                    companyName: $"{mark} Traders");
                Assert.That(await Db.CountActivitiesAsync(id, "Call " + matching), Is.EqualTo(1),
                    "The rule's call task is not on the lead.");
                Assert.That(await Db.CountAutomationRunsAsync(id, ruleName, TestDatabase.RunDone), Is.EqualTo(1),
                    "The rule log has no Done line for the lead.");
                return id;
            });

            await Step("A lead that doesn't match gets none — and the log says why", async () =>
            {
                var other = World.Unique("Plain Lead");
                var otherId = await Flows.CreateLeadAsync(admin, other, $"plain.{World.Stamp}@example.com", 1000,
                    companyName: "Unrelated Pvt Ltd");
                Assert.That(await Db.CountActivitiesAsync(otherId, "Call " + other), Is.EqualTo(0),
                    "A lead that doesn't match the condition got the task.");
                Assert.That(await Db.CountAutomationRunsAsync(otherId, ruleName, TestDatabase.RunSkipped), Is.EqualTo(1),
                    "The skip was not written to the rule log.");

                await admin.GotoAsync("/Settings/Automation");
                var skipped = admin.Locator("#runsTable tr.run-row").Filter(new() { HasText = other });
                Assert.That(await skipped.CountAsync(), Is.GreaterThanOrEqualTo(1), "The log on the page doesn't show the skip.");
                var text = await admin.Locator("#runsTable").InnerTextAsync();
                Assert.That(text, Does.Contain("Skipped").And.Contain("Company name is Unrelated Pvt Ltd"),
                    "The skip should say what the lead had.");
                Assert.That(text, Does.Contain(matching), "The Done line for the matching lead should be in the log too.");
            });
        }
        finally
        {
            await Db.RetireAutomationRulesAsync(tenantId, ruleName);
        }
    }

    [Test]
    public async Task A_status_change_rule_notifies_the_sales_manager()
    {
        var tenantId = await Db.TenantIdAsync(World.A.Name);
        var mark = World.Unique("QualCo");
        var ruleName = World.Unique("Tell Mira about qualified");
        var manager = World.A["manager"];
        var admin = await SignedInAsync(World.A.Admin, Wide);

        try
        {
            await Step("Rule: when a lead's status changes to Qualified, notify the manager", async () =>
            {
                await admin.GotoAsync("/Settings/Automation/Edit");
                await StartLeadRuleAsync(admin, ruleName, mark);

                await admin.Locator("#trigger").SelectOptionAsync("1");
                await admin.Locator("#triggerField").SelectOptionAsync("status");
                await Ui.SelectByPatternAsync(admin.Locator("#triggerTo"), Flows.QualifiedPattern,
                    excludePattern: Flows.NotQualifiedPattern);

                var act = admin.Locator(".act-line").First;
                await act.Locator(".act-kind").SelectOptionAsync("notify");
                act = admin.Locator(".act-line").First;                 // re-rendered
                await act.Locator(".act-assign").SelectOptionAsync("user");
                act = admin.Locator(".act-line").First;
                await Ui.SelectByTextAsync(act.Locator(".act-user"), manager.FullName);
                await act.Locator(".act-message").FillAsync("Qualified: {name}");

                await SaveRuleAsync(admin, switchOn: true, "saved and switched on");
            });

            var leadName = World.Unique("Qual Lead");
            await Step("Qualifying a matching lead puts a message in the manager's bell", async () =>
            {
                var id = await Flows.CreateLeadAsync(admin, leadName, $"qual.{World.Stamp}@example.com", 25000,
                    companyName: $"{mark} Exports");
                await Flows.QualifyLeadAsync(admin);

                await Ui.WaitUntilAsync(admin,
                    async () => await Db.CountNotificationsAsync(manager.Email, "Qualified: " + leadName) == 1,
                    "The manager was not notified when the lead was qualified.");
                Assert.That(await Db.CountAutomationRunsAsync(id, ruleName, TestDatabase.RunDone), Is.EqualTo(1),
                    "The rule log has no Done line for the status change.");
            });
        }
        finally
        {
            await Db.RetireAutomationRulesAsync(tenantId, ruleName);
        }
    }

    [Test]
    public async Task The_plan_limit_is_read_live_and_explained()
    {
        var tenantId = await Db.TenantIdAsync(World.A.Name);
        var (planId, originalLimit) = await Db.PlanAutomationLimitAsync(tenantId);
        var prefix = World.Unique("Limit rule");
        var admin = await SignedInAsync(World.A.Admin, Wide);

        try
        {
            var active = await Db.CountActiveAutomationRulesAsync(tenantId);

            await Step("The super admin sets the plan's limit to leave room for exactly one more", async () =>
            {
                var super = await SignedInAsync(World.SuperAdmin, Wide);
                await super.GotoAsync($"/Admin/Plans/Detail/{planId}");
                await super.Locator("#maxAutomationRules").FillAsync((active + 1).ToString());
                await super.Locator("#saveAutomationLimit").ClickAsync();
                await Ui.ExpectSuccessAsync(super, "automation rule");
                Assert.That((await Db.PlanAutomationLimitAsync(tenantId)).Limit, Is.EqualTo(active + 1));
            });

            await Step("The first rule switches on", async () =>
            {
                await admin.GotoAsync("/Settings/Automation/Edit");
                await StartLeadRuleAsync(admin, prefix + " one", prefix);
                await admin.Locator(".act-line .act-subject").First.FillAsync("Limit check {name}");
                await SaveRuleAsync(admin, switchOn: true, "saved and switched on");
            });

            await Step("A second can be saved, but not switched on — and the page says why", async () =>
            {
                await admin.GotoAsync("/Settings/Automation/Edit");
                await StartLeadRuleAsync(admin, prefix + " two", prefix);
                await admin.Locator(".act-line .act-subject").First.FillAsync("Limit check {name}");

                await admin.Locator("#isActive").CheckAsync();
                await admin.Locator("#saveRule").ClickAsync();
                var error = admin.Locator("#saveError");
                await Assertions.Expect(error).ToBeVisibleAsync();
                Assert.That(await error.InnerTextAsync(), Does.Match(@"plan allows \d+ switched-on rule"),
                    "The refusal should name the plan's limit.");

                await admin.Locator("#isActive").UncheckAsync();
                await admin.Locator("#saveRule").ClickAsync();
                await Ui.ExpectSuccessAsync(admin, "It's switched off");

                var row = admin.Locator("tr.rule-row").Filter(new() { HasText = prefix + " two" });
                // One click, not CheckAsync: the switch posts the form and the
                // refused page comes back with it OFF, so CheckAsync would keep
                // clicking until it timed out waiting for a tick that — rightly —
                // never stays.
                await row.Locator(".rule-toggle").ClickAsync();
                var red = admin.Locator(".alert-danger").Filter(new() { HasText = "plan allows" });
                await Assertions.Expect(red.First).ToBeVisibleAsync();
            });
        }
        finally
        {
            await Db.SetPlanAutomationLimitAsync(planId, originalLimit);
            await Db.RetireAutomationRulesAsync(tenantId, prefix);
        }
    }

    [Test]
    public async Task A_rep_cannot_open_automation()
    {
        var rep = await SignedInAsync(World.A["rep"], Wide);
        await Ui.ExpectRefusedAsync(rep, "/Settings/Automation");
        await Ui.ExpectRefusedAsync(rep, "/Settings/Automation/Edit");
        Assert.That(await rep.Locator("a.lf-nav-item[href*='/Settings/Automation']").CountAsync(), Is.EqualTo(0),
            "A rep should not see Automation in the menu.");
    }

    [Test]
    public async Task A_time_based_rule_acts_once_on_a_lead_that_has_gone_quiet()
    {
        var tenantId = await Db.TenantIdAsync(World.A.Name);
        var mark = World.Unique("QuietCo");
        var ruleName = World.Unique("Nudge quiet leads");
        var admin = await SignedInAsync(World.A.Admin, Wide);

        try
        {
            await Step("Rule from the example: no activity for 3 days → follow-up task for the owner", async () =>
            {
                await admin.GotoAsync("/Settings/Automation/Edit?template=quiet");
                Assert.That(await admin.Locator("#trigger").InputValueAsync(), Is.EqualTo("2"), "The example should open as a time-based rule.");
                Assert.That(await admin.Locator("#timeKind").InputValueAsync(), Is.EqualTo("noActivity"));

                await admin.Locator("#ruleName").FillAsync(ruleName);
                await admin.Locator("#timeDays").FillAsync("3");

                await admin.Locator("#addCondition").ClickAsync();
                var line = admin.Locator(".cond-line").Last;
                await line.Locator(".cond-field").SelectOptionAsync("company");
                await line.Locator(".cond-op").SelectOptionAsync("contains");
                await line.Locator(".cond-value").FillAsync(mark);

                var people = await admin.Locator(".act-assign option").AllInnerTextsAsync();
                Assert.That(people.Any(p => p.Contains("made the change")), Is.False,
                    "A time-based rule must not offer \"the person who made the change\".");

                await SaveRuleAsync(admin, switchOn: true, "saved and switched on");

                var row = admin.Locator("tr.rule-row").Filter(new() { HasText = ruleName });
                Assert.That(await row.InnerTextAsync(), Does.Contain("no activity for 3 days").And.Contain("Time-based"));
            });

            var leadName = World.Unique("Quiet Lead");
            var leadId = await Step("A matching lead, made to look 5 days old", async () =>
            {
                var id = await Flows.CreateLeadAsync(admin, leadName, $"quiet.{World.Stamp}@example.com", 15000,
                    companyName: $"{mark} Supplies");
                await Db.BackdateLeadAsync(id, 5);
                return id;
            });

            await Step("Check time-based rules now: the follow-up task appears", async () =>
            {
                await admin.GotoAsync("/Settings/Automation");
                await admin.Locator("#checkTimeNow").ClickAsync();
                await Ui.ExpectSuccessAsync(admin, "time-based rule");

                Assert.That(await Db.CountActivitiesAsync(leadId, "Follow up with " + leadName), Is.EqualTo(1),
                    "The time-based rule's task is not on the lead.");
                Assert.That(await Db.CountAutomationRunsAsync(leadId, ruleName, TestDatabase.RunDone), Is.EqualTo(1),
                    "The rule log has no Done line for the quiet lead.");
            });

            await Step("Checking again does not act twice on the same quiet spell", async () =>
            {
                await admin.GotoAsync("/Settings/Automation");
                await admin.Locator("#checkTimeNow").ClickAsync();
                await Ui.ExpectSuccessAsync(admin, "time-based rule");

                Assert.That(await Db.CountActivitiesAsync(leadId, "Follow up with " + leadName), Is.EqualTo(1),
                    "A second check added a second task for the same quiet spell.");
            });
        }
        finally
        {
            await Db.RetireAutomationRulesAsync(tenantId, ruleName);
        }
    }
}
