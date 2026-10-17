// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/MobileLayoutTests.cs
//
// The phone-sized checks behind round 087's layout fix: at iPhone SE
// width (375px) the content uses the whole screen and nothing makes the
// page scroll sideways. Before the fix, the main column was 115px wide
// (375 minus the 260px sidebar that isn't there on a phone).
//
// 094 — the mobile round:
//   • Every main list and form a rep uses is checked, not just two: the
//     dashboard, Tasks, Leads (and Add lead), the pipeline (and New deal),
//     Contacts and Companies. A failure names the element that sticks out
//     (Phone.ExpectFitsAsync).
//   • Pinch-zoom is allowed (no maximum-scale in the viewport), and form
//     boxes are 16px on a phone, so tapping one does not zoom the page.
//   • The menu: opens from the hamburger, says it is open, closes with
//     Escape, and a link in it takes you there.
//   • The lead list shows cards on a phone, not the wide table.
//
// The lead and deal pages themselves — and doing the work on them — are
// in MobileWorkTests, which make their own lead and deal.
// =====================================================================

using System.Text.RegularExpressions;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Smoke"), Category("Mobile")]
public class MobileLayoutTests : E2ETest
{
    [TestCase("/Dashboard")]
    [TestCase("/Tasks")]
    [TestCase("/Leads")]
    [TestCase("/Leads/Create")]
    [TestCase("/Pipeline")]
    [TestCase("/Pipeline/Create")]
    [TestCase("/Contacts")]
    [TestCase("/Companies")]
    public async Task Page_fits_a_phone_screen(string path)
    {
        var page = await SignedInAsync(World.A.Admin, Phone.Size);
        await page.GotoAsync(path);

        var widths = await page.EvaluateAsync<int[]>(
            @"() => {
                const main = document.querySelector('.lf-main');
                return [
                    Math.round(main ? main.getBoundingClientRect().width : 0),
                    window.innerWidth
                ];
            }");
        int mainWidth = widths[0], screenWidth = widths[1];

        Assert.That(mainWidth, Is.GreaterThanOrEqualTo(screenWidth - 2),
            $"The content column is {mainWidth}px wide on a {screenWidth}px screen.");

        await Phone.ExpectFitsAsync(page, path);
    }

    [Test]
    public async Task The_page_can_be_zoomed_and_tapping_a_box_does_not_zoom_it()
    {
        var page = await SignedInAsync(World.A.Admin, Phone.Size);
        await page.GotoAsync("/Leads/Create");

        await Step("Pinch-zoom is allowed", async () =>
        {
            var viewport = await page.Locator("meta[name=viewport]").GetAttributeAsync("content") ?? "";
            Assert.That(viewport, Does.Not.Match(@"maximum-scale\s*=\s*1(\.0)?\b|user-scalable\s*=\s*(no|0)"),
                $"The viewport stops people zooming in: \"{viewport}\".");
        });

        await Step("Form boxes are 16px on a phone (Safari zooms in on anything smaller)", async () =>
        {
            foreach (var box in new[] { "#Input_FullName", "#Input_Email", "#Input_CountryId" })
            {
                var size = await page.Locator(box).EvaluateAsync<string>("e => getComputedStyle(e).fontSize");
                Assert.That(double.Parse(size.Replace("px", ""), System.Globalization.CultureInfo.InvariantCulture),
                    Is.GreaterThanOrEqualTo(16), $"{box} is {size} on a phone.");
            }
        });
    }

    [Test]
    public async Task The_menu_opens_from_the_hamburger_on_a_phone()
    {
        var page = await SignedInAsync(World.A.Admin, Phone.Size);
        await page.GotoAsync("/Dashboard");

        var sidebar = page.Locator("#lfSidebar");
        var hamburger = page.Locator(".lf-hamburger");
        var open = new Regex(@"\bopen\b");

        await Step("The menu is out of the way until it is opened", async () =>
        {
            Assert.That(await IsOnScreenAsync(sidebar), Is.False, "The sidebar covers the page before it is opened.");
            Assert.That(await hamburger.GetAttributeAsync("aria-expanded"), Is.EqualTo("false"));
        });

        await Step("The hamburger opens it, and says so", async () =>
        {
            await hamburger.ClickAsync();
            await Assertions.Expect(sidebar).ToHaveClassAsync(open);
            Assert.That(await hamburger.GetAttributeAsync("aria-expanded"), Is.EqualTo("true"));
        });

        await Step("Escape closes it", async () =>
        {
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(sidebar).Not.ToHaveClassAsync(open);
            Assert.That(await hamburger.GetAttributeAsync("aria-expanded"), Is.EqualTo("false"));
        });

        await Step("A link in it goes there", async () =>
        {
            await hamburger.ClickAsync();
            await Assertions.Expect(sidebar).ToHaveClassAsync(open);
            await sidebar.Locator("a.lf-nav-item", new() { HasText = "Leads" }).First.ClickAsync();
            await Ui.WaitForPageAsync(page, u => Ui.PathOf(u).StartsWith("/Leads", StringComparison.OrdinalIgnoreCase), "Leads");
            Assert.That(await IsOnScreenAsync(page.Locator("#lfSidebar")), Is.False, "The menu is still covering the page.");
        });
    }

    [Test]
    public async Task The_lead_list_is_cards_on_a_phone()
    {
        var desk = await SignedInAsync(World.A.Admin);
        var name = World.Unique("Meera Mobile");
        var leadId = await Step("Add a lead (on a desktop)", () =>
            Flows.CreateLeadAsync(desk, name, World.EmailFor("A", "mm" + Random.Shared.Next(10000, 99999)), 250000m, World.A));

        var page = await SignedInAsync(World.A.Admin, Phone.Size);
        await page.GotoAsync("/Leads?SearchTerm=" + Uri.EscapeDataString(name));

        await Step("No wide table; one card, with Call and Open", async () =>
        {
            await Assertions.Expect(page.Locator(".lx-table")).ToBeHiddenAsync();
            var card = page.Locator($".lx-card[data-lead-id='{leadId}']");
            await Assertions.Expect(card).ToBeVisibleAsync();
            await Assertions.Expect(card).ToContainTextAsync(name);
            await Assertions.Expect(card.Locator("a[href^='tel:']")).ToBeVisibleAsync();
            await card.GetByRole(AriaRole.Link, new() { Name = "Open", Exact = true }).ClickAsync();
            await Ui.WaitForPageAsync(page, u => u.Contains(leadId.ToString(), StringComparison.OrdinalIgnoreCase), "The lead");
        });
    }

    private static Task<bool> IsOnScreenAsync(ILocator el) =>
        el.EvaluateAsync<bool>("e => e.getBoundingClientRect().right > 0");
}
