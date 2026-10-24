// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/AcceptedQuoteTests.cs
//
// TEST 89 (round 102) —
//   An_accepted_quote_cannot_be_changed_without_the_required_controls
//
// Once the customer accepts a quote, what they agreed to is fixed. Every
// way of changing it is tried, and after each one the quote's status,
// total and lines are exactly what the customer accepted:
//
//   • editing its lines or price — refused;
//   • moving it back to Draft or Sent, or to Rejected — refused;
//   • the customer answering again on the public link — refused;
//   • deleting it — refused (round 102: it could be deleted until an
//     invoice was raised, which erased the acceptance);
//   • deleting its deal — refused (round 102, DeleteRules);
//   • its payment schedule may be arranged, but only through the schedule's
//     own control: stages that don't add up to the quote are refused, and
//     the quote's total never moves.
//
// (The way to change the price is a new quote on the same deal.)
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture]
public class AcceptedQuoteTests : E2ETest
{
    [Test]
    public async Task An_accepted_quote_cannot_be_changed_without_the_required_controls()
    {
        var a = World.A;
        var admin = await Api.AsAsync(a.Admin);
        await Api.EnsureTokensWorkAsync(admin);
        var page = await SignedInAsync(a.Admin);

        var dealTitle = World.Unique("Accepted quote deal");
        var (_, dealId) = await Step("Make a deal", () =>
            Flows.LeadToDealAsync(page, a, World.Unique("Asha Accepted"), World.EmailFor("A", "accepted" + Random.Shared.Next(1000, 9999)),
                dealTitle, 120000m));

        var quoteId = await Step("Send a quote: 2 × 60,000", () =>
            Flows.SendQuoteAsync(page, dealTitle, new QuoteLine("Fit-out", 2, 60000m)));

        await Step("The customer accepts it", async () =>
        {
            await page.GotoAsync($"/Quotes/Detail?id={quoteId}");
            var link = await Flows.CustomerLinkAsync(page);
            await Flows.AcceptAsCustomerAsync(await NewPageAsync("customer"), link);
        });

        var accepted = await Db.QuoteAsync(quoteId);
        var acceptedLines = await Db.QuoteLinesAsync(quoteId);
        Assert.That(accepted.PublicLinkToken, Is.Not.Null.And.Not.Empty, "The quote has no customer link token.");

        async Task ExpectUnchangedAsync(string after)
        {
            var now = await Db.QuoteAsync(quoteId);
            Assert.That(now.Status, Is.EqualTo(accepted.Status), $"After {after}, the quote is no longer accepted.");
            Assert.That(now.GrandTotal, Is.EqualTo(accepted.GrandTotal), $"After {after}, the quote's total changed.");
            Assert.That(await Db.QuoteLinesAsync(quoteId), Is.EqualTo(acceptedLines), $"After {after}, the quote's lines changed.");
            Assert.That(await Db.IsRowDeletedAsync("Quotes", quoteId), Is.False, $"After {after}, the quote is deleted.");
        }

        await Step("Editing its lines and price is refused", async () =>
        {
            var r = await admin.PutAsync($"/api/quotes/{quoteId}", new
            {
                issueDateUtc = DateTime.UtcNow.Date,
                expiresAtUtc = DateTime.UtcNow.Date.AddDays(30),
                currency = a.Currency,
                items = new[] { new { name = "Fit-out", description = "", unitPrice = 90000m, quantity = 2m, lineDiscount = 0m, taxRate = 0m } },
            });
            Assert.That(r.Status, Is.EqualTo(400), $"An accepted quote's lines could be edited: {r}");
            await ExpectUnchangedAsync("the edit");
        });

        await Step("Moving it back to Draft or Sent, or to Rejected, is refused", async () =>
        {
            foreach (var status in new[] { "Draft", "Sent", "Rejected" })
            {
                var r = await admin.PutAsync($"/api/quotes/{quoteId}/status", new { status });
                Assert.That(r.Status, Is.InRange(400, 499), $"An accepted quote could be moved to {status}: {r}");
            }
            await ExpectUnchangedAsync("the status changes");
        });

        await Step("The customer can't change their answer on the public link", async () =>
        {
            // The app answers "Quote already decided — no change needed":
            // a refusal by doing nothing. Either that or a 4xx is right; what
            // matters is that the quote is still accepted (checked below).
            var reject = await Api.Anonymous.PutAsync($"/api/quotes/public/{accepted.PublicLinkToken}/status", new { status = "Rejected" });
            Assert.That(reject.Status, Is.LessThan(500), $"Rejecting an accepted quote caused a server error: {reject}");

            // Accepting again is a harmless repeat (nothing to change) —
            // it may answer OK, but must change nothing.
            var again = await Api.Anonymous.PutAsync($"/api/quotes/public/{accepted.PublicLinkToken}/status", new { status = "Accepted" });
            Assert.That(again.Status, Is.LessThan(500), $"Accepting a second time caused a server error: {again}");

            await ExpectUnchangedAsync("the customer's second answer");
        });

        await Step("Deleting it is refused, and says why", async () =>
        {
            var r = await admin.DeleteAsync($"/api/quotes/{quoteId}");
            Assert.That(r.Status, Is.EqualTo(400), $"An accepted quote could be deleted: {r}");
            Assert.That(r.Error, Does.Contain("accepted").IgnoreCase, $"The refusal doesn't say why: {r}");
            await ExpectUnchangedAsync("the delete");
        });

        await Step("Deleting its deal is refused", async () =>
        {
            var r = await admin.DeleteAsync($"/api/deals/{dealId}");
            Assert.That(r.Status, Is.EqualTo(400), $"The deal of an accepted quote could be deleted: {r}");
            Assert.That(await Db.IsRowDeletedAsync("Deals", dealId), Is.False, "The deal was deleted.");
            await ExpectUnchangedAsync("deleting the deal");
        });

        await Step("Its payment schedule goes through its own control: stages that don't add up are refused", async () =>
        {
            var path = $"/api/quotes/{quoteId}/billing-schedule";

            var bad = await admin.PutAsync(path, new
            {
                milestones = new[]
                {
                    new { sortOrder = 10, name = "Deposit", percent = (decimal?)30m },
                    new { sortOrder = 20, name = "Handover", percent = (decimal?)60m },   // 90% in all
                },
            });
            Assert.That(bad.Status, Is.EqualTo(400), $"A schedule that doesn't add up to the quote was saved: {bad}");

            var good = await admin.PutAsync(path, new
            {
                milestones = new[]
                {
                    new { sortOrder = 10, name = "Deposit", percent = (decimal?)40m },
                    new { sortOrder = 20, name = "Handover", percent = (decimal?)60m },
                },
            });
            Assert.That(good.IsSuccess, Is.True, $"A schedule that adds up to the quote was refused: {good}");

            var cleared = await admin.PutAsync(path, new { milestones = Array.Empty<object>() });
            Assert.That(cleared.IsSuccess, Is.True, $"Clearing the schedule was refused: {cleared}");

            await ExpectUnchangedAsync("arranging the payment schedule");
        });
    }
}
