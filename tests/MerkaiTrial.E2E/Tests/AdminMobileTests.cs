// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/AdminMobileTests.cs
//
// NEW (121). The super admin console on a phone (iPhone SE, 375 × 667):
//
//   • Every console page uses the whole width and does not scroll sideways
//     (a failure NAMES what sticks out — Phone.ExpectFitsAsync). Wide
//     tables scroll inside their own box, on purpose; that is allowed.
//   • Pinch-zoom is allowed, and the search box is 16px (Safari zooms the
//     page when a smaller box is tapped).
//   • The menu: out of the way until the menu button opens it; the button
//     says whether it is open; Escape closes it; a link in it goes there.
//   • A row's "Help" menu on People opens inside the screen and its items
//     can be tapped.
//   • The top bar's search finds a workspace (it did nothing before 121).
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Mobile"), Category("SuperAdmin")]
public class AdminMobileTests : E2ETest
{
    // "{A}" becomes workspace A's id.
    [TestCase("/Admin")]
    [TestCase("/Admin/Tenants")]
    [TestCase("/Admin/Tenants/Create")]
    [TestCase("/Admin/Tenants/Detail/{A}")]
    [TestCase("/Admin/Tenants/Edit/{A}")]
    [TestCase("/Admin/Tenants/Settings/{A}")]
    [TestCase("/Admin/Tenants/People/{A}")]
    [TestCase("/Admin/Plans")]
    [TestCase("/Admin/Countries")]
    [TestCase("/Admin/Countries/Detail/IN?tab=tax")]
    [TestCase("/Admin/Countries/Edit/IN")]
    [TestCase("/Admin/TaxRates/Create?forCountry=IN")]
    [TestCase("/Admin/Verticals")]
    [TestCase("/Admin/Verticals/Create")]
    [TestCase("/Admin/StartingRoles")]
    [TestCase("/Admin/StartingRoles/Edit")]
    [TestCase("/Admin/Team")]
    [TestCase("/Admin/AuditLog")]
    [TestCase("/Admin/WhatsAppTemplates")]
    [TestCase("/Admin/ViewAs")]
    public async Task Console_page_fits_a_phone_screen(string path)
    {
        if (path.Contains("{A}"))
            path = path.Replace("{A}", (await Db.TenantIdAsync(World.A.Name)).ToString());

        var page = await SignedInAsync(World.SuperAdmin, Phone.Size);
        var r = await page.GotoAsync(path);
        Assert.That(r?.Status, Is.EqualTo(200), $"{path} did not open (HTTP {r?.Status}).");

        var widths = await page.EvaluateAsync<int[]>(
            @"() => {
                const main = document.querySelector('.sa-main');
                return [Math.round(main ? main.getBoundingClientRect().width : 0), window.innerWidth];
            }");
        Assert.That(widths[0], Is.GreaterThanOrEqualTo(widths[1] - 2),
            $"On {path} the content is {widths[0]}px wide on a {widths[1]}px screen.");

        await Phone.ExpectFitsAsync(page, path);
    }

    [Test]
    public async Task The_console_can_be_zoomed_and_its_boxes_do_not_zoom_it()
    {
        var page = await SignedInAsync(World.SuperAdmin, Phone.Size);
        await page.GotoAsync("/Admin/Tenants");

        var viewport = await page.Locator("meta[name=viewport]").GetAttributeAsync("content") ?? "";
        Assert.That(viewport, Does.Not.Match(@"maximum-scale\s*=\s*1(\.0)?\b|user-scalable\s*=\s*(no|0)"),
            $"The console's viewport stops people zooming in: \"{viewport}\".");

        foreach (var box in new[] { "#tSearch", "#tStatus" })
        {
            var size = await page.Locator(box).EvaluateAsync<string>("e => getComputedStyle(e).fontSize");
            Assert.That(double.Parse(size.Replace("px", ""), System.Globalization.CultureInfo.InvariantCulture),
                Is.GreaterThanOrEqualTo(16), $"{box} is {size} on a phone — Safari zooms the page when it is tapped.");
        }
    }

    [Test]
    public async Task The_console_menu_opens_says_so_and_closes_on_a_phone()
    {
        var page = await SignedInAsync(World.SuperAdmin, Phone.Size);
        await page.GotoAsync("/Admin");
        var sidebar = page.Locator("#saSidebar");
        var button = page.Locator("#saHamburger");

        await Step("Out of the way until opened", async () =>
        {
            Assert.That(await IsOnScreenAsync(sidebar), Is.False, "The menu covers the page before it is opened.");
            Assert.That(await button.GetAttributeAsync("aria-expanded"), Is.EqualTo("false"));
        });

        await Step("The menu button opens it, and says so", async () =>
        {
            await button.ClickAsync();
            await Assertions.Expect(sidebar).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bopen\b"));
            Assert.That(await button.GetAttributeAsync("aria-expanded"), Is.EqualTo("true"));
        });

        await Step("Escape closes it", async () =>
        {
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(sidebar).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bopen\b"));
            Assert.That(await button.GetAttributeAsync("aria-expanded"), Is.EqualTo("false"));
        });

        await Step("A link in it goes there", async () =>
        {
            await button.ClickAsync();
            await sidebar.Locator("a.sa-nav-item", new() { HasText = "Audit Log" }).ClickAsync();
            await Ui.WaitForAddressAsync(page, u => Ui.PathOf(u).StartsWith("/Admin/AuditLog", StringComparison.OrdinalIgnoreCase));
            Assert.That(await IsOnScreenAsync(page.Locator("#saSidebar")), Is.False, "The menu is still covering the page.");
        });
    }

    [Test]
    public async Task A_rows_help_menu_opens_inside_the_screen_on_a_phone()
    {
        var tenantA = await Db.TenantIdAsync(World.A.Name);
        var page = await SignedInAsync(World.SuperAdmin, Phone.Size);
        await page.GotoAsync($"/Admin/Tenants/People/{tenantA}");

        var row = page.Locator($"#workspacePeople tr[data-person='{World.A["rep"].Email}']");
        await row.Locator("[data-bs-toggle=dropdown]").ScrollIntoViewIfNeededAsync();
        await row.Locator("[data-bs-toggle=dropdown]").ClickAsync();
        var menu = row.Locator(".dropdown-menu");
        await Assertions.Expect(menu).ToBeVisibleAsync();

        var edges = await menu.EvaluateAsync<double[]>("m => { const r = m.getBoundingClientRect(); return [r.left, r.right, window.innerWidth]; }");
        Assert.That(edges[0], Is.GreaterThanOrEqualTo(-1), $"The Help menu starts off the left edge ({edges[0]}px).");
        Assert.That(edges[1], Is.LessThanOrEqualTo(edges[2] + 1), $"The Help menu runs off the right edge ({edges[1]}px on a {edges[2]}px screen).");
        await Assertions.Expect(menu.Locator("button[data-action=CopyResetLink]")).ToBeVisibleAsync();
    }

    [Test]
    public async Task The_top_bar_search_finds_a_workspace()
    {
        var page = await SignedInAsync(World.SuperAdmin, new ViewportSize { Width = 1366, Height = 900 });
        await page.GotoAsync("/Admin");
        await page.Locator("#saSearchBox").FillAsync(World.A["rep"].Email);
        await page.Locator("#saSearchBox").PressAsync("Enter");
        await Ui.WaitForAddressAsync(page, u => Ui.PathOf(u).StartsWith("/Admin/Tenants", StringComparison.OrdinalIgnoreCase));
        await Assertions.Expect(page.Locator($"#tenantsTable tr[data-tenant='{World.A.Name}']")).ToBeVisibleAsync();
    }

    private static Task<bool> IsOnScreenAsync(ILocator el) =>
        el.EvaluateAsync<bool>("e => e.getBoundingClientRect().right > 0");
}
