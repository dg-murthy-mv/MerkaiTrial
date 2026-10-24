// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/RevokedAccessTests.cs
//
// TEST 83 (round 102) — Revoked_workspace_membership_loses_access_immediately
//
// A person whose access is taken away loses it on their NEXT click or
// API call — not when their session or token happens to expire.
//
// Uses a person of its own ("leaver", added to workspace A for this test),
// because it strips their role, switches them off and finally deletes them.
//
//   1. Before: the leaver works normally — pages and an API token.
//   2. Their role is cut to Viewer:
//        • the open browser session is signed out at the next click;
//        • the API token they held is refused (401) at once;
//        • signed in again, they have the Viewer's rights only.
//   3. They are switched off:
//        • their token is refused at once, the browser is signed out,
//          and signing in is refused.
//   4. They are switched back on:
//        • NOTHING from before comes back to life — the old token and the
//          old session stay dead (round 102 changes the stamp on "off");
//        • a fresh sign-in works.
//   5. They are deleted: their token is refused, signing in is refused.
//
// Before round 102 the API trusted a token for its whole 5 minutes, and
// switching someone back on revived every old session.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture]
public class RevokedAccessTests : E2ETest
{
    [Test]
    public async Task Revoked_workspace_membership_loses_access_immediately()
    {
        var a = World.A;
        var admin = await Api.AsAsync(a.Admin);
        await Api.EnsureTokensWorkAsync(admin);
        var tenantId = await Db.TenantIdAsync(a.Name);

        var leaver = await Step("Add a person of this test's own to workspace A, as a Sales Rep", () =>
            WorldBuilder.AddPersonForTestAsync(a, "leaver" + Random.Shared.Next(1000, 9999), "Lena", Ui.RolePatterns.Rep));
        var leaverId = await Db.UserIdAsync(leaver.Email);

        // ── 1. Before ────────────────────────────────────────────────
        var page = await SignedInAsync(leaver);
        var heldToken = await Step("Before: the leaver opens Leads, and their API token works", async () =>
        {
            await page.GotoAsync("/Leads");
            Assert.That(Ui.PathOf(page.Url), Does.StartWith("/Leads"), "The leaver could not open Leads to begin with.");

            var token = Api.Mint(await Db.ApiIdentityAsync(leaver.Email));
            var r = await With(token).GetAsync("/api/leads?pageNumber=1&pageSize=1");
            Assert.That(r.Status, Is.EqualTo(200), $"The leaver's token did not work to begin with: {r}");
            return token;
        });

        // ── 2. Role cut to Viewer ────────────────────────────────────
        await Step("The admin changes the leaver's role to Viewer", async () =>
        {
            var viewerRoles = await Db.UserRoleIdsAsync(a["viewer"].Email);
            Assert.That(viewerRoles, Is.Not.Empty, "Workspace A's viewer has no role to copy.");
            var r = await admin.PostAsync($"/api/users/{leaverId}/roles",
                new { tenantId, userId = leaverId, roleIds = viewerRoles });
            Assert.That(r.IsSuccess, Is.True, $"Changing the role failed: {r}");
        });

        await Step("The token they held is refused at once (401)", async () =>
        {
            var r = await With(heldToken).GetAsync("/api/leads?pageNumber=1&pageSize=1");
            Assert.That(r.Status, Is.EqualTo(401),
                $"A token minted before the role change still worked: {r}. The API must check the session's stamp on every call.");
        });

        await Step("Their open browser session is signed out at the next click", async () =>
        {
            await page.GotoAsync("/Leads");
            Assert.That(Ui.PathOf(page.Url), Does.StartWith("/Account/Login"),
                $"After the role change the old session still opened {Ui.PathOf(page.Url)}.");
        });

        await Step("Signed in again, they have the Viewer's rights only", async () =>
        {
            await Ui.SignInAsync(page, leaver);
            await page.GotoAsync("/Leads");
            Assert.That(Ui.PathOf(page.Url), Does.StartWith("/Leads"), "As a Viewer they should still see Leads.");
            await Ui.ExpectRefusedAsync(page, "/Leads/Create");

            var asViewer = await Api.AsAsync(leaver);
            var create = await asViewer.PostAsync("/api/leads", new { });
            Assert.That(create.Status, Is.EqualTo(403), $"A Viewer could call 'create lead': {create}");
            var list = await asViewer.GetAsync("/api/leads?pageNumber=1&pageSize=1");
            Assert.That(list.Status, Is.EqualTo(200), $"A Viewer could not list leads: {list}");
        });

        // ── 3. Switched off ──────────────────────────────────────────
        var tokenBeforeOff = Api.Mint(await Db.ApiIdentityAsync(leaver.Email));
        await Step("The admin switches the leaver off", async () =>
        {
            var r = await admin.PatchAsync($"/api/users/{leaverId}/status",
                new { tenantId, userId = leaverId, isActive = false });
            Assert.That(r.IsSuccess, Is.True, $"Switching the person off failed: {r}");
        });

        await Step("Switched off: the token is refused, the session is gone, and signing in is refused", async () =>
        {
            var r = await With(tokenBeforeOff).GetAsync("/api/leads?pageNumber=1&pageSize=1");
            Assert.That(r.Status, Is.EqualTo(401), $"A switched-off person's token still worked: {r}");

            await page.GotoAsync("/Dashboard");
            Assert.That(Ui.PathOf(page.Url), Does.StartWith("/Account/Login"),
                $"A switched-off person's session still opened {Ui.PathOf(page.Url)}.");

            await ExpectSignInRefusedAsync(leaver);
        });

        // ── 4. Switched back on ──────────────────────────────────────
        await Step("The admin switches the leaver back on", async () =>
        {
            var r = await admin.PatchAsync($"/api/users/{leaverId}/status",
                new { tenantId, userId = leaverId, isActive = true });
            Assert.That(r.IsSuccess, Is.True, $"Switching the person back on failed: {r}");
        });

        await Step("Back on: the OLD token stays refused, and a fresh sign-in works", async () =>
        {
            var old = await With(tokenBeforeOff).GetAsync("/api/leads?pageNumber=1&pageSize=1");
            Assert.That(old.Status, Is.EqualTo(401),
                $"Switching the person back on brought their old token back to life: {old}. " +
                "Switching someone off must end their sessions for good.");

            var fresh = await NewPageAsync("leaver-again");
            await Ui.SignInAsync(fresh, leaver);
            await fresh.GotoAsync("/Leads");
            Assert.That(Ui.PathOf(fresh.Url), Does.StartWith("/Leads"), "A fresh sign-in after being switched back on did not work.");
        });

        // ── 5. Deleted ───────────────────────────────────────────────
        var tokenBeforeDelete = Api.Mint(await Db.ApiIdentityAsync(leaver.Email));
        await Step("The admin deletes the leaver: their token is refused and signing in is refused", async () =>
        {
            var r = await admin.DeleteAsync($"/api/users/{leaverId}?tenantId={tenantId}");
            Assert.That(r.IsSuccess, Is.True, $"Deleting the person failed: {r}");

            var after = await With(tokenBeforeDelete).GetAsync("/api/leads?pageNumber=1&pageSize=1");
            Assert.That(after.Status, Is.EqualTo(401), $"A deleted person's token still worked: {after}");

            await ExpectSignInRefusedAsync(leaver);
        });

        // Gone for good — no later test should try to use them.
        a.People.Remove(leaver.Key);
        a.RoleOf.Remove(leaver.Key);
    }

    /// <summary>A caller that always sends this one token, as a client holding it would.</summary>
    private static ApiCaller With(string token) => new("a held token", () => token);

    /// <summary>The sign-in page refuses this person: it stays on /Account/Login and says so.</summary>
    private async Task ExpectSignInRefusedAsync(TestUser user)
    {
        var p = await NewPageAsync("refused-" + user.Key);
        await p.GotoAsync("/Account/Login");
        await p.Locator("#Email").FillAsync(user.Email);
        await p.Locator("#Password").FillAsync(user.Password);
        await p.Locator("form button[type=submit]").ClickAsync();
        await Ui.WaitForDocumentAsync(p);

        // Give a wrongly-accepted sign-in the time to redirect, then look.
        await p.WaitForTimeoutAsync(1500);
        Assert.That(Ui.PathOf(p.Url), Does.StartWith("/Account/Login"),
            $"{user.Key} could still sign in (landed on {Ui.PathOf(p.Url)}).");
    }
}
