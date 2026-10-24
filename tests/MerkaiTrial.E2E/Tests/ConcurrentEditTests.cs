// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/ConcurrentEditTests.cs
//
// TEST 90 (round 102) —
//   Concurrent_updates_do_not_silently_overwrite_another_users_changes
//
// Leads, deals, contacts and companies carry a version (round 102). A save
// made from an older copy is refused — 409 Conflict through the API, a
// message and a Reload link on the page — instead of quietly replacing
// what the other person saved.
//
// In the browser (a lead):
//   • the manager and the admin open the same lead's Edit page and change
//     its ADDRESS (not the name, email, phone or company: changing those
//     runs the duplicate check first, which asks its own question);
//   • the admin saves first;
//   • the manager's save is refused: the page says someone else changed
//     it, keeps what the manager typed, and offers Reload; the admin's
//     change is still there;
//   • after Reload the manager sees the admin's change and can save.
//
// Through the API:
//   • a deal: a save from an old version → 409 and nothing changes; a save
//     from the current version → saved;
//   • a contact: two saves from the same version at the same instant →
//     exactly one wins, the other gets 409, and the record holds the
//     winner's change whole;
//   • a company: an old version → 409; a damaged version → 400.
// =====================================================================

using System.Text.Json;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture]
public class ConcurrentEditTests : E2ETest
{
    [Test]
    public async Task Concurrent_updates_do_not_silently_overwrite_another_users_changes()
    {
        var a = World.A;
        var m = Random.Shared.Next(10000, 99999).ToString();

        // ── In the browser: a lead ───────────────────────────────────
        var managerPage = await SignedInAsync(a["manager"]);
        var leadName = World.Unique("Concurrent Lead");
        var leadId = await Step("The manager adds a lead", () =>
            Flows.CreateLeadAsync(managerPage, leadName, World.EmailFor("A", "concurrent" + m), 40000m, a, "Original Co " + m));

        var adminPage = await SignedInAsync(a.Admin);
        await Step("The manager and the admin both open its Edit page", async () =>
        {
            await managerPage.GotoAsync($"/Leads/Edit/{leadId}");
            await adminPage.GotoAsync($"/Leads/Edit/{leadId}");
            await Assertions.Expect(managerPage.Locator("#Input_Address")).ToBeVisibleAsync();
            await Assertions.Expect(adminPage.Locator("#Input_Address")).ToBeVisibleAsync();
            Assert.That(await managerPage.Locator("input[name=Version]").InputValueAsync(), Is.Not.Empty,
                "The Edit page carries no version — it can't tell a stale save from a fresh one.");
        });

        var adminAddress = "12 Admin Road " + m;
        var managerAddress = "34 Manager Street " + m;

        await Step("The admin saves first", async () =>
        {
            await adminPage.Locator("#Input_Address").FillAsync(adminAddress);
            await SaveLeadAsync(adminPage);
            await Ui.WaitForAddressAsync(adminPage, u => Ui.PathOf(u).StartsWith("/Leads/Detail", StringComparison.OrdinalIgnoreCase));
            Assert.That(await Db.TextAsync("Leads", "Address", leadId), Is.EqualTo(adminAddress));
        });

        await Step("The manager's save, from the older page, is refused — the admin's change stays", async () =>
        {
            await managerPage.Locator("#Input_Address").FillAsync(managerAddress);
            await SaveLeadAsync(managerPage);
            await Ui.WaitForDocumentAsync(managerPage);

            await Assertions.Expect(managerPage.Locator("#recordChangedNotice"))
                .ToBeVisibleAsync(new() { Timeout = E2ESettings.ActionTimeoutMs });
            await Assertions.Expect(managerPage.Locator("main").First).ToContainTextAsync("Someone else changed this lead");

            Assert.That(await Db.TextAsync("Leads", "Address", leadId), Is.EqualTo(adminAddress),
                "The manager's stale save overwrote the admin's change.");
            Assert.That(await managerPage.Locator("#Input_Address").InputValueAsync(), Is.EqualTo(managerAddress),
                "The page threw away what the manager typed.");
        });

        await Step("Reload shows the admin's change; the manager's save then goes through", async () =>
        {
            await managerPage.Locator("#recordChangedReload").ClickAsync();
            await Ui.WaitForDocumentAsync(managerPage);
            await Assertions.Expect(managerPage.Locator("#Input_Address")).ToHaveValueAsync(adminAddress);

            await managerPage.Locator("#Input_Address").FillAsync(managerAddress);
            await SaveLeadAsync(managerPage);
            await Ui.WaitForAddressAsync(managerPage, u => Ui.PathOf(u).StartsWith("/Leads/Detail", StringComparison.OrdinalIgnoreCase));
            Assert.That(await Db.TextAsync("Leads", "Address", leadId), Is.EqualTo(managerAddress));
        });

        // ── Through the API ──────────────────────────────────────────
        var admin = await Api.AsAsync(a.Admin);
        await Api.EnsureTokensWorkAsync(admin);

        var dealTitle = World.Unique("Concurrent deal");
        var (_, dealId) = await Step("Make a deal (with its contact and company)", () =>
            Flows.LeadToDealAsync(adminPage, a, World.Unique("Kiran Concurrent"), World.EmailFor("A", "concdeal" + m),
                dealTitle, 55000m, "Concurrent Co " + m));
        var (contactIdN, companyIdN) = await Db.DealContactAsync(dealId);
        var contactId = contactIdN!.Value;
        var companyId = companyIdN!.Value;

        await Step("A deal saved from an old version gets 409 and nothing changes; from the current one it saves", async () =>
        {
            var v1 = await GetAsync(admin, $"/api/deals/{dealId}");
            var first = await admin.PutAsync($"/api/deals/{dealId}", DealBody(v1, dealTitle + " (first)"));
            Assert.That(first.IsSuccess, Is.True, $"The first save failed: {first}");

            var stale = await admin.PutAsync($"/api/deals/{dealId}", DealBody(v1, dealTitle + " (stale)"));
            Assert.That(stale.Status, Is.EqualTo(409), $"A save from an old version was not refused: {stale}");
            Assert.That(stale.Error, Does.Contain("Someone else changed this deal"), $"The 409 doesn't say why: {stale}");
            Assert.That(await Db.TextAsync("Deals", "Title", dealId), Is.EqualTo(dealTitle + " (first)"),
                "The stale save changed the deal.");

            var v2 = await GetAsync(admin, $"/api/deals/{dealId}");
            Assert.That(v2.GetProperty("version").GetString(), Is.Not.EqualTo(v1.GetProperty("version").GetString()),
                "The deal's version did not change when it was saved.");
            var fresh = await admin.PutAsync($"/api/deals/{dealId}", DealBody(v2, dealTitle + " (second)"));
            Assert.That(fresh.IsSuccess, Is.True, $"A save from the current version failed: {fresh}");
            Assert.That(await Db.TextAsync("Deals", "Title", dealId), Is.EqualTo(dealTitle + " (second)"));
        });

        await Step("Two saves of a contact from the same version at the same instant: one wins whole, the other gets 409", async () =>
        {
            var v = await GetAsync(admin, $"/api/contacts/{contactId}");
            var titles = new[] { "Director " + m, "Partner " + m };

            var answers = await Api.AllAtOnceAsync(
                () => admin.PutAsync($"/api/contacts/{contactId}", ContactBody(v, titles[0])),
                () => admin.PutAsync($"/api/contacts/{contactId}", ContactBody(v, titles[1])));
            TestContext.Progress.WriteLine($"    the two saves answered {answers[0].Status}, {answers[1].Status}");

            Assert.That(answers.Count(x => x.IsSuccess), Is.EqualTo(1), $"Expected exactly one save to win: {answers[0]} / {answers[1]}");
            Assert.That(answers.Count(x => x.Status == 409), Is.EqualTo(1), $"Expected the other to get 409: {answers[0]} / {answers[1]}");

            var winner = titles[Array.FindIndex(answers, x => x.IsSuccess)];
            Assert.That(await Db.TextAsync("Contacts", "JobTitle", contactId), Is.EqualTo(winner),
                "The contact does not hold the winning save's change.");
        });

        await Step("A company saved from an old version gets 409; a damaged version gets 400", async () =>
        {
            var v1 = await GetAsync(admin, $"/api/companies/{companyId}");
            var original = await Db.TextAsync("Companies", "Name", companyId) ?? "Concurrent Co " + m;

            var first = await admin.PutAsync($"/api/companies/{companyId}", CompanyBody(v1, original + " (first)"));
            Assert.That(first.IsSuccess, Is.True, $"The first save failed: {first}");

            var stale = await admin.PutAsync($"/api/companies/{companyId}", CompanyBody(v1, original + " (stale)"));
            Assert.That(stale.Status, Is.EqualTo(409), $"A save from an old version was not refused: {stale}");
            Assert.That(await Db.TextAsync("Companies", "Name", companyId), Is.EqualTo(original + " (first)"),
                "The stale save changed the company.");

            var damaged = await admin.PutAsync($"/api/companies/{companyId}", CompanyBody(v1, original + " (damaged)", version: "not a version!"));
            Assert.That(damaged.Status, Is.EqualTo(400), $"A damaged version was not refused: {damaged}");
            Assert.That(await Db.TextAsync("Companies", "Name", companyId), Is.EqualTo(original + " (first)"),
                "The damaged save changed the company.");
        });
    }

    // ── helpers ──────────────────────────────────────────────────────

    /// <summary>The lead Edit form's own Save button (not "Save changes anyway").</summary>
    private static Task SaveLeadAsync(IPage page) =>
        page.Locator("form:has(#Input_Address) button[type=submit]").First.ClickAsync();

    private static async Task<JsonElement> GetAsync(ApiCaller who, string path)
    {
        var r = await who.GetAsync(path);
        Assert.That(r.IsSuccess, Is.True, $"GET {path} failed: {r}");
        var j = r.Json();
        Assert.That(j.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Length > 0, Is.True,
            $"GET {path} carries no version — run Sql/102_RowVersion.sql on the test database.");
        return j;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static object? Raw(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? JsonSerializer.Deserialize<object>(v.GetRawText()) : null;

    private static object DealBody(JsonElement d, string title, string? version = null) => new
    {
        title,
        description = Str(d, "description"),
        stage = Str(d, "stage"),
        probability = d.GetProperty("probability").GetInt32(),
        expectedValue = d.GetProperty("expectedValue").GetDecimal(),
        currency = Str(d, "currency"),
        expectedCloseDateUtc = d.GetProperty("expectedCloseDateUtc").GetDateTime(),
        ownerUserId = Str(d, "ownerUserId"),
        sourceId = Raw(d, "sourceId"),
        tags = Str(d, "tags"),
        version = version ?? Str(d, "version"),
    };

    private static object ContactBody(JsonElement c, string jobTitle) => new
    {
        id = c.GetProperty("id").GetGuid(),
        companyId = Raw(c, "companyId"),
        firstName = Str(c, "firstName"),
        lastName = Str(c, "lastName"),
        jobTitle,
        email = Str(c, "email"),
        phone = Str(c, "phone"),
        mobile = Str(c, "mobile"),
        address = Str(c, "address"),
        city = Str(c, "city"),
        country = Str(c, "country"),
        postalCode = Str(c, "postalCode"),
        notes = Str(c, "notes"),
        isPrimary = c.TryGetProperty("isPrimary", out var p) && p.ValueKind == JsonValueKind.True,
        version = Str(c, "version"),
    };

    private static object CompanyBody(JsonElement co, string name, string? version = null) => new
    {
        id = co.GetProperty("id").GetGuid(),
        name,
        country = Str(co, "country") ?? "India",
        taxId = Str(co, "taxId"),
        vertical = Str(co, "vertical") ?? "Generic",
        version = version ?? Str(co, "version"),
    };
}
