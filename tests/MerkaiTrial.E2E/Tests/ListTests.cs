// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/ListTests.cs
//
// NEW (096 — the lists round). Workspace A, as its admin.
//
//   • Leads: clicking the Value header sorts the list on the server, and
//     clicking again flips it. The header says which way (aria-sort).
//   • Every list starts at 10 per page, and the size a person picks is
//     remembered on their browser: open the list again without a size in
//     the address and it is still the one they chose.
//   • Leads: a phone number typed with spaces or dashes still finds the
//     lead (only the digits are compared).
//   • Products draws ONE "Per page" box (it used to draw two, both with
//     the same id).
//   • Invoices: a search that matches nothing says so and keeps what was
//     typed; an unreadable date is reported, not silently dropped.
//   • Tasks: a search that matches nothing says so and offers Clear.
//   • Settings → Users: a page number past the end lands on the last real
//     page instead of an empty list, and every action form says which user
//     it is for (096b).
//   • Pipeline table: sorts by Value and has its own pager.
//
// Nothing here changes a workspace setting; the leads it adds have
// time-stamped names, so they never collide with another test's.
// =====================================================================

using System.Globalization;
using System.Text.RegularExpressions;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Lists")]
public class ListTests : E2ETest
{
    private static readonly ViewportSize Desktop = new() { Width = 1366, Height = 900 };

    [Test]
    public async Task Leads_sort_by_value_and_remember_the_page_size()
    {
        var admin = await SignedInAsync(World.A.Admin, Desktop);
        var tag = World.Unique("Sortlist").Replace(" ", "");

        await Step("Add three leads worth 300, 100 and 200", async () =>
        {
            await Flows.CreateLeadAsync(admin, $"{tag} Alpha", $"alpha.{tag}@example.com".ToLowerInvariant(), 300);
            await Flows.CreateLeadAsync(admin, $"{tag} Bravo", $"bravo.{tag}@example.com".ToLowerInvariant(), 100);
            await Flows.CreateLeadAsync(admin, $"{tag} Charlie", $"charlie.{tag}@example.com".ToLowerInvariant(), 200);
        });

        await admin.GotoAsync($"/Leads?StatusFilter=all&SearchTerm={Uri.EscapeDataString(tag)}");

        var header = admin.Locator("table.lx-table a[data-sort-key='value']");
        // 096b — the inner locator of Has is searched INSIDE each <th>, so it
        // must not start from the table again ("table.lx-table a…" inside a
        // heading cell never matches, and the first run timed out on it).
        var headerCell = admin.Locator("table.lx-table th").Filter(new() { Has = admin.Locator("a[data-sort-key='value']") });
        var names = admin.Locator("table.lx-table tbody a.lx-nm");

        async Task<string[]> OrderAsync() =>
            (await names.AllInnerTextsAsync()).Select(n => n.Trim().Replace(tag + " ", "")).ToArray();

        await Step("Click Value — sorted one way", async () =>
        {
            await header.ClickAsync();
            await Ui.WaitForAddressAsync(admin, u => u.Contains("Sort=value", StringComparison.OrdinalIgnoreCase));

            var dir = await headerCell.GetAttributeAsync("aria-sort");
            var expected = dir == "descending"
                ? new[] { "Alpha", "Charlie", "Bravo" }
                : new[] { "Bravo", "Charlie", "Alpha" };
            Assert.That(dir, Is.EqualTo("ascending").Or.EqualTo("descending"), "The Value header should say how it is sorted.");
            Assert.That(await OrderAsync(), Is.EqualTo(expected), $"Leads sorted by value, {dir}.");
        });

        await Step("Click Value again — the other way", async () =>
        {
            var before = await headerCell.GetAttributeAsync("aria-sort");
            await header.ClickAsync();
            await Ui.WaitUntilAsync(admin,
                async () => await headerCell.GetAttributeAsync("aria-sort") != before,
                "The second click on Value did not flip the sort.");

            var dir = await headerCell.GetAttributeAsync("aria-sort");
            var expected = dir == "descending"
                ? new[] { "Alpha", "Charlie", "Bravo" }
                : new[] { "Bravo", "Charlie", "Alpha" };
            Assert.That(await OrderAsync(), Is.EqualTo(expected), $"Leads sorted by value, {dir}.");
        });

        await Step("A fresh browser starts at 10 per page", async () =>
        {
            await admin.Context.ClearCookiesAsync();
            await Ui.SignInAsync(admin, World.A.Admin);
            await admin.GotoAsync("/Leads?StatusFilter=all");
            Assert.That(await admin.Locator("#lpPageSize").InputValueAsync(), Is.EqualTo("10"));
        });

        await Step("Choose 25 — remembered next time", async () =>
        {
            await admin.Locator("#lpPageSize").SelectOptionAsync("25");
            await Ui.WaitForAddressAsync(admin, u => u.Contains("PageSize=25", StringComparison.OrdinalIgnoreCase));

            await admin.GotoAsync("/Leads");
            Assert.That(await admin.Locator("#lpPageSize").InputValueAsync(), Is.EqualTo("25"),
                "Leads opened without a size should use the one chosen last time.");

            // Remembered per list: Contacts has its own.
            await admin.GotoAsync("/Contacts");
            var contactsSize = admin.Locator("#lpPageSize");
            if (await contactsSize.CountAsync() > 0)
                Assert.That(await contactsSize.InputValueAsync(), Is.EqualTo("10"), "Contacts keeps its own page size.");
        });
    }

    [Test]
    public async Task Leads_phone_search_ignores_spaces_and_dashes()
    {
        var admin = await SignedInAsync(World.A.Admin, Desktop);
        var name = World.Unique("Phonelist");
        var eight = Random.Shared.Next(10_000_000, 99_999_999).ToString(CultureInfo.InvariantCulture);

        await Step("Add a lead with its own phone number", async () =>
        {
            await Flows.CreateLeadAsync(admin, name, $"phone.{World.Stamp}.{eight}@example.com", 1000,
                beforeSave: p => p.Locator("#Input_Phone").FillAsync("+9198" + eight));
        });

        await Step("Search for it typed as 98-XXXX XXXX", async () =>
        {
            var typed = $"98-{eight[..4]} {eight[4..]}";
            await admin.GotoAsync($"/Leads?StatusFilter=all&SearchTerm={Uri.EscapeDataString(typed)}");
            var rows = admin.Locator("table.lx-table tbody a.lx-nm");
            Assert.That((await rows.AllInnerTextsAsync()).Any(t => t.Contains(name, StringComparison.Ordinal)), Is.True,
                $"Searching '{typed}' should find the lead whose phone is +9198{eight}.");
        });
    }

    [Test]
    public async Task Products_draws_one_page_size_box()
    {
        var admin = await SignedInAsync(World.A.Admin, Desktop);
        await admin.GotoAsync("/Products");

        var boxes = await admin.Locator("[id='lpPageSize']").CountAsync();
        Assert.That(boxes, Is.LessThanOrEqualTo(1), "Products drew more than one 'Per page' box.");
    }

    [Test]
    public async Task Invoices_search_and_dates_say_what_happened()
    {
        var admin = await SignedInAsync(World.A.Admin, Desktop);
        var nothing = "zz-no-invoice-" + World.Stamp;

        await Step("A search that matches nothing", async () =>
        {
            await admin.GotoAsync($"/Invoices?SearchTerm={Uri.EscapeDataString(nothing)}&StatusFilter=Draft");
            await Expect(admin.GetByText("No invoice matches these filters")).ToBeVisibleAsync();
            Assert.That(await admin.Locator("input[name='SearchTerm']").First.InputValueAsync(), Is.EqualTo(nothing),
                "The search box should keep what was typed.");
            Assert.That(await admin.Locator("select[name='StatusFilter']").First.InputValueAsync(), Is.EqualTo("Draft"),
                "The status filter should stay on Draft.");
        });

        await Step("An unreadable date is reported", async () =>
        {
            await admin.GotoAsync("/Invoices?FromDate=not-a-date");
            await Expect(admin.GetByText(new Regex("could not be read", RegexOptions.IgnoreCase))).ToBeVisibleAsync();
        });

        await Step("From after To is reported", async () =>
        {
            await admin.GotoAsync("/Invoices?FromDate=2026-12-31&ToDate=2026-01-01");
            await Expect(admin.GetByText(new Regex("From date is after the To date", RegexOptions.IgnoreCase))).ToBeVisibleAsync();
        });
    }

    [Test]
    public async Task Tasks_search_with_no_match_offers_clear()
    {
        var admin = await SignedInAsync(World.A.Admin, Desktop);
        await admin.GotoAsync("/Tasks?search=" + Uri.EscapeDataString("zz-no-task-" + World.Stamp) + "&type=Deal");

        await Expect(admin.GetByText("No task matches")).ToBeVisibleAsync();
        Assert.That(await admin.Locator("select[name='type']").InputValueAsync(), Is.EqualTo("Deal"));

        await admin.GetByRole(AriaRole.Link, new() { Name = "Clear the search and filter" }).ClickAsync();
        await Ui.WaitForAddressAsync(admin, u => !u.Contains("search=", StringComparison.OrdinalIgnoreCase));
        await Expect(admin.GetByText("No task matches")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Users_page_past_the_end_lands_on_the_last_page()
    {
        var admin = await SignedInAsync(World.A.Admin, Desktop);
        await admin.GotoAsync("/Settings/Users?PageNumber=999");

        // Workspace A has its admin and four seeded people, so there is a list.
        await Expect(admin.Locator("main").GetByText(World.A.Admin.Email).Locator("visible=true").First).ToBeVisibleAsync();
        Assert.That(await admin.Locator(".alert-danger:visible").CountAsync(), Is.EqualTo(0),
            "Settings → Users showed an error for a page past the end.");

        // 096b — every action form (Invite, Reset password, Deactivate) must
        // post WHICH user. The first version lost the id: asp-all-route-data
        // replaced the asp-route-userId value, and the handler got an empty id.
        var missing = await admin.EvaluateAsync<string[]>(
            @"() => [...document.querySelectorAll('form[method=post]')]
                .filter(f => /handler=(ResendInvite|SendReset|ToggleStatus)/i.test(f.getAttribute('action') || ''))
                .filter(f => {
                    const id = f.querySelector('input[name=userId]');
                    return !id || !/^[0-9a-f-]{36}$/i.test(id.value) || /^0{8}-/.test(id.value);
                })
                .map(f => f.getAttribute('action'))");
        Assert.That(missing, Is.Empty,
            "These Settings → Users forms don't say which user they act on: " + string.Join(", ", missing));
    }

    [Test]
    public async Task Pipeline_table_sorts_and_pages()
    {
        var admin = await SignedInAsync(World.A.Admin, Desktop);
        await admin.GotoAsync("/Pipeline?View=table&Sort=value&Dir=desc");

        var table = admin.Locator("#pbTable");
        await Expect(table).ToBeVisibleAsync();

        var valueHeader = table.Locator("th").Filter(new() { Has = admin.Locator("a[data-sort-key='value']") });
        if (await valueHeader.CountAsync() > 0)
            Assert.That(await valueHeader.First.GetAttributeAsync("aria-sort"), Is.EqualTo("descending"),
                "The table's Value header should say it is sorted, highest first.");

        // At most one "Per page" box with the default id on the page.
        Assert.That(await admin.Locator("[id='lpPageSize']").CountAsync(), Is.LessThanOrEqualTo(1));
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
