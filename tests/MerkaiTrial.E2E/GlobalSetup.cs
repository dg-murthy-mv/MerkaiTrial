// =====================================================================
// FILE: tests/MerkaiTrial.E2E/GlobalSetup.cs
//
// Runs once, before any test:
//   1. Makes sure Playwright's Chromium is downloaded (first run only;
//      a few seconds every run after that).
//   2. Starts one browser for the whole run.
//   3. Checks the apps are up, then builds the World (WorldBuilder).
//   4. (095) Starts DbWaitWatch, which notes every database query that
//      waits 8 seconds or more and which process held it up.
//   5. (095) Tidies the outbox the tests search (TestDatabase.PrepareOutboxAsync).
//   0. (095) First of all, asks Windows not to sleep or suspend the network
//      until the run is over (KeepAwake).
// and once after the last test: stops the watch, closes the browser(s),
// and hands the computer back to its own power settings.
//
// 109: OtherBrowserAsync("firefox" | "webkit") — Firefox and WebKit (the
// engine of Safari on iPhone and Mac), downloaded and started the first
// time a test asks (CrossBrowserTests), so a run that never needs them
// never downloads them. Chromium stays the browser for everything else.
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

    // ── 109: Firefox and WebKit, on demand ────────────────────────────
    private static readonly Dictionary<string, IBrowser> Others = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim OthersGate = new(1, 1);

    /// <summary>
    /// 109. "firefox" or "webkit" (or "chromium" — the run's own browser),
    /// downloaded on first use (a minute or two, once per computer) and
    /// started once for the rest of the run.
    /// </summary>
    public static async Task<IBrowser> OtherBrowserAsync(string engine)
    {
        if (string.Equals(engine, "chromium", StringComparison.OrdinalIgnoreCase)) return Browser;

        await OthersGate.WaitAsync();
        try
        {
            if (Others.TryGetValue(engine, out var running)) return running;
            if (_playwright is null) throw new InvalidOperationException("Playwright is not running — GlobalSetup failed.");

            var type = engine.ToLowerInvariant() switch
            {
                "firefox" => _playwright.Firefox,
                "webkit"  => _playwright.Webkit,
                _ => throw new ArgumentException($"Unknown browser '{engine}' — firefox or webkit.", nameof(engine)),
            };

            TestContext.Progress.WriteLine($"[e2e setup] Making sure Playwright's {engine} is downloaded (first time only: a minute or two).");
            var exit = Microsoft.Playwright.Program.Main(new[] { "install", engine.ToLowerInvariant() });
            if (exit != 0)
                throw new InvalidOperationException(
                    $"Installing Playwright's {engine} failed (exit code {exit}). Check the internet connection, " +
                    $"or run: pwsh bin/Debug/net8.0/playwright.ps1 install {engine.ToLowerInvariant()}");

            var browser = await type.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = !E2ESettings.Headed,
                SlowMo = E2ESettings.SlowMo,
            });
            Others[engine] = browser;
            TestContext.Progress.WriteLine($"[e2e setup] {engine} {browser.Version} started.");
            return browser;
        }
        finally
        {
            OthersGate.Release();
        }
    }

    /// <summary>
    /// 109-update. Closes Firefox / WebKit as soon as its test is done. Kept
    /// open for the rest of the run, three browser engines at once took
    /// enough memory on an 8 GB computer for SQL Server to stall again
    /// (32-second queries during the first run of these tests).
    /// </summary>
    public static async Task CloseOtherBrowserAsync(string engine)
    {
        if (string.Equals(engine, "chromium", StringComparison.OrdinalIgnoreCase)) return;
        await OthersGate.WaitAsync();
        try
        {
            if (Others.Remove(engine, out var browser))
            {
                try { await browser.CloseAsync(); }
                catch (Exception ex) { TestContext.Progress.WriteLine($"[e2e] Closing {engine}: {ex.Message}"); }
            }
        }
        finally
        {
            OthersGate.Release();
        }
    }

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        KeepAwake.Start();   // 095 — see KeepAwake

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

        // 095 — before the World is built, so setup is watched too.
        DbWaitWatch.Start(E2ESettings.ConnectionString);

        // 095 — the outbox the tests search: old rows out, index in.
        await Db.PrepareOutboxAsync();

        await new WorldBuilder(_browser, Db).BuildAsync();
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        await DbWaitWatch.StopAsync();     // 095
        await Db.DropOutboxIndexAsync();   // 095 — see PrepareOutboxAsync
        foreach (var other in Others.Values) await other.CloseAsync();   // 109
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
        KeepAwake.Stop();                  // 095
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
