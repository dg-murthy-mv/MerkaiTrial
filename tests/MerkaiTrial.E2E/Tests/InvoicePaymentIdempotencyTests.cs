// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/InvoicePaymentIdempotencyTests.cs
//
// NEW (100) — test 80, Critical · Financial integrity.
//
//   Duplicate_payment_requests_do_not_double_count_invoice_payments
//
// One issued invoice in workspace A (₹1,18,000: 1 × 1,00,000 at 18% GST),
// and every way the same payment can arrive more than once:
//
//   1. A RETRY. The same payment, the same Idempotency-Key header, sent
//      again after the first answered (a timeout the client gave up on).
//      → the same payment comes back; one is recorded.
//   2. SIMULTANEOUS DUPLICATES. Five copies with one key, all at once.
//      → all five answered with that one payment; one is recorded.
//   3. THE KEY IN THE BODY instead of the header, sent twice.
//      → one recorded.
//   4. THE PAGE SENT TWICE. The invoice page's Record Payment form, posted
//      twice in the same instant (a double-click, or Back and resend) —
//      same form, same one-time key. → one recorded.
//   5. TWO CLERKS, DIFFERENT KEYS, EACH PAYING THE WHOLE BALANCE AT ONCE.
//      These ARE different payments, so keys don't help — the invoice row
//      lock does: one is recorded, the other is refused as "more than
//      what's owed". The invoice is never paid twice over.
//   6. A KEY THAT IS NOT A GUID → 400, nothing recorded.
//
// After each: the payments as stored, and the invoice's balance and status
// worked out from them — Balance = Total − every captured payment, never
// below zero, and Paid only when that reaches zero.
// =====================================================================

using System.Globalization;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Financial")]
public class InvoicePaymentIdempotencyTests : E2ETest
{
    private const int Paid = 4, PartiallyPaid = 3;   // InvoiceStatus as stored

    [Test]
    public async Task Duplicate_payment_requests_do_not_double_count_invoice_payments()
    {
        var api = await Api.AsAsync(World.A.Admin);
        await Api.EnsureTokensWorkAsync(api);

        var page = await SignedInAsync(World.A.Admin);
        var customer = await NewPageAsync("customer");

        var invoiceId = await Step("An issued invoice: lead → deal → quote → accepted → invoiced", async () =>
        {
            var deal = World.Unique("Pay Twice deal");
            await Flows.LeadToDealAsync(page, World.A, World.Unique("Pay Twice"),
                World.EmailFor("A", "paytwice" + Random.Shared.Next(10000, 99999)), deal, 100000m);
            var quoteId = await Flows.SendQuoteAsync(page, deal, new QuoteLine("Annual licence", 1, 100000m, TaxRate: 18));
            return await Flows.IssuedInvoiceAsync(page, customer, quoteId);
        });

        var total = (await Db.InvoiceAsync(invoiceId)).Total;
        Assert.That(total, Is.EqualTo(118000m), "The invoice total (1,00,000 + 18% GST).");

        // ── 1. A retry ───────────────────────────────────────────────────
        await Step("A retry with the same key records one payment and returns it again", async () =>
        {
            var key = Guid.NewGuid();
            var first = await PayAsync(api, invoiceId, 10000m, headerKey: key);
            var again = await PayAsync(api, invoiceId, 10000m, headerKey: key);

            Assert.That(first.IsSuccess, Is.True, $"The first payment was refused: {first}");
            Assert.That(again.IsSuccess, Is.True, $"The retry should be answered with the payment already recorded, not refused: {again}");
            Assert.That(Id(again), Is.EqualTo(Id(first)), "The retry came back with a different payment.");

            await ExpectLedgerAsync(invoiceId, total, captured: new[] { 10000m });
            Assert.That((await Db.PaymentsAsync(invoiceId)).Single().IdempotencyKey, Is.EqualTo(key), "The key was not stored on the payment.");
        });

        // ── 2. Simultaneous duplicates ──────────────────────────────────
        await Step("Five copies with one key, all at once, record one payment", async () =>
        {
            var key = Guid.NewGuid();
            var results = await Api.AllAtOnceAsync(Enumerable.Range(0, 5)
                .Select(_ => (Func<Task<ApiResponse>>)(() => PayAsync(api, invoiceId, 20000m, headerKey: key)))
                .ToArray());

            Assert.That(results.All(r => r.IsSuccess), Is.True,
                "Every copy should be answered with the one payment: " + string.Join(" | ", results.Select(r => r.ToString())));
            Assert.That(results.Select(Id).Distinct().Count(), Is.EqualTo(1), "The copies were answered with different payments.");

            await ExpectLedgerAsync(invoiceId, total, captured: new[] { 10000m, 20000m });
        });

        // ── 3. The key in the body ───────────────────────────────────────
        await Step("The key in the request body works the same way", async () =>
        {
            var key = Guid.NewGuid();
            var results = await Api.AllAtOnceAsync(
                () => PayAsync(api, invoiceId, 5000m, bodyKey: key),
                () => PayAsync(api, invoiceId, 5000m, bodyKey: key));
            Assert.That(results.All(r => r.IsSuccess), Is.True, string.Join(" | ", results.Select(r => r.ToString())));
            await ExpectLedgerAsync(invoiceId, total, captured: new[] { 10000m, 20000m, 5000m });
        });

        // ── 4. The page sent twice ───────────────────────────────────────
        await Step("The invoice page's payment form posted twice at once records one payment", async () =>
        {
            await page.GotoAsync($"/Invoices/Detail?id={invoiceId}");
            var form = page.Locator("#paymentForm");
            Assert.That(await form.CountAsync(), Is.EqualTo(1), "The invoice page has no Record Payment form.");
            Assert.That(await form.Locator("input[name=paymentKey]").CountAsync(), Is.EqualTo(1),
                "The payment form carries no one-time key (round 100).");

            var statuses = await page.EvaluateAsync<int[]>(
                @"async () => {
                    const form = document.getElementById('paymentForm');
                    form.querySelector('input[name=amount]').value = '3000';
                    const send = () => fetch(form.action || location.href,
                        { method: 'POST', body: new FormData(form), credentials: 'same-origin', redirect: 'follow' })
                        .then(r => r.status, () => 0);
                    return await Promise.all([send(), send()]);
                }");
            TestContext.Progress.WriteLine($"    the two posts answered {string.Join(", ", statuses)}");

            await ExpectLedgerAsync(invoiceId, total, captured: new[] { 10000m, 20000m, 5000m, 3000m });
        });

        // ── 5. Two clerks, different keys, the whole balance each ───────
        await Step("Two different payments of the whole balance at once: one is recorded, the other refused", async () =>
        {
            var balance = (await Db.InvoiceAsync(invoiceId)).Balance;
            Assert.That(balance, Is.EqualTo(total - 38000m));

            var results = await Api.AllAtOnceAsync(
                () => PayAsync(api, invoiceId, balance, headerKey: Guid.NewGuid()),
                () => PayAsync(api, invoiceId, balance, headerKey: Guid.NewGuid()));

            Assert.That(results.Count(r => r.IsSuccess), Is.EqualTo(1),
                "Exactly one of the two should be recorded: " + string.Join(" | ", results.Select(r => r.ToString())));
            var refused = results.Single(r => !r.IsSuccess);
            Assert.That(refused.Status, Is.EqualTo(400), $"The second should be refused with a reason: {refused}");
            Assert.That(refused.Error, Does.Contain("owed").IgnoreCase.Or.Contain("paid").IgnoreCase,
                $"The refusal should say the invoice is already settled: {refused}");

            await ExpectLedgerAsync(invoiceId, total, captured: new[] { 10000m, 20000m, 5000m, 3000m, balance });
            Assert.That((await Db.InvoiceAsync(invoiceId)).Status, Is.EqualTo(Paid.ToString(CultureInfo.InvariantCulture)),
                "Fully paid, the invoice should be Paid.");
        });

        // ── 6. A key that is not a GUID ─────────────────────────────────
        await Step("An Idempotency-Key that is not a GUID is refused and records nothing", async () =>
        {
            var before = (await Db.PaymentsAsync(invoiceId)).Count;
            var r = await api.PostAsync($"/api/invoices/{invoiceId}/payments",
                new { amount = 1m, method = "Cash", paidAtUtc = DateTime.UtcNow },
                new Dictionary<string, string> { ["Idempotency-Key"] = "not-a-guid" });
            Assert.That(r.Status, Is.EqualTo(400), $"Expected 400 for a malformed key: {r}");
            Assert.That((await Db.PaymentsAsync(invoiceId)).Count, Is.EqualTo(before), "A payment was recorded anyway.");
        });
    }

    // =================================================================

    private static Task<ApiResponse> PayAsync(ApiCaller api, Guid invoiceId, decimal amount, Guid? headerKey = null, Guid? bodyKey = null)
    {
        var headers = headerKey is Guid h ? new Dictionary<string, string> { ["Idempotency-Key"] = h.ToString() } : null;
        return api.PostAsync($"/api/invoices/{invoiceId}/payments", new
        {
            amount,
            method = "BankTransfer",
            paidAtUtc = DateTime.UtcNow,
            notes = "E2E duplicate payment test",
            idempotencyKey = bodyKey,
        }, headers);
    }

    private static Guid Id(ApiResponse r) => r.Json().GetProperty("id").GetGuid();

    /// <summary>
    /// The payments as stored are exactly <paramref name="captured"/>, and
    /// the invoice's balance and status follow from them.
    /// </summary>
    private static async Task ExpectLedgerAsync(Guid invoiceId, decimal total, decimal[] captured)
    {
        var payments = (await Db.PaymentsAsync(invoiceId)).Where(p => p.Status == "Captured").ToList();
        Assert.That(payments.Select(p => p.Amount).OrderBy(a => a), Is.EqualTo(captured.OrderBy(a => a)),
            $"Captured payments: expected {string.Join(", ", captured)}, found {string.Join(", ", payments.Select(p => p.Amount))}.");

        var paid = payments.Sum(p => p.Amount);
        var invoice = await Db.InvoiceAsync(invoiceId);
        Assert.That(paid, Is.LessThanOrEqualTo(total), "More has been recorded as paid than the invoice is for.");
        Assert.That(invoice.Balance, Is.EqualTo(Math.Max(0, total - paid)), "The invoice's balance does not match its payments.");

        var expected = paid >= total ? Paid : PartiallyPaid;
        Assert.That(invoice.Status, Is.EqualTo(expected.ToString(CultureInfo.InvariantCulture)),
            "The invoice's status does not match its payments (3 = partly paid, 4 = paid).");
    }
}
