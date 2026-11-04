// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/SearchTests.cs
//
// NEW (116). Contact and lead search find the same things after 116 made
// them fast (no LOWER() per row; phone digits from the stored columns
// Contacts.PhoneDigits / MobileDigits and Leads.PhoneDigits; the matching
// companies read into a list first).
//
// In workspace A, a contact and a lead whose details only this test
// knows (a random number in each), then searches through the API:
//
//   contact                                      lead
//   • the phone's digits, typed plainly          • the phone's digits
//   • the phone exactly as stored (+91 98… -…)   • the phone as stored
//   • the MOBILE's digits                        • a piece from the MIDDLE
//   • a piece from the MIDDLE of the first name    of the name
//   • the name in CAPITALS                       • the name in capitals
//   • a piece of its COMPANY's name              • a piece of its company
//   • something that matches nothing → neither found
//
// And the database fills the digits columns itself: the stored values
// are exactly the digits of what was saved.
//
// Every wrong answer is collected and reported together.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Lists")]
public class SearchTests : E2ETest
{
    [Test]
    public async Task Contact_and_lead_search_find_by_any_part_of_name_phone_or_company()
    {
        var a = World.A;
        var tenantId = await Db.TenantIdAsync(a.Name);
        var api = await Api.AsAsync(a.Admin);
        api.WaitWhenPaced = true;
        var problems = new List<string>();

        // Unique to this run: 8 random digits after 98 / 97 / 96.
        string Eight() => Random.Shared.Next(10000000, 99999999).ToString();
        var p1 = Eight(); var p2 = Eight(); var p3 = Eight();
        var tag = Random.Shared.Next(10000, 99999).ToString();

        var contactPhone  = $"+91 98{p1[..3]}-{p1[3..]}";          // +91 98123-45678
        var contactMobile = $"+91 97{p2[..3]}.{p2[3..]}";          // +91 97123.45678
        var leadPhone     = $"+91 (96{p3[..3]}) {p3[3..]}";        // +91 (96123) 45678
        var firstName     = $"Priyamvada{tag}";
        var companyName   = $"Zephyrine Traders {tag}";
        var leadName      = $"Anantharaman Search {tag}";
        var leadCompany   = $"Quillon Exports {tag}";

        var (contactId, leadId) = await Step("A contact (with a company, a phone and a mobile) and a lead, all only this test knows", async () =>
        {
            var co = await api.PostAsync("/api/companies", new { tenantId, name = companyName, country = "IN", vertical = "Generic" });
            Assert.That(co.IsSuccess, Is.True, $"Adding the company failed: {co}");
            var companyId = ApiIds.IdOf(co);

            var c = await api.PostAsync("/api/contacts", new
            {
                tenantId, companyId, firstName, lastName = "Search",
                email = World.EmailFor("A", "search" + tag), phone = contactPhone, mobile = contactMobile,
            });
            Assert.That(c.IsSuccess, Is.True, $"Adding the contact failed: {c}");

            var l = await api.PostAsync("/api/leads", new
            {
                tenantId, fullName = leadName, email = World.EmailFor("A", "searchlead" + tag), phone = leadPhone,
                companyName = leadCompany, estimatedValue = 1000m, currency = "INR",
            });
            Assert.That(l.IsSuccess, Is.True, $"Adding the lead failed: {l}");
            return (ApiIds.IdOf(c), ApiIds.IdOf(l));
        });

        await Step("The database filled the digits-only columns itself", async () =>
        {
            Expect("contact PhoneDigits", await Db.ValueAsync("Contacts", "PhoneDigits", contactId), "9198" + p1);
            Expect("contact MobileDigits", await Db.ValueAsync("Contacts", "MobileDigits", contactId), "9197" + p2);
            Expect("lead PhoneDigits", await Db.ValueAsync("Leads", "PhoneDigits", leadId), "9196" + p3);
        });

        await Step("Contact search: by phone digits, phone as stored, mobile digits, middle of the name, CAPITALS, company", async () =>
        {
            foreach (var (what, term) in new[]
                     {
                         ("the phone's digits", "98" + p1),
                         ("the phone exactly as stored", contactPhone),
                         ("the mobile's digits", "97" + p2),
                         ("a piece from the middle of the first name", $"yamvada{tag}"),
                         ("the name in capitals", firstName.ToUpperInvariant()),
                         ("a piece of the company's name", $"phyrine Traders {tag}"),
                     })
                await ExpectFoundAsync("contact", $"/api/contacts?pageNumber=1&pageSize=50&searchTerm={Uri.EscapeDataString(term)}",
                    contactId, what, term, true);
        });

        await Step("Lead search: by phone digits, phone as stored, middle of the name, CAPITALS, company", async () =>
        {
            foreach (var (what, term) in new[]
                     {
                         ("the phone's digits", "96" + p3),
                         ("the phone exactly as stored", leadPhone),
                         ("a piece from the middle of the name", $"tharaman Search {tag}"),
                         ("the name in capitals", leadName.ToUpperInvariant()),
                         ("a piece of the company", $"llon Exports {tag}"),
                     })
                await ExpectFoundAsync("lead", $"/api/leads?pageNumber=1&pageSize=50&searchTerm={Uri.EscapeDataString(term)}",
                    leadId, what, term, true);
        });

        await Step("A search that matches nothing finds neither", async () =>
        {
            var nothing = $"Nomatch{tag}Xq";
            await ExpectFoundAsync("contact", $"/api/contacts?pageNumber=1&pageSize=50&searchTerm={nothing}", contactId, "nonsense", nothing, false);
            await ExpectFoundAsync("lead", $"/api/leads?pageNumber=1&pageSize=50&searchTerm={nothing}", leadId, "nonsense", nothing, false);
        });

        if (problems.Count > 0)
            Assert.Fail($"{problems.Count} search problem(s):\n  • " + string.Join("\n  • ", problems));

        void Expect(string what, string? actual, string expected)
        {
            if (actual != expected) problems.Add($"{what}: '{actual ?? "(null)"}' (expected '{expected}')");
        }

        async Task ExpectFoundAsync(string kind, string path, Guid id, string what, string term, bool shouldFind)
        {
            var r = await api.GetAsync(path);
            if (!r.IsSuccess) { problems.Add($"{kind} search for {what} ('{term}') answered {r}"); return; }
            var found = r.Body.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase);
            if (found != shouldFind)
                problems.Add($"{kind} search for {what} ('{term}'): {(shouldFind ? "not found" : "found, but shouldn't be")}");
        }
    }
}
