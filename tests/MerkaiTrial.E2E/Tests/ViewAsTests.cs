// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/ViewAsTests.cs
//
// VUL-27 (round 106) — View As is recorded, limited and can't be
// turned into a takeover.
//
//   View_As_is_audited_and_cannot_take_over_an_account
//
//   • Starting View As writes ViewAsStart to the VIEWED workspace's audit
//     log: who was viewed, by which super admin.
//   • While viewing, what is done is recorded as done BY the viewed person
//     but by the super admin as the ACTOR — a lead added appears with the
//     super admin's id as the one who really did it.
//   • While viewing, the person's password can't be changed: the page
//     shows no form and a direct post is refused; their password is as it
//     was.
//   • Exiting writes ViewAsEnd.
//   • A super admin can't View As a super admin (themselves included):
//     refused, nothing recorded, still themselves.
//   • A workspace admin can't use View As at all: refused, nothing recorded.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class ViewAsTests : E2ETest
{
    [Test]
    public async Task View_As_is_audited_and_cannot_take_over_an_account()
    {
        var a = World.A;
        var tenantA = await Db.TenantIdAsync(a.Name);
        var rep = a["rep"];
        var repId = await Db.UserIdAsync(rep.Email);
        var superId = await Db.UserIdAsync(World.SuperAdmin.Email);
        var superTenant = Guid.Parse(await Db.ValueAsync("Users", "TenantId", superId) ?? Guid.Empty.ToString());
        var m = Random.Shared.Next(10000, 99999).ToString();
        var problems = new List<string>();
        var start = DateTime.UtcNow.AddSeconds(-2);

        var page = await SignedInAsync(World.SuperAdmin);

        await Step("The super admin starts View As of workspace A's rep: ViewAsStart is in A's audit log", async () =>
        {
            var r = await StartAsync(page, tenantA, repId);
            Assert.That(r.Status is 0 or (>= 200 and < 400), Is.True, $"Starting View As failed: {r}");
            await page.GotoAsync("/Dashboard");

            var rows = await Db.AuditAsync("ViewAsStart", start, tenantA);
            var row = rows.FirstOrDefault(x => x.EntityId == repId);
            if (row is null) { problems.Add("Starting View As wrote no ViewAsStart row to workspace A's audit log."); return; }
            if (row.ActorUserId != superId) problems.Add($"The ViewAsStart row names {row.ActorUserId} as the actor, not the super admin.");
            if (row.Data is null || !row.Data.Contains(rep.Email, StringComparison.OrdinalIgnoreCase))
                problems.Add("The ViewAsStart row doesn't say who was viewed.");
        });

        await Step("A lead added while viewing is recorded with the super admin as the real actor", async () =>
        {
            var name = "ViewAs Lead " + m;
            await Flows.CreateLeadAsync(page, name, World.EmailFor("A", "viewas" + m), 1000m, a);
            var rows = await Db.AuditAsync("LeadCreated", start, tenantA);
            var row = rows.FirstOrDefault(x => x.Data?.Contains(name, StringComparison.Ordinal) == true) ?? rows.FirstOrDefault();
            if (row is null) { problems.Add("Adding a lead while viewing wrote no LeadCreated audit row."); return; }
            if (row.ActorUserId != superId)
                problems.Add($"The lead added during View As records actor {row.ActorUserId?.ToString() ?? "(none)"} — not the super admin who really did it.");
        });

        await Step("While viewing, the person's password can't be changed — no form, and a direct post is refused", async () =>
        {
            var before = await Db.UserSecretsAsync(rep.Email);

            await page.GotoAsync("/Account/ChangePassword");
            if (await page.Locator("#NewPassword").CountAsync() > 0)
                problems.Add("During View As the Change password page offers its form.");

            // The page rightly has no form now, so borrow the anti-forgery
            // token from the dashboard: it is the same token for every page
            // of this sign-in, so the post below is refused for being View
            // As, not for a missing token.
            await page.GotoAsync("/Dashboard");
            var token = await PageHttp.FormTokenAsync(page);
            var r = await PageHttp.FetchAsync(page, "POST", "/Account/ChangePassword", new[]
            {
                KeyValuePair.Create("__RequestVerificationToken", token),
                KeyValuePair.Create("CurrentPassword", rep.Password),
                KeyValuePair.Create("NewPassword", "Taken-Over-Account-77!"),
                KeyValuePair.Create("ConfirmPassword", "Taken-Over-Account-77!"),
            }, follow: false);

            var after = await Db.UserSecretsAsync(rep.Email);
            if (after.PasswordHash != before.PasswordHash) problems.Add("During View As the viewed person's password was CHANGED.");
            if (r.Status is >= 300 and < 400 && (r.Url ?? "").Contains("/Account/Login", StringComparison.OrdinalIgnoreCase))
                problems.Add("The change-password post during View As went through to sign-in (as a password change does).");
        });

        await Step("Exiting View As writes ViewAsEnd", async () =>
        {
            await page.GotoAsync("/Admin/ViewAs");
            var token = await PageHttp.FormTokenAsync(page);
            await PageHttp.FetchAsync(page, "POST", "/Admin/ViewAs?handler=Exit",
                new[] { KeyValuePair.Create("__RequestVerificationToken", token) }, follow: false);

            var rows = await Db.AuditAsync("ViewAsEnd", start, tenantA);
            if (!rows.Any(x => x.EntityId == repId)) problems.Add("Exiting View As wrote no ViewAsEnd row to workspace A's audit log.");
        });

        await Step("A super admin can't View As a super admin (themselves): refused, nothing recorded, still themselves", async () =>
        {
            var self = await StartAsync(page, superTenant, superId);
            var rows = await Db.AuditAsync("ViewAsStart", start);
            if (rows.Any(x => x.EntityId == superId)) problems.Add("View As of a super admin was recorded as STARTED.");

            await page.GotoAsync("/Admin/ViewAs");
            var stillAdmin = Ui.PathOf(page.Url).StartsWith("/Admin", StringComparison.OrdinalIgnoreCase);
            if (!stillAdmin) problems.Add($"After trying View As of a super admin, the super admin can't open /Admin (on {Ui.PathOf(page.Url)}).");
            if (self.Status >= 500) problems.Add($"Trying View As of a super admin gave a server error: {self}");
        });

        await Step("A workspace admin can't use View As at all: refused, nothing recorded", async () =>
        {
            var admin = await SignedInAsync(a.Admin);
            // Change password always has a form (the Dashboard has none
            // outside View As), so it is where the token comes from.
            await admin.GotoAsync("/Account/ChangePassword");
            var token = await PageHttp.FormTokenAsync(admin);
            var r = await PageHttp.FetchAsync(admin, "POST", "/Admin/ViewAs?handler=Start", new[]
            {
                KeyValuePair.Create("__RequestVerificationToken", token),
                KeyValuePair.Create("tenantId", tenantA.ToString()),
                KeyValuePair.Create("userId", repId.ToString()),
            }, follow: false);

            var rows = await Db.AuditAsync("ViewAsStart", start, tenantA);
            if (rows.Count > 1) problems.Add("A workspace admin's View As attempt was recorded as started.");

            if (r.Status >= 500) problems.Add($"A workspace admin's View As attempt gave a server error: {r}");
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    private static async Task<PageHttp.Result> StartAsync(IPage page, Guid tenantId, Guid userId)
    {
        await page.GotoAsync("/Admin/ViewAs");
        var token = await PageHttp.FormTokenAsync(page);
        return await PageHttp.FetchAsync(page, "POST", "/Admin/ViewAs?handler=Start", new[]
        {
            KeyValuePair.Create("__RequestVerificationToken", token),
            KeyValuePair.Create("tenantId", tenantId.ToString()),
            KeyValuePair.Create("userId", userId.ToString()),
        }, follow: false);
    }
}
