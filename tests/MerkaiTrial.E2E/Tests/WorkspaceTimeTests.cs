// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/WorkspaceTimeTests.cs
//
// TEST 91 (round 103) —
//   A_task_due_date_and_automation_follow_the_correct_workspace_timezone
//
// Workspace T is an Indian business on New York time, so its clocks change
// twice a year (A, B and D are on India time and C on Thailand's, which
// never change). The test machine's own zone plays no part: every expected
// time is worked out for America/New_York here and compared with what the
// app stored (UTC) and showed (workspace time).
//
//   1. Workspace T really is on New York time (Admin → Tenants → Create
//      offered "Eastern Time (US)" and it was stored as America/New_York).
//   2. Tasks typed on the lead's Tasks tab, in workspace time:
//        • an ordinary winter morning and an ordinary summer morning —
//          09:00 is 14:00 UTC in winter and 13:00 UTC in summer;
//        • 02:30 on the morning the clocks go forward (14 March 2027), a
//          time that never happens: stored as 07:30 UTC and shown as 03:30
//          (moved forward by the jump). Before 103 it was stored as 02:30
//          UTC and shown as 21:30 the evening BEFORE;
//        • 01:30 on the night the clocks go back (7 November 2027), a time
//          that happens twice: the first one, 05:30 UTC (before 103, the
//          second: 06:30 UTC, an hour late);
//        • 09:00 the morning after the clocks went forward: 13:00 UTC.
//   3. An automation rule ("a lead is added → a task due today, and one due
//      in N days") on T's clock: today's is 18:00 New York time, and the
//      later one 10:00 New York time on a day chosen to be just after the
//      next clock change — so it must use THAT day's offset, not today's.
//   4. The super admin moves T to London time (Admin → Tenants → Edit):
//      the tasks already there keep their moment and show London's clock;
//      01:30 on the morning London's clocks go forward (28 March 2027) is
//      stored as 01:30 UTC and shown as 02:30.
// =====================================================================

using System.Globalization;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("TimeZones")]
public class WorkspaceTimeTests : E2ETest
{
    private static readonly TimeZoneInfo NewYork = Zone("America/New_York", "Eastern Standard Time");
    private static readonly TimeZoneInfo London = Zone("Europe/London", "GMT Standard Time");

    [Test]
    public async Task A_task_due_date_and_automation_follow_the_correct_workspace_timezone()
    {
        var t = await Step("Workspace T (India, on New York time) — built the first time it is needed",
            () => WorldBuilder.EnsureWorkspaceTAsync());
        var tenantId = await Db.TenantIdAsync(t.Name);

        await Step("Workspace T is stored on New York time", async () =>
        {
            var tz = await Db.TenantTimezoneAsync(tenantId);
            Assert.That(tz, Is.EqualTo("America/New_York"),
                "Admin → Tenants → Create did not store New York as workspace T's time zone " +
                "(was \"Eastern Time (US)\" offered?). The daylight-saving checks need a zone whose clocks change.");
        });

        var page = await SignedInAsync(t.Admin);
        var m = Random.Shared.Next(10000, 99999).ToString();
        var leadId = await Step("A lead to hang the tasks on", () =>
            Flows.CreateLeadAsync(page, World.Unique("Clock Lead"), World.EmailFor("T", "clock" + m), 5000m, t, "Clock Co " + m));

        // ── 2. Tasks typed in workspace time ─────────────────────────────
        var cases = new (string Label, DateTime Local, DateTime Utc, DateTime Shown)[]
        {
            ("an ordinary winter morning",
                At(2027, 1, 15, 9, 0),  At(2027, 1, 15, 14, 0),  At(2027, 1, 15, 9, 0)),
            ("an ordinary summer morning (daylight time)",
                At(2027, 7, 15, 9, 0),  At(2027, 7, 15, 13, 0),  At(2027, 7, 15, 9, 0)),
            ("02:30 on the morning the clocks go forward — a time that never happens",
                At(2027, 3, 14, 2, 30), At(2027, 3, 14, 7, 30),  At(2027, 3, 14, 3, 30)),
            ("01:30 on the night the clocks go back — a time that happens twice",
                At(2027, 11, 7, 1, 30), At(2027, 11, 7, 5, 30),  At(2027, 11, 7, 1, 30)),
            ("09:00 the morning after the clocks went forward",
                At(2027, 3, 15, 9, 0),  At(2027, 3, 15, 13, 0),  At(2027, 3, 15, 9, 0)),
        };

        var subjects = new Dictionary<DateTime, string>();
        foreach (var c in cases)
        {
            var subject = $"Clock {m} {c.Local:yyyyMMdd-HHmm}";
            subjects[c.Utc] = subject;

            await Step($"A task due {c.Label} ({c.Local:dd/MM/yyyy HH:mm} New York) is stored as {c.Utc:HH:mm} UTC and shown as {c.Shown:HH:mm}", async () =>
            {
                await AddLeadTaskAsync(page, leadId, subject, c.Local);

                var stored = await Db.TaskDueTimesAsync(leadId, subject);
                Assert.That(stored, Has.Count.EqualTo(1), $"Expected one task \"{subject}\".");
                Assert.That(stored[0], Is.EqualTo(c.Utc),
                    $"{c.Local:dd/MM/yyyy HH:mm} in New York was stored as {stored[0]:dd/MM/yyyy HH:mm} UTC, " +
                    $"not {c.Utc:dd/MM/yyyy HH:mm} UTC.");

                await ExpectShownAsync(page, leadId, subject, c.Shown);
            });
        }

        // ── 3. Automation on the workspace's clock ───────────────────────
        var (planId, originalLimit) = await Db.PlanAutomationLimitAsync(tenantId);
        var limitRaised = false;
        var prefix = World.Unique("Clock rule");
        var mark = "ClockAuto " + m;

        // The first day, from today on, whose 10:00 is on the other side of
        // New York's next clock change — so the rule must use that day's
        // offset, not today's.
        var todayNy = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, NewYork).Date;
        var laterDays = Enumerable.Range(1, 365).First(d =>
            NewYork.GetUtcOffset(todayNy.AddDays(d).AddHours(10)) != NewYork.GetUtcOffset(todayNy.AddHours(10)));
        TestContext.Progress.WriteLine($"    the later task is due in {laterDays} day(s), just after New York's next clock change");

        try
        {
            await Step("Make room for one switched-on rule on the plan", async () =>
            {
                var active = await Db.CountActiveAutomationRulesAsync(tenantId);
                if (active + 1 > originalLimit)
                {
                    await Db.SetPlanAutomationLimitAsync(planId, active + 1);
                    limitRaised = true;
                }
            });

            var admin = await Api.AsAsync(t.Admin);
            await Api.EnsureTokensWorkAsync(admin);

            await Step($"Rule: a lead is added → a task due today, and a task due in {laterDays} day(s)", async () =>
            {
                var r = await admin.PostAsync("/api/automation/rules", new
                {
                    name = prefix,
                    entityType = "Lead",
                    trigger = "RecordCreated",
                    conditions = new[] { new { field = "company", op = "contains", value = mark } },
                    actions = new object[]
                    {
                        new { kind = "task", subject = "ClockToday {name}", dueInDays = 0, assignTo = "owner" },
                        new { kind = "task", subject = "ClockLater {name}", dueInDays = laterDays, assignTo = "owner" },
                    },
                    isActive = true,
                });
                Assert.That(r.IsSuccess, Is.True, $"Saving the rule was refused: {r}");
            });

            var autoName = World.Unique("Clock Auto Lead");
            var before = DateTime.UtcNow;
            var autoLeadId = await Step("Add a lead the rule matches", () =>
                Flows.CreateLeadAsync(page, autoName, World.EmailFor("T", "clockauto" + m), 7000m, t, mark + " Ltd"));
            var after = DateTime.UtcNow;

            await Step("Today's task is due 18:00 New York time (or an hour from now, if that has passed)", async () =>
            {
                var due = await WaitForOneTaskAsync(autoLeadId, "ClockToday");
                var ok = new[] { before, after }.Any(now =>
                {
                    var local = TimeZoneInfo.ConvertTimeFromUtc(now, NewYork).Date.AddHours(18);
                    var expected = TimeZoneInfo.ConvertTimeToUtc(local, NewYork);
                    return expected > now ? due == expected : due >= before.AddHours(1).AddSeconds(-5) && due <= after.AddHours(1).AddSeconds(5);
                });
                Assert.That(ok, Is.True,
                    $"The task due today was stored as {due:dd/MM/yyyy HH:mm} UTC = " +
                    $"{TimeZoneInfo.ConvertTimeFromUtc(due, NewYork):dd/MM/yyyy HH:mm} New York, not 18:00 New York today " +
                    "(or an hour after the lead was added, when 18:00 had passed).");
            });

            await Step($"The later task is due 10:00 New York time in {laterDays} day(s), on that day's offset — and shown as 10:00", async () =>
            {
                var due = await WaitForOneTaskAsync(autoLeadId, "ClockLater");
                var candidates = new[] { before, after }
                    .Select(now => TimeZoneInfo.ConvertTimeFromUtc(now, NewYork).Date.AddDays(laterDays).AddHours(10))
                    .Distinct()
                    .ToList();
                var match = candidates.Where(local => TimeZoneInfo.ConvertTimeToUtc(local, NewYork) == due).ToList();
                Assert.That(match, Is.Not.Empty,
                    $"The later task was stored as {due:dd/MM/yyyy HH:mm} UTC = " +
                    $"{TimeZoneInfo.ConvertTimeFromUtc(due, NewYork):dd/MM/yyyy HH:mm} New York; expected 10:00 New York on " +
                    string.Join(" or ", candidates.Select(c => c.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture))) + ".");

                await ExpectShownAsync(page, autoLeadId, "ClockLater", match[0]);
            });
        }
        finally
        {
            await Db.RetireAutomationRulesAsync(tenantId, prefix);
            if (limitRaised) await Db.SetPlanAutomationLimitAsync(planId, originalLimit);
        }

        // ── 4. The workspace moves to London time ────────────────────────
        await Step("The super admin moves workspace T to London time (Admin → Tenants → Edit)", async () =>
        {
            var super = await SignedInAsync(World.SuperAdmin);
            await super.GotoAsync($"/Admin/Tenants/Edit/{tenantId}");
            await Ui.SelectByTextAsync(super.Locator("#Input_TimeZone"), "Europe/London");
            await super.Locator("form button[type=submit]", new() { HasText = "Save" }).First.ClickAsync();
            await Ui.WaitForAddressAsync(super, u => Ui.PathOf(u).StartsWith("/Admin/Tenants/Detail", StringComparison.OrdinalIgnoreCase));
            Assert.That(await Db.TenantTimezoneAsync(tenantId), Is.EqualTo("Europe/London"));
        });

        await Step("The tasks already there keep their moment and show London's clock (15/01/2027 09:00 New York = 14:00 London)", async () =>
        {
            var winter = At(2027, 1, 15, 14, 0);
            Assert.That((await Db.TaskDueTimesAsync(leadId, subjects[winter]))[0], Is.EqualTo(winter),
                "Changing the workspace's zone changed a task's stored moment.");
            await ExpectShownAsync(page, leadId, subjects[winter], At(2027, 1, 15, 14, 0));
        });

        await Step("01:30 on the morning London's clocks go forward (28/03/2027) is stored as 01:30 UTC and shown as 02:30", async () =>
        {
            var subject = $"Clock {m} London gap";
            await AddLeadTaskAsync(page, leadId, subject, At(2027, 3, 28, 1, 30));
            var stored = await Db.TaskDueTimesAsync(leadId, subject);
            Assert.That(stored, Has.Count.EqualTo(1));
            Assert.That(stored[0], Is.EqualTo(At(2027, 3, 28, 1, 30)),
                $"01:30 on 28/03/2027 in London was stored as {stored[0]:dd/MM/yyyy HH:mm} UTC.");
            Assert.That(TimeZoneInfo.ConvertTimeFromUtc(stored[0], London).Hour, Is.EqualTo(2));
            await ExpectShownAsync(page, leadId, subject, At(2027, 3, 28, 2, 30));
        });
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static DateTime At(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Unspecified);

    private static TimeZoneInfo Zone(string iana, string windows)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById(windows); }
    }

    /// <summary>The lead's Tasks tab → a task for me, due <paramref name="dueLocal"/> (workspace time).</summary>
    private static async Task AddLeadTaskAsync(IPage page, Guid leadId, string subject, DateTime dueLocal)
    {
        await page.GotoAsync($"/Leads/Detail/{leadId}");
        await page.Locator("#tasks-tab").ClickAsync();
        await Assertions.Expect(page.Locator("#tasks")).ToBeVisibleAsync();

        await page.Locator("#TaskInput_ActivityType").SelectOptionAsync("Task");
        await page.Locator("#TaskInput_Subject").FillAsync(subject);
        await page.Locator("#TaskInput_DueDate").FillAsync(dueLocal.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture));

        var assign = page.Locator("#TaskInput_AssignedToUserId");
        if (await assign.CountAsync() > 0) await assign.SelectOptionAsync("");

        await page.Locator("#tasks form button[type=submit]").First.ClickAsync();
        await Ui.ExpectSuccessAsync(page, "Task created");
    }

    /// <summary>The lead's Tasks tab lists the task with <paramref name="shown"/> as its due time.</summary>
    private static async Task ExpectShownAsync(IPage page, Guid leadId, string subject, DateTime shown)
    {
        await page.GotoAsync($"/Leads/Detail/{leadId}");
        await page.Locator("#tasks-tab").ClickAsync();
        var item = page.Locator("#tasks .list-group-item", new() { HasText = subject });
        await Assertions.Expect(item).ToHaveCountAsync(1);

        var text = System.Text.RegularExpressions.Regex.Replace(await item.InnerTextAsync(), @"\s+", " ").Trim();
        Assert.That(Ui.FindDateTimes(text).Contains(shown), Is.True,
            $"Expected \"{subject}\" to show as due {shown:dd/MM/yyyy HH:mm} (workspace time), but it shows: \"{text}\"");
    }

    /// <summary>The one task a rule made on the record, waiting briefly — the rule runs just after the save.</summary>
    private static async Task<DateTime> WaitForOneTaskAsync(Guid entityId, string subjectContains)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        List<DateTime> found;
        while ((found = await Db.TaskDueTimesAsync(entityId, subjectContains)).Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(500);

        Assert.That(found, Has.Count.EqualTo(1), $"Expected the rule to make one \"{subjectContains}\" task on the lead.");
        return found[0];
    }
}
