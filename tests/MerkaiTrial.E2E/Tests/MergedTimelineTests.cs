// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/MergedTimelineTests.cs
//
// NEW (099). After a merge, the kept lead's timeline also shows the
// merged lead's own history — when it was added and its status changes —
// labelled "from merged lead <name>". Nothing is copied: it is read from
// the merged lead through the merge's audit rows.
//
//   1. Two leads in workspace A (different emails, so no duplicate prompt).
//   2. The second is qualified — a status change on ITS history.
//   3. Merge the second into the first from the first lead's page.
//   4. The first lead's page shows "Status: … → … · from merged lead …"
//      and "Lead created · from merged lead …".
// =====================================================================

using System.Text.RegularExpressions;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("LeadMerge")]
public class MergedTimelineTests : E2ETest
{
    [Test]
    public async Task The_kept_lead_shows_the_merged_leads_history()
    {
        var admin = await SignedInAsync(World.A.Admin, new ViewportSize { Width = 1366, Height = 900 });
        var tag = $"MT{Random.Shared.Next(100000, 999999)}";
        var keepName = $"Tara Keep {tag}";
        var otherName = $"Tara Other {tag}";

        var keepId = await Step("Add the lead to keep", () =>
            Flows.CreateLeadAsync(admin, keepName, $"tara.keep.{tag}@example.com", 100000m, companyName: $"Tara Keep Co {tag}"));

        var otherId = await Step("Add the other lead and qualify it", async () =>
        {
            var id = await Flows.CreateLeadAsync(admin, otherName, $"tara.other.{tag}@example.com", 200000m, companyName: $"Tara Other Co {tag}");
            await Flows.QualifyLeadAsync(admin);
            return id;
        });

        await Step("Merge the other lead into the kept one", async () =>
        {
            await admin.GotoAsync($"/Leads/Detail/{keepId}");
            await admin.Locator("#leadMerge").ClickAsync();
            await Ui.WaitForPageAsync(admin, u => u.Contains("/Leads/Merge", StringComparison.OrdinalIgnoreCase), "Merge leads");

            await admin.Locator("#mergeSearch").FillAsync(otherName);
            await admin.GetByRole(AriaRole.Button, new() { Name = "Find" }).ClickAsync();
            var result = admin.Locator("#mergeResults a", new() { HasText = otherName });
            await Assertions.Expect(result).ToHaveCountAsync(1);
            await result.ClickAsync();
            await Assertions.Expect(admin.Locator("#mergeForm")).ToBeVisibleAsync();

            await admin.Locator("#mergeSubmit").ClickAsync();     // confirm() is accepted by the session
            await Ui.ExpectSuccessAsync(admin, "was merged into");
            Assert.That(await Db.IsLeadDeletedAsync(otherId), Is.True, "The other lead was not removed by the merge.");
        });

        await Step("The kept lead's timeline shows the merged lead's history, labelled as such", async () =>
        {
            await admin.GotoAsync($"/Leads/Detail/{keepId}");
            var label = $"from merged lead {otherName}";

            // The timeline may sit in a tab that is filled when opened.
            if (!(await admin.ContentAsync()).Contains(label))
            {
                var tab = admin.Locator("a, button").Filter(new() { HasTextRegex = new Regex(@"^\s*(Timeline|History)\s*$", RegexOptions.IgnoreCase) });
                if (await tab.CountAsync() > 0) await tab.First.ClickAsync();
            }

            await Ui.WaitUntilAsync(admin,
                async () => (await admin.ContentAsync()).Contains(label),
                $"The kept lead's page never showed \"{label}\".");

            var html = await admin.ContentAsync();
            Assert.That(html, Does.Contain("Lead created · " + label), "The merged lead's \"created\" entry is missing.");
            Assert.That(Regex.IsMatch(html, @"Status: [^<]+ → [^<]+ · " + Regex.Escape(label)), Is.True,
                "The merged lead's status change is missing from the kept lead's timeline.");
        });
    }
}
