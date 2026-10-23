// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/QuoteApprovalBypassTests.cs
//
// NEW (100) — test 79, Critical · Financial controls.
//
//   A_rep_cannot_issue_an_unapproved_discounted_quote
//
// Workspace C has a rule: a discount over 10% needs the sales manager's
// approval. WorkspaceSettingsTests proves the normal path through the
// screens. This proves the ways AROUND it are closed — every one tried
// directly against the API, as the rep, the way someone who opened the
// browser's developer tools would:
//
//   DIRECT ENDPOINTS      Mark the 15% quote Sent / Accepted / Approved /
//                         PendingApproval straight through the status
//                         endpoint; email it; post the page's own status
//                         form; use the deal-quote route removed in 100;
//                         invoice it.
//   THE APPROVAL ITSELF   Approve before asking; ask, then approve or send
//                         it back yourself; a second rep approves it; edit
//                         or send it while it waits.
//   CHANGING TOTALS       Once the manager approves 15%, raise the discount
//                         to 40% (as a percentage, and as a plain amount) —
//                         the approval no longer counts and it cannot be
//                         sent. And edit-and-send at the same instant: it
//                         is never sent with the unapproved discount.
//   GOING AROUND THE      Switch approvals off, change or delete the rule,
//   MANAGER               reorder the rules — all refused to a rep.
//
// After every attempt the quote is read from the DATABASE: it must not be
// Sent (or later) unless it carries exactly what was approved, and it must
// have no customer link until then.
//
// Positive control at the end: the manager approves the 15% quote and the
// rep can send it — so the refusals above are the rule working, not the
// quote being broken.
//
// SETUP. The test adds its own rule, named for this run, put first in
// workspace C's list so it is the one that applies: a discount over 10%
// needs the MANAGER, by name. It is removed again at the end.
// =====================================================================

using System.Globalization;
using System.Text.Json;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Financial")]
public class QuoteApprovalBypassTests : E2ETest
{
    /// <summary>QuoteStatus as stored (an int column).</summary>
    private static readonly Dictionary<string, string> QuoteStatus = new()
    {
        ["0"] = "Draft", ["1"] = "Sent", ["2"] = "Viewed", ["3"] = "Accepted", ["4"] = "Rejected",
        ["5"] = "Expired", ["6"] = "Revised", ["7"] = "PendingApproval", ["8"] = "Approved",
    };

    private static string StatusName(string stored) => QuoteStatus.TryGetValue(stored, out var n) ? n : stored;

    /// <summary>Sent, or anything the customer can only reach after it was sent.</summary>
    private static bool IsOut(string name) => name is "Sent" or "Viewed" or "Accepted" or "Rejected" or "Expired";

    [Test]
    public async Task A_rep_cannot_issue_an_unapproved_discounted_quote()
    {
        var c = await WorldBuilder.EnsureWorkspaceCAsync();
        var admin = await Api.AsAsync(c.Admin);
        var rep = await Api.AsAsync(c["rep"]);
        var rep2 = await Api.AsAsync(c["rep2"]);
        var manager = await Api.AsAsync(c["manager"]);
        await Api.EnsureTokensWorkAsync(admin);

        var ruleName = $"E2E bypass check {World.Stamp}";
        Guid? ruleId = null;

        try
        {
            ruleId = await Step("Workspace C: approvals on, and a rule — over 10% needs the manager — put first", async () =>
            {
                Must(await admin.PutAsync("/api/quote-approvals/enabled", new { isEnabled = true }), "Switching approvals on");

                var saved = Must(await admin.PutAsync("/api/quote-approvals/rules", new
                {
                    id = (Guid?)null,
                    name = ruleName,
                    description = "Round 100 end-to-end test — removed when the test ends.",
                    isActive = true,
                    discountOverPercent = 10m,
                    totalOverAmount = (decimal?)null,
                    conditionMode = "Any",
                    steps = new[]
                    {
                        new
                        {
                            name = "Sales manager",
                            approverKind = "User",
                            approverRoleId = (Guid?)null,
                            approverUserId = (Guid?)manager.Identity!.UserId,
                            allowSelfApproval = false,
                        },
                    },
                }), "Saving the rule");
                var id = saved.Json().GetProperty("id").GetGuid();

                var page = Must(await admin.GetAsync("/api/quote-approvals/rules?currency=THB"), "Reading the rules");
                var order = page.Json().GetProperty("rules").EnumerateArray()
                    .Select(r => r.GetProperty("id").GetGuid()).Where(r => r != id).Prepend(id).ToList();
                Must(await admin.PostAsync("/api/quote-approvals/rules/reorder", new { ruleIdsInOrder = order }), "Putting the rule first");
                return id;
            });

            var repPage = await SignedInAsync(c["rep"]);
            var title = World.Unique("Bypass deal");
            var (_, dealId) = await Step("The rep adds, qualifies and converts their own lead", () =>
                Flows.LeadToDealAsync(repPage, c, World.Unique("Bypass Lead"),
                    World.EmailFor("C", "bypass" + Random.Shared.Next(10000, 99999)), title, 200000m));

            var problems = new List<string>();

            var quoteId = await Step("The rep saves a quote with a 15% discount (a draft)", async () =>
            {
                var r = Must(await rep.PostAsync("/api/quotes", new
                {
                    dealId,
                    issueDateUtc = DateTime.UtcNow.Date,
                    expiresAtUtc = DateTime.UtcNow.Date.AddDays(30),
                    currency = "THB",
                    items = new[] { Line(15m) },
                }), "Saving the quote");
                var id = r.Json().GetProperty("id").GetGuid();
                var q = await Db.QuoteAsync(id);
                Assert.That(StatusName(q.Status), Is.EqualTo("Draft"), "A new quote should be a draft.");
                Assert.That(q.MaxDiscountPercent, Is.EqualTo(15m).Within(0.01m), "The quote's discount as stored.");
                return id;
            });

            // ── Direct endpoints ─────────────────────────────────────────
            await Step("Direct endpoints: the rep cannot send, accept, approve or invoice it", async () =>
            {
                foreach (var status in new[] { "Sent", "Accepted", "Approved", "PendingApproval", "Viewed" })
                    await RefusedAsync(problems, quoteId, $"status → {status}",
                        rep.PutAsync($"/api/quotes/{quoteId}/status", new { status, baseUrl = E2ESettings.BaseUrl }));

                await RefusedAsync(problems, quoteId, "email it to the customer",
                    rep.PostAsync($"/api/quotes/{quoteId}/send", new { baseUrl = E2ESettings.BaseUrl }));
                await RefusedAsync(problems, quoteId, "approve it without asking",
                    rep.PostAsync($"/api/quote-approvals/quotes/{quoteId}/approve", new { comment = "self" }));
                await RefusedAsync(problems, quoteId, "invoice it",
                    rep.PostAsync("/api/invoices/from-quote", new { quoteId }));
                await RefusedAsync(problems, quoteId, "a second quote through the removed deal-quote route",
                    rep.PostAsync($"/api/deals/{dealId}/quotes", new
                    {
                        dealId, expiresAtUtc = DateTime.UtcNow.Date.AddDays(30), currency = "THB",
                        items = new[] { Line(40m) }, status = "Sent",
                    }));

                Assert.That(await Db.CountInvoicesForQuoteAsync(quoteId), Is.EqualTo(0), "An invoice was raised from the unapproved quote.");
                Assert.That(await Db.CountWhereAsync("Quotes", "DealId", dealId), Is.EqualTo(1),
                    "Another quote appeared on the deal — the removed deal-quote route still works.");

                // The quote page's own status form, posted by the rep's browser.
                var token = await FormTokenAsync(repPage, $"/Quotes/Detail?id={quoteId}");
                await PostFormAsync(repPage, $"/Quotes/Detail?id={quoteId}&handler=UpdateStatus",
                    ("__RequestVerificationToken", token), ("id", quoteId.ToString()), ("newStatus", "Sent"));
                await ExpectNotOutAsync(problems, quoteId, "the quote page's status form → Sent");
            });

            // ── The approval itself ──────────────────────────────────────
            await Step("Asking for approval: the rep and a second rep cannot decide it, nor edit or send it while it waits", async () =>
            {
                Must(await rep.PostAsync($"/api/quote-approvals/quotes/{quoteId}/submit", new { comment = "15% for a two-year deal" }),
                    "Submitting for approval");
                Assert.That(StatusName((await Db.QuoteAsync(quoteId)).Status), Is.EqualTo("PendingApproval"));

                await RefusedAsync(problems, quoteId, "the rep approves their own request",
                    rep.PostAsync($"/api/quote-approvals/quotes/{quoteId}/approve", new { comment = "self" }));
                await RefusedAsync(problems, quoteId, "the rep sends back their own request",
                    rep.PostAsync($"/api/quote-approvals/quotes/{quoteId}/request-changes", new { comment = "self" }));
                await RefusedAsync(problems, quoteId, "another rep approves it",
                    rep2.PostAsync($"/api/quote-approvals/quotes/{quoteId}/approve", new { comment = "rep2" }));
                await RefusedAsync(problems, quoteId, "status → Sent while it waits",
                    rep.PutAsync($"/api/quotes/{quoteId}/status", new { status = "Sent" }));
                await RefusedAsync(problems, quoteId, "status → Approved while it waits",
                    rep.PutAsync($"/api/quotes/{quoteId}/status", new { status = "Approved" }));
                await RefusedAsync(problems, quoteId, "raise the discount to 40% while it waits",
                    rep.PutAsync($"/api/quotes/{quoteId}", EditBody(40m)));

                var q = await Db.QuoteAsync(quoteId);
                Assert.That(StatusName(q.Status), Is.EqualTo("PendingApproval"), "The quote should still be waiting for approval.");
                Assert.That(q.MaxDiscountPercent, Is.EqualTo(15m).Within(0.01m), "The discount changed while the quote was waiting.");
            });

            // ── Going around the manager ─────────────────────────────────
            await Step("Going around the manager: the rep cannot switch approvals off or change the rules", async () =>
            {
                var before = Must(await admin.GetAsync("/api/quote-approvals/rules?currency=THB"), "Reading the rules").Body;

                await ExpectForbiddenAsync(problems, "switch approvals off",
                    rep.PutAsync("/api/quote-approvals/enabled", new { isEnabled = false }));
                await ExpectForbiddenAsync(problems, "switch approvals off (old route)",
                    rep.PutAsync("/api/quote-approvals/settings", new { isEnabled = false }));
                await ExpectForbiddenAsync(problems, "raise the rule's limit to 50%",
                    rep.PutAsync("/api/quote-approvals/rules", new
                    {
                        id = ruleId, name = ruleName, isActive = true, discountOverPercent = 50m, conditionMode = "Any",
                        steps = new[] { new { approverKind = "WorkspaceAdmin", allowSelfApproval = true } },
                    }));
                await ExpectForbiddenAsync(problems, "delete the rule", rep.DeleteAsync($"/api/quote-approvals/rules/{ruleId}"));
                await ExpectForbiddenAsync(problems, "delete the rule (POST alias)", rep.PostAsync($"/api/quote-approvals/rules/{ruleId}/delete"));
                await ExpectForbiddenAsync(problems, "reorder the rules",
                    rep.PostAsync("/api/quote-approvals/rules/reorder", new { ruleIdsInOrder = new[] { ruleId } }));

                var after = Must(await admin.GetAsync("/api/quote-approvals/rules?currency=THB"), "Reading the rules").Body;
                if (after != before) problems.Add("The approval rules changed after the rep's attempts.");
            });

            // ── Changing the totals after approval ───────────────────────
            await Step("After the manager approves 15%, raising the discount voids the approval and it cannot be sent", async () =>
            {
                Must(await manager.PostAsync($"/api/quote-approvals/quotes/{quoteId}/approve", new { comment = "OK at 15%" }),
                    "The manager approving");
                Assert.That(StatusName((await Db.QuoteAsync(quoteId)).Status), Is.EqualTo("Approved"));

                // As a percentage.
                Must(await rep.PutAsync($"/api/quotes/{quoteId}", EditBody(40m)), "Editing the approved quote");
                var q = await Db.QuoteAsync(quoteId);
                Assert.That(StatusName(q.Status), Is.EqualTo("Draft"), "Editing an approved quote must put it back to Draft.");
                Assert.That(q.MaxDiscountPercent, Is.EqualTo(40m).Within(0.01m));
                await RefusedAsync(problems, quoteId, "send the 40% quote on the strength of the 15% approval",
                    rep.PutAsync($"/api/quotes/{quoteId}/status", new { status = "Sent" }));
                await RefusedAsync(problems, quoteId, "approve the 40% quote yourself",
                    rep.PostAsync($"/api/quote-approvals/quotes/{quoteId}/approve", new { comment = "self" }));

                // As a plain amount, after a fresh approval of 15%.
                Must(await rep.PutAsync($"/api/quotes/{quoteId}", EditBody(15m)), "Putting it back to 15%");
                Must(await rep.PostAsync($"/api/quote-approvals/quotes/{quoteId}/submit", new { comment = "back to 15%" }), "Submitting again");
                Must(await manager.PostAsync($"/api/quote-approvals/quotes/{quoteId}/approve", new { comment = "OK" }), "Approving again");
                Must(await rep.PutAsync($"/api/quotes/{quoteId}", EditBody(null, discountAmount: 80000m)), "Editing to an 80,000 discount");
                q = await Db.QuoteAsync(quoteId);
                Assert.That(StatusName(q.Status), Is.EqualTo("Draft"), "Editing an approved quote must put it back to Draft.");
                await RefusedAsync(problems, quoteId, "send the quote with an 80,000 (40%) discount",
                    rep.PutAsync($"/api/quotes/{quoteId}/status", new { status = "Sent" }));
            });

            await Step("Edit and send at the same instant: never sent with an unapproved discount", async () =>
            {
                Must(await rep.PutAsync($"/api/quotes/{quoteId}", EditBody(15m)), "Putting it back to 15%");
                Must(await rep.PostAsync($"/api/quote-approvals/quotes/{quoteId}/submit", new { comment = "15% again" }), "Submitting again");
                Must(await manager.PostAsync($"/api/quote-approvals/quotes/{quoteId}/approve", new { comment = "OK" }), "Approving again");

                var results = await Api.AllAtOnceAsync(
                    () => rep.PutAsync($"/api/quotes/{quoteId}", EditBody(45m)),
                    () => rep.PutAsync($"/api/quotes/{quoteId}/status", new { status = "Sent", baseUrl = E2ESettings.BaseUrl }));
                TestContext.Progress.WriteLine($"    edit → {results[0].Status}, send → {results[1].Status}");

                var q = await Db.QuoteAsync(quoteId);
                var name = StatusName(q.Status);
                if (IsOut(name) && q.MaxDiscountPercent > 15.01m)
                    problems.Add($"Edit and send together: the quote went out ({name}) with a {q.MaxDiscountPercent:0.##}% discount " +
                                 "that was never approved.");
                if (IsOut(name) && results[0].IsSuccess)
                    problems.Add("Edit and send together: both were accepted, and the quote is out — the customer's copy changed after sending.");
            });

            // ── Positive control ─────────────────────────────────────────
            await Step("Positive control: approved by the manager, the rep's 15% quote can be sent", async () =>
            {
                var q = await Db.QuoteAsync(quoteId);
                if (IsOut(StatusName(q.Status)))
                {
                    // The race above let the SEND win (with 15%, which is
                    // fine). A fresh quote shows the same thing.
                    var r = Must(await rep.PostAsync("/api/quotes", new
                    {
                        dealId, issueDateUtc = DateTime.UtcNow.Date, expiresAtUtc = DateTime.UtcNow.Date.AddDays(30),
                        currency = "THB", items = new[] { Line(15m) },
                    }), "A fresh 15% quote");
                    quoteId = r.Json().GetProperty("id").GetGuid();
                }
                else
                {
                    Must(await rep.PutAsync($"/api/quotes/{quoteId}", EditBody(15m)), "Putting it back to 15%");
                }

                Must(await rep.PostAsync($"/api/quote-approvals/quotes/{quoteId}/submit", new { comment = "final" }), "Submitting");
                Must(await manager.PostAsync($"/api/quote-approvals/quotes/{quoteId}/approve", new { comment = "Approved" }), "The manager approving");
                Must(await rep.PutAsync($"/api/quotes/{quoteId}/status", new { status = "Sent", baseUrl = E2ESettings.BaseUrl }), "The rep sending it");

                var sent = await Db.QuoteAsync(quoteId);
                Assert.That(StatusName(sent.Status), Is.EqualTo("Sent"), "The approved quote could not be sent.");
                Assert.That(sent.MaxDiscountPercent, Is.EqualTo(15m).Within(0.01m), "The sent quote's discount.");
                Assert.That(sent.PublicLinkToken, Is.Not.Null.And.Not.Empty, "The sent quote has no customer link.");
            });

            Assert.That(problems, Is.Empty,
                $"The approval rule was got round {problems.Count} way(s):\n  • " + string.Join("\n  • ", problems));
        }
        finally
        {
            if (ruleId is Guid id)
            {
                var r = await admin.DeleteAsync($"/api/quote-approvals/rules/{id}");
                if (!r.IsSuccess) TestContext.Progress.WriteLine($"[e2e] Could not remove the test's approval rule: {r}");
            }
        }
    }

    // =================================================================

    /// <summary>2 × ฿100,000 at 7% VAT, with <paramref name="discountPercent"/> off.</summary>
    private static object Line(decimal discountPercent) => new
    {
        name = "Workstations",
        description = "",
        unitPrice = 100000m,
        quantity = 2m,
        discountPercent,
        taxRate = 0.07m,
    };

    /// <summary>The whole quote as the editor posts it, with one line.</summary>
    private static object EditBody(decimal? discountPercent, decimal discountAmount = 0m) => new
    {
        issueDateUtc = DateTime.UtcNow.Date,
        expiresAtUtc = DateTime.UtcNow.Date.AddDays(30),
        currency = "THB",
        items = new[]
        {
            new
            {
                name = "Workstations", description = "", unitPrice = 100000m, quantity = 2m,
                lineDiscount = discountAmount, discountPercent, taxRate = 0.07m,
            },
        },
    };

    private static ApiResponse Must(ApiResponse r, string what) =>
        r.IsSuccess ? r : throw new AssertionException($"{what} failed: {r}");

    /// <summary>The attempt must be refused, and the quote must not be out.</summary>
    private static async Task RefusedAsync(List<string> problems, Guid quoteId, string attempt, Task<ApiResponse> call)
    {
        var r = await call;
        if (r.IsSuccess)
            problems.Add($"The rep could {attempt}: {r}");
        else if (r.Status >= 500)
            problems.Add($"Trying to {attempt} caused a server error instead of a refusal: {r}");
        await ExpectNotOutAsync(problems, quoteId, attempt);
    }

    private static async Task ExpectForbiddenAsync(List<string> problems, string attempt, Task<ApiResponse> call)
    {
        var r = await call;
        if (r.Status != 403)
            problems.Add($"The rep tried to {attempt}: expected 403, got {r}");
    }

    private static async Task ExpectNotOutAsync(List<string> problems, Guid quoteId, string attempt)
    {
        var q = await Db.QuoteAsync(quoteId);
        var name = StatusName(q.Status);
        if (IsOut(name))
            problems.Add($"After \"{attempt}\" the unapproved quote is {name}.");
        if (!string.IsNullOrEmpty(q.PublicLinkToken) && !IsOut(name))
            problems.Add($"After \"{attempt}\" the unapproved quote has a customer link.");
    }

    private static async Task<string> FormTokenAsync(IPage page, string path)
    {
        await page.GotoAsync(path);
        var token = page.Locator("input[name=__RequestVerificationToken]");
        if (await token.CountAsync() == 0) throw new AssertionException($"{path} has no anti-forgery token.");
        return await token.First.InputValueAsync();
    }

    private static async Task PostFormAsync(IPage page, string path, params (string Key, string Value)[] fields)
    {
        var pairs = fields.Select(f => new[] { f.Key, f.Value }).ToArray();
        var status = await page.EvaluateAsync<int>(
            @"async (a) => {
                try {
                    const r = await fetch(a.url, { method: 'POST', body: new URLSearchParams(a.form), credentials: 'same-origin' });
                    return r.status;
                } catch (e) { return 0; }
            }",
            new { url = E2ESettings.BaseUrl + path, form = pairs });
        TestContext.Progress.WriteLine($"    POST {path} → {status}");
    }
}
