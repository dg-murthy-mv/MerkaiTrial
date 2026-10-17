// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/MobileWorkTests.cs
//
// NEW (094). A rep's day on a phone (375px wide), in workspace A. Each
// test makes what it needs on a desktop first — a lead, or a lead turned
// into a deal — and then does the work from a phone-sized browser.
//
//   1. A lead on a phone: the page fits the screen (the header's four
//      buttons used to push it sideways); the quick bar has Call and Log
//      activity; Log activity opens the form, and a call logged there is
//      saved on the lead.
//   2. A deal on a phone: the page fits; Add task in the quick bar opens
//      the task form, and the task is saved on the deal.
//   3. The pipeline on a phone: the deal's card has a Move button (phones
//      cannot drag); it lists the moves the process allows from the
//      card's stage; choosing one (answering its note, if it asks) moves
//      the deal — checked in the database, not just on screen.
//
// Run just these:  -Filter "FullyQualifiedName~Mobile"
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Mobile")]
public class MobileWorkTests : E2ETest
{
    private static TestTenant A => World.A;

    // =================================================================
    // 1. A lead
    // =================================================================

    [Test]
    public async Task A_call_can_be_logged_on_a_lead_from_a_phone()
    {
        var desk = await SignedInAsync(A.Admin);
        var name = World.Unique("Ravi Road");
        var leadId = await Step("Add a lead (on a desktop)", () =>
            Flows.CreateLeadAsync(desk, name, World.EmailFor("A", "rr" + Random.Shared.Next(10000, 99999)), 180000m, A));

        var phone = await SignedInAsync(A.Admin, Phone.Size);
        var subject = $"Called from the car {World.Stamp}";

        await Step("The lead's page fits the screen", async () =>
        {
            await phone.GotoAsync($"/Leads/Detail/{leadId}");
            await Assertions.Expect(phone.Locator("#statusChangeForm")).ToBeVisibleAsync();
            await Phone.ExpectFitsAsync(phone, "The lead's page");
        });

        await Step("The quick bar offers Call and Log activity", async () =>
        {
            await Assertions.Expect(phone.Locator("#qaCall")).ToBeVisibleAsync();
            Assert.That(await phone.Locator("#qaCall").GetAttributeAsync("href"), Does.StartWith("tel:"));
            await Assertions.Expect(phone.Locator("#qaLog")).ToBeVisibleAsync();
        });

        await Step("Log activity opens the form; a call logged there is saved", async () =>
        {
            await phone.Locator("#qaLog").ClickAsync();
            await Assertions.Expect(phone.Locator("#activities")).ToBeVisibleAsync();
            await phone.Locator("#ActivityInput_ActivityType").SelectOptionAsync("Call");
            await phone.Locator("#ActivityInput_Subject").FillAsync(subject);
            await phone.Locator("#activities form button[type=submit]").First.ClickAsync();

            await Ui.WaitUntilAsync(phone,
                async () => await Db.CountActivitiesAsync(leadId, subject) == 1,
                "The call logged from the phone was not saved on the lead.");
        });
    }

    // =================================================================
    // 2. A deal
    // =================================================================

    [Test]
    public async Task A_task_can_be_added_to_a_deal_from_a_phone()
    {
        var desk = await SignedInAsync(A.Admin);
        var (dealId, _) = await Step("Make a deal (on a desktop)", () => NewDealAsync(desk, "Kavya Phone"));

        var phone = await SignedInAsync(A.Admin, Phone.Size);
        var subject = $"Send the site photos {World.Stamp}";

        await Step("The deal's page fits the screen", async () =>
        {
            await phone.GotoAsync($"/Pipeline/Detail/{dealId}");
            await Assertions.Expect(phone.Locator("#qaTask")).ToBeVisibleAsync();
            await Phone.ExpectFitsAsync(phone, "The deal's page");
        });

        await Step("Add task opens the form; the task is saved on the deal", async () =>
        {
            await phone.Locator("#qaTask").ClickAsync();
            await Assertions.Expect(phone.Locator("#tasks")).ToBeVisibleAsync();
            await phone.Locator("#TaskInput_Subject").FillAsync(subject);
            // The due date comes filled in (tomorrow morning); leave it.
            await phone.Locator("#tasks form button[type=submit]").First.ClickAsync();

            await Ui.WaitUntilAsync(phone,
                async () => await Db.CountActivitiesAsync(dealId, subject) == 1,
                "The task added from the phone was not saved on the deal.");
        });
    }

    // =================================================================
    // 3. The board
    // =================================================================

    [Test]
    public async Task A_deal_can_be_moved_on_the_board_from_a_phone()
    {
        var desk = await SignedInAsync(A.Admin);
        var (dealId, _) = await Step("Make a deal (on a desktop)", () => NewDealAsync(desk, "Arjun Board"));
        var before = await Db.DealStageAsync(dealId);

        var phone = await SignedInAsync(A.Admin, Phone.Size);
        var card = phone.Locator($".pb-card[data-deal-id='{dealId}']");

        await Step("The board fits the screen, and the deal's card has a Move button", async () =>
        {
            await phone.GotoAsync("/Pipeline?View=board");
            await Assertions.Expect(card).ToHaveCountAsync(1);
            await Phone.ExpectFitsAsync(phone, "The pipeline board");
            await card.ScrollIntoViewIfNeededAsync();
            await Assertions.Expect(card.Locator(".pb-move")).ToBeVisibleAsync();
        });

        var target = await Step("Move lists where the deal can go", async () =>
        {
            await card.Locator(".pb-move").ClickAsync();
            await Assertions.Expect(phone.Locator("#pbMoveModal")).ToBeVisibleAsync();

            var options = phone.Locator("#pbMoveOptions button");
            if (await options.CountAsync() == 0)
                throw new AssertionException(
                    $"Move offered nothing from stage '{before}'. " +
                    await Ui.TextOrEmptyAsync(phone.Locator("#pbMoveNone")));

            // An open stage if there is one: closing a deal can ask for
            // things (an accepted quote) a fresh deal does not have.
            var open = phone.Locator("#pbMoveOptions button[data-category='open']");
            return await open.CountAsync() > 0 ? open.First : options.First;
        });

        var to = await target.GetAttributeAsync("data-to") ?? "";
        var label = (await target.InnerTextAsync()).Trim();
        var needsNote = await target.GetAttributeAsync("data-needs-note") == "true";
        TestContext.Progress.WriteLine($"[e2e] Moving '{before}' → '{to}' with \"{label}\"{(needsNote ? " (asks for a note)" : "")}");

        await Step($"Choosing \"{label}\" moves the deal", async () =>
        {
            await target.ClickAsync();
            if (needsNote)
            {
                await Assertions.Expect(phone.Locator("#pbNoteModal")).ToBeVisibleAsync();
                await phone.Locator("#pbNoteText").FillAsync("Moved from a phone in the E2E run.");
                await phone.Locator("#pbNoteConfirm").ClickAsync();
            }

            try
            {
                await Ui.WaitUntilAsync(phone,
                    async () => await Db.DealStageAsync(dealId) == to,
                    $"The deal did not move to '{to}'.");
            }
            catch (AssertionException ex)
            {
                var now = await Db.DealStageAsync(dealId);
                var refused = await Ui.TextOrEmptyAsync(phone.Locator("#pbRefused:not(.pb-hidden) #pbRefusedText"));
                throw new AssertionException($"{ex.Message} It is in '{now}'." +
                                             (refused.Length > 0 ? $" The board said: {refused}" : ""));
            }
        });
    }

    // ── helpers ───────────────────────────────────────────────────────

    /// <summary>Lead → qualify → convert, on the given (desktop) page.</summary>
    private static async Task<(Guid Id, string Title)> NewDealAsync(IPage page, string person)
    {
        var leadName = World.Unique(person);
        var dealTitle = World.Unique(person.Split(' ')[0] + " deal");
        await Flows.CreateLeadAsync(page, leadName, World.EmailFor("A", "m" + Random.Shared.Next(10000, 99999)), 300000m, A);
        await Flows.QualifyLeadAsync(page);
        await Flows.ConvertLeadAsync(page, dealTitle, 300000m);

        var tenantId = await Db.TenantIdAsync(A.Name);
        return (await Db.DealIdByTitleAsync(tenantId, dealTitle), dealTitle);
    }
}
