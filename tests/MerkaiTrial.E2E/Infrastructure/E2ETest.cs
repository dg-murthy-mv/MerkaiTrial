// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/E2ETest.cs
//
// Base class for every test. Gives a test as many browser sessions as it
// needs (an admin AND a customer, say), and after the test:
//   • passed  → closes them, nothing kept
//   • failed  → keeps a trace + screenshot of each (see BrowserSession)
//
// Step("…") names what the test is doing, so a failure reads
// "Step 'Accept the quote as the customer' failed: …" rather than a
// bare timeout.
// =====================================================================

using NUnit.Framework.Interfaces;

namespace MerkaiTrial.E2E.Infrastructure;

public abstract class E2ETest
{
    private readonly List<BrowserSession> _sessions = new();

    protected static TestDatabase Db => GlobalSetup.Db;

    /// <summary>A fresh, signed-out browser session.</summary>
    protected async Task<IPage> NewPageAsync(string label = "anonymous", ViewportSize? viewport = null,
        string? networkAddress = null, IBrowser? browser = null)   // 109 — browser: Firefox / WebKit for CrossBrowserTests
    {
        var name = $"{TestContext.CurrentContext.Test.Name}-{label}";
        var session = await BrowserSession.StartAsync(browser ?? GlobalSetup.Browser, name, viewport, networkAddress);
        _sessions.Add(session);
        return session.Page;
    }

    /// <summary>A fresh session, signed in as <paramref name="user"/>.</summary>
    protected async Task<IPage> SignedInAsync(TestUser user, ViewportSize? viewport = null, IBrowser? browser = null)
    {
        var page = await NewPageAsync(user.Key, viewport, browser: browser);
        await Ui.SignInAsync(page, user, superAdmin: ReferenceEquals(user, World.SuperAdmin));
        return page;
    }

    protected static async Task Step(string name, Func<Task> action)
    {
        TestContext.Progress.WriteLine($"  → {name}");
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not SuccessException and not IgnoreException)
        {
            throw new AssertionException($"Step '{name}' failed: {ex.Message}", ex);
        }
    }

    protected static async Task<T> Step<T>(string name, Func<Task<T>> action)
    {
        T result = default!;
        await Step(name, async () => { result = await action(); });
        return result;
    }

    [TearDown]
    public Task CloseSessionsAsync() =>
        CloseSessionsNowAsync(TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Passed);

    /// <summary>
    /// 109-update. Closes this test's sessions now — for a test that must
    /// close its browser before it ends (CrossBrowserTests). A session of a
    /// test that did not succeed keeps its trace, as at teardown.
    /// </summary>
    protected async Task CloseSessionsNowAsync(bool passed)
    {
        foreach (var s in _sessions)
        {
            if (passed) s.Succeeded();
            try { await s.DisposeAsync(); }
            catch (Exception ex) { TestContext.Progress.WriteLine($"[e2e] Closing a session failed: {ex.Message}"); }
        }
        _sessions.Clear();
    }
}
