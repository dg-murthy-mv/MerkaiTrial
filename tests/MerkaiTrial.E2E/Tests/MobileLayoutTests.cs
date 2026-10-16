// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/MobileLayoutTests.cs
//
// The phone-sized checks behind round 087's layout fix: at iPhone SE
// width (375px) the content uses the whole screen and nothing makes the
// page scroll sideways. Before the fix, the main column was 115px wide
// (375 minus the 260px sidebar that isn't there on a phone).
//
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Smoke"), Category("Mobile")]
public class MobileLayoutTests : E2ETest
{
    private static readonly ViewportSize IPhoneSe = new() { Width = 375, Height = 667 };

    // Two screens for now. The mobile round adds one case per screen it
    // fixes, so a fixed screen stays fixed.
    [TestCase("/Dashboard")]
    [TestCase("/Leads")]
    public async Task Page_fits_a_phone_screen(string path)
    {
        var page = await SignedInAsync(World.A.Admin, IPhoneSe);
        await page.GotoAsync(path);

        var widths = await page.EvaluateAsync<int[]>(
            @"() => {
                const main = document.querySelector('.lf-main');
                return [
                    Math.round(main ? main.getBoundingClientRect().width : 0),
                    document.documentElement.scrollWidth,
                    window.innerWidth
                ];
            }");
        int mainWidth = widths[0], pageWidth = widths[1], screenWidth = widths[2];

        Assert.That(mainWidth, Is.GreaterThanOrEqualTo(screenWidth - 2),
            $"The content column is {mainWidth}px wide on a {screenWidth}px screen.");
        Assert.That(pageWidth, Is.LessThanOrEqualTo(screenWidth + 1),
            $"{path} is {pageWidth}px wide on a {screenWidth}px screen, so it scrolls sideways.");
    }

    [Test]
    public async Task The_menu_opens_from_the_hamburger_on_a_phone()
    {
        var page = await SignedInAsync(World.A.Admin, IPhoneSe);
        await page.GotoAsync("/Dashboard");

        var sidebar = page.Locator("#lfSidebar");
        Assert.That(await IsOnScreenAsync(sidebar), Is.False, "The sidebar covers the page before it is opened.");

        await page.Locator(".lf-hamburger").ClickAsync();
        await Assertions.Expect(sidebar).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bopen\b"));
    }

    private static Task<bool> IsOnScreenAsync(ILocator el) =>
        el.EvaluateAsync<bool>("e => e.getBoundingClientRect().right > 0");
}
