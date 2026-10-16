// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/SignInTests.cs
//
// Everyone can get in, nobody gets in with a wrong password, and nobody
// sees a page without signing in.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Smoke")]
public class SignInTests : E2ETest
{
    [TestCase("admin")]
    [TestCase("manager")]
    [TestCase("rep")]
    [TestCase("viewer")]
    public async Task Each_role_signs_in_and_sees_the_dashboard(string who)
    {
        var page = await SignedInAsync(World.A[who]);

        await Assertions.Expect(page.Locator(".dash-header")).ToContainTextAsync("Sales Dashboard");
        await Assertions.Expect(page.Locator(".dash-header")).ToContainTextAsync(World.A.Name);
    }

    [Test]
    public async Task Super_admin_signs_in_to_the_admin_area()
    {
        var page = await SignedInAsync(World.SuperAdmin);
        Assert.That(Ui.PathOf(page.Url), Does.StartWith("/Admin"));
    }

    [Test]
    public async Task A_wrong_password_is_refused_with_one_message()
    {
        var page = await NewPageAsync();

        // One failed attempt on a real account (well below any lockout),
        // one on an address that has no account.
        var real = await Step("Wrong password for a real account",
            () => FailedSignInMessageAsync(page, World.A["rep"].Email));
        var fake = await Step("Any password for an unknown address",
            () => FailedSignInMessageAsync(page, World.EmailFor("A", "nobody")));

        // They must read the same — otherwise the sign-in page tells anyone
        // which emails have accounts.
        Assert.That(fake, Is.EqualTo(real),
            "A real email and an unknown one got different sign-in errors.");
    }

    private static async Task<string> FailedSignInMessageAsync(IPage page, string email)
    {
        // A fresh load each time, so the error read below can only have
        // come from this attempt.
        await page.GotoAsync("/Account/Login");
        await page.Locator("#Email").FillAsync(email);
        await page.Locator("#Password").FillAsync("Not-The-Right-One-123");
        await page.Locator("form button[type=submit]").ClickAsync();

        var error = page.Locator(".alert-error");
        await Assertions.Expect(error).ToBeVisibleAsync();
        Assert.That(Ui.PathOf(page.Url), Does.EndWith("/Account/Login"));
        return (await error.InnerTextAsync()).Trim();
    }

    [TestCase("/Dashboard")]
    [TestCase("/Leads")]
    [TestCase("/Settings/Users")]
    public async Task Pages_need_a_sign_in(string path)
    {
        var page = await NewPageAsync();
        await page.GotoAsync(path);
        Assert.That(Ui.PathOf(page.Url), Does.EndWith("/Account/Login"),
            $"{path} opened without signing in.");
    }
}
