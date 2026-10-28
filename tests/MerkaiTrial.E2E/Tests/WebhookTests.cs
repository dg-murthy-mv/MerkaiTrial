// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/WebhookTests.cs
//
// VUL-19 (round 105) — webhook events must be genuine, and count once.
//
//   Webhook_events_must_be_signed_fresh_and_used_once
//
// THE EMAIL WEBHOOK (Resend → POST /api/inbound/resend)
//   run-e2e.ps1 gives the test WebApi a signing secret made fresh from
//   this run's key (agreed in 105: it can only check incoming events; it
//   can't send anything, and it is never the real Resend secret). The
//   test signs events the way Resend does.
//
//   • Refused, and nothing changes: no signature; a wrong signature; a
//     signature for a different body; the right signature with the body
//     changed; a timestamp ten minutes old or ten minutes ahead (correctly
//     signed); no event id.
//   • A genuine "bounced" for one of our emails is recorded; then a
//     genuine "marked as spam" after it.
//   • The SAME signed "bounced" sent again — byte for byte, well inside
//     its five minutes — is answered "duplicate" and changes nothing.
//     (Without that, the replay would move the email back to "bounced".)
//   • Two copies of one new event at the same moment: one is recorded.
//
// THE PAYMENT WEBHOOK (agreed in 105: switched off)
//   It used to mark an invoice paid from nothing but its number. Now every
//   call — signed out, or signed in as the workspace's admin — is
//   answered "not found", and the invoice stays unpaid.
// =====================================================================

using System.Text.Json;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class WebhookTests : E2ETest
{
    // OutboundDeliveryStatus
    private const int None = 0, Bounced = 3, Complained = 4;

    [Test]
    public async Task Webhook_events_must_be_signed_fresh_and_used_once()
    {
        var a = World.A;
        var tenantA = await Db.TenantIdAsync(a.Name);
        var apiA = await Api.AsAsync(a.Admin);
        var m = Random.Shared.Next(10000, 99999).ToString();
        var problems = new List<string>();

        var person = await Step("A person of this test's own — their invite email is the one events will be about", () =>
            WorldBuilder.AddPersonForTestAsync(a, "hook" + m, "Hana", "Viewer"));

        var providerId = "re_e2e_" + Guid.NewGuid().ToString("N");
        var messageId = await Step("The invite email, made to look sent (the test servers never really send)", async () =>
        {
            var id = await Db.LatestOutboundIdAsync(person.Email)
                ?? throw new AssertionException($"No email was queued to {person.Email}.");
            await Db.PrepareOutboundForWebhookAsync(id, providerId);
            return id;
        });

        await Step("Control: the webhook is set up on the test server", async () =>
        {
            var probe = await Webhooks.PostAsync(Webhooks.Signed(Webhooks.DeliveryEvent("email.sent", providerId)));
            if (probe.Status == 503)
                Assert.Fail("The WebApi has no webhook secret (503). Start the servers through run-e2e.ps1 — " +
                            "it gives the test WebApi this run's secret. " + probe);
            Assert.That(probe.IsSuccess, Is.True, $"A genuine, signed event was refused, so the refusals below would prove nothing: {probe}");
        });

        await Step("Unsigned, wrongly signed, altered, stale, future and id-less events are refused; nothing changes", async () =>
        {
            var body = Webhooks.DeliveryEvent("email.bounced", providerId);
            var genuine = Webhooks.Signed(body);
            var otherBody = Webhooks.DeliveryEvent("email.complained", providerId);
            var old = Webhooks.Now(-600);
            var ahead = Webhooks.Now(600);
            var id = "msg_e2e_" + Guid.NewGuid().ToString("N");

            var cases = new List<(string What, Func<Task<ApiResponse>> Post)>
            {
                ("with no signature", () => Webhooks.PostAsync(body, genuine.Id, genuine.Timestamp, null)),
                ("with a made-up signature", () => Webhooks.PostAsync(body, genuine.Id, genuine.Timestamp, "v1," + Convert.ToBase64String(new byte[32]))),
                ("signed with another secret", () => Webhooks.PostAsync(Webhooks.Signed(body, secret: "whsec_" + Convert.ToBase64String(new byte[32])))),
                ("whose signature is for a different body", () => Webhooks.PostAsync(body, genuine.Id, genuine.Timestamp,
                    Webhooks.Sign(Webhooks.Secret, genuine.Id, genuine.Timestamp, otherBody))),
                ("whose body was changed after signing", () => Webhooks.PostAsync(otherBody, genuine.Id, genuine.Timestamp, genuine.Signature)),
                ("whose id was changed after signing", () => Webhooks.PostAsync(body, id, genuine.Timestamp, genuine.Signature)),
                ("signed, but ten minutes old", () => Webhooks.PostAsync(Webhooks.Signed(body, timestamp: old))),
                ("signed, but dated ten minutes ahead", () => Webhooks.PostAsync(Webhooks.Signed(body, timestamp: ahead))),
                ("with no event id", () => Webhooks.PostAsync(body, null, genuine.Timestamp,
                    Webhooks.Sign(Webhooks.Secret, "", genuine.Timestamp, body))),
                ("with no timestamp", () => Webhooks.PostAsync(body, genuine.Id, null, genuine.Signature)),
            };

            foreach (var (what, post) in cases)
            {
                var r = await post();
                if (r.IsSuccess) problems.Add($"An event {what} was ACCEPTED: {r}");
                else if (r.Status >= 500) problems.Add($"An event {what} gave a server error: {r}");
            }

            var (status, _) = await Db.OutboundDeliveryAsync(messageId);
            if (status != None) problems.Add($"After only refused events the email's delivery status is {status}, not {None} (nothing heard).");
        });

        var bounced = Webhooks.Signed(Webhooks.DeliveryEvent("email.bounced", providerId));

        await Step("A genuine \"bounced\", then a genuine \"marked as spam\", are recorded", async () =>
        {
            var r1 = await Webhooks.PostAsync(bounced);
            Assert.That(r1.IsSuccess, Is.True, $"A genuine bounce was refused: {r1}");
            Assert.That((await Db.OutboundDeliveryAsync(messageId)).Status, Is.EqualTo(Bounced), "The genuine bounce wasn't recorded.");

            var r2 = await Webhooks.PostAsync(Webhooks.Signed(Webhooks.DeliveryEvent("email.complained", providerId)));
            Assert.That(r2.IsSuccess, Is.True, $"A genuine spam report was refused: {r2}");
            Assert.That((await Db.OutboundDeliveryAsync(messageId)).Status, Is.EqualTo(Complained), "The genuine spam report wasn't recorded.");
        });

        await Step("The same signed \"bounced\" sent again, byte for byte: \"duplicate\", and nothing changes", async () =>
        {
            var before = await Db.OutboundDeliveryAsync(messageId);
            var again = await Webhooks.PostAsync(bounced);

            if (again.Status >= 500) problems.Add($"The replayed event gave a server error: {again}");
            if (again.IsSuccess && !again.Body.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
                problems.Add($"The replayed event was answered as new: {again}");

            var after = await Db.OutboundDeliveryAsync(messageId);
            if (after.Status != before.Status)
                problems.Add($"Replaying the old \"bounced\" moved the email from {before.Status} back to {after.Status}.");
            else if (after.AtUtc != before.AtUtc)
                problems.Add("Replaying the old event touched the email's delivery record.");
        });

        await Step("Two copies of one new event at the same moment: it is recorded once", async () =>
        {
            var other = await WorldBuilder.AddPersonForTestAsync(a, "hook2" + m, "Hugo", "Viewer");
            var otherMessage = await Db.LatestOutboundIdAsync(other.Email)
                ?? throw new AssertionException($"No email was queued to {other.Email}.");
            var otherProvider = "re_e2e_" + Guid.NewGuid().ToString("N");
            await Db.PrepareOutboundForWebhookAsync(otherMessage, otherProvider);

            var e = Webhooks.Signed(Webhooks.DeliveryEvent("email.bounced", otherProvider));
            var both = await Api.AllAtOnceAsync(() => Webhooks.PostAsync(e), () => Webhooks.PostAsync(e));

            var recorded = both.Count(r => r.IsSuccess && r.Body.Contains("accepted", StringComparison.OrdinalIgnoreCase));
            if (recorded != 1)
                problems.Add($"Two copies of one event at the same moment were recorded {recorded} times: {both[0]} / {both[1]}");
            if ((await Db.OutboundDeliveryAsync(otherMessage)).Status != Bounced)
                problems.Add("The event sent twice at once wasn't recorded at all.");
        });

        await Step("The payment webhook is switched off: \"not found\" for everyone, and the invoice stays unpaid", async () =>
        {
            var contact = await ApiRecords.ContactAsync(apiA, tenantA, "Hook Contact " + m, "hookc" + m);
            var deal = await ApiRecords.DealAsync(apiA, tenantA, contact, "Hook Deal " + m);
            var invoiceId = await ApiRecords.InvoiceAsync(apiA, deal, "Hook line " + m);
            var number = await Db.ValueAsync("Invoices", "Number", invoiceId);
            var statusBefore = await Db.ValueAsync("Invoices", "Status", invoiceId);
            var balanceBefore = await Db.ValueAsync("Invoices", "Balance", invoiceId);

            foreach (var provider in new[] { "razorpay", "stripe", "paypal", "promptpay", "unknown" })
            {
                var path = $"/api/payments/webhook/{provider}?invoice={Uri.EscapeDataString(number ?? "")}&invoiceNumber={Uri.EscapeDataString(number ?? "")}";
                var body = JsonSerializer.Serialize(new { invoiceNumber = number, status = "paid", amount = 3000 });

                var anon = await Api.Anonymous.PostAsync(path, JsonDocument.Parse(body).RootElement);
                if (anon.Status != 404) problems.Add($"POST {path} signed out → {anon} (expected 404).");

                var admin = await apiA.PostAsync(path, JsonDocument.Parse(body).RootElement);
                if (admin.Status != 404) problems.Add($"POST {path} as the workspace admin → {admin} (expected 404).");
            }

            if (await Db.ValueAsync("Invoices", "Status", invoiceId) != statusBefore)
                problems.Add("Calling the payment webhook changed the invoice's status.");
            if (await Db.ValueAsync("Invoices", "Balance", invoiceId) != balanceBefore)
                problems.Add("Calling the payment webhook changed the invoice's balance.");
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }
}
