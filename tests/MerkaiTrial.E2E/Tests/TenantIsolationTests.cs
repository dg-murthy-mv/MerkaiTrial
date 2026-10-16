// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/TenantIsolationTests.cs
//
// The most important promise a multi-company CRM makes: one company never
// sees another's records — not in lists, not in search, and not by
// pasting a record's address into the browser.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Smoke")]
public class TenantIsolationTests : E2ETest
{
    [Test]
    public async Task One_workspace_cannot_see_another_workspaces_lead()
    {
        var secretName = World.Unique("Bhavna Secret");

        var b = await SignedInAsync(World.B.Admin);
        var leadId = await Step("Workspace B adds a lead", () =>
            Flows.CreateLeadAsync(b, secretName, World.EmailFor("B", "customer"), 42000m));

        var a = await SignedInAsync(World.A.Admin);

        await Step("Workspace A's search does not find it", async () =>
        {
            await a.GotoAsync("/Leads?SearchTerm=" + Uri.EscapeDataString(secretName));
            await Assertions.Expect(a.Locator(".lx-table a", new() { HasText = secretName })).ToHaveCountAsync(0);
        });

        await Step("Workspace A cannot open it by its address", async () =>
        {
            await a.GotoAsync($"/Leads/Detail/{leadId}");
            await Assertions.Expect(a.GetByText(secretName)).ToHaveCountAsync(0);
        });

        await Step("Workspace A cannot edit it by its address", async () =>
        {
            await a.GotoAsync($"/Leads/Edit/{leadId}");
            Assert.That(await a.Locator("#Input_FullName").CountAsync() == 0
                        || await a.Locator("#Input_FullName").InputValueAsync() != secretName,
                Is.True, "Workspace A opened workspace B's lead in the edit form.");
        });

        await Step("Workspace B still sees its own lead", async () =>
        {
            await b.GotoAsync($"/Leads/Detail/{leadId}");
            await Assertions.Expect(b.Locator("h2", new() { HasText = secretName })).ToBeVisibleAsync();
        });
    }
}
