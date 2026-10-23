// =====================================================================
// FILE: tests/MerkaiTrial.E2E/E2ESettings.cs
//
// Where the apps are and which database they use. run-e2e.ps1 sets all of
// these; the defaults below match it, so running from Visual Studio's Test
// Explorer works too once the servers are up (run-e2e.ps1 -ServersOnly).
//
//   E2E_BASE_URL   Admin.Web for the test run      https://localhost:7393
//   E2E_API_URL    WebApi for the test run (090)   https://localhost:7391
//   E2E_DB         the TEST database connection    (Database must end in _E2E)
//   HEADED=1       show the browser window
//   E2E_SLOWMO=250 pause this many ms between browser actions (watching)
//
//   100 — calling the WebApi directly as a given person:
//   E2E_JWT_KEY        the signing key run-e2e.ps1 gave BOTH test servers
//                      for this run only (a fresh random one each run —
//                      never your real key). With -ServersOnly it is also
//                      written to e2e-logs/e2e-jwt.key while the servers
//                      run, so Test Explorer finds it.
//   E2E_JWT_ISSUER     merkaitrial-admin  (what run-e2e.ps1 sets)
//   E2E_JWT_AUDIENCE   merkaitrial-api
// =====================================================================

namespace MerkaiTrial.E2E;

public static class E2ESettings
{
    public const string DefaultBaseUrl = "https://localhost:7393";

    public const string DefaultConnectionString =
        "Server=localhost;Database=MerkaiTrial_E2E;Trusted_Connection=True;TrustServerCertificate=True";

    /// <summary>
    /// The password every seeded account gets. 12+ characters and none of
    /// the words PasswordService bans (password, merkai, madeevision,
    /// 12345678, qwerty, welcome). It only ever exists in the _E2E database.
    /// </summary>
    public const string Password = "Blue-Harbour-Tide-2026!";

    /// <summary>What the password-reset test changes its own account to.</summary>
    public const string ResetPassword = "Green-Valley-Lantern-77!";

    public static string BaseUrl =>
        (Environment.GetEnvironmentVariable("E2E_BASE_URL") is { Length: > 0 } v ? v : DefaultBaseUrl)
        .TrimEnd('/');

    /// <summary>
    /// 090. The WebApi the test run starts. Only used to check that files
    /// cannot be fetched from it directly, without signing in.
    /// </summary>
    public const string DefaultApiBaseUrl = "https://localhost:7391";

    public static string ApiBaseUrl =>
        (Environment.GetEnvironmentVariable("E2E_API_URL") is { Length: > 0 } v ? v : DefaultApiBaseUrl)
        .TrimEnd('/');

    // ── 100: tokens for the WebApi ─────────────────────────────────────

    /// <summary>
    /// The run's signing key, or null when the servers were not started by
    /// run-e2e.ps1 (then the tests that call the API directly say so).
    /// </summary>
    public static string? JwtSigningKey
    {
        get
        {
            if (Environment.GetEnvironmentVariable("E2E_JWT_KEY") is { Length: > 0 } v) return v.Trim();
            try
            {
                // -ServersOnly: run-e2e.ps1 leaves the key here while its servers run.
                var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
                for (var d = dir; d != null; d = d.Parent)
                {
                    var file = Path.Combine(d.FullName, "e2e-logs", "e2e-jwt.key");
                    if (File.Exists(file)) return File.ReadAllText(file).Trim();
                    if (File.Exists(Path.Combine(d.FullName, "MerkaiTrial.E2E.csproj"))) break;
                }
            }
            catch { /* no key — the caller reports it */ }
            return null;
        }
    }

    public static string JwtIssuer =>
        Environment.GetEnvironmentVariable("E2E_JWT_ISSUER") is { Length: > 0 } v ? v : "merkaitrial-admin";

    public static string JwtAudience =>
        Environment.GetEnvironmentVariable("E2E_JWT_AUDIENCE") is { Length: > 0 } v ? v : "merkaitrial-api";

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("E2E_DB") is { Length: > 0 } v ? v : DefaultConnectionString;

    public static bool Headed =>
        Environment.GetEnvironmentVariable("HEADED") is "1" or "true" or "TRUE" or "True";

    public static float? SlowMo =>
        float.TryParse(Environment.GetEnvironmentVariable("E2E_SLOWMO"), out var ms) && ms > 0 ? ms : null;

    /// <summary>Default wait for any one browser action, in milliseconds.
    /// Generous: the first request to each page compiles its Razor view.</summary>
    public const float ActionTimeoutMs = 20_000;

    /// <summary>Where traces and screenshots of failed tests go.</summary>
    public static string ArtifactsDir =>
        Path.Combine(TestContext.CurrentContext.WorkDirectory, "e2e-artifacts");

    public static BrowserNewContextOptions ContextOptions(ViewportSize? viewport = null) => new()
    {
        BaseURL = BaseUrl,
        // The apps run on the ASP.NET development certificate.
        IgnoreHTTPSErrors = true,
        ViewportSize = viewport ?? new ViewportSize { Width = 1366, Height = 900 },
        Locale = "en-GB",
    };
}
