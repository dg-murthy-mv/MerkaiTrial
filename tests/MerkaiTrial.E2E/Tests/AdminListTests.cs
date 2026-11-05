// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/AdminListTests.cs
//
// NEW (120). The super admin lists find, filter, order and page; the
// platform API checks what it is sent.
//
//   Workspaces (Admin → Tenants)
//     • search by a PERSON's email finds the workspace they are in
//     • status and plan filters narrow the list
//     • "Most people" puts the biggest first
//     • page 2 holds other workspaces than page 1, and its links keep
//       the filters; a page past the end goes to the last page; nonsense
//       page numbers and sizes still show a list
//   Countries
//     • page 2 really is page 2 (the old pager sent "Page=2", which the
//       page never read)
//   API
//     • page 0 and a page size of 100,000 are answered, not a 500
//     • a vertical's colour and icon, and a country's code, dial code and
//       currency, must look right — anything else is a 400, and nothing is made
//   Messages
//     • "saved" shows once, not twice
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("SuperAdmin")]
public class AdminListTests : E2ETest
{
    [Test]
    public async Task The_workspace_list_searches_filters_orders_and_pages()
    {
        var a = World.A;
        var page = await SignedInAsync(World.SuperAdmin, new ViewportSize { Width = 1366, Height = 900 });
        var problems = new List<string>();
        ILocator Rows() => page.Locator("#tenantsTable tbody tr[data-tenant]");
        async Task<List<string>> NamesAsync() => (await Rows().EvaluateAllAsync<string[]>("rs => rs.map(r => r.dataset.tenant)")).ToList();

        await Step("Searching for the rep's email finds workspace A", async () =>
        {
            await page.GotoAsync("/Admin/Tenants?SearchTerm=" + Uri.EscapeDataString(a["rep"].Email));
            var names = await NamesAsync();
            if (!names.Contains(a.Name)) problems.Add($"Searching for {a["rep"].Email} did not find workspace A. Found: {string.Join(", ", names)}");
            if (names.Count != 1) problems.Add($"Searching for one person's email found {names.Count} workspaces.");
        });

        string planOfA = "";
        await Step("Status and plan narrow the list", async () =>
        {
            await page.GotoAsync("/Admin/Tenants?SearchTerm=" + Uri.EscapeDataString(a.Name));
            planOfA = (await page.Locator($"tr[data-tenant='{a.Name}'] [data-col=plan]").InnerTextAsync()).Trim();

            await page.GotoAsync($"/Admin/Tenants?SearchTerm={Uri.EscapeDataString(a.Name)}&IsActive=false");
            if ((await NamesAsync()).Contains(a.Name)) problems.Add("Workspace A (active) is listed under 'Switched off'.");

            await page.GotoAsync($"/Admin/Tenants?SearchTerm={Uri.EscapeDataString(a.Name)}&Plan={Uri.EscapeDataString(planOfA)}");
            if (!(await NamesAsync()).Contains(a.Name)) problems.Add($"Workspace A is missing under its own plan ({planOfA}).");

            await page.GotoAsync($"/Admin/Tenants?SearchTerm={Uri.EscapeDataString(a.Name)}&Plan=no-such-plan");
            if ((await NamesAsync()).Contains(a.Name)) problems.Add("Workspace A is listed under a plan it is not on.");
        });

        await Step("\"Most people\" puts the biggest first", async () =>
        {
            await page.GotoAsync("/Admin/Tenants?Sort=users&PageSize=25");
            var counts = (await page.Locator("#tenantsTable tbody [data-col=users]").AllInnerTextsAsync())
                .Select(t => int.TryParse(t.Trim(), out var n) ? n : -1).ToList();
            for (var i = 1; i < counts.Count; i++)
                if (counts[i] > counts[i - 1]) { problems.Add($"'Most people' is out of order: {string.Join(", ", counts)}"); break; }
        });

        await Step("Page 2 is other workspaces, its links keep the filters, and past the end goes to the last page", async () =>
        {
            await page.GotoAsync("/Admin/Tenants?PageSize=10&Sort=name");
            var total = int.Parse((await page.Locator("#tenantCount").InnerTextAsync()).Trim());
            if (total <= 10) { TestContext.Progress.WriteLine($"  (only {total} workspaces — page 2 not checked)"); return; }

            var first = await NamesAsync();
            await page.GotoAsync("/Admin/Tenants?PageSize=10&Sort=name&PageNumber=2");
            var second = await NamesAsync();
            if (second.Count == 0) problems.Add("Page 2 of the workspaces is empty.");
            if (second.Intersect(first).Any()) problems.Add("Page 2 repeats workspaces from page 1: " + string.Join(", ", second.Intersect(first)));

            var link = page.Locator(".lp-foot a[href*='PageNumber=1']").First;
            if (await link.CountAsync() > 0 && !((await link.GetAttributeAsync("href")) ?? "").Contains("Sort=name"))
                problems.Add("The pager's links drop the chosen order.");

            await page.GotoAsync("/Admin/Tenants?PageSize=10&PageNumber=9999");
            if (!page.Url.Contains("PageNumber=" + (int)Math.Ceiling(total / 10.0)))
                problems.Add($"A page past the end did not go to the last page: {page.Url}");
        });

        await Step("Nonsense page numbers and sizes still show a list", async () =>
        {
            var r = await page.GotoAsync("/Admin/Tenants?PageNumber=-3&PageSize=7");
            if (r?.Status != 200 || await page.Locator("#tenantsTable, #tenantsEmpty").CountAsync() == 0)
                problems.Add($"PageNumber=-3&PageSize=7 did not show the list (HTTP {r?.Status}).");
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    [Test]
    public async Task Countries_page_two_is_page_two()
    {
        var page = await SignedInAsync(World.SuperAdmin, new ViewportSize { Width = 1366, Height = 900 });
        await page.GotoAsync("/Admin/Countries?PageSize=10");
        var first = (await page.Locator("tr[data-country]").EvaluateAllAsync<string[]>("rs => rs.map(r => r.dataset.country)")).ToList();
        if (first.Count < 10) Assert.Ignore($"Only {first.Count} active countries — nothing on page 2 to check.");

        var next = page.Locator(".lp-foot a[href*='PageNumber=2']").First;
        await next.ClickAsync();
        await Ui.WaitForAddressAsync(page, u => u.Contains("PageNumber=2"));
        var second = (await page.Locator("tr[data-country]").EvaluateAllAsync<string[]>("rs => rs.map(r => r.dataset.country)")).ToList();
        Assert.That(second, Is.Not.Empty, "Page 2 of the countries is empty.");
        Assert.That(second.Intersect(first), Is.Empty, "Page 2 of the countries shows page 1 again.");
    }

    [Test]
    public async Task The_platform_api_answers_odd_paging_and_refuses_bad_values()
    {
        var super = await Api.AsAsync(World.SuperAdmin);
        super.WaitWhenPaced = true;
        var problems = new List<string>();

        await Step("Page 0 and a huge page size are answered, not a server error", async () =>
        {
            foreach (var path in new[]
            {
                "/api/tenants/paginated?page=0&pageSize=100000",
                "/api/tenants/paginated?page=-5&pageSize=0&sort=nonsense",
                "/api/countries?pageNumber=0&pageSize=-1",
                "/api/company-verticals?pageNumber=0&pageSize=0",
                "/api/tax-rates?pageNumber=-1&pageSize=100000",
            })
            {
                var r = await super.GetAsync(path);
                if (r.Status != 200) problems.Add($"{path} answered {r}");
            }
            var big = (await super.GetAsync("/api/tenants/paginated?page=1&pageSize=100000")).Json();
            if (big.TryGetProperty("pageSize", out var ps) && ps.GetInt32() > 100)
                problems.Add($"The workspace list gave a page of {ps.GetInt32()} — it should stop at 100.");
        });

        await Step("Bad values are refused (400) and nothing is made", async () =>
        {
            var bad = new (string Path, object Body, string What)[]
            {
                ("/api/company-verticals", new { name = "E2E bad colour", icon = "building", color = "red;background:url(x)" }, "a colour that is not one of the offered ones"),
                ("/api/company-verticals", new { name = "E2E bad icon", icon = "\"><img src=x>", color = "primary" }, "an icon that is not an icon name"),
                ("/api/company-verticals", new { name = "", icon = "building", color = "primary" }, "no name"),
                ("/api/countries", new { code = "1!", name = "Nowhere", defaultTaxRate = 5 }, "a country code that is not letters"),
                ("/api/countries", new { code = "ZQ", name = "Nowhere", dialCode = "66", defaultTaxRate = 5 }, "a dial code without +"),
                ("/api/countries", new { code = "ZQ", name = "Nowhere", currencyCode = "B$", defaultTaxRate = 5 }, "a currency that is not 3 letters"),
                ("/api/countries", new { code = "ZQ", name = "Nowhere", defaultTaxRate = 150 }, "a tax rate over 100%"),
            };
            foreach (var (path, body, what) in bad)
            {
                var r = await super.PostAsync(path, body);
                if (r.Status != 400) problems.Add($"POST {path} with {what} answered {r} — expected 400.");
            }
            if ((await super.GetAsync("/api/countries/ZQ")).Status == 200) problems.Add("Country ZQ was made from a refused request.");
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    [Test]
    public async Task A_saved_message_shows_once()
    {
        var page = await SignedInAsync(World.SuperAdmin, new ViewportSize { Width = 1366, Height = 900 });
        await page.GotoAsync("/Admin/Countries/Edit/IN");
        await page.EvaluateAsync("() => { window.__e2eOld = true; }");
        await page.Locator("form[data-confirm-off] button[type=submit]").First.ClickAsync();
        await page.WaitForFunctionAsync("() => window.__e2eOld !== true");
        await Assertions.Expect(page.Locator(".alert-success").First).ToBeVisibleAsync();
        Assert.That(await page.Locator(".alert-success").CountAsync(), Is.EqualTo(1),
            "After saving a country the success message shows more than once.");
    }
}
