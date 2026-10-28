// =====================================================================
// ErrorDetails.cs
// Location: MerkaiTrial.Application/Common/ErrorDetails.cs
//
// NEW FILE (105 — VUL-12). One switch, read by both hosts, for whether an
// unexpected error may show its details (exception text, stack trace) to
// whoever caused it.
//
//   Diagnostics:ShowErrorDetails   not set → Development only (as before)
//                                  false   → never, even in Development
//
// run-e2e.ps1 sets it to false, so the test servers answer errors the way
// production does and the tests can check that nothing leaks. The details
// always go to the log either way.
//
// THE FAULT PROBE
//   Diagnostics:FaultProbe = true (Development only) maps GET /__e2e/fault
//   on both hosts: it throws an exception whose message is full of things
//   that must never reach a visitor — a SQL statement, a connection string
//   with a password, a table name. The test calls it and checks the page
//   it gets back holds none of them. It is mapped only when BOTH the
//   environment is Development AND the setting is on; run-e2e.ps1 turns
//   it on for the test servers and nothing else does. The probe can only
//   throw; it reads and changes nothing.
// =====================================================================

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace MerkaiTrial.Application.Common;

public static class ErrorDetails
{
    public const string ShowSetting  = "Diagnostics:ShowErrorDetails";
    public const string ProbeSetting = "Diagnostics:FaultProbe";

    public const string FaultProbePath = "/__e2e/fault";

    /// <summary>
    /// What the probe throws. Every part of it stands for something a real
    /// error message can carry: SQL, a connection string, a password, a
    /// type name. None of it may reach the response.
    /// </summary>
    public const string FaultProbeMessage =
        "E2E fault probe: SELECT PasswordHash, SecurityStamp FROM dbo.Users WHERE Email = 'probe@example.invalid'; " +
        "Server=probe-sql.internal;Database=MerkaiProbe;User Id=sa;Password=Pr0be-S3cret-105;";

    public static bool Show(IConfiguration config, IHostEnvironment env)
        => bool.TryParse(config[ShowSetting], out var show) ? show : env.IsDevelopment();

    public static bool FaultProbeOn(IConfiguration config, IHostEnvironment env)
        => env.IsDevelopment() && bool.TryParse(config[ProbeSetting], out var on) && on;
}
