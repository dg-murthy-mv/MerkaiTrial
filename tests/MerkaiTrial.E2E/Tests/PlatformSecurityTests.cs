// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/PlatformSecurityTests.cs
//
// NEW (119). The super admin area, locked and recorded.
//
//   • Platform API endpoints refuse a workspace admin and a sales rep
//     (403): countries and verticals (open to anyone signed in until
//     119), workspaces, starting roles. Reading the country list still
//     works for everyone signed in (their forms use it).
//   • Every /Admin page refuses a workspace admin.
//   • Deleting a workspace needs its name typed — checked on the server;
//     a wrong name, or the form sent by hand with no name, deletes
//     nothing. MadeeVision's own workspace shows no Delete at all.
//   • Every console change is recorded (PlatformChange): a saved country
//     as "done", the refused delete as "refused" with the page's sentence;
//     Admin → Audit Log shows both, and its filters find them.
//   • While viewing as someone, the console's pages send you back to
//     View As (only "Stop viewing as" is there).
//
// Workspace B is the one whose deletion is attempted — with a WRONG name
// only, so it is never deleted.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("SuperAdmin")]
public class PlatformSecurityTests : E2ETest
{
    [Test]
    public async Task Platform_api_endpoints_refuse_everyone_but_the_super_admin()
    {
        var tenantA = await Db.TenantIdAsync(World.A.Name);
        var tenantB = await Db.TenantIdAsync(World.B.Name);
        var problems = new List<string>();

        var calls = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Post,   "/api/countries", new { code = "ZZ", name = "Nowhere", dialCode = "+999", currencyCode = "ZZZ", taxLabel = "Tax", defaultTaxRate = 1 }),
            (HttpMethod.Put,    "/api/countries/IN", new { name = "India", dialCode = "+91", currencyCode = "INR", taxLabel = "GST", defaultTaxRate = 99, isActive = true }),
            (HttpMethod.Post,   "/api/company-verticals", new { tenantId = tenantB, name = "E2E planted vertical", createdBy = "e2e" }),
            (HttpMethod.Put,    $"/api/company-verticals/{Guid.NewGuid()}", new { name = "x", updatedBy = "e2e" }),
            (HttpMethod.Delete, $"/api/company-verticals/{Guid.NewGuid()}", null),
            (HttpMethod.Get,    "/api/tenants/paginated", null),
            (HttpMethod.Delete, $"/api/tenants/{tenantB}", null),
            (HttpMethod.Patch,  $"/api/tenants/{tenantB}/status?isActive=false", null),
            (HttpMethod.Post,   "/api/admin/role-templates", new { name = "e2e_never", displayName = "Never", permissions = "{}", sortOrder = 999, isActive = false }),
        };

        foreach (var who in new[] { World.A.Admin, World.A["rep"] })
        {
            await Step($"As {who.Email}: every platform endpoint answers 403", async () =>
            {
                var api = await Api.AsAsync(who);
                api.WaitWhenPaced = true;
                foreach (var (method, path, body) in calls)
                {
                    var r = await api.SendAsync(method, path, body);
                    if (r.Status != 403) problems.Add($"{who.Key}: {method} {path} answered {r} — expected 403.");
                }

                var read = await api.GetAsync("/api/countries?pageSize=5");
                if (read.Status != 200) problems.Add($"{who.Key}: reading the country list answered {read} — their forms need it.");
            });
        }

        await Step("Nothing those calls asked for happened", async () =>
        {
            var super = await Api.AsAsync(World.SuperAdmin);
            super.WaitWhenPaced = true;
            var b = await super.GetAsync($"/api/tenants/{tenantB}");
            if (b.Status != 200) problems.Add($"Workspace B is gone after the refused calls: {b}");
            var zz = await super.GetAsync("/api/countries/ZZ");
            if (zz.Status == 200) problems.Add("Country ZZ was created by a refused call.");
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    [Test]
    public async Task Every_admin_console_page_refuses_a_workspace_admin()
    {
        var tenantA = await Db.TenantIdAsync(World.A.Name);
        var page = await SignedInAsync(World.A.Admin);
        foreach (var path in new[]
        {
            "/Admin", "/Admin/Tenants", "/Admin/Tenants/Create", $"/Admin/Tenants/Detail/{tenantA}",
            $"/Admin/Tenants/Edit/{tenantA}", $"/Admin/Tenants/Settings/{tenantA}", $"/Admin/Tenants/People/{tenantA}",
            "/Admin/Plans", "/Admin/Countries", "/Admin/Countries/Edit/IN", "/Admin/Countries/Detail/IN",
            "/Admin/TaxRates/Create", "/Admin/Verticals", "/Admin/Verticals/Create", "/Admin/StartingRoles",
            "/Admin/StartingRoles/Edit", "/Admin/Team", "/Admin/AuditLog", "/Admin/WhatsAppTemplates", "/Admin/ViewAs",
        })
            await Step($"{path} is refused", () => Ui.ExpectRefusedAsync(page, path));
    }

    [Test]
    public async Task Console_changes_are_recorded_and_a_workspace_is_deleted_only_with_its_name()
    {
        var since = DateTime.UtcNow.AddSeconds(-5);
        var tenantB = await Db.TenantIdAsync(World.B.Name);
        var superId = await Db.UserIdAsync(World.SuperAdmin.Email);
        var ownTenant = Guid.Parse((await Db.ValueAsync("Users", "TenantId", superId))!);
        var page = await SignedInAsync(World.SuperAdmin, new ViewportSize { Width = 1366, Height = 900 });
        var problems = new List<string>();

        await Step("Save India unchanged (Countries → Edit) — recorded as done", async () =>
        {
            await page.GotoAsync("/Admin/Countries/Edit/IN");
            await page.EvaluateAsync("() => { window.__e2eOld = true; }");
            await page.Locator("form[data-confirm-off] button[type=submit]").First.ClickAsync();
            await page.WaitForFunctionAsync("() => window.__e2eOld !== true");
        });

        await Step("Delete workspace B with a WRONG name: refused, B is still there", async () =>
        {
            await page.GotoAsync($"/Admin/Tenants/Detail/{tenantB}");
            await page.Locator("#confirmName").FillAsync("not the right name");
            await page.Locator("#deleteWorkspaceButton").ClickAsync();   // the confirm() is accepted for us
            var error = page.Locator(".alert-danger").Filter(new() { HasText = "Nothing was deleted" });
            await Assertions.Expect(error).ToBeVisibleAsync();
        });

        await Step("The same form sent by hand with no name: refused too", async () =>
        {
            var token = await PageHttp.FormTokenAsync(page);
            await PageHttp.FetchAsync(page, "POST", $"/Admin/Tenants/Detail/{tenantB}?handler=Delete", new[]
            {
                KeyValuePair.Create("__RequestVerificationToken", token),
                KeyValuePair.Create("id", tenantB.ToString()),
            });
            var super = await Api.AsAsync(World.SuperAdmin);
            var b = await super.GetAsync($"/api/tenants/{tenantB}");
            Assert.That(b.Status, Is.EqualTo(200), $"WORKSPACE B WAS DELETED without its name typed: {b}");
        });

        await Step("MadeeVision's own workspace shows no Delete", async () =>
        {
            await page.GotoAsync($"/Admin/Tenants/Detail/{ownTenant}");
            if (await page.Locator("#deleteWorkspaceForm").CountAsync() != 0)
                problems.Add("MadeeVision's own workspace offers a Delete form.");
        });

        await Step("Both are recorded as console changes (PlatformChange)", async () =>
        {
            var rows = await Db.AuditAsync("PlatformChange", since, ownTenant);
            var country = rows.FirstOrDefault(r => r.EntityType == "Country");
            if (country is null) problems.Add("Saving India wrote no PlatformChange row.");
            else if (!(country.Data ?? "").Contains("\"outcome\":\"done\"")) problems.Add($"Saving India was not recorded as done: {country.Data}");

            var deletes = rows.Where(r => r.EntityType == "Workspace" && (r.Data ?? "").Contains("\"handler\":\"Delete\"")).ToList();
            if (deletes.Count < 2) problems.Add($"Expected 2 recorded delete attempts on workspace B, found {deletes.Count}.");
            if (deletes.Any(r => !(r.Data ?? "").Contains("\"outcome\":\"refused\"")))
                problems.Add("A refused workspace delete was not recorded as refused: " + string.Join(" | ", deletes.Select(d => d.Data)));
            if (deletes.Any(r => r.EntityId != tenantB)) problems.Add("A recorded delete attempt does not point at workspace B.");
            if (rows.Any(r => (r.Data ?? "").Contains("RequestVerificationToken")))
                problems.Add("A recorded change carries the form's anti-forgery token.");
        });

        await Step("Admin → Audit Log shows them, and its filters find them", async () =>
        {
            await page.GotoAsync("/Admin/AuditLog?Kind=problems&Days=1");
            var refused = page.Locator("#platformAudit tr[data-action=PlatformChange]")
                .Filter(new() { HasText = "Workspace — delete" });
            await Assertions.Expect(refused.First).ToBeVisibleAsync();
            Assert.That((await refused.First.Locator("[data-col=outcome]").InnerTextAsync()).Trim(), Is.EqualTo("Refused"));

            await page.GotoAsync($"/Admin/AuditLog?Workspace={tenantB}&Days=1");
            await Assertions.Expect(page.Locator("#platformAudit [data-col=what]").Filter(new() { HasText = "Workspace — delete" }).First).ToBeVisibleAsync();

            await page.GotoAsync("/Admin/AuditLog?Kind=changes&Days=1&Search=Country");
            await Assertions.Expect(page.Locator("#platformAudit [data-col=what]").Filter(new() { HasText = "Country" }).First).ToBeVisibleAsync();

            await page.GotoAsync("/Admin/AuditLog?Days=1&Search=zz-nothing-matches-this-zz");
            await Assertions.Expect(page.Locator("#auditEmpty")).ToBeVisibleAsync();
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    [Test]
    public async Task While_viewing_as_someone_the_console_sends_you_back_to_View_As()
    {
        var tenantA = await Db.TenantIdAsync(World.A.Name);
        var repId = await Db.UserIdAsync(World.A["rep"].Email);
        var page = await SignedInAsync(World.SuperAdmin);

        await page.GotoAsync("/Admin/ViewAs");
        var token = await PageHttp.FormTokenAsync(page);
        await PageHttp.FetchAsync(page, "POST", "/Admin/ViewAs?handler=Start", new[]
        {
            KeyValuePair.Create("__RequestVerificationToken", token),
            KeyValuePair.Create("tenantId", tenantA.ToString()),
            KeyValuePair.Create("userId", repId.ToString()),
        }, follow: false);

        try
        {
            await Step("/Admin/Plans while viewing as the rep lands on View As", async () =>
            {
                await page.GotoAsync("/Admin/Plans");
                Assert.That(Ui.PathOf(page.Url), Does.StartWith("/Admin/ViewAs"),
                    $"While viewing as someone, /Admin/Plans opened {page.Url}.");
            });
        }
        finally
        {
            await page.GotoAsync("/Admin/ViewAs");
            var t2 = await PageHttp.FormTokenAsync(page);
            await PageHttp.FetchAsync(page, "POST", "/Admin/ViewAs?handler=Exit",
                new[] { KeyValuePair.Create("__RequestVerificationToken", t2) }, follow: false);
        }
    }
}
