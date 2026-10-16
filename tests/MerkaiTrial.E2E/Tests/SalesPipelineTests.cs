// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/SalesPipelineTests.cs
//
// The whole sales journey in one test, the way a workspace admin and
// their customer would do it:
//
//   lead → qualified → deal → quote sent → customer accepts on /q/ →
//   invoice → issued → paid
//
// One long test on purpose: each step needs the previous one, and when it
// breaks, the step name in the failure says exactly where.
// =====================================================================

using System.Globalization;
using System.Text.RegularExpressions;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Smoke"), Category("Pipeline")]
public class SalesPipelineTests : E2ETest
{
    [Test]
    public async Task Lead_to_paid_invoice()
    {
        var leadName = World.Unique("Priya Pipeline");
        var dealTitle = World.Unique("Office fit-out");
        var customerEmail = World.EmailFor("A", "customer" + Random.Shared.Next(1000, 9999));

        var admin = await SignedInAsync(World.A.Admin);

        // ── Lead ──────────────────────────────────────────────────────
        await Step("Add a lead", () => Flows.CreateLeadAsync(admin, leadName, customerEmail, 250000m));

        await Step("Mark the lead qualified", async () =>
        {
            var status = admin.Locator("#statusChangeForm select[name=status]");
            await Ui.SelectByPatternAsync(status, @"qualified", excludePattern: @"(dis|un|not\s*)qualified");
            // The form submits itself on change; the Convert button appears
            // once the page comes back with a qualified status.
            await Assertions.Expect(admin.Locator("a[href*='ConvertToDeal']").First)
                .ToBeVisibleAsync(new() { Timeout = E2ESettings.ActionTimeoutMs });
        });

        // ── Deal ──────────────────────────────────────────────────────
        await Step("Convert the lead to a deal", async () =>
        {
            await admin.Locator("a[href*='ConvertToDeal']").First.ClickAsync();
            await admin.Locator("#Input_DealTitle").FillAsync(dealTitle);
            await admin.Locator("#Input_ExpectedValue").FillAsync("250000");
            await admin.Locator("#Input_ExpectedCloseDate").FillAsync(
                DateTime.Today.AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            // A stage a quote can be raised from (Quotes → New only lists
            // Discovery, Qualification and Proposal deals); the first stage
            // if this workspace calls them something else.
            var stage = admin.Locator("label.lc-stage", new() { HasTextRegex = new(@"discovery|qualification|proposal", RegexOptions.IgnoreCase) });
            if (await stage.CountAsync() == 0) stage = admin.Locator("label.lc-stage");
            await stage.First.ClickAsync();
            await Assertions.Expect(admin.Locator("input[name='Input.Stage']:checked")).ToHaveCountAsync(1);

            await admin.GetByRole(AriaRole.Button, new() { Name = "Create the deal" }).ClickAsync();
            await admin.WaitForURLAsync(u => Ui.PathOf(u).StartsWith("/Pipeline", StringComparison.OrdinalIgnoreCase));
        });

        // ── Quote ─────────────────────────────────────────────────────
        await Step("Create a quote for the deal and send it", async () =>
        {
            await admin.GotoAsync("/Quotes/Create");
            await Ui.SelectByTextAsync(admin.Locator("#SelectedDealId"), dealTitle);
            await admin.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();

            // The editor opens with one empty line already there. Wait for
            // it to finish drawing first: checking straight after "Continue"
            // can look at the page before the editor has run, see no lines,
            // and add a second one. Then use the existing line; press "Add
            // line" only if the editor really started empty.
            await Assertions.Expect(admin.Locator("#qcForm")).ToBeVisibleAsync();   // step 2 has loaded
            var rows = admin.Locator("#qeBody tr:has([data-f=name])");
            try
            {
                await Ui.WaitUntilAsync(admin, async () => await rows.CountAsync() > 0,
                    "no starter line", timeoutMs: 8_000);
            }
            catch (AssertionException)
            {
                // This workspace's editor starts empty — add the one line.
                await admin.Locator("#qeAddLine").ClickAsync();
            }
            await Assertions.Expect(rows).ToHaveCountAsync(1);
            var row = rows.First;
            await row.Locator("[data-f=name]").FillAsync("Design and fit-out");
            await row.Locator("[data-f=quantity]").FillAsync("2");
            await row.Locator("[data-f=unitPrice]").FillAsync("125000");

            await admin.Locator("#qcSend").ClickAsync();

            // Either the quote opens, or the page says why it didn't. The
            // first run only reported "Timeout 60000ms exceeded" here.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!admin.Url.Contains("/Quotes/Detail", StringComparison.OrdinalIgnoreCase))
            {
                if (DateTime.UtcNow > deadline)
                    throw new AssertionException(
                        $"Save & send did not open the quote (still on {Ui.PathOf(admin.Url)}). " +
                        await Ui.DescribePageProblemsAsync(admin));
                await admin.WaitForTimeoutAsync(500);
            }
        });

        var quoteUrl = admin.Url;

        var publicLink = await Step("The quote has a customer link", async () =>
        {
            var link = admin.Locator("#qdLink");
            try
            {
                await Assertions.Expect(link).ToBeVisibleAsync();
            }
            catch (Exception ex) when (ex is not AssertionException)
            {
                // No customer link means the quote never reached "Sent".
                // Say what the page said rather than "expected to be visible".
                throw new AssertionException(
                    "The quote was saved but has no customer link, so it was not sent. " +
                    await Ui.DescribePageProblemsAsync(admin));
            }
            var text = (await link.InnerTextAsync()).Trim();
            Assert.That(text, Does.Contain("/q/"), "The customer link does not look like /q/{token}.");
            return Ui.Rebase(text);
        });

        // ── The customer ──────────────────────────────────────────────
        await Step("The customer accepts the quote on the public page", async () =>
        {
            var customer = await NewPageAsync("customer");
            await customer.GotoAsync(publicLink);
            await customer.Locator(".btn-accept").ClickAsync();
            await Assertions.Expect(customer.GetByText("You have accepted this quote")).ToBeVisibleAsync();
        });

        // ── Invoice ───────────────────────────────────────────────────
        await Step("Create the invoice from the accepted quote", async () =>
        {
            await admin.GotoAsync(quoteUrl);
            await admin.GetByRole(AriaRole.Button, new() { Name = "Create invoice" }).ClickAsync();

            // Either straight to the invoice, or back on the quote with
            // "A draft invoice exists — Open draft".
            var openDraft = admin.Locator("main a[href*='/Invoices/Detail']");
            await Ui.WaitUntilAsync(admin,
                async () => admin.Url.Contains("/Invoices/Detail", StringComparison.OrdinalIgnoreCase)
                            || await openDraft.CountAsync() > 0,
                "No invoice appeared after Create invoice.");

            if (!admin.Url.Contains("/Invoices/Detail", StringComparison.OrdinalIgnoreCase))
            {
                await openDraft.First.ClickAsync();
                await admin.WaitForURLAsync(u => u.Contains("/Invoices/Detail", StringComparison.OrdinalIgnoreCase));
            }
        });

        await Step("Issue the invoice", async () =>
        {
            var issue = admin.GetByRole(AriaRole.Button, new() { Name = "Issue invoice" });
            if (await issue.CountAsync() > 0)
            {
                await issue.ClickAsync();   // the confirm() is accepted by the session
                await Assertions.Expect(issue).ToHaveCountAsync(0, new() { Timeout = E2ESettings.ActionTimeoutMs });
            }
        });

        await Step("Record the full payment", async () =>
        {
            await admin.Locator("button[data-bs-target='#paymentModal']").ClickAsync();
            var modal = admin.Locator("#paymentModal");
            await Assertions.Expect(modal).ToBeVisibleAsync();
            // The amount is pre-filled with the balance.
            await modal.Locator("button[type=submit]").ClickAsync();
        });

        await Step("The invoice is paid", async () =>
        {
            // Exactly "Paid" — "Partially paid" must not count.
            var paid = admin.Locator("main span.badge",
                new() { HasTextRegex = new(@"^\s*Paid\s*$", RegexOptions.IgnoreCase) });
            await Assertions.Expect(paid.First)
                .ToBeVisibleAsync(new() { Timeout = E2ESettings.ActionTimeoutMs });
        });
    }
}
