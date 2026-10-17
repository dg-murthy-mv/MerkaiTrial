// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/InputValidationTests.cs
//
// NEW (095). What a person types, checked twice: in the browser before
// the form is sent, and on the server before anything is saved. All in
// workspace A (India), so a phone number without a + is an Indian one.
//
// Each test does the same three things:
//   • a mistake the BROWSER catches: the message appears under its own
//     box, and nothing is sent to the server at all (the test counts the
//     POSTs);
//   • a mistake only the SERVER can catch (or with the browser's check
//     switched off — data-validate="off" — to prove the server is the
//     guard): the page comes back with the message under the box and
//     nothing is saved;
//   • fixed, it saves.
//
//   1. Contacts: a bad email and a +91 number one digit short (browser);
//      an 8-digit number with no + for an Indian contact (server); then
//      098765 43210 saves.
//   2. Leads: a negative estimated value and a number too short to be one
//      (browser); a number that is not Indian for an Indian lead (server).
//   3. Deals: a value of 0 (browser); a close date in 2099 (server); then
//      ₹150 crore — over the old cap of 999,999,999 — saves.
//   4. Products: no SKU, a negative price and a 150% tax rate (browser); then a
//      price of 0 saves — a free item is a real quote line.
// =====================================================================

using System.Globalization;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Validation")]
public class InputValidationTests : E2ETest
{
    private static TestTenant A => World.A;

    // =================================================================
    // 1. Contacts
    // =================================================================

    [Test]
    public async Task A_contact_with_a_bad_email_or_phone_is_not_saved()
    {
        var admin = await SignedInAsync(A.Admin);
        var posts = CountPosts(admin, "/Contacts/Create");
        var email = World.EmailFor("A", "asha" + Random.Shared.Next(10000, 99999));
        var tenantId = await Db.TenantIdAsync(A.Name);

        await admin.GotoAsync("/Contacts/Create");
        await admin.Locator("#Input_FirstName").FillAsync("Asha");
        await admin.Locator("#Input_LastName").FillAsync("Validate " + World.Stamp);

        await Step("The browser stops a bad email and a +91 number one digit short, and sends nothing", async () =>
        {
            await admin.Locator("#Input_Email").FillAsync("asha@company");
            await admin.Locator("#Input_Phone").FillAsync("+91 98765 4321");
            await admin.GetByRole(AriaRole.Button, new() { Name = "Create Contact" }).ClickAsync();

            await ExpectMessageAsync(admin, "Input.Email", "Enter an email address like name@company.com.");
            await ExpectMessageAsync(admin, "Input.Phone", "An Indian number has 10 digits");
            Assert.That(posts(), Is.EqualTo(0), "The form was sent with mistakes the browser should have caught.");
        });

        await Step("With the browser's check off, the server refuses an 8-digit number for an Indian contact", async () =>
        {
            await admin.Locator("#Input_Email").FillAsync(email);
            await admin.Locator("#Input_Phone").FillAsync("8123 4567");
            await BrowserCheckOffAsync(admin, "#Input_FirstName");
            await admin.GetByRole(AriaRole.Button, new() { Name = "Create Contact" }).ClickAsync();

            await ExpectMessageAsync(admin, "Input.Phone", "An Indian number has 10 digits");
            Assert.That(posts(), Is.EqualTo(1));
            Assert.That(Ui.PathOf(admin.Url), Does.EndWith("/Contacts/Create"));
            Assert.That(await Db.CountContactsAsync(tenantId, email), Is.EqualTo(0), "The refused contact was saved.");
        });

        await Step("098765 43210 — a local Indian number — saves", async () =>
        {
            await admin.Locator("#Input_Phone").FillAsync("098765 43210");
            await admin.GetByRole(AriaRole.Button, new() { Name = "Create Contact" }).ClickAsync();
            await Ui.WaitForPageAsync(admin, u => u.Contains("/Contacts/Detail", StringComparison.OrdinalIgnoreCase), "The new contact");
            Assert.That(await Db.CountContactsAsync(tenantId, email), Is.EqualTo(1));
        });
    }

    // =================================================================
    // 2. Leads
    // =================================================================

    [Test]
    public async Task A_lead_with_a_negative_value_or_a_wrong_phone_is_not_saved()
    {
        var admin = await SignedInAsync(A.Admin);
        var posts = CountPosts(admin, "/Leads/Create");
        var name = World.Unique("Vikram Validate");
        var tenantId = await Db.TenantIdAsync(A.Name);

        await admin.GotoAsync("/Leads/Create");
        await admin.Locator("#Input_FullName").FillAsync(name);
        await admin.Locator("#Input_Email").FillAsync(World.EmailFor("A", "vv" + Random.Shared.Next(10000, 99999)));
        await admin.Locator("#Input_CompanyName").FillAsync(name + " Pvt Ltd");
        if (await Ui.SelectByTextAsync(admin.Locator("#Input_CountryId"), "India", required: false) is null)
            await Ui.SelectFirstIfEmptyAsync(admin.Locator("#Input_CountryId"));
        if (await Ui.SelectByTextAsync(admin.Locator("#Input_CurrencyId"), "INR", required: false) is null)
            await Ui.SelectFirstIfEmptyAsync(admin.Locator("#Input_CurrencyId"));
        await Ui.SelectFirstIfEmptyAsync(admin.Locator("#Input_ChannelId"));

        await Step("The browser stops a negative value and a number too short to be one", async () =>
        {
            await admin.Locator("#Input_Phone").FillAsync("+66 8123");
            await admin.Locator("#Input_EstimatedValue").FillAsync("-5");
            await admin.GetByRole(AriaRole.Button, new() { Name = "Create Lead" }).ClickAsync();

            await ExpectMessageAsync(admin, "Input.Phone", "too short for a phone number");
            await ExpectMessageAsync(admin, "Input.EstimatedValue", "can't be negative");
            Assert.That(posts(), Is.EqualTo(0), "The form was sent with mistakes the browser should have caught.");
        });

        await Step("With the browser's check off, the server refuses a number that is not Indian for an Indian lead", async () =>
        {
            await admin.Locator("#Input_Phone").FillAsync("12345678");
            await admin.Locator("#Input_EstimatedValue").FillAsync("250000");
            await BrowserCheckOffAsync(admin, "#Input_FullName");
            await admin.GetByRole(AriaRole.Button, new() { Name = "Create Lead" }).ClickAsync();

            await ExpectMessageAsync(admin, "Input.Phone", "An Indian number has 10 digits");
            Assert.That(posts(), Is.EqualTo(1));
            Assert.That(await Db.CountLeadsAsync(tenantId, name), Is.EqualTo(0), "The refused lead was saved.");
        });
    }

    // =================================================================
    // 3. Deals
    // =================================================================

    [Test]
    public async Task A_deal_needs_a_value_and_a_sensible_close_date_and_can_be_worth_150_crore()
    {
        var admin = await SignedInAsync(A.Admin);
        // A deal needs a contact. The form lists the workspace's contacts a
        // page at a time, so the test makes sure there is one and takes the
        // first offered rather than hunting for its own.
        await Step("Make sure the workspace has a contact", () => NewContactAsync(admin));
        var posts = CountPosts(admin, "/Pipeline/Create");
        var title = World.Unique("Metro line deal");

        await admin.GotoAsync("/Pipeline/Create");
        await admin.Locator("#Input_Title").FillAsync(title);
        await Ui.SelectFirstIfEmptyAsync(admin.Locator("#pcContact"));

        await Step("The browser stops a value of 0", async () =>
        {
            await admin.Locator("#Input_ExpectedValue").FillAsync("0");
            await admin.GetByRole(AriaRole.Button, new() { Name = "Create deal" }).ClickAsync();

            await ExpectMessageAsync(admin, "Input.ExpectedValue", "The expected value must be more than 0.");
            Assert.That(posts(), Is.EqualTo(0));
        });

        await Step("The server refuses a close date in 2099", async () =>
        {
            await admin.Locator("#Input_ExpectedValue").FillAsync("1500000000");      // ₹150 crore
            await admin.Locator("#Input_ExpectedCloseDate").FillAsync("2099-01-15");
            await admin.GetByRole(AriaRole.Button, new() { Name = "Create deal" }).ClickAsync();

            await ExpectMessageAsync(admin, "Input.ExpectedCloseDate", "Check the year");
            Assert.That(posts(), Is.EqualTo(1));
        });

        await Step("With next month's date, ₹150 crore saves (the old cap was 999,999,999)", async () =>
        {
            await admin.Locator("#Input_ExpectedValue").FillAsync("1500000000");
            await admin.Locator("#Input_ExpectedCloseDate").FillAsync(
                DateTime.Today.AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            await admin.GetByRole(AriaRole.Button, new() { Name = "Create deal" }).ClickAsync();

            await Ui.WaitForPageAsync(admin, u => u.Contains("/Pipeline/Detail", StringComparison.OrdinalIgnoreCase), "The new deal");
            var tenantId = await Db.TenantIdAsync(A.Name);
            Assert.That(await Db.DealIdByTitleAsync(tenantId, title), Is.Not.EqualTo(Guid.Empty));
        });
    }

    // =================================================================
    // 4. Products
    // =================================================================

    [Test]
    public async Task A_product_price_and_tax_rate_are_checked_and_a_free_item_saves()
    {
        var admin = await SignedInAsync(A.Admin);
        var posts = CountPosts(admin, "/Products/Create");
        var name = World.Unique("Free site visit");

        await admin.GotoAsync("/Products/Create");
        await admin.Locator("#Input_Name").FillAsync(name);
        await Ui.SelectFirstIfEmptyAsync(admin.Locator("#Input_Category"));

        await Step("The browser stops a missing SKU, a negative price and a 150% tax rate", async () =>
        {
            await admin.Locator("#Input_ListPrice").FillAsync("-1");
            await admin.Locator("#Input_TaxRate").FillAsync("150");
            await admin.GetByRole(AriaRole.Button, new() { Name = "Create Product" }).ClickAsync();

            await ExpectMessageAsync(admin, "Input.ListPrice", "can't be negative");
            await ExpectMessageAsync(admin, "Input.TaxRate", "between 0 and 100");
            await ExpectMessageAsync(admin, "Input.Sku", "Enter a SKU");
            Assert.That(posts(), Is.EqualTo(0));
        });

        await Step("A price of 0 with 18% tax saves", async () =>
        {
            await admin.Locator("#Input_Sku").FillAsync(World.Unique("FSV"));
            await admin.Locator("#Input_ListPrice").FillAsync("0");
            await admin.Locator("#Input_TaxRate").FillAsync("18");
            await admin.GetByRole(AriaRole.Button, new() { Name = "Create Product" }).ClickAsync();
            await Ui.WaitForPageAsync(admin, u => u.Contains("/Products/Detail", StringComparison.OrdinalIgnoreCase), "The new product");
            await Assertions.Expect(admin.GetByText(name).First).ToBeVisibleAsync();
        });
    }

    // ── helpers ───────────────────────────────────────────────────────

    /// <summary>Counts the POSTs the page sends to <paramref name="path"/> from now on.</summary>
    private static Func<int> CountPosts(IPage page, string path)
    {
        var count = 0;
        page.Request += (_, r) =>
        {
            if (r.Method == "POST" && Ui.PathOf(r.Url).StartsWith(path, StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref count);
        };
        return () => Volatile.Read(ref count);
    }

    /// <summary>The message under one box (the asp-validation-for span), containing the text.</summary>
    private static async Task ExpectMessageAsync(IPage page, string field, string contains)
    {
        var spot = page.Locator($"[data-valmsg-for='{field}']").First;
        try
        {
            await Assertions.Expect(spot).ToContainTextAsync(contains);
        }
        catch (Exception ex) when (ex is not AssertionException)
        {
            var said = await Ui.TextOrEmptyAsync(spot);
            throw new AssertionException(
                $"Under {field} the page should say \"{contains}\"; it says \"{said}\" (at {Ui.PathOf(page.Url)}). " +
                await Ui.DescribePageProblemsAsync(page));
        }
    }

    /// <summary>
    /// Switches the browser's checking off for the form holding
    /// <paramref name="anyField"/>, so the form is sent as typed — what an
    /// old browser, or someone posting by hand, would do. The server is
    /// then the only guard.
    /// </summary>
    private static Task<bool> BrowserCheckOffAsync(IPage page, string anyField) =>
        page.Locator($"form:has({anyField})").First
            .EvaluateAsync<bool>("f => { f.setAttribute('data-validate', 'off'); return true; }");

    /// <summary>Contacts → Add, with a valid Indian mobile. Returns the name the deal form lists it under.</summary>
    private static async Task<(string Name, string Email)> NewContactAsync(IPage page)
    {
        var last = "Deal " + World.Stamp + "-" + Random.Shared.Next(100, 999);
        var email = World.EmailFor("A", "dc" + Random.Shared.Next(10000, 99999));
        await page.GotoAsync("/Contacts/Create");
        await page.Locator("#Input_FirstName").FillAsync("Ravi");
        await page.Locator("#Input_LastName").FillAsync(last);
        await page.Locator("#Input_Email").FillAsync(email);
        await page.Locator("#Input_Phone").FillAsync("+91 98765 43210");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create Contact" }).ClickAsync();
        await Ui.WaitForPageAsync(page, u => u.Contains("/Contacts/Detail", StringComparison.OrdinalIgnoreCase), "The new contact");
        return ("Ravi " + last, email);
    }
}
