// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/RecordPickerTests.cs
//
// NEW (108). Choosing a contact, a company or a deal in a big workspace.
//
// Before 108 these drop-downs listed records straight from the database:
// New Deal offered the first 10 contacts only, New Quote the newest 500
// deals (and only those in three hard-coded stages), the contact forms up
// to 5,000 companies. Now each starts with a short list (the newest 20)
// and has a search box that asks the server, so ANY record can be chosen.
//
// What this checks, as the workspace admin, with records made for it:
//   • each drop-down starts short (20 at most, plus "Choose…")
//   • typing a name finds the record from the whole workspace
//   • on New Deal, choosing the contact still fills in its company
//   • the deal saves with that contact and company
//   • New Quote finds that (open) deal by its title and opens the editor
//   • a new contact can be given a company found by search
//   • a search with no match says so
// =====================================================================

using System.Globalization;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Pipeline")]
public class RecordPickerTests : E2ETest
{
    private static TestTenant A => World.A;

    /// <summary>The placeholder ("Choose a contact…") plus the starting 20.</summary>
    private const int MostOptionsAtFirst = 21;

    [Test]
    public async Task Contacts_companies_and_deals_can_be_found_and_chosen_from_the_whole_workspace()
    {
        var tenantId = await Db.TenantIdAsync(A.Name);
        var api = await Api.AsAsync(A.Admin);
        api.WaitWhenPaced = true;

        var mark = World.Stamp.Replace("-", "") + Random.Shared.Next(100, 999);
        var companyName = $"Picker Co {mark}";
        var lastName = $"Picker {mark}";
        var dealTitle = $"Picker deal {mark}";

        var (companyId, contactId) = await Step("Make a company and a contact at it, through the API", async () =>
        {
            var c = await api.PostAsync("/api/companies", new { tenantId, name = companyName, country = "IN", vertical = "Generic" });
            Assert.That(c.IsSuccess, Is.True, $"Adding the company failed: {c}");
            var company = ApiIds.IdOf(c);

            var p = await api.PostAsync("/api/contacts", new
            {
                tenantId, firstName = "Pia", lastName, companyId = company,
                email = World.EmailFor("A", "picker" + mark),
            });
            Assert.That(p.IsSuccess, Is.True, $"Adding the contact failed: {p}");
            return (company, ApiIds.IdOf(p));
        });

        var admin = await SignedInAsync(A.Admin);

        // ── New Deal ──────────────────────────────────────────────────
        await Step("New Deal: the contact and company lists start short", async () =>
        {
            await admin.GotoAsync("/Pipeline/Create");
            Assert.That((await Ui.OptionTextsAsync(admin.Locator("#pcContact"))).Length, Is.LessThanOrEqualTo(MostOptionsAtFirst),
                "The contact drop-down lists more than the newest 20 — it should search instead.");
            Assert.That((await Ui.OptionTextsAsync(admin.Locator("#pcCompany"))).Length, Is.LessThanOrEqualTo(MostOptionsAtFirst),
                "The company drop-down lists more than the newest 20 — it should search instead.");
        });

        await Step("New Deal: a search with no match says so", async () =>
        {
            await admin.Locator("[data-record-search='pcContact']").FillAsync("zz-no-such-person-" + mark);
            await Assertions.Expect(admin.Locator("[data-record-search-status='pcContact']"))
                .ToContainTextAsync("No matches", new() { Timeout = E2ESettings.ActionTimeoutMs });
        });

        await Step("New Deal: typing the contact's name finds them, and their company is filled in", async () =>
        {
            await Ui.SearchAndSelectAsync(admin, "pcContact", lastName);
            Assert.That(await admin.Locator("#pcContact").InputValueAsync(), Is.EqualTo(contactId.ToString()));

            // The page's own script fills the company from the chosen contact.
            await Ui.WaitUntilAsync(admin,
                async () => string.Equals(await admin.Locator("#pcCompany").InputValueAsync(), companyId.ToString(), StringComparison.OrdinalIgnoreCase),
                $"Choosing the contact did not fill in their company '{companyName}'.");
        });

        await Step("New Deal: the deal saves with that contact and company", async () =>
        {
            await admin.Locator("#Input_Title").FillAsync(dealTitle);
            await admin.Locator("#Input_ExpectedValue").FillAsync("75000");
            await admin.Locator("#Input_ExpectedCloseDate").FillAsync(
                DateTime.Today.AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            await admin.GetByRole(AriaRole.Button, new() { Name = "Create deal" }).ClickAsync();
            await Ui.WaitForPageAsync(admin, u => u.Contains("/Pipeline/Detail", StringComparison.OrdinalIgnoreCase), "The new deal");

            var detail = await api.GetAsync($"/api/deals?search={Uri.EscapeDataString(dealTitle)}&pageSize=5");
            Assert.That(detail.IsSuccess, Is.True, $"Reading the deal back failed: {detail}");
            Assert.That(detail.Body, Does.Contain(dealTitle));
            Assert.That(detail.Body, Does.Contain(companyName), "The deal was saved without the company the contact brought in.");
        });

        // ── New Quote ─────────────────────────────────────────────────
        await Step("New Quote: the deal list starts short, and the new deal is found by its title", async () =>
        {
            await admin.GotoAsync("/Quotes/Create");
            Assert.That((await Ui.OptionTextsAsync(admin.Locator("#SelectedDealId"))).Length, Is.LessThanOrEqualTo(MostOptionsAtFirst),
                "The deal drop-down lists more than the newest 20 — it should search instead.");

            await Ui.SearchAndSelectAsync(admin, "SelectedDealId", dealTitle);
            await admin.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();
            await Assertions.Expect(admin.Locator("#qcForm")).ToBeVisibleAsync(new() { Timeout = E2ESettings.ActionTimeoutMs });
        });

        // ── New Contact ───────────────────────────────────────────────
        await Step("New Contact: a company found by search is saved on the contact", async () =>
        {
            await admin.GotoAsync("/Contacts/Create");
            Assert.That((await Ui.OptionTextsAsync(admin.Locator("#Input_CompanyId"))).Length, Is.LessThanOrEqualTo(MostOptionsAtFirst),
                "The company drop-down lists more than 20 — it should search instead.");

            await Ui.SearchAndSelectAsync(admin, "Input_CompanyId", companyName);

            var second = $"Second {mark}";
            await admin.Locator("#Input_FirstName").FillAsync("Pran");
            await admin.Locator("#Input_LastName").FillAsync(second);
            await admin.Locator("#Input_Email").FillAsync(World.EmailFor("A", "picker2" + mark));
            await admin.Locator("#Input_Phone").FillAsync("+91 98765 43210");
            await admin.GetByRole(AriaRole.Button, new() { Name = "Create Contact" }).ClickAsync();
            await Ui.WaitForPageAsync(admin, u => u.Contains("/Contacts/Detail", StringComparison.OrdinalIgnoreCase), "The new contact");

            var back = await api.GetAsync($"/api/contacts?searchTerm={Uri.EscapeDataString(second)}&pageSize=5");
            Assert.That(back.IsSuccess, Is.True, $"Reading the contact back failed: {back}");
            Assert.That(back.Body, Does.Contain(companyName), "The new contact was saved without the company chosen by search.");
        });
    }
}
