// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/NotificationTests.cs
//
// NEW (089). Notifications, end to end, in workspace D (India / INR).
// The event used throughout is "a lead was assigned to you": the admin
// assigns a lead on its Edit page, and the new owner is told.
//
//   1. Assigning a lead notifies the new owner:
//        • the rep's own choice (in the app AND by email) is saved, and
//          the page says so;
//        • exactly one notification is written, for the rep — none for the
//          admin who did the assigning;
//        • an email about it is queued for the rep (nothing is sent — the
//          test servers have no email key);
//        • the rep's bell shows it, unread, with the admin's name;
//        • another person in the same workspace cannot open it or mark it
//          read, even with its link;
//        • clicking it opens the lead, marks it read and the bell count
//          drops by one.
//   2. Turning it off is respected — unless the admin requires it:
//        • rep2 turns lead assignments off; a lead assigned to her raises
//          no notification and no email;
//        • the admin ticks Required (in the app) in Workspace defaults;
//        • rep2's settings show the row locked on, with "Set by your
//          workspace admin";
//        • un-ticking it by hand in the browser and saving does not switch
//          it off;
//        • the next lead assigned to her reaches her bell.
//      The workspace default is put back afterwards, pass or fail.
//
// Notifications are read from the database only to COUNT them exactly
// (one, not "at least one") and to check read/unread. Everything a person
// would do — choosing settings, opening the bell, clicking — is done
// through the screens.
// =====================================================================

using System.Globalization;
using System.Text.RegularExpressions;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Notifications")]
public class NotificationTests : E2ETest
{
    /// <summary>NotificationEventType.LeadAssigned — the value the
    /// settings rows post, and the number stored on a queued email.</summary>
    private const string LeadAssigned = "LeadAssigned";
    private const int LeadAssignedNumber = 6;

    private static TestTenant D => World.D ?? throw new InvalidOperationException("Workspace D was not built.");

    [OneTimeSetUp]
    public async Task BuildWorkspaceDAsync() => await WorldBuilder.EnsureWorkspaceDAsync();

    // =================================================================
    // 1. Assigned → told
    // =================================================================

    [Test]
    public async Task Assigning_a_lead_notifies_the_new_owner_in_the_app_and_by_email()
    {
        var rep = D["rep"];
        var rep2 = D["rep2"];
        var repPage = await SignedInAsync(rep);

        await Step("The rep chooses to hear about lead assignments in the app and by email", () =>
            SetMyLeadAssignedAsync(repPage, inApp: true, email: true));

        var admin = await SignedInAsync(D.Admin);
        var leadName = World.Unique("Meera Notify");
        var since = DateTime.UtcNow.AddSeconds(-5);

        var leadId = await Step("The admin adds a lead and assigns it to the rep", async () =>
        {
            var id = await Flows.CreateLeadAsync(admin, leadName,
                World.EmailFor("D", $"meera{Random.Shared.Next(1000, 9999)}"), 350000m, D);
            await AssignLeadAsync(admin, id, rep);
            return id;
        });

        await Step("Exactly one notification was written, for the rep, and none for the admin who assigned it", async () =>
        {
            Assert.That(await Db.CountNotificationsAsync(rep.Email, leadName), Is.EqualTo(1),
                "Notifications for the rep about this lead.");
            Assert.That(await Db.CountNotificationsAsync(D.Admin.Email, leadName), Is.EqualTo(0),
                "The admin was notified about their own assignment.");
        });

        await Step("An email about it was queued for the rep", async () =>
        {
            var queued = await Db.CountQueuedEmailsAsync(rep.Email, LeadAssignedNumber, since, atLeast: 1, waitSeconds: 10);
            Assert.That(queued, Is.GreaterThanOrEqualTo(1),
                "The rep asked for lead assignments by email, but no email was queued.");
        });

        var notificationId = await Step("The rep's bell shows it, unread, with who assigned it", async () =>
        {
            var row = await OpenBellRowAsync(repPage, leadName);
            await Assertions.Expect(row).ToHaveClassAsync(new Regex(@"\bunread\b"));
            await Assertions.Expect(row).ToContainTextAsync("You were assigned the lead");
            await Assertions.Expect(row).ToContainTextAsync(D.Admin.FullName);
            Assert.That(await BellCountAsync(repPage), Is.GreaterThanOrEqualTo(1), "The bell shows no unread count.");
            return Ui.IdFromUrl(await row.GetAttributeAsync("href") ?? "");
        });

        await Step("A colleague cannot open it or mark it read, even with its link", async () =>
        {
            var other = await SignedInAsync(rep2);
            var response = await other.GotoAsync($"/Notifications?open={notificationId}");
            var status = response?.Status ?? 0;

            Assert.That(status, Is.LessThan(500),
                $"Opening someone else's notification link crashed the page (HTTP {status}); it should just show the list.");
            Assert.That(Ui.PathOf(other.Url).Equals("/Notifications", StringComparison.OrdinalIgnoreCase), Is.True,
                $"Someone else's notification link took the colleague to {other.Url}.");
            Assert.That(await other.Locator("main").First.InnerTextAsync(), Does.Not.Contain(leadName),
                "The colleague can see the rep's notification.");
            Assert.That(await Db.IsNotificationReadAsync(notificationId), Is.False,
                "The colleague's click marked the rep's notification read.");
        });

        await Step("Clicking it opens the lead, marks it read and the count drops by one", async () =>
        {
            var row = await OpenBellRowAsync(repPage, leadName);
            var before = await BellCountAsync(repPage);

            await row.ClickAsync();
            await repPage.WaitForURLAsync(u => u.Contains($"/Leads/Detail/{leadId}", StringComparison.OrdinalIgnoreCase),
                new PageWaitForURLOptions { Timeout = E2ESettings.ActionTimeoutMs });

            Assert.That(await Db.IsNotificationReadAsync(notificationId), Is.True, "Opening it did not mark it read.");
            Assert.That(await BellCountAsync(repPage), Is.EqualTo(before - 1), "The bell count did not drop by one.");

            await repPage.GotoAsync("/Notifications");
            var item = repPage.Locator(".nf-item", new() { HasText = leadName });
            await Assertions.Expect(item).ToHaveCountAsync(1);
            await Assertions.Expect(item).Not.ToHaveClassAsync(new Regex(@"\bunread\b"));
        });
    }

    // =================================================================
    // 2. Off is off — unless Required
    // =================================================================

    [Test]
    public async Task Turning_lead_assignments_off_is_respected_unless_the_admin_requires_them()
    {
        var rep2 = D["rep2"];
        var me = await SignedInAsync(rep2);
        var admin = await SignedInAsync(D.Admin);
        (bool InApp, bool Locked)? original = null;

        try
        {
            await Step("Sana turns lead assignments off, in the app and by email", () =>
                SetMyLeadAssignedAsync(me, inApp: false, email: false));

            var quiet = World.Unique("Quiet Lead");
            await Step("A lead assigned to her raises no notification and no email", async () =>
            {
                var since = DateTime.UtcNow.AddSeconds(-5);
                var id = await Flows.CreateLeadAsync(admin, quiet,
                    World.EmailFor("D", $"quiet{Random.Shared.Next(1000, 9999)}"), 120000m, D);
                await AssignLeadAsync(admin, id, rep2);

                Assert.That(await Db.CountNotificationsAsync(rep2.Email, quiet), Is.EqualTo(0),
                    "She turned lead assignments off in the app, but was notified.");
                Assert.That(await Db.CountQueuedEmailsAsync(rep2.Email, LeadAssignedNumber, since, waitSeconds: 3), Is.EqualTo(0),
                    "She turned lead assignment emails off, but one was queued.");
            });

            original = await Step("The admin makes lead assignments Required, in the app", () => RequireInAppAsync(admin));

            await Step("Her settings show it locked on, set by the workspace admin", async () =>
            {
                // No ?tab= here on purpose: the next step waits for the
                // save's redirect to add it.
                await me.GotoAsync("/Settings/Notifications");
                var row = await LeadAssignedRowAsync(me, "EventTypes");
                await Assertions.Expect(row).ToContainTextAsync("Set by your workspace admin");

                var inApp = row.Locator("input[type=checkbox][name=InAppOn]");
                await Assertions.Expect(inApp).ToBeCheckedAsync();
                await Assertions.Expect(inApp).ToBeDisabledAsync();
            });

            await Step("Un-ticking it by hand in the browser and saving does not switch it off", async () =>
            {
                // What a hand-made request would send: the box enabled and
                // empty, and its hidden "still on" copy gone.
                var row = await LeadAssignedRowAsync(me, "EventTypes");
                await row.EvaluateAsync<bool>(
                    @"r => {
                        r.querySelectorAll('input[name=InAppOn]').forEach(i => {
                            if (i.type === 'hidden') { i.remove(); } else { i.disabled = false; i.checked = false; }
                        });
                        return true;
                    }");
                await me.GetByRole(AriaRole.Button, new() { Name = "Save preferences" }).ClickAsync();
                await me.WaitForURLAsync(u => u.Contains("tab=preferences", StringComparison.OrdinalIgnoreCase),
                    new PageWaitForURLOptions { Timeout = E2ESettings.ActionTimeoutMs });

                await me.GotoAsync("/Settings/Notifications?tab=preferences");
                var after = await LeadAssignedRowAsync(me, "EventTypes");
                await Assertions.Expect(after.Locator("input[type=checkbox][name=InAppOn]")).ToBeCheckedAsync();
            });

            var loud = World.Unique("Loud Lead");
            await Step("The next lead assigned to her reaches her bell anyway", async () =>
            {
                var id = await Flows.CreateLeadAsync(admin, loud,
                    World.EmailFor("D", $"loud{Random.Shared.Next(1000, 9999)}"), 180000m, D);
                await AssignLeadAsync(admin, id, rep2);

                Assert.That(await Db.CountNotificationsAsync(rep2.Email, loud), Is.EqualTo(1),
                    "Lead assignments are Required in the app, but she was not notified.");
                await OpenBellRowAsync(me, loud);
            });
        }
        finally
        {
            if (original is { } o)
            {
                try { await SetDefaultsAsync(admin, o.InApp, o.Locked); }
                catch (Exception ex)
                {
                    TestContext.Progress.WriteLine(
                        $"[e2e] Could not put workspace D's lead-assignment default back: {ex.Message}");
                }
            }
        }
    }

    // =================================================================
    // Helpers
    // =================================================================

    /// <summary>
    /// The "lead assigned" row on Settings → Notifications: by the event
    /// type its hidden input posts, or — if the value is spelled
    /// differently — by its label. <paramref name="hiddenName"/> is
    /// "EventTypes" on My preferences, "DefaultEventTypes" on Workspace
    /// defaults.
    /// </summary>
    private static async Task<ILocator> LeadAssignedRowAsync(IPage page, string hiddenName)
    {
        var byValue = page.Locator($".ns-row:has(input[type=hidden][name={hiddenName}][value='{LeadAssigned}'])");
        if (await byValue.CountAsync() == 1) return byValue;

        var byLabel = page.Locator(".ns-row", new()
        {
            HasTextRegex = new Regex(@"lead[^.]*assign|assign[^.]*lead", RegexOptions.IgnoreCase)
        });
        if (await byLabel.CountAsync() == 1) return byLabel;

        var rows = await page.Locator(".ns-row .ns-row-label").AllInnerTextsAsync();
        throw new AssertionException(
            $"Settings → Notifications ({Ui.PathOf(page.Url)}) has no single 'lead assigned' row. " +
            (rows.Count == 0
                ? await Ui.DescribePageProblemsAsync(page)
                : "Rows: " + string.Join(" | ", rows.Select(r => Regex.Replace(r, @"\s+", " ").Trim()))));
    }

    /// <summary>My preferences → the lead-assigned row → Save, and checks
    /// both the message and that the choice is still there on reload.</summary>
    private static async Task SetMyLeadAssignedAsync(IPage page, bool inApp, bool email)
    {
        await page.GotoAsync("/Settings/Notifications?tab=preferences");
        var row = await LeadAssignedRowAsync(page, "EventTypes");
        await row.Locator("input[type=checkbox][name=InAppOn]").SetCheckedAsync(inApp);
        await row.Locator("input[type=checkbox][name=EmailOn]").SetCheckedAsync(email);

        await page.GetByRole(AriaRole.Button, new() { Name = "Save preferences" }).ClickAsync();
        await Ui.ExpectSuccessAsync(page, "Your notification preferences have been saved");

        await page.GotoAsync("/Settings/Notifications?tab=preferences");
        row = await LeadAssignedRowAsync(page, "EventTypes");
        Assert.That(await row.Locator("input[type=checkbox][name=InAppOn]").IsCheckedAsync(), Is.EqualTo(inApp),
            "The in-app choice did not stick after saving.");
        Assert.That(await row.Locator("input[type=checkbox][name=EmailOn]").IsCheckedAsync(), Is.EqualTo(email),
            "The email choice did not stick after saving.");
    }

    /// <summary>Workspace defaults → lead assigned: In app on and Required
    /// ticked. Returns what the two boxes were before, to put back.</summary>
    private static async Task<(bool InApp, bool Locked)> RequireInAppAsync(IPage admin)
    {
        await admin.GotoAsync("/Settings/Notifications?tab=defaults");
        var row = await LeadAssignedRowAsync(admin, "DefaultEventTypes");
        var before = (
            await row.Locator("input[type=checkbox][name=DefaultInAppOn]").IsCheckedAsync(),
            await row.Locator("input[type=checkbox][name=DefaultLockedOn]").IsCheckedAsync());

        await SetDefaultsAsync(admin, inApp: true, locked: true);
        return before;
    }

    /// <summary>Workspace defaults → lead assigned → Save workspace defaults.</summary>
    private static async Task SetDefaultsAsync(IPage admin, bool inApp, bool locked)
    {
        await admin.GotoAsync("/Settings/Notifications?tab=defaults");
        var row = await LeadAssignedRowAsync(admin, "DefaultEventTypes");
        await row.Locator("input[type=checkbox][name=DefaultInAppOn]").SetCheckedAsync(inApp);
        await row.Locator("input[type=checkbox][name=DefaultLockedOn]").SetCheckedAsync(locked);

        await admin.GetByRole(AriaRole.Button, new() { Name = "Save workspace defaults" }).ClickAsync();
        await Ui.ExpectSuccessAsync(admin, "Workspace defaults saved");
    }

    /// <summary>Leads → Edit → Assign To → Save Changes.</summary>
    private static async Task AssignLeadAsync(IPage admin, Guid leadId, TestUser owner)
    {
        await admin.GotoAsync($"/Leads/Edit/{leadId}");
        await Ui.SelectByTextAsync(admin.Locator("#Input_OwnerUserId"), owner.FullName);
        await admin.GetByRole(AriaRole.Button, new() { Name = "Save Changes" }).ClickAsync();
        await Ui.ExpectSuccessAsync(admin, "Lead updated");
    }

    /// <summary>
    /// The number on the bell, once the page's first poll has answered
    /// (until then the dropdown says "Loading…"). 0 when none is shown.
    /// </summary>
    private static async Task<int> BellCountAsync(IPage page)
    {
        var items = page.Locator("#lfNotifItems");
        await Ui.WaitUntilAsync(page,
            async () => !((await items.TextContentAsync()) ?? "Loading").Contains("Loading"),
            "The notification bell never finished loading.");

        var count = page.Locator("#lfNotifCount");
        if (await count.IsHiddenAsync()) return 0;

        var text = ((await count.TextContentAsync()) ?? "").Trim();
        if (text == "99+") return 100;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new AssertionException($"The bell shows \"{text}\", which is not a number.");
    }

    /// <summary>Dashboard → open the bell → the row about this lead.</summary>
    private static async Task<ILocator> OpenBellRowAsync(IPage page, string leadName)
    {
        await page.GotoAsync("/Dashboard");
        await BellCountAsync(page);
        await page.Locator("#lfNotifBell").ClickAsync();

        var row = page.Locator("#lfNotifItems a.lf-notif-row", new() { HasText = leadName });
        try
        {
            await Assertions.Expect(row).ToBeVisibleAsync();
        }
        catch (Exception ex) when (ex is not AssertionException)
        {
            var listed = ((await page.Locator("#lfNotifItems").TextContentAsync()) ?? "").Trim();
            throw new AssertionException(
                $"The bell has no notification about \"{leadName}\". It lists: " +
                (listed.Length == 0 ? "(nothing)" : Regex.Replace(listed, @"\s+", " ")));
        }
        return row;
    }
}
