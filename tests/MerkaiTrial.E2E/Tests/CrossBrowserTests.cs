// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/CrossBrowserTests.cs
//
// NEW (109). Every other test runs in Chromium (the engine of Chrome and
// Edge). These run the heart of the product in the two other engines:
//
//   firefox  Firefox, on a desktop-sized window
//   webkit   WebKit — the engine of Safari on Mac, and of EVERY browser on
//            an iPhone or iPad (Apple requires it, Chrome included). Its
//            deal move is done on an iPhone-sized screen, with the board's
//            Move button, as a person on an iPhone would.
//
// The journey, as workspace A's admin, with the customer's page opened in
// the same engine:
//   sign in → add a lead → qualify and convert it → move the deal to
//   another open stage → send a quote → the customer accepts it → the quote
//   shows Accepted.
//
// Both engines are downloaded the first time (a minute or two, once per
// computer) and started only when these tests run. To run only these:
//   run-e2e.ps1 -Filter "Category=CrossBrowser"
// =====================================================================

using System.Text.RegularExpressions;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("CrossBrowser")]
public class CrossBrowserTests : E2ETest
{
    private static TestTenant A => World.A;

    [TestCase("firefox")]
    [TestCase("webkit")]
    public async Task Sign_in_lead_deal_quote_and_customer_acceptance_work_in(string engine)
    {
        var passed = false;
        try
        {
            await JourneyAsync(engine);
            passed = true;
        }
        finally
        {
            // 109-update — the engine is closed as soon as its test is done (memory; see GlobalSetup).
            // Its sessions first, so a failed one still saves its trace.
            await CloseSessionsNowAsync(passed);
            await GlobalSetup.CloseOtherBrowserAsync(engine);
        }
    }

    private async Task JourneyAsync(string engine)
    {
        var browser = await Step($"Start {engine}", () => GlobalSetup.OtherBrowserAsync(engine));
        var tenantId = await Db.TenantIdAsync(A.Name);
        var tag = Random.Shared.Next(10000, 99999).ToString();

        var admin = await Step($"Sign in to Merkai in {engine}", () => SignedInAsync(A.Admin, browser: browser));

        var dealTitle = World.Unique($"{engine} deal");
        var (_, dealId) = await Step("Add a lead, qualify it and convert it to a deal", () =>
            Flows.LeadToDealAsync(admin, A, World.Unique($"{Cap(engine)} Lead"), World.EmailFor("A", engine + tag),
                dealTitle, 180000m, $"{Cap(engine)} Browser Co {tag}"));

        var open = await Db.OpenStageKeysAsync(tenantId);
        var before = await Db.DealStageAsync(dealId) ?? "";

        if (engine == "webkit")
        {
            await Step("On an iPhone-sized screen, move the deal with the board's Move button", async () =>
            {
                var phone = await SignedInAsync(A.Admin, Phone.Size, browser);
                await MoveOnBoardAsync(phone, dealId, before, open);
            });
        }
        else
        {
            await Step("On the deal's page, move it to another open stage", () => MoveOnDealPageAsync(admin, dealId, before, open));
        }

        var quoteId = await Step("Send a quote for the deal", () =>
            Flows.SendQuoteAsync(admin, dealTitle, new QuoteLine("Browser check works", 1, 180000m, TaxRate: 18)));
        var link = await Step("The quote has a customer link", () => Flows.CustomerLinkAsync(admin));

        await Step($"The customer opens the link in {engine} and accepts the quote", async () =>
        {
            var customer = await NewPageAsync("customer", browser: browser);
            await Flows.AcceptAsCustomerAsync(customer, link);
        });

        await Step("Back in Merkai, the quote shows Accepted", async () =>
        {
            await admin.GotoAsync($"/Quotes/Detail?id={quoteId}");
            await Assertions.Expect(admin.Locator("main").First)
                .ToContainTextAsync(new Regex("accepted", RegexOptions.IgnoreCase), new() { Timeout = E2ESettings.ActionTimeoutMs });
        });
    }

    private static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>The deal page's stage bar: a move to another OPEN stage, answering the note box if one opens.</summary>
    private static async Task MoveOnDealPageAsync(IPage page, Guid dealId, string before, HashSet<string> open)
    {
        await page.GotoAsync($"/Pipeline/Detail/{dealId}");
        var buttons = page.Locator(".transition-bar .move-btn");
        var count = await buttons.CountAsync();

        ILocator? target = null;
        string to = "";
        for (var i = 0; i < count && target is null; i++)
        {
            var key = await buttons.Nth(i).GetAttributeAsync("data-stage") ?? "";
            if (open.Contains(key) && !string.Equals(key, before, StringComparison.OrdinalIgnoreCase))
            {
                target = buttons.Nth(i);
                to = key;
            }
        }
        if (target is null)
            throw new AssertionException(
                $"The deal page offers no move from '{before}' to another open stage. The bar says: " +
                await Ui.TextOrEmptyAsync(page.Locator(".transition-bar")));

        var needsNote = await target.GetAttributeAsync("data-needs-note") == "true";
        await target.ClickAsync();
        if (needsNote)
        {
            var modal = page.Locator("#moveNoteModal");
            await Assertions.Expect(modal).ToBeVisibleAsync();
            await page.Locator("#moveNoteText").FillAsync("Moved in another browser (e2e).");
            await modal.Locator("button[type=submit]").ClickAsync();
        }

        await WaitForStageAsync(page, dealId, to);
    }

    /// <summary>The board's Move button (phone-sized screens), to an open stage, answering the note box if asked.</summary>
    private static async Task MoveOnBoardAsync(IPage phone, Guid dealId, string before, HashSet<string> open)
    {
        await phone.GotoAsync("/Pipeline?View=board");
        var card = phone.Locator($".pb-card[data-deal-id='{dealId}']");
        await Assertions.Expect(card).ToHaveCountAsync(1);
        await card.ScrollIntoViewIfNeededAsync();
        await card.Locator(".pb-move").ClickAsync();
        await Assertions.Expect(phone.Locator("#pbMoveModal")).ToBeVisibleAsync();

        var options = phone.Locator("#pbMoveOptions button[data-category='open']");
        var count = await options.CountAsync();
        ILocator? target = null;
        string to = "";
        for (var i = 0; i < count && target is null; i++)
        {
            var key = await options.Nth(i).GetAttributeAsync("data-to") ?? "";
            if (open.Contains(key) && !string.Equals(key, before, StringComparison.OrdinalIgnoreCase))
            {
                target = options.Nth(i);
                to = key;
            }
        }
        if (target is null)
            throw new AssertionException(
                $"Move offered no other open stage from '{before}'. " + await Ui.TextOrEmptyAsync(phone.Locator("#pbMoveModal")));

        var needsNote = await target.GetAttributeAsync("data-needs-note") == "true";
        await target.ClickAsync();
        if (needsNote)
        {
            await Assertions.Expect(phone.Locator("#pbNoteModal")).ToBeVisibleAsync();
            await phone.Locator("#pbNoteText").FillAsync("Moved from an iPhone-sized WebKit screen (e2e).");
            await phone.Locator("#pbNoteConfirm").ClickAsync();
        }

        await WaitForStageAsync(phone, dealId, to);
    }

    private static async Task WaitForStageAsync(IPage page, Guid dealId, string to)
    {
        try
        {
            await Ui.WaitUntilAsync(page, async () => string.Equals(await Db.DealStageAsync(dealId), to, StringComparison.OrdinalIgnoreCase),
                $"The deal did not move to '{to}'.");
        }
        catch (AssertionException ex)
        {
            var refused = await Ui.TextOrEmptyAsync(page.Locator(".alert-danger:visible, #pbRefused:not(.pb-hidden) #pbRefusedText"));
            throw new AssertionException($"{ex.Message} It is in '{await Db.DealStageAsync(dealId)}'." +
                                         (refused.Length > 0 ? $" The page said: {refused}" : ""));
        }
    }
}
