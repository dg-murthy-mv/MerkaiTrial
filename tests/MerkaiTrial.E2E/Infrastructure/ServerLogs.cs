// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/ServerLogs.cs
//
// NEW (105 — VUL-20). The two test servers' logs, as written so far this
// run: e2e-logs\webapi.out.log, webapi.err.log, adminweb.out.log and
// adminweb.err.log. run-e2e.ps1 says where (E2E_LOG_DIR); without it, the
// e2e-logs folder next to this project is used.
//
// Read while the servers are still writing them, so the files are opened
// shared. A server that was already running when run-e2e.ps1 started
// (-ServersOnly) wrote its log there too, so this works for that as well.
// =====================================================================

using System.Text;

namespace MerkaiTrial.E2E.Infrastructure;

public static class ServerLogs
{
    public static readonly string[] Files = { "webapi.out.log", "webapi.err.log", "adminweb.out.log", "adminweb.err.log" };

    public static string? Folder
    {
        get
        {
            if (Environment.GetEnvironmentVariable("E2E_LOG_DIR") is { Length: > 0 } dir && Directory.Exists(dir)) return dir;

            // bin/Debug/net8.0 → the project folder → e2e-logs
            var probe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "e2e-logs"));
            return Directory.Exists(probe) ? probe : null;
        }
    }

    /// <summary>Each log that exists, by name, with everything written to it so far.</summary>
    public static Dictionary<string, string> ReadAll()
    {
        var folder = Folder ?? throw new AssertionException(
            "The servers' logs were not found. Run the tests through run-e2e.ps1, which writes them to e2e-logs " +
            "and tells the tests where (E2E_LOG_DIR).");

        var logs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Files)
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            logs[name] = reader.ReadToEnd();
        }

        if (!logs.ContainsKey("webapi.out.log") || !logs.ContainsKey("adminweb.out.log"))
            throw new AssertionException($"The servers' logs are not in {folder} (expected webapi.out.log and adminweb.out.log).");
        return logs;
    }
}
