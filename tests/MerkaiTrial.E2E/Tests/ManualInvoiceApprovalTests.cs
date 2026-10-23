// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/ManualInvoiceApprovalTests.cs
//
// NEW (101) — test 82, Critical · Financial controls.
//
//   A_manual_invoice_over_the_limits_is_issued_only_by_its_approver
//
// Test 79 proved a rep cannot get a discounted QUOTE past approval. This
// proves the side door — an invoice typed in by hand, with no quote — is
// closed too. A draft may be saved freely (it goes nowhere); ISSUING it is
// what sends it to the customer, and that is what the rules control.
//
// Workspace C, rule "discount over 10% → the sales MANAGER, by name". The
// person raising invoices is rep2, given an extra role for this test only
// ("raise and change invoices" — the built-in Sales Rep can't).
//
//   1. Within the limits (5%): rep2 issues it. The rule only bites when it
//      should.
//   2. A draft with no due date saves (or is refused with a reason) — never
//      a server error.
//   3. Over the limits (15%): rep2 can save the draft but cannot issue it —
//      not with Issue, not by setting the status to Sent, not with "save
//      and issue", and it can't be paid or emailed while it is a draft. The
//      invoice page's own buttons say the manager must issue it.
//   4. Edit and issue at the same instant (a 5% draft raised to 40% while it
//      is being issued): it is never issued carrying the 40% nobody with
//      the authority saw.
//   5. The right people: the MANAGER the rule names can issue the 15%
//      invoice (before 101 only team managers and admins could, whatever
//      the rule said), and a workspace admin can issue the other.
//   6. A rep without invoice rights can't raise one at all (403).
//
// Clean-up: rep2's roles are put back, the rule and the extra role removed.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Financial")]
public class ManualInvoiceApprovalTests : E2ETest
{
    private const string Draft = "0", Sent = "1";   // InvoiceStatus as stored

    [Test]
    public async Task A_manual_invoice_over_the_limits_is_issued_only_by_its_approver()
    {
        var c = await WorldBuilder.EnsureWorkspaceCAsync();
        var admin = await Api.AsAsync(c.Admin);
        await Api.EnsureTokensWorkAsync(admin);
        var manager = await Api.AsAsync(c["manager"]);
        var rep2User = c["rep2"];
        var rep2Id = await Db.UserIdAsync(rep2User.Email);
        var tenantC = await Db.TenantIdAsync(c.Name);

        var originalRoles = await Db.UserRoleIdsAsync(rep2User.Email);
        Guid? roleId = null, ruleId = null;
        var ruleName = $"E2E manual invoice check {World.Stamp}";

        try
        {
            await Step("Rep2 gets an extra role: raise and change invoices", async () =>
            {
                var role = Must(await admin.PostAsync("/api/roles", new
                {
                    name = $"e2e-invoicing-{World.Stamp}",
                    displayName = $"E2E Invoicing {World.Stamp}",
                    description = "Round 101 end-to-end test — removed when the test ends.",
                    permissions = "{\"Invoices\":[\"Read\",\"Create\",\"Update\"]}",
                }), "Creating the role");
                roleId = role.Json().GetProperty("id").GetGuid();

                Must(await admin.PostAsync($"/api/users/{rep2Id}/roles", new
                {
                    tenantId = tenantC,
                    userId = rep2Id,
                    roleIds = originalRoles.Append(roleId.Value).ToList(),
                }), "Giving rep2 the role");
            });

            ruleId = await Step("Workspace C: approvals on, and a rule — over 10% needs the manager, by name — put first", async () =>
            {
                Must(await admin.PutAsync("/api/quote-approvals/enabled", new { isEnabled = true }), "Switching approvals on");
                var saved = Must(await admin.PutAsync("/api/quote-approvals/rules", new
                {
                    id = (Guid?)null,
                    name = ruleName,
                    description = "Round 101 end-to-end test — removed when the test ends.",
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

            var rep2 = await Api.AsAsync(rep2User);   // after the role, so the token carries it
            var page2 = await SignedInAsync(rep2User);
            var (_, dealId) = await Step("Rep2 adds, qualifies and converts their own lead", () =>
                Flows.LeadToDealAsync(page2, c, World.Unique("Manual Invoice Lead"),
                    World.EmailFor("C", "manual" + Random.Shared.Next(10000, 99999)), World.Unique("Manual invoice deal"), 200000m));

            var problems = new List<string>();

            // ── 1. Within the limits ─────────────────────────────────────
            await Step("Within the limits (5%): rep2 issues it", async () =>
            {
                var id = await DraftAsync(rep2, dealId, 5m);
                Must(await rep2.PostAsync($"/api/invoices/{id}/issue"), "Issuing the 5% invoice");
                Assert.That((await Db.InvoiceAsync(id)).Status, Is.EqualTo(Sent), "The 5% invoice was not issued.");
            });

            // ── 2. No due date ───────────────────────────────────────────
            await Step("A draft with no due date saves, or is refused with a reason — never a server error", async () =>
            {
                var r = await rep2.PostAsync("/api/invoices", new
                {
                    dealId, issueDateUtc = DateTime.UtcNow.Date, currency = "THB", lines = new[] { Line(5m) },
                });
                if (r.Status >= 500)
                    problems.Add($"A manual invoice with no due date caused a server error instead of saving or a reason: {r}");
            });

            // ── 3. Over the limits ───────────────────────────────────────
            var overId = await Step("Over the limits (15%): the draft saves, but rep2 cannot issue it any way", async () =>
            {
                var id = await DraftAsync(rep2, dealId, 15m);

                var flow = Must(await rep2.GetAsync($"/api/invoices/{id}/workflow"), "Reading the invoice's buttons").Json();
                if (flow.GetProperty("canIssue").GetBoolean())
                    problems.Add("The invoice page offers rep2 Issue on the 15% invoice.");
                var names = flow.GetProperty("issueApproverNames").EnumerateArray().Select(n => n.GetString() ?? "").ToList();
                if (!names.Any(n => n.Contains(c["manager"].FullName, StringComparison.OrdinalIgnoreCase)))
                    problems.Add($"The invoice page should name the manager the rule names as who issues it; it names: {string.Join(", ", names)}.");

                await RefusedAsync(problems, id, "issue it", rep2.PostAsync($"/api/invoices/{id}/issue"));
                await RefusedAsync(problems, id, "set its status to Sent", rep2.PutAsync($"/api/invoices/{id}/status", new { status = "Sent" }));
                await RefusedAsync(problems, id, "record a payment on it",
                    rep2.PostAsync($"/api/invoices/{id}/payments", new { amount = 1000m, method = "Cash", paidAtUtc = DateTime.UtcNow }));
                await RefusedAsync(problems, id, "email it",
                    rep2.PostAsync($"/api/invoices/{id}/email", new { toAddress = "customer@example.com", message = "x" }));

                // "Save and issue" in one go: it may save, but only as a draft.
                var both = await rep2.PostAsync("/api/invoices", new
                {
                    dealId, issueDateUtc = DateTime.UtcNow.Date, dueDateUtc = DateTime.UtcNow.Date.AddDays(30),
                    currency = "THB", lines = new[] { Line(15m) }, sendImmediately = true,
                });
                if (both.IsSuccess)
                {
                    var bothId = both.Json().GetProperty("id").GetGuid();
                    if ((await Db.InvoiceAsync(bothId)).Status != Draft)
                        problems.Add("\"Save and issue\" issued rep2's 15% invoice.");
                    else
                        await AdminIssuesAsync(admin, bothId);   // part 5: an admin can
                }
                else if (both.Status >= 500)
                    problems.Add($"\"Save and issue\" caused a server error: {both}");

                return id;
            });

            // ── 4. Edit and issue at the same instant ────────────────────
            await Step("Edit (to 40%) and issue at the same instant: never issued with the 40%", async () =>
            {
                var id = await DraftAsync(rep2, dealId, 5m);
                var results = await Api.AllAtOnceAsync(
                    () => rep2.PutAsync($"/api/invoices/{id}", EditBody(40m)),
                    () => rep2.PostAsync($"/api/invoices/{id}/issue"));
                TestContext.Progress.WriteLine($"    edit → {results[0].Status}, issue → {results[1].Status}");

                var status = (await Db.InvoiceAsync(id)).Status;
                var discount = await Db.InvoiceMaxDiscountPercentAsync(id);
                if (status == Sent && discount > 10.01m)
                    problems.Add($"Edit and issue together: the invoice was issued with a {discount:0.##}% discount rep2 may not issue.");
                if (status == Sent && results[0].IsSuccess)
                    problems.Add("Edit and issue together: both were accepted, and the issued invoice changed after issuing.");
            });

            // ── 5. The right people ──────────────────────────────────────
            await Step("The manager the rule names can issue the 15% invoice", async () =>
            {
                var r = await manager.PostAsync($"/api/invoices/{overId}/issue");
                if (!r.IsSuccess)
                    problems.Add($"The manager named by the rule could not issue the 15% invoice: {r}");
                else
                    Assert.That((await Db.InvoiceAsync(overId)).Status, Is.EqualTo(Sent));
            });

            // ── 6. No invoice rights at all ──────────────────────────────
            await Step("A rep without invoice rights can't raise one (403)", async () =>
            {
                var rep = await Api.AsAsync(c["rep"]);
                var r = await rep.PostAsync("/api/invoices", new
                {
                    dealId, issueDateUtc = DateTime.UtcNow.Date, dueDateUtc = DateTime.UtcNow.Date.AddDays(30),
                    currency = "THB", lines = new[] { Line(15m) },
                });
                if (r.Status != 403)
                    problems.Add($"A Sales Rep without invoice rights: POST /api/invoices → {r.Status} (expected 403).");
            });

            Assert.That(problems, Is.Empty,
                $"Manual invoices got round the approval rule {problems.Count} way(s):\n  • " + string.Join("\n  • ", problems));
        }
        finally
        {
            try
            {
                await admin.PostAsync($"/api/users/{rep2Id}/roles", new { tenantId = tenantC, userId = rep2Id, roleIds = originalRoles });
                if (ruleId is Guid rid) await admin.DeleteAsync($"/api/quote-approvals/rules/{rid}");
                if (roleId is Guid id) await admin.DeleteAsync($"/api/roles/{id}");
            }
            catch (Exception ex)
            {
                TestContext.Progress.WriteLine($"[e2e] Clean-up after the manual invoice test failed: {ex.Message}");
            }
        }
    }

    // =================================================================

    /// <summary>2 × ฿100,000 at 7% VAT, <paramref name="discountPercent"/> off.</summary>
    private static object Line(decimal discountPercent) => new
    {
        name = "Workstations",
        description = "",
        unitPrice = 100000m,
        quantity = 2m,
        discountPercent,
        taxRate = 0.07m,
    };

    private static object EditBody(decimal discountPercent) => new
    {
        dueDateUtc = DateTime.UtcNow.Date.AddDays(30),
        notes = "edited",
        lines = new[] { Line(discountPercent) },
    };

    /// <summary>A manual draft invoice on the deal, saved by <paramref name="who"/>. Returns its id.</summary>
    private static async Task<Guid> DraftAsync(ApiCaller who, Guid dealId, decimal discountPercent)
    {
        var r = Must(await who.PostAsync("/api/invoices", new
        {
            dealId,
            issueDateUtc = DateTime.UtcNow.Date,
            dueDateUtc = DateTime.UtcNow.Date.AddDays(30),
            currency = "THB",
            lines = new[] { Line(discountPercent) },
        }), $"Saving a {discountPercent}% draft");
        var id = r.Json().GetProperty("id").GetGuid();
        Assert.That((await Db.InvoiceAsync(id)).Status, Is.EqualTo(Draft), "A new manual invoice should be a draft.");
        return id;
    }

    private static async Task AdminIssuesAsync(ApiCaller admin, Guid id)
    {
        Must(await admin.PostAsync($"/api/invoices/{id}/issue"), "A workspace admin issuing the 15% invoice");
        Assert.That((await Db.InvoiceAsync(id)).Status, Is.EqualTo(Sent), "A workspace admin could not issue it.");
    }

    private static ApiResponse Must(ApiResponse r, string what) =>
        r.IsSuccess ? r : throw new AssertionException($"{what} failed: {r}");

    /// <summary>The attempt must be refused (not a server error), and the invoice must still be a draft.</summary>
    private static async Task RefusedAsync(List<string> problems, Guid id, string attempt, Task<ApiResponse> call)
    {
        var r = await call;
        if (r.IsSuccess)
            problems.Add($"Rep2 could {attempt} on the 15% invoice: {r}");
        else if (r.Status >= 500)
            problems.Add($"Trying to {attempt} caused a server error instead of a refusal: {r}");
        if ((await Db.InvoiceAsync(id)).Status != Draft)
            problems.Add($"After \"{attempt}\" the 15% invoice is no longer a draft.");
    }
}
