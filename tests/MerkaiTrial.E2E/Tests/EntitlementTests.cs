// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/EntitlementTests.cs
//
// TEST 92 (round 103) —
//   Expired_trials_and_plan_limit_changes_are_enforced_on_write_operations
//
// The server, not the screen, decides what a workspace may write. Uses
// workspace E (India, an admin and one rep) and nobody else's, because it
// lowers E's limits and runs its trial out. Everything it changes is put
// back at the end, pass or fail.
//
// Part 1 — the super admin changes E's limits (Admin → Tenants → Settings)
// and the very next write obeys them:
//   • one lead's room left: two leads sent at the same instant → exactly
//     one is saved (before 103 both counted the same number and both got
//     in);
//   • full: the Add lead form says so, and nothing is saved;
//   • at the deal limit, converting a qualified lead is refused, and
//     nothing is half-made — no deal, contact or company, and the lead is
//     not marked converted (before 103 conversion checked no limit at all);
//   • at the people limit, adding a person is refused;
//   • the limits are raised again: the same three writes go through at once.
//
// Part 2 — the trial runs out (its end date moved to a minute ago; nothing
// on screen ends a trial early):
//   • with a token issued while the trial was running, every kind of write
//     — add, change, delete a lead, add a task, add a person, run the
//     time-based rules — is refused with 403 "Trial ended", and nothing
//     changes;
//   • reads still work (the client can still see and export their data);
//   • on the pages, saving a new lead lands on "Your trial has ended";
//   • the trial is extended: the same token writes again at once.
//
// Part 3 — the workspace is paused (suspended): read-only in the same way,
// and the page says "paused" rather than "trial ended". Before 103 the
// suspended switch was only ever checked by inbound email.
// =====================================================================

using System.Globalization;
using System.Text.RegularExpressions;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Entitlements")]
public class EntitlementTests : E2ETest
{
    [Test]
    public async Task Expired_trials_and_plan_limit_changes_are_enforced_on_write_operations()
    {
        var e = await Step("Workspace E (India, an admin and one rep) — built the first time it is needed",
            () => WorldBuilder.EnsureWorkspaceEAsync());
        var tenantId = await Db.TenantIdAsync(e.Name);
        var admin = await Api.AsAsync(e.Admin);
        await Api.EnsureTokensWorkAsync(admin);
        var page = await SignedInAsync(e.Admin);
        var m = Random.Shared.Next(10000, 99999).ToString();

        var originalLimits = await Db.WorkspaceLimitsAsync(tenantId)
            ?? throw new AssertionException("Workspace E has no TenantSettings row, so its limits can't be tested.");
        var originalTrial = await Db.TrialStateAsync(tenantId);

        try
        {
            // ══ Part 1: limits changed by the super admin ═════════════════
            var (firstLead, secondLead) = await Step("Two qualified leads; the first becomes a deal", async () =>
            {
                var one = await Flows.CreateLeadAsync(page, World.Unique("Quota One"), World.EmailFor("E", "quota1" + m), 10000m, e, "Quota One " + m);
                await Flows.QualifyLeadAsync(page);
                await Flows.ConvertLeadAsync(page, "Quota deal one " + m, 10000m);

                var two = await Flows.CreateLeadAsync(page, World.Unique("Quota Two"), World.EmailFor("E", "quota2" + m), 20000m, e, "Quota Two " + m);
                await Flows.QualifyLeadAsync(page);
                return (one, two);
            });

            var leads = await Db.CountLiveAsync("Leads", tenantId);
            var deals = await Db.CountLiveAsync("Deals", tenantId);
            var users = await Db.CountLiveAsync("Users", tenantId);
            Assert.That(deals, Is.GreaterThanOrEqualTo(1), "The first lead's conversion made no deal.");

            var super = await SignedInAsync(World.SuperAdmin);
            await Step($"The super admin lowers E's limits: room for one more lead ({leads + 1}), no more deals ({deals}), no more people ({users})", () =>
                SetLimitsOnPageAsync(super, tenantId, new TestDatabase.WorkspaceLimits(users, leads + 1, deals)));

            await Step("One lead's room left: two leads sent at the same instant → exactly one is saved, the other is refused", async () =>
            {
                var answers = await Api.AllAtOnceAsync(
                    () => admin.PostAsync("/api/leads", LeadBody(tenantId, e, "Race A " + m)),
                    () => admin.PostAsync("/api/leads", LeadBody(tenantId, e, "Race B " + m)));
                TestContext.Progress.WriteLine($"    the two saves answered {answers[0].Status}, {answers[1].Status}");

                Assert.That(answers.Count(a => a.IsSuccess), Is.EqualTo(1), $"Expected exactly one to be saved: {answers[0]} / {answers[1]}");
                var refused = answers.Single(a => !a.IsSuccess);
                Assert.That(refused.Status, Is.EqualTo(422), $"The second should be refused for the plan: {refused}");
                Assert.That(refused.Body, Does.Contain("plan_limit_exceeded"), $"The refusal doesn't say it's the plan: {refused}");
                Assert.That(await Db.CountLiveAsync("Leads", tenantId), Is.EqualTo(leads + 1),
                    "The workspace went over its lead limit.");
            });

            await Step("Full: the Add lead form says so, and nothing is saved", async () =>
            {
                await SubmitNewLeadFormAsync(page, e, World.Unique("Quota Full"), World.EmailFor("E", "quotafull" + m));
                var said = await FormRefusalAsync(page);
                Assert.That(said, Does.Contain("limit").IgnoreCase,
                    $"The form was refused, but not for the plan's limit: \"{said}\"");
                Assert.That(await Db.CountLiveAsync("Leads", tenantId), Is.EqualTo(leads + 1));
            });

            await Step("At the deal limit, converting a qualified lead is refused — no deal, contact or company is made", async () =>
            {
                var before = (Deals: await Db.CountLiveAsync("Deals", tenantId),
                              Contacts: await Db.CountLiveAsync("Contacts", tenantId),
                              Companies: await Db.CountLiveAsync("Companies", tenantId));

                var r = await admin.PostAsync($"/api/leads/{secondLead}/convert-to-deal", ConvertBody(tenantId, secondLead, "Quota deal two " + m));
                Assert.That(r.Status, Is.EqualTo(400), $"Converting at the deal limit was not refused: {r}");
                Assert.That(r.Error, Does.Contain("limit").IgnoreCase, $"The refusal doesn't say it's the plan: {r}");

                Assert.That(await Db.CountLiveAsync("Deals", tenantId), Is.EqualTo(before.Deals), "A deal was made anyway.");
                Assert.That(await Db.CountLiveAsync("Contacts", tenantId), Is.EqualTo(before.Contacts), "A contact was half-made.");
                Assert.That(await Db.CountLiveAsync("Companies", tenantId), Is.EqualTo(before.Companies), "A company was half-made.");
                Assert.That((await Db.LeadStateAsync(secondLead)).IsConverted, Is.False, "The lead was marked converted.");
            });

            var extraEmail = World.EmailFor("E", "extra" + m);
            await Step("At the people limit, adding a person is refused", async () =>
            {
                var r = await admin.PostAsync("/api/users", UserBody(tenantId, extraEmail));
                Assert.That(r.Status, Is.EqualTo(400), $"Adding a person at the limit was not refused: {r}");
                Assert.That(r.Error, Does.Contain("limit").IgnoreCase, $"The refusal doesn't say it's the plan: {r}");
                Assert.That(await Db.LiveUserExistsAsync(extraEmail), Is.False, "The person was added anyway.");
            });

            await Step("The super admin raises the limits; the same writes go through at once", async () =>
            {
                var now = (Leads: await Db.CountLiveAsync("Leads", tenantId), Deals: await Db.CountLiveAsync("Deals", tenantId),
                           Users: await Db.CountLiveAsync("Users", tenantId));
                await SetLimitsOnPageAsync(super, tenantId, new TestDatabase.WorkspaceLimits(now.Users + 2, now.Leads + 5, now.Deals + 5));

                var convert = await admin.PostAsync($"/api/leads/{secondLead}/convert-to-deal", ConvertBody(tenantId, secondLead, "Quota deal two " + m));
                Assert.That(convert.IsSuccess, Is.True, $"Converting failed after the deal limit was raised: {convert}");
                Assert.That((await Db.LeadStateAsync(secondLead)).IsConverted, Is.True);

                var person = await admin.PostAsync("/api/users", UserBody(tenantId, extraEmail));
                Assert.That(person.IsSuccess, Is.True, $"Adding a person failed after the limit was raised: {person}");

                var lead = await admin.PostAsync("/api/leads", LeadBody(tenantId, e, "After raise " + m));
                Assert.That(lead.IsSuccess, Is.True, $"Adding a lead failed after the limit was raised: {lead}");
            });

            // ══ Part 2: the trial runs out ════════════════════════════════
            var probe = await Step("A lead to try the writes on", async () =>
            {
                var r = await admin.PostAsync("/api/leads", LeadBody(tenantId, e, "Probe " + m));
                Assert.That(r.IsSuccess, Is.True, $"Adding the probe lead failed: {r}");
                return r.Json().GetProperty("id").GetGuid();
            });
            var probeStatus = (await Db.LeadStateAsync(probe)).Status;

            // Issued while the trial is running, as a client would hold it.
            var heldToken = Api.Mint(await Db.ApiIdentityAsync(e.Admin.Email), expiresUtc: DateTime.UtcNow.AddMinutes(20));
            var held = new ApiCaller("E's admin, with a token from before", () => heldToken);

            await Step("The trial runs out (its end date moved to a minute ago)", () =>
                Db.SetTrialStateAsync(tenantId, originalTrial with
                {
                    Plan = "trial", TrialExpiresAt = DateTime.UtcNow.AddMinutes(-1), IsSuspended = false,
                }));

            await Step("Every kind of write is refused with 403 \"Trial ended\", and nothing changes", async () =>
            {
                var leadsBefore = await Db.CountLiveAsync("Leads", tenantId);
                var taskSubject = "Expired task " + m;
                var lateEmail = World.EmailFor("E", "late" + m);

                await ExpectReadOnlyAsync("Add a lead", "Trial ended",
                    held.PostAsync("/api/leads", LeadBody(tenantId, e, "Expired add " + m)));
                await ExpectReadOnlyAsync("Change a lead's status", "Trial ended",
                    held.PutAsync($"/api/leads/{probe}/status?status=" + Uri.EscapeDataString(probeStatus + "x")));
                await ExpectReadOnlyAsync("Delete a lead", "Trial ended",
                    held.DeleteAsync($"/api/leads/{probe}"));
                await ExpectReadOnlyAsync("Add a task", "Trial ended",
                    held.PostAsync("/api/activities", new
                    {
                        entityType = "Lead", entityId = probe, activityType = "Task", subject = taskSubject,
                        isTask = true, dueDate = DateTime.UtcNow.AddDays(2),
                    }));
                await ExpectReadOnlyAsync("Add a person", "Trial ended",
                    held.PostAsync("/api/users", UserBody(tenantId, lateEmail)));
                await ExpectReadOnlyAsync("Run the time-based rules", "Trial ended",
                    held.PostAsync("/api/automation/time-check"));

                Assert.That(await Db.CountLiveAsync("Leads", tenantId), Is.EqualTo(leadsBefore), "A lead was added.");
                Assert.That((await Db.LeadStateAsync(probe)).Status, Is.EqualTo(probeStatus), "The lead's status changed.");
                Assert.That(await Db.IsRowDeletedAsync("Leads", probe), Is.False, "The lead was deleted.");
                Assert.That(await Db.CountTasksAsync(probe, taskSubject), Is.EqualTo(0), "The task was added.");
                Assert.That(await Db.LiveUserExistsAsync(lateEmail), Is.False, "The person was added.");
            });

            await Step("Reads still work: the lead list and the lead itself", async () =>
            {
                var list = await held.GetAsync("/api/leads?pageNumber=1&pageSize=5");
                Assert.That(list.Status, Is.EqualTo(200), $"Listing leads in an expired workspace failed: {list}");
                var one = await held.GetAsync($"/api/leads/{probe}");
                Assert.That(one.Status, Is.EqualTo(200), $"Opening a lead in an expired workspace failed: {one}");
            });

            await Step("On the pages: the leads list opens, but saving a new lead lands on \"Your trial has ended\"", async () =>
            {
                var before = await Db.CountLiveAsync("Leads", tenantId);
                await page.GotoAsync("/Leads");
                Assert.That(Ui.PathOf(page.Url), Does.StartWith("/Leads"), "The leads list did not open in an expired workspace.");

                await SubmitNewLeadFormAsync(page, e, World.Unique("Expired Page"), World.EmailFor("E", "expiredpage" + m));
                await ExpectReadOnlyPageAsync(page, "trial-ended");
                Assert.That(await Db.CountLiveAsync("Leads", tenantId), Is.EqualTo(before), "The form saved a lead anyway.");
            });

            await Step("The trial is extended: the same token writes again at once", async () =>
            {
                await Db.SetTrialStateAsync(tenantId, originalTrial with
                {
                    Plan = "trial", TrialExpiresAt = DateTime.UtcNow.AddDays(14), IsSuspended = false,
                });
                var r = await held.PostAsync("/api/leads", LeadBody(tenantId, e, "Extended " + m));
                Assert.That(r.IsSuccess, Is.True, $"Writing after the trial was extended failed: {r}");
            });

            // ══ Part 3: paused ════════════════════════════════════════════
            await Step("Paused (suspended): read-only the same way, and the page says \"paused\"", async () =>
            {
                await Db.SetTrialStateAsync(tenantId, originalTrial with { IsSuspended = true });
                var before = await Db.CountLiveAsync("Leads", tenantId);

                await ExpectReadOnlyAsync("Add a lead", "Workspace paused",
                    held.PostAsync("/api/leads", LeadBody(tenantId, e, "Paused add " + m)));
                var list = await held.GetAsync("/api/leads?pageNumber=1&pageSize=5");
                Assert.That(list.Status, Is.EqualTo(200), $"Listing leads in a paused workspace failed: {list}");

                await SubmitNewLeadFormAsync(page, e, World.Unique("Paused Page"), World.EmailFor("E", "pausedpage" + m));
                await ExpectReadOnlyPageAsync(page, "paused");
                Assert.That(await Db.CountLiveAsync("Leads", tenantId), Is.EqualTo(before), "A lead was saved in a paused workspace.");
            });

            await Step("Switched back on: writes work again at once", async () =>
            {
                await Db.SetTrialStateAsync(tenantId, originalTrial with { IsSuspended = false });
                var r = await held.PostAsync("/api/leads", LeadBody(tenantId, e, "Resumed " + m));
                Assert.That(r.IsSuccess, Is.True, $"Writing after the workspace was switched back on failed: {r}");
            });
        }
        finally
        {
            await Db.SetWorkspaceLimitsAsync(tenantId, originalLimits);
            await Db.SetTrialStateAsync(tenantId, originalTrial);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static object LeadBody(Guid tenantId, TestTenant e, string name) => new
    {
        tenantId,
        fullName = name,
        email = World.EmailFor("E", Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]", "")),
        phone = UniquePhone(),
        companyName = name + " Co",
        estimatedValue = 1000m,
        currency = e.Currency,
    };

    private static object ConvertBody(Guid tenantId, Guid leadId, string title) => new
    {
        tenantId,
        leadId,
        dealTitle = title,
        stage = "",
        expectedValue = 20000m,
        currency = "INR",
        expectedCloseDateUtc = DateTime.UtcNow.Date.AddDays(30),
    };

    private static object UserBody(Guid tenantId, string email) => new
    {
        tenantId,
        firstName = "Extra",
        lastName = "E2E",
        email,
        isTenantAdmin = false,
        roleIds = Array.Empty<Guid>(),
    };

    /// <summary>403, and the workspace's read-only answer (not a permission refusal, which is also 403).</summary>
    private static async Task ExpectReadOnlyAsync(string what, string title, Task<ApiResponse> call)
    {
        var r = await call;
        Assert.That(r.Status, Is.EqualTo(403), $"{what}: expected 403 (read-only), got {r}");
        Assert.That(r.Error, Is.EqualTo(title), $"{what}: refused, but not as \"{title}\": {r}");
    }

    /// <summary>Admin → Tenants → Settings for the workspace: the three limits, then Save; checks they were stored.</summary>
    private static async Task SetLimitsOnPageAsync(IPage super, Guid tenantId, TestDatabase.WorkspaceLimits limits)
    {
        await super.GotoAsync($"/Admin/Tenants/Settings/{tenantId}");
        await super.Locator("#Input_MaxUsers").FillAsync(limits.MaxUsers.ToString(CultureInfo.InvariantCulture));
        await super.Locator("#Input_MaxLeads").FillAsync(limits.MaxLeads.ToString(CultureInfo.InvariantCulture));
        await super.Locator("#Input_MaxDeals").FillAsync(limits.MaxDeals.ToString(CultureInfo.InvariantCulture));
        await super.GetByRole(AriaRole.Button, new() { Name = "Save Settings" }).ClickAsync();

        try
        {
            await Ui.WaitForAddressAsync(super, u => Ui.PathOf(u).StartsWith("/Admin/Tenants/Detail", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (Ui.IsTimeout(ex))
        {
            var invalid = await super.EvaluateAsync<string>(
                "() => [...document.querySelectorAll('input:invalid')].map(i => i.name + ': ' + i.validationMessage).join(' | ')");
            throw new AssertionException("Saving the workspace's limits did not reach the workspace page. " +
                                         (invalid.Length > 0 ? "The browser refused: " + invalid + ". " : "") +
                                         await Ui.DescribePageProblemsAsync(super));
        }

        var stored = await Db.WorkspaceLimitsAsync(tenantId);
        Assert.That(stored, Is.EqualTo(limits), "The limits saved on the page are not what is stored.");
    }

    /// <summary>Leads → Add lead, filled and sent — without waiting to land anywhere in particular.</summary>
    private static async Task SubmitNewLeadFormAsync(IPage page, TestTenant e, string name, string email)
    {
        await page.GotoAsync("/Leads/Create");
        await page.Locator("#Input_FullName").FillAsync(name);
        await page.Locator("#Input_Email").FillAsync(email);
        await page.Locator("#Input_Phone").FillAsync(UniquePhone());
        await page.Locator("#Input_CompanyName").FillAsync(name + " Pvt Ltd");
        if (await Ui.SelectByTextAsync(page.Locator("#Input_CountryId"), e.Country, required: false) is null)
            await Ui.SelectFirstIfEmptyAsync(page.Locator("#Input_CountryId"));
        if (await Ui.SelectByTextAsync(page.Locator("#Input_CurrencyId"), e.Currency, required: false) is null)
            await Ui.SelectFirstIfEmptyAsync(page.Locator("#Input_CurrencyId"));
        await Ui.SelectFirstIfEmptyAsync(page.Locator("#Input_ChannelId"));
        await page.Locator("#Input_EstimatedValue").FillAsync("1000");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create Lead" }).ClickAsync();
    }

    /// <summary>An Indian mobile number of its own, so the duplicate check never asks about it.</summary>
    private static string UniquePhone() => "+9198" + Random.Shared.Next(10000000, 99999999).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// After Add lead was sent: the form's own refusal, read from the page.
    /// "Save as a new lead anyway" is pressed if the duplicate check asks.
    /// Fails if the lead was saved.
    /// </summary>
    private static async Task<string> FormRefusalAsync(IPage page)
    {
        var problem = page.Locator(".alert-danger:visible, .validation-summary-errors:visible, span.text-danger:not(:empty)")
            .Filter(new() { HasNotTextRegex = new Regex(@"^\s*\*\s*$") });
        var duplicate = page.Locator("#dupWarning");
        bool OnCreate() => Ui.PathOf(page.Url).EndsWith("/Leads/Create", StringComparison.OrdinalIgnoreCase);

        await Ui.WaitUntilAsync(page,
            async () => !OnCreate() || await problem.CountAsync() > 0 || await duplicate.CountAsync() > 0,
            "Add lead neither saved nor showed an error.");

        if (OnCreate() && await problem.CountAsync() == 0 && await duplicate.CountAsync() > 0)
        {
            await page.Locator("#saveAnyway").ClickAsync();
            await Ui.WaitUntilAsync(page,
                async () => !OnCreate() || await problem.CountAsync() > 0,
                "\"Save as a new lead anyway\" neither saved nor showed an error.");
        }

        Assert.That(OnCreate(), Is.True, $"Add lead was not refused — it went on to {Ui.PathOf(page.Url)}.");
        return string.Join(" | ", (await problem.AllInnerTextsAsync()).Select(t => t.Trim()).Where(t => t.Length > 0));
    }

    /// <summary>The page the app shows for a refused save in a read-only workspace, with the reason given.</summary>
    private static async Task ExpectReadOnlyPageAsync(IPage page, string reason)
    {
        try
        {
            await Ui.WaitForAddressAsync(page, u => Ui.PathOf(u).StartsWith("/TrialExpired", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (Ui.IsTimeout(ex))
        {
            throw new AssertionException($"The save did not land on the read-only page (still on {Ui.PathOf(page.Url)}). " +
                                         await Ui.DescribePageProblemsAsync(page));
        }
        await Assertions.Expect(page.Locator("#readOnlyReason")).ToHaveAttributeAsync("data-reason", reason);
    }
}
