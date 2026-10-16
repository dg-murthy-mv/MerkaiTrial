// =====================================================================
// FILE: tests/MerkaiTrial.E2E/GlobalSetup.cs
//
// Runs once, before any test:
//   1. Makes sure Playwright's Chromium is downloaded (first run only;
//      a few seconds every run after that).
//   2. Starts one browser for the whole run.
//   3. Checks the apps are up, then builds the World (WorldBuilder).
// and once after the last test: closes the browser.
//
// If anything here fails, every test is reported as failed with this
// error — read the first one.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

// In the ROOT namespace on purpose: NUnit runs a [SetUpFixture] for the
// tests in its own namespace and below, so this covers MerkaiTrial.E2E.Tests.
namespace MerkaiTrial.E2E;

[SetUpFixture]
public sealed class GlobalSetup
{
    private static IPlaywright? _playwright;
    private static IBrowser? _browser;

    public static IBrowser Browser =>
        _browser ?? throw new InvalidOperationException("The browser is not running — GlobalSetup failed.");

    public static TestDatabase Db { get; } = new(E2ESettings.ConnectionString);

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        var exit = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
        if (exit != 0)
            throw new InvalidOperationException(
                $"Installing Playwright's Chromium failed (exit code {exit}). Check the internet connection, " +
                "or run: pwsh bin/Debug/net8.0/playwright.ps1 install chromium");

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = !E2ESettings.Headed,
            SlowMo = E2ESettings.SlowMo,
        });

        await EnsureAppIsUpAsync();
        await new WorldBuilder(_browser, Db).BuildAsync();
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }

    private static async Task EnsureAppIsUpAsync()
    {
        await using var session = await BrowserSession.StartAsync(Browser, "app-is-up");
        try
        {
            var response = await session.Page.GotoAsync("/Account/Login");
            if (response is null || !response.Ok)
                throw new InvalidOperationException($"HTTP {response?.Status}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Merkai is not answering at {E2ESettings.BaseUrl} ({ex.Message}). Start the test servers " +
                "first: run-e2e.ps1 does it for you, or run-e2e.ps1 -ServersOnly to keep them running " +
                "while you use Visual Studio's Test Explorer.", ex);
        }
        session.Succeeded();
    }
}
