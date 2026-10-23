// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/AutomationIdempotencyTests.cs
//
// NEW (100) — test 81, Critical · Background processing.
//
//   Retrying_an_automation_does_not_create_duplicate_tasks_or_notifications
//
// Two rules in workspace A, each with two actions — "create a task for the
// owner" and "notify the sales manager" — so a duplicate shows up twice
// over: as a second task AND as a second bell message.
//
// A TIME-BASED RULE ("a lead with no activity for 3 days"), on a lead made
// to look 5 days old:
//   1. REPEATED SCHEDULER RUNS. "Check time-based rules now" five times at
//      the same instant — the worker, a second server and a person pressing
//      the button together. → one task, one notification, one "acted on"
//      record, and no run logged as Failed.
//   2. A JOB RETRY. The check run again, twice, afterwards. → still one.
//   3. A WORKER INTERRUPTED. The worker acted, then stopped before its log
//      line was kept: the rule's log lines for the lead are removed (the
//      task and the "acted on" record stay, as they are saved together),
//      and the check runs again. → still one task, one notification.
//
// A CHANGE-SET-OFF RULE ("a lead's status becomes Qualified"):
//   4. THE SAME CHANGE SENT FOUR TIMES AT ONCE (a double-click, a retried
//      request). → the status changes once, the rule runs once: one task,
//      one notification, one Done line.
//   5. AND ONCE MORE AFTERWARDS (a retry that arrives late). → still one.
//
// ISOLATION. Both rules only act on leads whose company name carries this
// run's mark, and both are switched off and removed at the end. If the
// plan's automation limit would hold either rule back, it is raised for
// the test and put back afterwards.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Automation")]
public class AutomationIdempotencyTests : E2ETest
{
    [Test]
    public async Task Retrying_an_automation_does_not_create_duplicate_tasks_or_notifications()
    {
        var tenantId = await Db.TenantIdAsync(World.A.Name);
        var admin = await Api.AsAsync(World.A.Admin);
        await Api.EnsureTokensWorkAsync(admin);
        var manager = World.A["manager"];
        var managerId = (await Db.ApiIdentityAsync(manager.Email)).UserId;
        var page = await SignedInAsync(World.A.Admin);

        var mark = World.Unique("RetryCo");
        var prefix = World.Unique("Retry rule");
        var timeRule = prefix + " quiet";
        var eventRule = prefix + " qualified";

        var (planId, originalLimit) = await Db.PlanAutomationLimitAsync(tenantId);
        var limitRaised = false;

        try
        {
            await Step("Make room for two more switched-on rules on the plan", async () =>
            {
                var active = await Db.CountActiveAutomationRulesAsync(tenantId);
                if (active + 2 > originalLimit)
                {
                    await Db.SetPlanAutomationLimitAsync(planId, active + 2);
                    limitRaised = true;
                }
            });

            var qualifiedKey = await Step("Find workspace A's Qualified status", async () =>
            {
                var probe = await Flows.CreateLeadAsync(page, World.Unique("Retry Probe"),
                    World.EmailFor("A", "retryprobe" + Random.Shared.Next(10000, 99999)), 1000m);
                await page.GotoAsync($"/Leads/Detail/{probe}");
                return await page.EvaluateAsync<string?>(
                    @"() => {
                        const s = document.querySelector('#statusChangeForm select[name=status]');
                        if (!s) return null;
                        const o = Array.from(s.options).find(o =>
                            /qualified|ready to buy/i.test(o.text) && !/(dis|un|not\s*)qualified/i.test(o.text));
                        return o ? o.value : null;
                    }") ?? throw new AssertionException("The lead page offers no Qualified status.");
            });

            await Step("Rule 1 (time-based): no activity for 3 days → a task for the owner and a message to the manager", async () =>
                await SaveRuleAsync(admin, new
                {
                    name = timeRule,
                    entityType = "Lead",
                    trigger = "TimeBased",
                    timeKind = "noActivity",
                    timeDays = 3,
                    conditions = new[] { new { field = "company", op = "contains", value = mark } },
                    actions = Actions("Quiet", managerId),
                    isActive = true,
                }));

            await Step("Rule 2 (on a change): status becomes Qualified → a task for the owner and a message to the manager", async () =>
                await SaveRuleAsync(admin, new
                {
                    name = eventRule,
                    entityType = "Lead",
                    trigger = "FieldChanged",
                    triggerField = "status",
                    triggerToValue = qualifiedKey,
                    conditions = new[] { new { field = "company", op = "contains", value = mark } },
                    actions = Actions("Qualified", managerId),
                    isActive = true,
                }));

            // ── The time-based rule ──────────────────────────────────────
            var quietName = World.Unique("Retry Quiet Lead");
            var quietId = await Step("A matching lead, made to look 5 days old", async () =>
            {
                var id = await Flows.CreateLeadAsync(page, quietName,
                    World.EmailFor("A", "retryquiet" + Random.Shared.Next(10000, 99999)), 15000m, World.A, $"{mark} Supplies");
                await Db.BackdateLeadAsync(id, 5);
                return id;
            });

            await Step("Five checks at the same instant act once", async () =>
            {
                var results = await Api.AllAtOnceAsync(Enumerable.Range(0, 5)
                    .Select(_ => (Func<Task<ApiResponse>>)(() => admin.PostAsync("/api/automation/time-check")))
                    .ToArray());
                Assert.That(results.All(r => r.IsSuccess), Is.True,
                    "Every check should answer normally: " + string.Join(" | ", results.Select(r => r.ToString())));

                await ExpectOnceAsync(quietId, timeRule, "Quiet " + quietName, manager.Email);
                Assert.That(await Db.CountAutomationTimeFiresAsync(quietId, timeRule), Is.EqualTo(1),
                    "The lead should be recorded as acted on exactly once for this quiet spell.");
                Assert.That(await Db.CountAutomationRunsAsync(quietId, timeRule, TestDatabase.RunFailed), Is.EqualTo(0),
                    "A check that lost the race was logged as Failed — it should be treated as already done.");
            });

            await Step("A job retry: checking twice more does not act again", async () =>
            {
                for (var i = 0; i < 2; i++)
                {
                    var r = await admin.PostAsync("/api/automation/time-check");
                    Assert.That(r.IsSuccess, Is.True, $"The check failed: {r}");
                }
                await ExpectOnceAsync(quietId, timeRule, "Quiet " + quietName, manager.Email);
            });

            await Step("A worker interrupted after acting (its log line lost): the next check does not act again", async () =>
            {
                await Db.DeleteAutomationRunsAsync(quietId, timeRule);
                Assert.That(await Db.CountAllAutomationRunsAsync(quietId, timeRule), Is.EqualTo(0));

                var r = await admin.PostAsync("/api/automation/time-check");
                Assert.That(r.IsSuccess, Is.True, $"The check failed: {r}");

                Assert.That(await Db.CountTasksAsync(quietId, "Quiet " + quietName), Is.EqualTo(1),
                    "After the interruption the check created the task again.");
                Assert.That(await Db.CountNotificationsAsync(manager.Email, "Quiet " + quietName), Is.EqualTo(1),
                    "After the interruption the check notified the manager again.");
                Assert.That(await Db.CountAutomationTimeFiresAsync(quietId, timeRule), Is.EqualTo(1));
            });

            // ── The change-set-off rule ──────────────────────────────────
            var qualName = World.Unique("Retry Qual Lead");
            var qualId = await Step("A matching lead to qualify", () =>
                Flows.CreateLeadAsync(page, qualName,
                    World.EmailFor("A", "retryqual" + Random.Shared.Next(10000, 99999)), 25000m, World.A, $"{mark} Exports"));

            await Step("The same status change sent four times at once runs the rule once", async () =>
            {
                var path = $"/api/leads/{qualId}/status?status={Uri.EscapeDataString(qualifiedKey)}";
                var results = await Api.AllAtOnceAsync(Enumerable.Range(0, 4)
                    .Select(_ => (Func<Task<ApiResponse>>)(() => admin.PutAsync(path)))
                    .ToArray());
                Assert.That(results.All(r => r.IsSuccess), Is.True,
                    "Setting the status it already has should be a quiet success: " + string.Join(" | ", results.Select(r => r.ToString())));

                Assert.That(await Db.LeadStatusAsync(qualId), Is.EqualTo(qualifiedKey), "The lead's status.");
                await ExpectOnceAsync(qualId, eventRule, "Qualified " + qualName, manager.Email);
            });

            await Step("A late retry of the same change does not run it again", async () =>
            {
                var r = await admin.PutAsync($"/api/leads/{qualId}/status?status={Uri.EscapeDataString(qualifiedKey)}");
                Assert.That(r.IsSuccess, Is.True, $"The retry failed: {r}");
                await ExpectOnceAsync(qualId, eventRule, "Qualified " + qualName, manager.Email);
            });
        }
        finally
        {
            await Db.RetireAutomationRulesAsync(tenantId, prefix);
            if (limitRaised) await Db.SetPlanAutomationLimitAsync(planId, originalLimit);
        }
    }

    // =================================================================

    /// <summary>A task for the owner ("{label} {name}") and a message to the manager (same words).</summary>
    private static object[] Actions(string label, Guid managerId) => new object[]
    {
        new { kind = "task", subject = label + " {name}", dueInDays = 0, assignTo = "owner" },
        new { kind = "notify", assignTo = "user", userId = managerId.ToString(), message = label + " {name}" },
    };

    private static async Task SaveRuleAsync(ApiCaller admin, object rule)
    {
        var r = await admin.PostAsync("/api/automation/rules", rule);
        if (!r.IsSuccess) throw new AssertionException($"Saving the rule was refused: {r.Error} ({r.Status})");
    }

    /// <summary>One task, one bell message, one Done line — waiting briefly, since the engine runs after the save.</summary>
    private static async Task ExpectOnceAsync(Guid leadId, string ruleName, string text, string managerEmail)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await Db.CountTasksAsync(leadId, text) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(500);
        await Task.Delay(1500);   // anything late would have landed by now

        Assert.That(await Db.CountTasksAsync(leadId, text), Is.EqualTo(1), $"Tasks \"{text}\" on the lead.");
        Assert.That(await Db.CountNotificationsAsync(managerEmail, text), Is.EqualTo(1), $"The manager's messages \"{text}\".");
        Assert.That(await Db.CountAutomationRunsAsync(leadId, ruleName, TestDatabase.RunDone), Is.LessThanOrEqualTo(1),
            "The rule was logged as Done more than once for the lead.");
    }
}
