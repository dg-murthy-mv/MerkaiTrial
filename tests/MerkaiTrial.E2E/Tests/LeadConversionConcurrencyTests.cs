// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/LeadConversionConcurrencyTests.cs
//
// NEW (100) — test 78, Critical · Data integrity.
//
//   Two_simultaneous_lead_conversions_do_not_create_duplicate_contacts_or_companies
//
// "Two people press Convert at the same moment" in every form it takes:
//
//   1. THE SAME LEAD, FOUR REQUESTS AT ONCE (a double-click, a retry, two
//      tabs). Exactly one is accepted; the rest are told it has already
//      been converted. One deal, one contact, one company.
//   2. THE SAME LEAD THROUGH BOTH CONVERT ENDPOINTS AT ONCE ("to a deal"
//      and the older "to a contact and company"). One wins, the other is
//      refused; still one contact and one company.
//   3. THE CONVERSION PAGE POSTED TWICE TOGETHER — the real double-click on
//      "Create the deal", with the same form and the same anti-forgery
//      token. One deal.
//   4. TWO DIFFERENT LEADS FOR THE SAME CUSTOMER (same email, same company
//      name — two reps who each met them), converted at the same moment.
//      Two deals, but ONE contact and ONE company, and both deals point at
//      them. Before round 100 both requests looked for the contact and the
//      company before either had saved, and both made one.
//
// All in workspace A, through the API as its admin (and, for 3, its
// browser). Every lead, email and company name is unique to this run.
// =====================================================================

using System.Globalization;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Integrity")]
public class LeadConversionConcurrencyTests : E2ETest
{
    [Test]
    public async Task Two_simultaneous_lead_conversions_do_not_create_duplicate_contacts_or_companies()
    {
        var tenantId = await Db.TenantIdAsync(World.A.Name);
        var page = await SignedInAsync(World.A.Admin);
        var api = await Api.AsAsync(World.A.Admin);
        await Api.EnsureTokensWorkAsync(api);

        var qualified = await Step("Find workspace A's Qualified status", async () =>
        {
            var probeId = await Flows.CreateLeadAsync(page, World.Unique("Status Probe"),
                World.EmailFor("A", "probe" + Random.Shared.Next(10000, 99999)), 1000m);
            return await QualifiedKeyAsync(page, probeId);
        });

        // ── 1. The same lead, four requests at once ──────────────────────
        await Step("One lead, four conversions at the same moment: one deal, one contact, one company", async () =>
        {
            var (leadId, email, company) = await NewQualifiedLeadAsync(page, api, qualified, "Twin Click");

            var results = await Api.AllAtOnceAsync(Enumerable.Range(1, 4)
                .Select(n => (Func<Task<ApiResponse>>)(() => ConvertToDealAsync(api, leadId, $"Twin deal {n} {World.Stamp}")))
                .ToArray());

            var accepted = results.Count(r => r.IsSuccess);
            Assert.That(accepted, Is.EqualTo(1),
                $"Exactly one conversion should be accepted, {accepted} were: {string.Join(" | ", results.Select(r => r.ToString()))}");
            foreach (var refused in results.Where(r => !r.IsSuccess))
            {
                Assert.That(refused.Status, Is.EqualTo(400), $"A refused conversion should be a 400 with a reason, not {refused}.");
                Assert.That(refused.Error, Does.Contain("already been converted").IgnoreCase,
                    $"The refusal should say the lead was already converted: {refused}");
            }

            await ExpectOneOfEachAsync(tenantId, leadId, email, company, expectedDeals: 1);
        });

        // ── 2. Both convert endpoints at once ────────────────────────────
        await Step("One lead, converted to a deal and to a contact at the same moment: one wins", async () =>
        {
            var (leadId, email, company) = await NewQualifiedLeadAsync(page, api, qualified, "Two Doors");

            var results = await Api.AllAtOnceAsync(
                () => ConvertToDealAsync(api, leadId, $"Door deal {World.Stamp}"),
                () => api.PostAsync($"/api/leads/{leadId}/convert", new
                {
                    tenantId, leadId, createCompany = true, companyName = company, createDeal = false, convertedBy = "e2e",
                }));

            Assert.That(results.Count(r => r.IsSuccess), Is.EqualTo(1),
                "Exactly one of the two conversions should be accepted: " + string.Join(" | ", results.Select(r => r.ToString())));

            var lead = await Db.LeadConversionAsync(leadId);
            Assert.That(lead.IsConverted, Is.True, "The lead is not marked converted.");
            Assert.That(await Db.CountContactsAsync(tenantId, email), Is.EqualTo(1), "Contacts with the lead's email.");
            Assert.That(await Db.CountCompaniesAsync(tenantId, company), Is.EqualTo(1), "Companies with the lead's company name.");
            Assert.That((await Db.DealIdsForLeadAsync(leadId)).Count, Is.LessThanOrEqualTo(1), "Deals made from the lead.");
        });

        // ── 3. The conversion page, posted twice together ────────────────
        await Step("The conversion page posted twice at once (a double-click): one deal", async () =>
        {
            var (leadId, email, company) = await NewQualifiedLeadAsync(page, api, qualified, "Double Click");
            var dealTitle = $"Double click deal {World.Stamp}-{Random.Shared.Next(100, 999)}";

            await page.GotoAsync($"/Leads/ConvertToDeal/{leadId}");
            await page.Locator("#Input_DealTitle").FillAsync(dealTitle);
            await page.Locator("#Input_ExpectedValue").FillAsync("50000");
            await page.Locator("#Input_ExpectedCloseDate").FillAsync(
                DateTime.Today.AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var stage = page.Locator("label.lc-stage").First;
            await stage.ClickAsync();
            await Assertions.Expect(page.Locator("input[name='Input.Stage']:checked")).ToHaveCountAsync(1);

            // The same filled-in form, sent twice in the same instant from the
            // page itself — exactly what a fast double-click delivers.
            var statuses = await page.EvaluateAsync<int[]>(
                @"async () => {
                    const form = document.querySelector('#Input_DealTitle').form;
                    const send = () => fetch(form.action || location.href,
                        { method: 'POST', body: new FormData(form), credentials: 'same-origin', redirect: 'follow' })
                        .then(r => r.status, () => 0);
                    return await Promise.all([send(), send()]);
                }");
            TestContext.Progress.WriteLine($"    the two posts answered {string.Join(", ", statuses)}");

            await ExpectOneOfEachAsync(tenantId, leadId, email, company, expectedDeals: 1);
        });

        // ── 4. Two different leads, one customer ─────────────────────────
        await Step("Two leads for the same customer, converted at the same moment: one contact, one company", async () =>
        {
            var email = $"same.customer.{World.Stamp.Replace("-", "")}{Random.Shared.Next(100, 999)}@example.com";
            var company = World.Unique("Same Customer Co");

            var first = await Flows.CreateLeadAsync(page, World.Unique("Same Customer One"), email, 30000m, World.A, company);
            await QualifyAsync(api, first, qualified);
            var second = await Flows.CreateLeadAsync(page, World.Unique("Same Customer Two"), email, 40000m, World.A, company);
            await QualifyAsync(api, second, qualified);

            Assert.That(await Db.CountContactsAsync(tenantId, email), Is.EqualTo(0), "A contact with this email exists before the test.");
            Assert.That(await Db.CountCompaniesAsync(tenantId, company), Is.EqualTo(0), "A company with this name exists before the test.");

            var results = await Api.AllAtOnceAsync(
                () => ConvertToDealAsync(api, first, $"Same customer deal one {World.Stamp}"),
                () => ConvertToDealAsync(api, second, $"Same customer deal two {World.Stamp}"));

            Assert.That(results.All(r => r.IsSuccess), Is.True,
                "Both leads should convert — they are different leads: " + string.Join(" | ", results.Select(r => r.ToString())));

            Assert.That(await Db.CountContactsAsync(tenantId, email), Is.EqualTo(1),
                "Two contacts were created for one customer — both conversions looked for the contact before either saved it.");
            Assert.That(await Db.CountCompaniesAsync(tenantId, company), Is.EqualTo(1),
                "Two companies were created for one customer — both conversions looked for the company before either saved it.");

            var dealOne = (await Db.DealIdsForLeadAsync(first)).Single();
            var dealTwo = (await Db.DealIdsForLeadAsync(second)).Single();
            var (contactOne, companyOne) = await Db.DealContactAsync(dealOne);
            var (contactTwo, companyTwo) = await Db.DealContactAsync(dealTwo);
            Assert.That(contactTwo, Is.EqualTo(contactOne), "The two deals point at different contacts.");
            Assert.That(companyTwo, Is.EqualTo(companyOne), "The two deals' contacts are in different companies.");
            Assert.That(await Db.DealCompanyIdAsync(dealOne), Is.EqualTo(await Db.DealCompanyIdAsync(dealTwo)),
                "The two deals carry different companies.");
        });
    }

    // =================================================================

    /// <summary>A new lead in workspace A with a unique email and company, qualified through the API.</summary>
    private static async Task<(Guid LeadId, string Email, string Company)> NewQualifiedLeadAsync(
        IPage page, ApiCaller api, string qualifiedKey, string label)
    {
        var email = $"{label.Replace(" ", ".").ToLowerInvariant()}.{World.Stamp.Replace("-", "")}{Random.Shared.Next(100, 999)}@example.com";
        var company = World.Unique(label + " Co");
        var leadId = await Flows.CreateLeadAsync(page, World.Unique(label), email, 50000m, World.A, company);
        await QualifyAsync(api, leadId, qualifiedKey);
        return (leadId, email, company);
    }

    private static async Task QualifyAsync(ApiCaller api, Guid leadId, string key)
    {
        var r = await api.PutAsync($"/api/leads/{leadId}/status?status={Uri.EscapeDataString(key)}");
        if (!r.IsSuccess) throw new AssertionException($"Qualifying the lead failed: {r}");
    }

    private static Task<ApiResponse> ConvertToDealAsync(ApiCaller api, Guid leadId, string title) =>
        api.PostAsync($"/api/leads/{leadId}/convert-to-deal", new
        {
            leadId,
            dealTitle = title,
            stage = "Qualification",
            expectedValue = 50000m,
            currency = "INR",
            expectedCloseDateUtc = DateTime.UtcNow.Date.AddDays(30),
        });

    /// <summary>The status key of a Qualified status, from the lead page's own status drop-down.</summary>
    private static async Task<string> QualifiedKeyAsync(IPage page, Guid leadId)
    {
        await page.GotoAsync($"/Leads/Detail/{leadId}");
        var key = await page.EvaluateAsync<string?>(
            @"() => {
                const s = document.querySelector('#statusChangeForm select[name=status]');
                if (!s) return null;
                const o = Array.from(s.options).find(o =>
                    /qualified|ready to buy/i.test(o.text) && !/(dis|un|not\s*)qualified/i.test(o.text));
                return o ? o.value : null;
            }");
        return key ?? throw new AssertionException("The lead page offers no Qualified status to convert from.");
    }

    private static async Task ExpectOneOfEachAsync(Guid tenantId, Guid leadId, string email, string company, int expectedDeals)
    {
        var deals = await Db.DealIdsForLeadAsync(leadId);
        Assert.That(deals.Count, Is.EqualTo(expectedDeals), $"Deals made from the lead: {deals.Count}.");
        Assert.That(await Db.CountContactsAsync(tenantId, email), Is.EqualTo(1), "Contacts with the lead's email.");
        Assert.That(await Db.CountCompaniesAsync(tenantId, company), Is.EqualTo(1), "Companies with the lead's company name.");

        var lead = await Db.LeadConversionAsync(leadId);
        Assert.That(lead.IsConverted, Is.True, "The lead is not marked converted.");
        Assert.That(lead.DealId, Is.EqualTo(deals.Single()), "The lead points at a different deal from the one made.");
    }
}
