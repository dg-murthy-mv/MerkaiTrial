// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/LeadConversionTests.cs
//
// NEW (088). Workspace A — India / INR.
//
//   1. Converting a lead whose contact and company don't exist yet
//      creates both — and converting a SECOND lead with the same email
//      and company name reuses them instead of making duplicates.
//      (ConvertLeadToDealHandler: company matched by name, contact by
//      email.)
//
//      088b: the DEAL carries the company too (Deals.CompanyId) — the
//      first run found it was only on the contact, so the deal page
//      showed no company.
//
//   2. Money in rupees: lakh grouping (₹2,95,000.00, never 295,000.00)
//      and 18% GST, in the quote editor, on the quote page and on the
//      page the customer opens.
//
// Counts come from the test database (read-only): "exactly one contact
// with this email" is not something a list page can promise, because a
// list is paged, filtered and searched.
// =====================================================================

using System.Text.RegularExpressions;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Pipeline"), Category("Settings")]
public class LeadConversionTests : E2ETest
{
    [Test]
    public async Task Converting_a_lead_creates_its_contact_and_company_once()
    {
        var email = World.EmailFor("A", "kaveri" + Random.Shared.Next(1000, 9999));
        var company = World.Unique("Kaveri Traders") + " Pvt Ltd";
        var firstLead = World.Unique("Kavya Rao");
        var secondLead = World.Unique("Kavya Rao again");
        var firstDeal = World.Unique("Warehouse racking");
        var secondDeal = World.Unique("Warehouse racking phase 2");

        var tenantId = await Db.TenantIdAsync(World.A.Name);
        var admin = await SignedInAsync(World.A.Admin);

        await Step("Nothing exists for this customer yet", async () =>
        {
            Assert.That(await Db.CountContactsAsync(tenantId, email), Is.EqualTo(0), "A contact with this email already exists.");
            Assert.That(await Db.CountCompaniesAsync(tenantId, company), Is.EqualTo(0), "A company with this name already exists.");
        });

        // ── First conversion: both are new ────────────────────────────
        await Step("Add the first lead and qualify it", async () =>
        {
            await Flows.CreateLeadAsync(admin, firstLead, email, 180000m, World.A, company);
            await Flows.QualifyLeadAsync(admin);
        });

        var message = await Step("Convert it — the page says a contact and a company were created",
            () => Flows.ConvertLeadAsync(admin, firstDeal, 180000m));
        Assert.That(message, Does.Contain("New contact and company created"),
            $"After converting a lead with a new email and a new company name, the pipeline said: \"{message}\".");

        var (contactId, companyId) = await Step("Exactly one contact and one company now exist, linked to the deal", async () =>
        {
            Assert.That(await Db.CountContactsAsync(tenantId, email), Is.EqualTo(1), "Contacts with the lead's email.");
            Assert.That(await Db.CountCompaniesAsync(tenantId, company), Is.EqualTo(1), "Companies with the lead's company name.");

            var dealId = await Db.DealIdByTitleAsync(tenantId, firstDeal);
            var link = await Db.DealContactAsync(dealId);
            Assert.That(link.ContactId.HasValue, Is.True, "The new deal has no contact.");
            Assert.That(link.CompanyId.HasValue, Is.True, "The deal's contact is not linked to the new company.");
            Assert.That(await Db.DealCompanyIdAsync(dealId), Is.EqualTo(link.CompanyId),
                "The deal itself has no company (Deals.CompanyId is empty), so deal screens can't show it — " +
                "ConvertLeadToDealHandler must set it (round 088b).");
            return link;
        });

        await Step("The deal page names the company", async () =>
        {
            var dealId = await Db.DealIdByTitleAsync(tenantId, firstDeal);
            await admin.GotoAsync($"/Pipeline/Detail/{dealId}");
            await Assertions.Expect(admin.Locator("main").First).ToContainTextAsync(company);
        });

        // ── Second conversion: both are reused ────────────────────────
        await Step("Add a second lead with the same email and company, and qualify it", async () =>
        {
            await Flows.CreateLeadAsync(admin, secondLead, email, 90000m, World.A, company);
            await Flows.QualifyLeadAsync(admin);
        });

        var second = await Step("Convert it — nothing new is created",
            () => Flows.ConvertLeadAsync(admin, secondDeal, 90000m));
        Assert.That(second, Does.Not.Contain("New contact"),
            $"The second conversion created another contact. The pipeline said: \"{second}\".");
        Assert.That(second, Does.Not.Contain("New company"),
            $"The second conversion created another company. The pipeline said: \"{second}\".");

        await Step("Still exactly one contact and one company; both deals share them", async () =>
        {
            Assert.That(await Db.CountContactsAsync(tenantId, email), Is.EqualTo(1),
                "A duplicate contact was created for the same email.");
            Assert.That(await Db.CountCompaniesAsync(tenantId, company), Is.EqualTo(1),
                "A duplicate company was created for the same name.");

            var link = await Db.DealContactAsync(await Db.DealIdByTitleAsync(tenantId, secondDeal));
            Assert.That(link.ContactId, Is.EqualTo(contactId), "The second deal points at a different contact.");
            Assert.That(link.CompanyId, Is.EqualTo(companyId), "The shared contact's company changed.");
            Assert.That(await Db.DealCompanyIdAsync(await Db.DealIdByTitleAsync(tenantId, secondDeal)), Is.EqualTo(companyId),
                "The second deal has no company, or a different one.");
        });
    }

    [Test]
    public async Task A_quote_in_rupees_uses_lakh_grouping_and_GST()
    {
        var leadName = World.Unique("Arjun Money");
        var dealTitle = World.Unique("Showroom lighting");
        var admin = await SignedInAsync(World.A.Admin);

        await Step("Add, qualify and convert a lead", async () =>
        {
            await Flows.CreateLeadAsync(admin, leadName, World.EmailFor("A", "arjun" + Random.Shared.Next(1000, 9999)), 250000m);
            await Flows.QualifyLeadAsync(admin);
            await Flows.ConvertLeadAsync(admin, dealTitle, 250000m);
        });

        // 2 × ₹1,25,000 = ₹2,50,000; 18% GST = ₹45,000; total ₹2,95,000.
        await Step("Fill a quote line: 2 × 1,25,000 at 18% GST", () =>
            Flows.FillQuoteAsync(admin, dealTitle, new QuoteLine("Lighting design", 2, 125000m, TaxRate: 18)));

        await Step("The editor calls the tax GST and totals it in lakh grouping", async () =>
        {
            await Assertions.Expect(admin.Locator("#qeTable thead")).ToContainTextAsync("GST");
            await Assertions.Expect(admin.Locator("#qeSubtotal")).ToContainTextAsync("2,50,000");
            await Assertions.Expect(admin.Locator("#qeTax")).ToContainTextAsync("45,000");
            await Assertions.Expect(admin.Locator("#qeGrand")).ToContainTextAsync("2,95,000");
            await Assertions.Expect(admin.Locator("#qeGrand")).ToContainTextAsync("₹");
        });

        await Step("Send it", () => Flows.SubmitQuoteAsync(admin));

        await Step("The quote page shows ₹2,50,000.00 + ₹45,000.00 = ₹2,95,000.00", async () =>
        {
            var text = await admin.Locator("main").First.InnerTextAsync();
            ExpectMoney(text, "₹2,50,000.00", "subtotal");
            ExpectMoney(text, "₹45,000.00", "GST");
            ExpectMoney(text, "₹2,95,000.00", "total");
        });

        var link = await Step("The quote has a customer link", () => Flows.CustomerLinkAsync(admin));

        await Step("The customer sees the same figures, in INR", async () =>
        {
            var customer = await NewPageAsync("customer");
            await customer.GotoAsync(link);
            var text = await customer.Locator("body").InnerTextAsync();
            ExpectMoney(text, "₹2,95,000.00", "total on the customer's page");
            Assert.That(text, Does.Contain("INR"), "The customer's page does not say INR.");
        });
    }

    /// <summary>
    /// Asserts the amount is printed exactly, and if it isn't, says
    /// whether it came out with Western grouping (295,000.00) or with no
    /// rupee sign — the two ways this has gone wrong before.
    /// </summary>
    private static void ExpectMoney(string pageText, string expected, string what)
    {
        if (pageText.Contains(expected)) return;

        var digits = Regex.Replace(expected, @"[^\d.]", "");                 // 295000.00
        var western = decimal.Parse(digits, System.Globalization.CultureInfo.InvariantCulture)
            .ToString("N2", System.Globalization.CultureInfo.InvariantCulture); // 295,000.00
        var hint = pageText.Contains(western)
            ? $" It is printed as {western} — Western grouping. India's Countries.NumberFormat should be \"en-IN\"."
            : pageText.Contains(expected.TrimStart('₹'))
                ? " The number is right but the ₹ sign is missing — check India's Countries.CurrencySymbol."
                : "";
        Assert.Fail($"Expected the {what} as {expected}.{hint}");
    }
}
