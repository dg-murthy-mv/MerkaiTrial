// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/WorldBuilder.cs
//
// Builds the World (see World.cs) the way a real customer is set up —
// through the screens, not by inserting rows:
//
//   1. Super admin signs in. (Its password is set in the test database
//      first; that is the only account touched directly.)
//   2. Admin → Tenants → Create, twice: workspaces A and B, India / INR.
//   3. Each workspace admin opens the invite link from the Result page and
//      chooses a password.
//   4. Workspace A's admin adds the manager, rep, viewer and resetme
//      accounts in Settings → Users → Add person, and each one opens their
//      invite link and chooses a password.
//
// So by the time the first test runs, provisioning, invites, the
// set-password page and sign-in have all worked once already.
//
// 088: BuildSettingsWorkspaceAsync adds workspace C (Thailand / THB /
// Asia/Bangkok, with a manager and two reps) the same way. It is called
// by WorkspaceSettingsTests rather than here, so a problem creating C
// fails those tests only — not the whole run. Every person's role label
// is now kept (TestTenant.RoleOf) for the Record Visibility tests.
//
// 089: workspace D (India / INR, admin and two reps) for the custom
// field and notification tests, built the same way by
// EnsureWorkspaceDAsync the first time either fixture needs it. C and D
// now share one builder (BuildExtraWorkspaceAsync); C is built exactly
// as before.
//
// 092: EnsureWorkspaceCAsync — workspace C built the first time ANY
// fixture needs it (the Own-visibility tests as well as the settings
// tests), the same way D has been since 089.
// =====================================================================

using System.Text.RegularExpressions;

namespace MerkaiTrial.E2E.Infrastructure;

public sealed class WorldBuilder
{
    private readonly IBrowser _browser;
    private readonly TestDatabase _db;

    public WorldBuilder(IBrowser browser, TestDatabase db)
    {
        _browser = browser;
        _db = db;
    }

    public async Task BuildAsync()
    {
        Log($"Test database: {await _db.EnsureIsE2EDatabaseAsync()}");

        var superEmail = await _db.PrepareSuperAdminAsync(E2ESettings.Password);
        World.SuperAdmin = new TestUser("superadmin", superEmail, E2ESettings.Password, "Super", "Admin");
        Log($"Super admin: {superEmail}");

        // ── 1–2. Two workspaces ───────────────────────────────────────
        string inviteA, inviteB;
        await using (var session = await BrowserSession.StartAsync(_browser, "seed-provision"))
        {
            await Ui.SignInAsync(session.Page, World.SuperAdmin, superAdmin: true);
            (World.A, inviteA) = await ProvisionAsync(session.Page, "A", India);
            (World.B, inviteB) = await ProvisionAsync(session.Page, "B", India);
            session.Succeeded();
        }

        // ── 3. Workspace admins accept their invites ──────────────────
        await AcceptInviteAsync(World.A.Admin, inviteA);
        await AcceptInviteAsync(World.B.Admin, inviteB);

        // ── 4. The rest of workspace A ────────────────────────────────
        // Roles are matched loosely: each workspace's role names come from
        // the RoleTemplates table, which may say "Sales Representative" or
        // "Read-only" rather than "Sales Rep" or "Viewer".
        var people = new (string Key, string First, string RolePattern)[]
        {
            ("manager", "Mira",  Ui.RolePatterns.Manager),
            ("rep",     "Ravi",  Ui.RolePatterns.Rep),
            ("viewer",  "Vani",  Ui.RolePatterns.Viewer),
            ("resetme", "Rhea",  Ui.RolePatterns.Viewer),
        };

        await using (var session = await BrowserSession.StartAsync(_browser, "seed-people"))
        {
            await Ui.SignInAsync(session.Page, World.A.Admin);
            foreach (var p in people)
            {
                var user = new TestUser(p.Key, World.EmailFor("A", p.Key), E2ESettings.Password, p.First, "E2E");
                var (invite, role) = await AddPersonAsync(session.Page, user, p.RolePattern);
                World.A.People[p.Key] = user;
                World.A.RoleOf[p.Key] = role;
                await AcceptInviteAsync(user, invite);
            }
            session.Succeeded();
        }

        Log($"Ready: workspace A '{World.A.Name}' with {World.A.People.Count + 1} people, " +
            $"workspace B '{World.B.Name}'.");
    }

    /// <summary>
    /// 088. Workspace C — Thailand / THB / Asia/Bangkok — with a manager
    /// and two reps, for the workspace-settings tests. Sets World.C.
    /// </summary>
    public async Task BuildSettingsWorkspaceAsync()
    {
        World.C = await BuildExtraWorkspaceAsync("C", Thailand, new[]
        {
            ("manager", "Mana",  Ui.RolePatterns.Manager),
            ("rep",     "Ratt",  Ui.RolePatterns.Rep),
            ("rep2",    "Ploy",  Ui.RolePatterns.Rep),
        });
    }

    /// <summary>
    /// 089. Workspace D — India / INR — with two reps, for the custom
    /// field and notification tests. Sets World.D.
    /// </summary>
    public async Task BuildFieldsWorkspaceAsync()
    {
        World.D = await BuildExtraWorkspaceAsync("D", India, new[]
        {
            ("rep",  "Rohan", Ui.RolePatterns.Rep),
            ("rep2", "Sana",  Ui.RolePatterns.Rep),
        });
    }

    private static readonly SemaphoreSlim WorkspaceDGate = new(1, 1);
    private static readonly SemaphoreSlim WorkspaceCGate = new(1, 1);

    // 095: a failed build is REMEMBERED and reported again, not retried.
    // A retry used the same names as the half-built first attempt, so the
    // Create page refused it, and every later fixture reported "did not
    // reach the Result page" instead of what really went wrong first.
    private static Exception? _workspaceCFailure;
    private static Exception? _workspaceDFailure;

    private static InvalidOperationException Earlier(string key, Exception first) =>
        new($"Workspace {key} could not be built earlier in this run, so this fixture was skipped. " +
            $"The first failure: {first.Message}", first);

    /// <summary>
    /// 092. Workspace C, built the first time a fixture asks for it — the
    /// same rule as D. WorkspaceSettingsTests still builds it its own way
    /// when it runs first; both check World.C, so it is built once.
    /// </summary>
    public static async Task<TestTenant> EnsureWorkspaceCAsync()
    {
        await WorkspaceCGate.WaitAsync();
        try
        {
            if (World.C is not null) return World.C;
            if (_workspaceCFailure is not null) throw Earlier("C", _workspaceCFailure);

            try
            {
                await new WorldBuilder(GlobalSetup.Browser, GlobalSetup.Db).BuildSettingsWorkspaceAsync();
            }
            catch (Exception ex)
            {
                _workspaceCFailure = ex;
                throw;
            }
            return World.C ?? throw new InvalidOperationException("Workspace C was not built.");
        }
        finally
        {
            WorkspaceCGate.Release();
        }
    }

    /// <summary>
    /// 089. Workspace D, built the first time a test fixture asks for it.
    /// CustomFieldTests and NotificationTests both use it; whichever runs
    /// first builds it, the other reuses it. 095: a failed build is
    /// remembered — later fixtures report the FIRST failure, see above.
    /// </summary>
    public static async Task<TestTenant> EnsureWorkspaceDAsync()
    {
        await WorkspaceDGate.WaitAsync();
        try
        {
            if (World.D is not null) return World.D;
            if (_workspaceDFailure is not null) throw Earlier("D", _workspaceDFailure);

            try
            {
                await new WorldBuilder(GlobalSetup.Browser, GlobalSetup.Db).BuildFieldsWorkspaceAsync();
            }
            catch (Exception ex)
            {
                _workspaceDFailure = ex;
                throw;
            }
            return World.D ?? throw new InvalidOperationException("Workspace D was not built.");
        }
        finally
        {
            WorkspaceDGate.Release();
        }
    }

    /// <summary>
    /// A workspace provisioned by the super admin, its admin's invite
    /// accepted, and the given people added by that admin — each of whom
    /// accepts their own invite. The way C was always built (088), now
    /// shared with D (089).
    /// </summary>
    private async Task<TestTenant> BuildExtraWorkspaceAsync(
        string key, Market market, (string Key, string First, string RolePattern)[] people)
    {
        await _db.EnsureIsE2EDatabaseAsync();

        TestTenant tenant;
        string invite;
        await using (var session = await BrowserSession.StartAsync(_browser, $"seed-provision-{key}"))
        {
            await Ui.SignInAsync(session.Page, World.SuperAdmin, superAdmin: true);
            (tenant, invite) = await ProvisionAsync(session.Page, key, market);
            session.Succeeded();
        }

        await AcceptInviteAsync(tenant.Admin, invite);

        await using (var session = await BrowserSession.StartAsync(_browser, $"seed-people-{key}"))
        {
            await Ui.SignInAsync(session.Page, tenant.Admin);
            foreach (var p in people)
            {
                var user = new TestUser(p.Key, World.EmailFor(key, p.Key), E2ESettings.Password, p.First, "E2E");
                var (personInvite, role) = await AddPersonAsync(session.Page, user, p.RolePattern);
                tenant.People[p.Key] = user;
                tenant.RoleOf[p.Key] = role;
                await AcceptInviteAsync(user, personInvite);
            }
            session.Succeeded();
        }

        Log($"Ready: workspace {key} '{tenant.Name}' ({market.Country}) with {tenant.People.Count + 1} people.");
        return tenant;
    }

    // =================================================================

    /// <summary>What Admin → Tenants → Create is asked for, per market.</summary>
    private sealed record Market(string Country, string Currency, string TimeZoneNeedle, TimeSpan Offset, string Phone);

    private static readonly Market India    = new("India",    "INR", "Kolkata", TimeSpan.FromHours(5.5), "+919800000000");
    private static readonly Market Thailand = new("Thailand", "THB", "Bangkok", TimeSpan.FromHours(7),   "+66812345678");

    private async Task<(TestTenant Tenant, string InviteLink)> ProvisionAsync(IPage page, string key, Market market)
    {
        var name = $"E2E {key} {World.Stamp}";
        var admin = new TestUser("admin", World.EmailFor(key, "admin"), E2ESettings.Password, $"Admin{key}", "E2E");

        await page.GotoAsync("/Admin/Tenants/Create");
        await page.Locator("#Input_Name").FillAsync(name);
        await page.Locator("#Input_FromEmail").FillAsync(World.EmailFor(key, "office"));

        // One of the four markets that matter. The market's own time zone
        // if the list has it; the country's default is fine otherwise.
        await Ui.SelectByTextAsync(page.Locator("#Input_CountryId"), market.Country);
        await Ui.SelectByTextAsync(page.Locator("#Input_DefaultCurrency"), market.Currency);
        await Ui.SelectByTextAsync(page.Locator("#Input_TimeZone"), market.TimeZoneNeedle, required: false);

        // The plan with the most users, so adding five people never hits
        // the plan limit. Read from the live "Plans" panel on the page.
        var plan = await BiggestPlanAsync(page);
        if (plan is not null)
            await Ui.SelectByTextAsync(page.Locator("#Input_Plan"), plan, required: false);

        await page.Locator("#Input_AdminFirstName").FillAsync(admin.FirstName);
        await page.Locator("#Input_AdminLastName").FillAsync(admin.LastName);
        await page.Locator("#Input_AdminEmail").FillAsync(admin.Email);

        await page.GetByRole(AriaRole.Button, new() { Name = "Create workspace" }).ClickAsync();

        // Either the Result page, or — when an earlier workspace looks
        // similar (an earlier test run) — the "This may be a repeat trial"
        // warning with its confirmation box. Tick it and submit again.
        var confirm = page.Locator("#Input_ConfirmDespiteWarnings");
        var confirmed = false;
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (!PathIs(page, "/Admin/Tenants/Result"))
        {
            if (!confirmed && await confirm.IsVisibleAsync())
            {
                confirmed = true;
                await confirm.CheckAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Create workspace" }).ClickAsync();
            }
            else if (DateTime.UtcNow > deadline)
            {
                var errors = await Ui.TextOrEmptyAsync(page.Locator(".alert-danger, .text-danger:not(:empty)"));
                throw new AssertionException(
                    $"Creating workspace {key} did not reach the Result page (still on {page.Url}). {errors}");
            }

            await page.WaitForTimeoutAsync(250);
        }

        // 095: wait for what is needed — the invite link on the page — not
        // for a load event. A run on 18 Oct stopped here: the Result page
        // had loaded in 51 ms (the trace shows it, and Playwright's own
        // "NetworkIdle" fired half a second later), yet waiting for
        // "DOMContentLoaded" after the fact never returned, and setup
        // failed all 61 tests at the 60-second timeout. Reading the page
        // until the link is there cannot miss an event that already
        // happened. 20 seconds, checked every quarter second.
        string? link = null;
        var linkDeadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            try
            {
                link = TestDatabase.FindSetPasswordLink(await page.ContentAsync());
            }
            catch (PlaywrightException)
            {
                // The page was between documents at that instant; look again.
            }

            if (link is not null || DateTime.UtcNow > linkDeadline) break;
            await page.WaitForTimeoutAsync(250);
        }

        if (link is null)
            throw new AssertionException(
                $"Workspace {key} was created but its Result page shows no invite link.");

        Log($"Provisioned {name}");
        return (new TestTenant
        {
            Key = key,
            Name = name,
            Admin = admin,
            Country = market.Country,
            Currency = market.Currency,
            UtcOffset = market.Offset,
            SamplePhone = market.Phone,
        }, link);
    }

    /// <summary>Settings → Users → Add person. Returns the invite link
    /// shown on the confirmation card, and the role label ticked.</summary>
    private static async Task<(string Invite, string Role)> AddPersonAsync(IPage page, TestUser user, string rolePattern)
    {
        await page.GotoAsync("/Settings/Users/Create");
        await page.Locator("#Input_FirstName").FillAsync(user.FirstName);
        await page.Locator("#Input_LastName").FillAsync(user.LastName);
        await page.Locator("#Input_Email").FillAsync(user.Email);

        var roleName = await Ui.TickRoleAsync(page, rolePattern);

        await page.GetByRole(AriaRole.Button, new() { Name = "Add person" }).ClickAsync();

        var inviteBox = page.Locator("#inviteUrl");
        try
        {
            await inviteBox.WaitForAsync(new LocatorWaitForOptions { Timeout = E2ESettings.ActionTimeoutMs });
        }
        catch (Exception ex) when (Ui.IsTimeout(ex))
        {
            var errors = await Ui.TextOrEmptyAsync(page.Locator(".alert-danger, .text-danger:not(:empty)"));
            throw new AssertionException($"Adding {user.Key} ({user.Email}) showed no invite link. {errors}");
        }

        var link = await inviteBox.InputValueAsync();
        Log($"Added {user.Key} ({roleName})");
        return (link, roleName);
    }

    private async Task AcceptInviteAsync(TestUser user, string link)
    {
        await using var session = await BrowserSession.StartAsync(_browser, $"seed-invite-{user.Key}");
        await Ui.SetPasswordFromLinkAsync(session.Page, link, user.Password);
        session.Succeeded();
    }

    /// <summary>The DisplayName of the plan with the most users, from the
    /// Plans panel ("12 users", "-1 users" for unlimited).</summary>
    private static Task<string?> BiggestPlanAsync(IPage page) =>
        page.EvaluateAsync<string?>(
            @"() => {
                let best = null, bestUsers = -Infinity;
                for (const card of document.querySelectorAll('.card-body .border-bottom')) {
                    const name = card.querySelector('strong');
                    const li = [...card.querySelectorAll('li')].find(l => /users/i.test(l.textContent));
                    if (!name || !li) continue;
                    let n = parseInt(li.textContent.replace(/[^0-9-]/g, ''), 10);
                    if (isNaN(n)) continue;
                    if (n < 0) n = Number.MAX_SAFE_INTEGER;   // -1 = unlimited
                    if (n > bestUsers) { bestUsers = n; best = name.textContent.trim(); }
                }
                return best;
            }");

    private static bool PathIs(IPage page, string path) =>
        Ui.PathOf(page.Url).EndsWith(path, StringComparison.OrdinalIgnoreCase);

    private static void Log(string message) => TestContext.Progress.WriteLine($"[e2e setup] {message}");
}
