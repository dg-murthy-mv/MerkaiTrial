// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/BrowserSession.cs
//
// One person at one browser: a fresh browser context (its own cookies, so
// two sessions never share a sign-in) with one page.
//
// Every session records a Playwright TRACE — each click, each page, the
// network calls, a screenshot per step. If the session ends without being
// marked Succeeded(), the trace and a final screenshot are saved under
// e2e-artifacts/. Open a trace with:
//
//     pwsh bin/Debug/net8.0/playwright.ps1 show-trace <file>.zip
//
// or drag it onto https://trace.playwright.dev. That is what to send when
// a test fails.
// =====================================================================

namespace MerkaiTrial.E2E.Infrastructure;

public sealed class BrowserSession : IAsyncDisposable
{
    private readonly string _name;
    private bool _succeeded;

    public IBrowserContext Context { get; }
    public IPage Page { get; }

    private BrowserSession(string name, IBrowserContext context, IPage page)
    {
        _name = name;
        Context = context;
        Page = page;
    }

    public static async Task<BrowserSession> StartAsync(IBrowser browser, string name, ViewportSize? viewport = null)
    {
        var context = await browser.NewContextAsync(E2ESettings.ContextOptions(viewport));
        await context.Tracing.StartAsync(new TracingStartOptions
        {
            Title = name,
            Screenshots = true,
            Snapshots = true,
            Sources = false,
        });

        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(E2ESettings.ActionTimeoutMs);
        page.SetDefaultNavigationTimeout(60_000);

        // Merkai asks "Are you sure?" with confirm() before issuing an
        // invoice, sending again, deleting… A test means yes.
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();

        return new BrowserSession(name, context, page);
    }

    /// <summary>Call when the session's work finished; otherwise its trace is kept.</summary>
    public void Succeeded() => _succeeded = true;

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_succeeded)
            {
                await Context.Tracing.StopAsync();
            }
            else
            {
                Directory.CreateDirectory(E2ESettings.ArtifactsDir);
                var stem = Path.Combine(E2ESettings.ArtifactsDir, $"{Safe(_name)}-{DateTime.Now:HHmmss}");

                try
                {
                    await Page.ScreenshotAsync(new PageScreenshotOptions { Path = stem + ".png", FullPage = true });
                    Attach(stem + ".png", "Last screen");
                }
                catch (Exception ex)
                {
                    TestContext.Progress.WriteLine($"[e2e] Could not take a screenshot: {ex.Message}");
                }

                await Context.Tracing.StopAsync(new TracingStopOptions { Path = stem + ".zip" });
                Attach(stem + ".zip", "Playwright trace");
                TestContext.Progress.WriteLine($"[e2e] Saved trace for '{_name}': {stem}.zip");
            }
        }
        finally
        {
            await Context.CloseAsync();
        }
    }

    /// <summary>Attaches to the test result when there is a test to attach
    /// to (not during setup).</summary>
    private static void Attach(string path, string description)
    {
        try { TestContext.AddTestAttachment(path, description); }
        catch { /* setup has no test to attach to; the file is still on disk */ }
    }

    private static string Safe(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        var s = new string(chars);
        return s.Length > 80 ? s[..80] : s;
    }
}
