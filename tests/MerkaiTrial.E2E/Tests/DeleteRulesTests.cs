// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/DeleteRulesTests.cs
//
// TEST 87 (round 102) —
//   Deleted_or_archived_records_follow_the_configured_relationship_rules
//
// The rule, for every record (DeleteRules.cs):
//     not deleted while live records depend on it — the refusal says what
//     is in the way — and every delete that does happen stays in the audit
//     history.
//
// Customer one — company → contact → deal → draft quote:
//   • the company can't be deleted: it has a contact and a deal;
//   • the contact can't be deleted: it is on a deal;
//   • the deal can't be deleted: it has a quote;
//     … each refusal names what is in the way, and nothing is deleted;
//   • delete the quote, then the deal, then the contact, then the company —
//     in that order every one is allowed, and each is in the audit log.
//
// Customer two — a deal with an accepted quote and an issued invoice:
//   • neither the deal nor the quote can be deleted, through the API or
//     the deal page; the page shows the reason.
//
// Before round 102 the API deleted any deal (the rules lived only in two
// pages), and a company or contact could be deleted with anything on it.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture]
public class DeleteRulesTests : E2ETest
{
    [Test]
    public async Task Deleted_or_archived_records_follow_the_configured_relationship_rules()
    {
        var a = World.A;
        var adminApi = await Api.AsAsync(a.Admin);
        await Api.EnsureTokensWorkAsync(adminApi);
        var tenantId = await Db.TenantIdAsync(a.Name);
        var page = await SignedInAsync(a.Admin);

        // ── Customer one ─────────────────────────────────────────────
        var company = World.Unique("Delete Rules Co");
        var dealTitle = World.Unique("Delete rules deal");
        var (_, dealId) = await Step("Customer one: a lead converted to a deal, with a new contact and company", () =>
            Flows.LeadToDealAsync(page, a, World.Unique("Dara Rules"), World.EmailFor("A", "delrules" + Random.Shared.Next(1000, 9999)),
                dealTitle, 50000m, company));

        var (contactIdN, companyIdN) = await Db.DealContactAsync(dealId);
        Assert.That(contactIdN.HasValue && companyIdN.HasValue, Is.True, "The conversion did not create a contact and a company.");
        var contactId = contactIdN!.Value;
        var companyId = companyIdN!.Value;

        var quoteId = await Step("…and a draft quote on the deal", async () =>
        {
            await Flows.FillQuoteAsync(page, dealTitle, new QuoteLine("Rules check", 1, 50000m));
            await Flows.SubmitQuoteAsync(page, send: false);
            return Ui.IdFromUrl(page.Url);
        });

        await Step("The company can't be deleted — it has a contact and a deal, and says so", async () =>
        {
            var r = await adminApi.DeleteAsync($"/api/companies/{companyId}");
            ExpectRefused(r, "contact", "deal");
            Assert.That(await Db.IsRowDeletedAsync("Companies", companyId), Is.False, "The company was deleted anyway.");
        });

        await Step("The contact can't be deleted — it is on a deal, and says which", async () =>
        {
            var r = await adminApi.DeleteAsync($"/api/contacts/{contactId}");
            ExpectRefused(r, dealTitle);
            Assert.That(await Db.IsRowDeletedAsync("Contacts", contactId), Is.False, "The contact was deleted anyway.");
        });

        await Step("The deal can't be deleted — it has a quote", async () =>
        {
            var r = await adminApi.DeleteAsync($"/api/deals/{dealId}");
            ExpectRefused(r, "quote");
            Assert.That(await Db.IsRowDeletedAsync("Deals", dealId), Is.False, "The deal was deleted anyway.");
        });

        await Step("On the company's page, Delete shows the same reason", async () =>
        {
            await page.GotoAsync($"/Companies/Detail/{companyId}");
            await page.EvaluateAsync<bool>(
                @"() => { const f = [...document.querySelectorAll('form')].find(f => (f.action || '').includes('handler=Delete'));
                          if (!f) return false; f.submit(); return true; }");
            await Ui.WaitForDocumentAsync(page);
            await Assertions.Expect(page.Locator(".alert-danger").First).ToContainTextAsync("contact",
                new() { Timeout = E2ESettings.ActionTimeoutMs });
            Assert.That(await Db.IsRowDeletedAsync("Companies", companyId), Is.False, "The page deleted the company.");
        });

        await Step("Nothing refused was written to the audit log as deleted", async () =>
        {
            Assert.That(await Db.CountAuditAsync(companyId, "CompanyDeleted"), Is.EqualTo(0));
            Assert.That(await Db.CountAuditAsync(contactId, "ContactDeleted"), Is.EqualTo(0));
            Assert.That(await Db.CountAuditAsync(dealId, "DealDeleted"), Is.EqualTo(0));
        });

        await Step("In order — quote, deal, contact, company — each delete is allowed and audited", async () =>
        {
            var q = await adminApi.DeleteAsync($"/api/quotes/{quoteId}");
            Assert.That(q.IsSuccess, Is.True, $"Deleting the draft quote failed: {q}");

            var d = await adminApi.DeleteAsync($"/api/deals/{dealId}");
            Assert.That(d.IsSuccess, Is.True, $"With its quote gone, the deal should delete: {d}");

            var c = await adminApi.DeleteAsync($"/api/contacts/{contactId}");
            Assert.That(c.IsSuccess, Is.True, $"With its deal gone, the contact should delete: {c}");

            var co = await adminApi.DeleteAsync($"/api/companies/{companyId}");
            Assert.That(co.IsSuccess, Is.True, $"With its contact and deal gone, the company should delete: {co}");

            Assert.That(await Db.IsRowDeletedAsync("Deals", dealId), Is.True);
            Assert.That(await Db.IsRowDeletedAsync("Contacts", contactId), Is.True);
            Assert.That(await Db.IsRowDeletedAsync("Companies", companyId), Is.True);

            Assert.That(await Db.CountAuditAsync(dealId, "DealDeleted"), Is.EqualTo(1), "The deal's delete is not in the audit log.");
            Assert.That(await Db.CountAuditAsync(contactId, "ContactDeleted"), Is.EqualTo(1), "The contact's delete is not in the audit log.");
            Assert.That(await Db.CountAuditAsync(companyId, "CompanyDeleted"), Is.EqualTo(1), "The company's delete is not in the audit log.");
        });

        // ── Customer two ─────────────────────────────────────────────
        var dealTitle2 = World.Unique("Invoiced rules deal");
        var (_, dealId2) = await Step("Customer two: a deal whose quote was accepted and invoiced", () =>
            Flows.LeadToDealAsync(page, a, World.Unique("Ira Rules"), World.EmailFor("A", "delrules2" + Random.Shared.Next(1000, 9999)),
                dealTitle2, 60000m));
        var quoteId2 = await Flows.SendQuoteAsync(page, dealTitle2, new QuoteLine("Invoiced work", 1, 60000m));
        await Step("…accepted by the customer and invoiced", async () =>
            await Flows.IssuedInvoiceAsync(page, await NewPageAsync("customer"), quoteId2));

        await Step("The invoiced deal can't be deleted through the API, and the quote can't either", async () =>
        {
            var d = await adminApi.DeleteAsync($"/api/deals/{dealId2}");
            Assert.That(d.Status, Is.EqualTo(400), $"Deleting a deal with an issued invoice was not refused: {d}");
            Assert.That(d.Error.ToLowerInvariant(), Does.Match("invoice|closed|accepted"),
                $"The refusal doesn't say what is in the way: {d}");

            var q = await adminApi.DeleteAsync($"/api/quotes/{quoteId2}");
            Assert.That(q.Status, Is.EqualTo(400), $"Deleting an invoiced quote was not refused: {q}");

            Assert.That(await Db.IsRowDeletedAsync("Deals", dealId2), Is.False, "The invoiced deal was deleted.");
            Assert.That(await Db.IsRowDeletedAsync("Quotes", quoteId2), Is.False, "The invoiced quote was deleted.");
        });

        await Step("The deal page's own Delete is refused with the reason too", async () =>
        {
            await page.GotoAsync($"/Pipeline/Detail/{dealId2}");
            var posted = await page.EvaluateAsync<bool>(
                @"() => { const f = [...document.querySelectorAll('form')].find(f => (f.action || '').includes('handler=Delete') && !(f.action || '').includes('Entry') && !(f.action || '').includes('Attachment'));
                          if (!f) return false; f.submit(); return true; }");
            if (posted)
            {
                await Ui.WaitForDocumentAsync(page);
                await Assertions.Expect(page.Locator(".alert-danger").First).ToBeVisibleAsync(new() { Timeout = E2ESettings.ActionTimeoutMs });
            }
            // A closed deal shows no Delete at all — just as good.
            Assert.That(await Db.IsRowDeletedAsync("Deals", dealId2), Is.False, "The deal page deleted the invoiced deal.");
        });
    }

    /// <summary>Refused as a 400 (not a server error), naming each thing that is in the way.</summary>
    private static void ExpectRefused(ApiResponse r, params string[] mentions)
    {
        Assert.That(r.Status, Is.EqualTo(400), $"Expected a refusal with a reason, got {r}");
        foreach (var m in mentions)
            Assert.That(r.Error, Does.Contain(m).IgnoreCase, $"The refusal should mention \"{m}\": {r.Error}");
    }
}
